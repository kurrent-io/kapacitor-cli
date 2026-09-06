using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.OpenCode;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

partial class WatchCommand(
        ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http,
        ICredentialSource credentials) {
    readonly CursorMarkers  _markers  = new(config);
    readonly WatcherManager _watchers = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>
    /// The watcher's hub connection, carrying the bearer the credential source holds at connect time.
    /// </summary>
    internal HubConnection BuildHubConnection(string hubUrl) {
        var hubConnection = new HubConnectionBuilder()
            .WithUrl(
                hubUrl,
                options => {
                    // Resolved per connect and per reconnect, not captured: a reconnect after a long
                    // outage must send whatever the credential is by then, not the one this watcher
                    // started with.
                    options.AccessTokenProvider = async () => (await credentials.ResolveAsync(default)).Bearer;
                }
            )
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, CapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Halve KeepAliveInterval (15s → 7s); see ServerConnection for rationale.
        // ServerTimeout stays at the 30s default for rollout safety.
        hubConnection.KeepAliveInterval = TimeSpan.FromSeconds(7);

        return hubConnection;
    }

    /// <summary>Outcome of deciding whether the parent-exit watchdog can run.</summary>
    internal enum ParentWatchdog {
        /// <summary>Parent PID is alive — start the 5s liveness poll.</summary>
        Monitor,

        /// <summary>No parent PID was supplied — nothing to monitor.</summary>
        NoParentPid,

        /// <summary>A parent PID was supplied but it's already dead at startup
        /// (typically a transient process from bad PID resolution). Must be surfaced,
        /// never silently skipped.</summary>
        ParentAlreadyDead
    }

    /// <summary>
    /// Decides whether the parent-exit watchdog should run. Pure so the three
    /// outcomes — including the dead-at-startup case that caused stuck sessions —
    /// are unit-testable without spawning processes.
    /// </summary>
    internal static ParentWatchdog DecideParentWatchdog(int? parentPid, Func<int, bool> isAlive) =>
        parentPid is not { } ppid ? ParentWatchdog.NoParentPid
        : !isAlive(ppid)          ? ParentWatchdog.ParentAlreadyDead
        :                           ParentWatchdog.Monitor;

    /// <summary>
    /// Staged outcome for the wedged/parent-dead recovery loop. A watcher that hit
    /// <see cref="ParentWatchdog.ParentAlreadyDead"/> is alive-and-connected with no end path
    /// (the server sweep only sees GONE watchers, and Kiro/OpenCode have no idle fallback), so
    /// recovery re-resolves + re-arms the watchdog when possible and only ends on a long ceiling.
    /// </summary>
    internal enum ParentDeadRecovery {
        /// <summary>Re-resolution found a live parent — re-arm the watchdog; end nothing (preferred).</summary>
        ReArm,

        /// <summary>No live parent yet AND still under the ceiling with recent progress — keep polling.</summary>
        KeepWaiting,

        /// <summary>Resolution keeps failing AND no transcript progress for longer than the ceiling — end.</summary>
        EndTerminal
    }

    /// <summary>
    /// Pure staged decision for the parent-dead recovery loop. Re-arm takes priority (a found,
    /// live parent is the preferred outcome and never ends the session). Otherwise the session ends
    /// ONLY when the no-progress window exceeds the ceiling — a user parked at a Kiro/OpenCode prompt
    /// produces no transcript lines but must not be ended before then, and any new progress resets
    /// <paramref name="noProgressElapsed"/> via the caller.
    /// </summary>
    internal static ParentDeadRecovery DecideParentDeadRecovery(
            int?            reResolvedPid,
            Func<int, bool> isAlive,
            TimeSpan        noProgressElapsed,
            TimeSpan        ceiling
        ) =>
        reResolvedPid is { } pid && isAlive(pid) ? ParentDeadRecovery.ReArm
        : noProgressElapsed > ceiling            ? ParentDeadRecovery.EndTerminal
        :                                          ParentDeadRecovery.KeepWaiting;

    /// <summary>
    /// Long ceiling for the staged parent-dead / wedged-watcher recovery. Deliberately far above the
    /// 60-min idle default: an idle GUI conversation ends via the idle timeout, whereas this ceiling
    /// exists only to eventually end a wedged, alive-but-connected watcher (invisible to the server
    /// sweep) whose parent can't be re-resolved — without false-ending a user parked at a prompt.
    /// </summary>
    internal static readonly TimeSpan DefaultParentDeadCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the parent-dead recovery ceiling from KCAP_PARENT_DEAD_CEILING_MINUTES, falling back
    /// to <see cref="DefaultParentDeadCeiling"/> for unset/blank/non-numeric/non-positive values.
    /// Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveParentDeadCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultParentDeadCeiling;

    /// <summary>
    /// Shutdown completion signal for a JSONL transcript's final line: complete when the file is
    /// empty, newline-terminated, OR its last (newline-less) line parses as JSON. Length-stability
    /// is NOT proof of completion — a large write can pause mid-record for longer than any bounded
    /// wait, so a static-but-unparseable tail is still incomplete. Pure so it is unit-testable
    /// without a real file.
    /// </summary>
    internal static bool IsFinalLineComplete(string fileText) {
        if (fileText.Length == 0 || fileText[^1] == '\n') return true;

        var lastNl = fileText.LastIndexOf('\n');
        var tail   = lastNl < 0 ? fileText : fileText[(lastNl + 1)..];

        // A whitespace-only trailing partial carries nothing to lose → treat as complete; otherwise
        // the tail must parse as a complete JSON record (length-stability alone is NOT proof).
        return tail.Trim().Length == 0 || IsCompleteJsonRecord(tail);
    }

    /// <summary>
    /// Bounded wait (≤2s, 4×500ms) for the shutdown final drain: re-reads <paramref name="path"/>
    /// looking for <see cref="IsFinalLineComplete"/> before the watcher decides whether it's safe to
    /// send-and-advance the newline-less final line. Never throws — a read failure just counts as
    /// "not yet complete" for that iteration; the delay between iterations is likewise best-effort so
    /// cancellation/short-lived environments can't turn this into a hang.
    /// <para>Shares read+write: this polls while the vendor flushes its final records, so it must not
    /// block that write (see <see cref="SharedFileText"/>).</para>
    /// </summary>
    internal static async Task<bool> WaitForFinalLineCompletionAsync(string path, int attempts = 4, int delayMs = 500) {
        for (var i = 0; i < attempts; i++) {
            try {
                if (IsFinalLineComplete(await File.ReadAllTextSharedAsync(path))) return true;
            } catch {
                // Transient read failure (e.g. concurrent write) — try again on the next iteration.
            }

            try {
                await Task.Delay(delayMs);
            } catch {
                // Never let a delay hiccup abort the shutdown path.
            }
        }

        return false;
    }

    /// <summary>
    /// Maximum single wait between heartbeat touches. Comfortably below the
    /// <c>WatcherHeartbeat.Threshold</c> so no chunked wait can ever look stale, and matches the
    /// main loop's disconnected-branch poll cadence.
    /// </summary>
    internal static readonly TimeSpan HeartbeatSlice = TimeSpan.FromSeconds(5);

    /// <summary>
    /// cadence for the periodic full-prefix re-hash
    /// (<see cref="CursorRewriteGuard.VerifyFullPrefix"/>), the coarser-grained safety net beyond
    /// the per-poll two-zone checks: every Nth poll (~1s cadence — roughly once a minute), the
    /// whole file is re-read from byte 0 and its prefix hash compared to the last sample, so a
    /// rewrite entirely inside an already-checkpointed-and-forgotten middle region can still be
    /// caught even though the two-zone checks never look there.
    /// </summary>
    internal const int CursorFullPrefixVerifyEveryNPolls = 60;

    /// <summary>
    /// Splits a total wait into consecutive chunks of at most <paramref name="maxSlice"/> so the
    /// caller can refresh the heartbeat between each — keeping a reconnecting-but-alive watcher
    /// from ever crossing the staleness threshold while it waits out a long connect-retry backoff.
    /// Pure so the "no chunk exceeds the slice" guarantee is unit-testable without spawning a
    /// watcher or a real server. A non-positive total yields no chunks.
    /// </summary>
    internal static IReadOnlyList<TimeSpan> HeartbeatSlices(TimeSpan total, TimeSpan maxSlice) {
        var slices    = new List<TimeSpan>();
        var remaining = total;

        while (remaining > TimeSpan.Zero) {
            var chunk = remaining < maxSlice ? remaining : maxSlice;
            slices.Add(chunk);
            remaining -= chunk;
        }

        return slices;
    }

    public async Task<int> RunWatch(
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string? cwd,
            bool    skipTitle = false,
            int?    parentPid = null,
            string  vendor    = "claude"
        ) {
        // BEFORE the log redirection below: SignalR's WithUrl builds a Uri synchronously, so a
        // relative value fails there — ahead of the connect/retry loop — and `watch` is not
        // fail-open, so the top-level catch turns that into an opaque exit 1. Validating here gives
        // a hand-run watcher the actionable hint on the real stderr and the same exit 2 every
        // interactive command gives; a few lines later Console.Error is a log file nobody is looking at.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(HttpClientExtensions.SchemeMissingHint);
            return 2;
        }

        // Redirect all output to a log file so we don't hold parent's pipe FDs open
        var logDir = config.Path("logs");
        Directory.CreateDirectory(logDir);
        var logKey    = agentId is not null ? $"{sessionId}-{agentId}" : sessionId;
        var logPath   = Path.Combine(logDir, $"{logKey}.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        // Task 9: `logKey` is already the same `{sessionId}` / `{sessionId}-{agentId}`
        // key WatcherManager uses for the pid file, so it doubles as the heartbeat key. Touch
        // once here (startup) and then every main-loop iteration below so a hook-side
        // staleness probe can distinguish a wedged (hung-but-alive) watcher from a healthy
        // one — a PID-only liveness check can't tell the difference.
        var heartbeatPath = _watchers.GetHeartbeatFilePath(logKey);

        void TouchHeartbeat() {
            try {
                WatcherHeartbeat.Touch(heartbeatPath, DateTimeOffset.UtcNow);
            } catch {
                /* best-effort — a missed touch just risks one false-stale reading, never worse */
            }
        }

        TouchHeartbeat();

        // Detach from controlling terminal so closing the parent's terminal does
        // not deliver SIGHUP to the watcher. Without this, both the coding agent
        // and the watcher die simultaneously when the user closes the terminal
        // window, and the parent-PID poll below never fires — leaving the
        // session stuck "active" because session-end is never POSTed.
        if (!ProcessHelpers.DetachFromControllingTerminal()) {
            Log("setsid() failed; SIGHUP from terminal close may kill the watcher");
        }

        using var cts = new CancellationTokenSource();

        // A cancellable delay that keeps the heartbeat fresh across long waits. The connect-retry
        // backoff grows to 30s — longer than the ~20s staleness threshold — so a single Task.Delay
        // would let the heartbeat go stale mid-wait and get a healthy-but-reconnecting watcher
        // falsely reaped. Chunk the wait into ≤5s slices (the same cadence as the main loop's
        // disconnected branch), touching before each slice. Returns false if cancelled.
        async Task<bool> DelayWithHeartbeatAsync(TimeSpan total) {
            foreach (var chunk in HeartbeatSlices(total, HeartbeatSlice)) {
                TouchHeartbeat();

                try {
                    await Task.Delay(chunk, cts.Token);
                } catch (OperationCanceledException) {
                    return false;
                }
            }

            return true;
        }

        // Tracks whether shutdown was triggered by the parent coding-agent process
        // dying without firing session-end. When true (1), the watcher takes over the
        // server's session-end POST (which the parent normally fires) so the read
        // model doesn't keep the session "active" forever.
        //
        // Written by the parent-PID monitor task and signal handlers, read on the
        // main thread — use Interlocked/Volatile so the C# memory model formally
        // guarantees the write is observed even though awaits already act as memory
        // barriers in practice.
        var parentExited = 0;
        // set by the staged parent-dead recovery loop when it ends a wedged watcher on the ceiling.
        var wedgedCeilingExit = 0;

        // Handle SIGTERM/SIGINT for graceful shutdown.
        //
        // PosixSignalRegistration.Create returns an IDisposable that owns the
        // underlying handler slot; if it's not rooted for the lifetime of the
        // method the finalizer silently unregisters the handler. `using var`
        // keeps both registrations alive until RunWatch returns and disposes
        // them deterministically.
        //
        // For SIGTERM and SIGHUP, ctx.Cancel = true is required: .NET runs the
        // signal's default action (terminate) after the handler unless the
        // context is cancelled, which would kill the watcher before the main
        // loop notices cts and the drain / session-end POST never run.
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            cts.Cancel();
        };
        using var sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => {
            cts.Cancel();
            ctx.Cancel = true;
        });

        // SIGHUP defense-in-depth: setsid above should prevent the kernel from
        // delivering SIGHUP to us, but if a shell forwards SIGHUP explicitly to
        // its process group children before our setsid lands, or if setsid
        // failed, this handler keeps the watcher alive long enough to run the
        // parent-exit cleanup path (drain + session-end POST).
        using var sighupReg = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => {
            Log("Received SIGHUP; treating as parent-exit");
            Interlocked.Exchange(ref parentExited, 1);
            cts.Cancel();
            ctx.Cancel = true;
        });

        // declared before the parent-watchdog block so the staged parent-dead recovery
        // task can read state.LastActivityAt as its no-progress clock.
        var state = new WatchState();
        state.LastActivityAt = DateTimeOffset.UtcNow;

        // Task 11 (D0) — one runtime rewrite-guard instance for this watcher's whole
        // lifetime (its checkpoint/pending-range state is meant to persist poll-to-poll). Null
        // for every non-Cursor vendor — DrainNewLines only exercises guard/ack logic when both
        // vendor == "cursor" AND this is non-null.
        var cursorGuard = vendor == "cursor" ? new CursorRewriteGuard(config, sessionId) : null;

        // serializes a reconnect-discovered rewind
        // (ApplyReconnectRewindAsync, run from the Reconnected handler) against DrainNewLines'
        // own guard/state mutations (main loop + final drain). Both mutate WatchState's
        // CursorByteOffset/LinesProcessed and CursorRewriteGuard's checkpoint; without this a
        // rewind's three writes (byte offset, checkpoint reset, line cursor) could interleave with
        // a concurrently-running drain — the SignalR client can fire Reconnected on a background
        // task while the main loop is mid-DrainNewLines — re-creating the exact byte/line-frontier
        // divergence review fix #2 (r2) closed. Only ever contended for Cursor (the only vendor
        // with a guard/rewind interplay); null — and never awaited — for every other vendor.
        var cursorRewindGate = vendor == "cursor" ? new SemaphoreSlim(1, 1) : null;

        // Task 11 (D0) — a rewrite trip discards the unsent batch (DrainNewLines already
        // returned without advancing state) and quarantines the session (the guard itself writes
        // the marker); this just triggers the same clean-exit path StopWatcher uses.
        void OnCursorRewriteDetected() {
            Log("cursor_transcript_rewrite_detected: discarding unsent batch and exiting (session quarantined)");
            cts.Cancel();
        }

        // Task 8: the dedicated undelivered-transcript-tail spool, shared by the final-drain
        // needs-import marker below and the shutdown-during-outage tail spool.
        var transcriptSpool = new TranscriptSpool(config);

        // Watch the spawning coding-agent process. If it dies without firing
        // session-end (crash, force-kill, IDE-detach), self-terminate within ~5s and
        // POST session-end instead of orphaning. Crucially, the cases where we DON'T
        // monitor are now logged: a silently-skipped watchdog is exactly the failure
        // that left sessions stuck "active" with the watcher still connected — the
        // resolved parent PID was already dead at startup and nothing recorded it.
        // Local so both the initial Monitor case and the recovery re-arm start the identical poll.
        void ArmParentMonitor(int ppid) {
            Log($"Monitoring parent pid {ppid}");
            _ = Task.Run(async () => {
                while (!cts.Token.IsCancellationRequested) {
                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }

                    if (!ProcessHelpers.IsProcessAlive(ppid)) {
                        Log($"Parent pid {ppid} exited; shutting down watcher");
                        Interlocked.Exchange(ref parentExited, 1);
                        cts.Cancel();

                        return;
                    }
                }
            }, cts.Token);
        }

        switch (DecideParentWatchdog(parentPid, ProcessHelpers.IsProcessAlive)) {
            case ParentWatchdog.NoParentPid:
                Log("No parent pid supplied; parent-exit watchdog disabled (session-end relies on the agent's own hook)");

                break;

            case ParentWatchdog.ParentAlreadyDead:
                Log($"Parent pid {parentPid} already dead at watcher startup; entering staged recovery — "
                  + "will periodically re-resolve + re-arm the watchdog, and only end after a long ceiling "
                  + "with no transcript progress and continued resolution failure.");

                // Staged recovery: re-resolve the durable agent (using the vendor alias) and re-arm if
                // found; otherwise end ONLY after `ceiling` of no transcript progress. Any new progress
                // resets the window (state.LastActivityAt advances on drained lines). Session watchers
                // only — subagent watchers are torn down by the parent's lifecycle.
                if (agentId is null) {
                    var ceiling = ResolveParentDeadCeiling(Environment.GetEnvironmentVariable("KCAP_PARENT_DEAD_CEILING_MINUTES"));

                    _ = Task.Run(async () => {
                        while (!cts.Token.IsCancellationRequested) {
                            try {
                                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                            } catch (OperationCanceledException) {
                                return;
                            }

                            var reResolved        = ProcessHelpers.GetCodingAgentPid(vendor);
                            var noProgressElapsed = DateTimeOffset.UtcNow - state.LastActivityAt;

                            switch (DecideParentDeadRecovery(reResolved, ProcessHelpers.IsProcessAlive, noProgressElapsed, ceiling)) {
                                case ParentDeadRecovery.ReArm:
                                    Log($"Parent re-resolved to live pid {reResolved}; re-arming the watchdog");
                                    ArmParentMonitor(reResolved!.Value);

                                    return;

                                case ParentDeadRecovery.EndTerminal:
                                    Log($"Parent unresolved and no transcript progress for >{ceiling.TotalMinutes:F0}m; "
                                      + "ending session (parent_dead_ceiling)");
                                    Interlocked.Exchange(ref wedgedCeilingExit, 1);
                                    cts.Cancel();

                                    return;

                                case ParentDeadRecovery.KeepWaiting:
                                default:
                                    break; // poll again
                            }
                        }
                    }, cts.Token);
                }

                break;

            case ParentWatchdog.Monitor:
                ArmParentMonitor(parentPid!.Value);

                break;
        }

        // Antigravity posts /hooks/session-start BEFORE the watcher spawns, so the session is
        // already committed server-side — the below-threshold buffering (which exists to avoid
        // junk sessions the server hasn't seen) doesn't apply. Treat it as past-threshold from
        // the start so short conversations stream live and still idle-end + post session-end
        // (otherwise a <10-line conversation would never reach threshold, never idle-end, and
        // leave the session Active with the watcher lingering — the IDE process outlives it).
        //
        // Cursor's own sessionStart hook ALSO posts (and spawns this very
        // watcher) before any transcript line is ever read, so the same reasoning applies: the
        // generic 10-line buffer exists only to avoid polluting the server with a session it has
        // never heard of, which isn't true for Cursor either. Without this, a top-level Cursor
        // watcher re-added its still-unread lines to BufferedLines every poll (the line cursor
        // never advances while buffering) until they eventually flushed as duplicates, and — worse
        // — a watcher that force-quit before crossing the artificial threshold skipped its final
        // drain AND shutdown spool (below) entirely, and was permanently ineligible for the Cursor
        // idle ceiling (ShouldEndOnIdle's `isSessionWatcher` branch requires ThresholdReached).
        // Scoped to session (agentId is null) AND child watchers alike — child watchers never
        // consult ThresholdReached in the buffering switch below (it only matches `agentId: null`
        // cases) or in the final-drain/end-synthesis gates (both explicitly agentId-is-null-gated
        // too), so setting it unconditionally here is a no-op for them either way.
        if (SkipsThresholdBuffering(vendor)) state.ThresholdReached = true;
        var idleTimeout = ResolveIdleWindow(vendor, isSessionWatcher: agentId is null, Environment.GetEnvironmentVariable);
        var idleExit = false;

        // Idle grace between a Codex collab child's task_complete and its live
        // subagent-stop POST (codex child watchers only — see the loop's stop check). The
        // agent type is read lazily from the child rollout's own header, once.
        var     codexSubagentStopGrace   = CodexSubagentTurnTracker.ResolveStopGrace(Environment.GetEnvironmentVariable("KCAP_CODEX_SUBAGENT_IDLE_MINUTES"));
        string? codexChildAgentType      = null;
        var     codexStopNextAttemptAt   = DateTimeOffset.MinValue;

        if (skipTitle) {
            state.TitleGenerated = true;
        }

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(config, cwd);
            state.LastRepoDetection = DateTimeOffset.UtcNow;
        }

        // No cwd-derived repo (launched outside any checkout, or no cwd at all): fall back to
        // scanning the transcript's own tool-use paths for a git root. Session-watcher only
        // (agentId is null), matching the scope of the cwd-based detection above — a subagent
        // watcher is always spawned with cwd: null too and has never had its own repo detection.
        if (vendor == "claude" && agentId is null && (cwd is null || GitRepository.FindRoot(cwd) is null)) {
            state.EvidenceScanner = new RepoEvidenceScanner<RepositoryPayload>(
                GitRepository.FindRoot, root => RepositoryDetection.DetectRepositoryAsync(config, root),
                p => p.Owner is not null && p.RepoName is not null);

            try {
                foreach (var line in File.ReadLinesShared(transcriptPath)) {
                    if (await state.EvidenceScanner.OnLineAsync(vendor, line) is { } repo) {
                        ApplyEvidenceRepo(state, repo);

                        break;
                    }
                }
            } catch {
                // fail-open: this one-shot prefix scan is best-effort and must never block startup
            }
        }

        Log($"Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

        var hubConnection = BuildHubConnection($"{Url}/hubs/sessions");

        // runs DrainNewLines under cursorRewindGate when one
        // exists, so it can never observe a half-applied reconnect rewind (see cursorRewindGate's
        // declaration above). Thin wrapper over the directly-testable GatedDrainNewLinesAsync (see
        // its doc — RunWatch itself can't be driven without a live SignalR reconnect).
        Task<IReadOnlyList<string>> DrainNewLinesGatedAsync(bool isFinalDrainLocal, CancellationToken drainCt) =>
            GatedDrainNewLinesAsync(
                cursorRewindGate, hubConnection, sessionId, transcriptPath, agentId, state, vendor, drainCt,
                isFinalDrain: isFinalDrainLocal, cursorGuard: cursorGuard, onCursorRewriteDetected: OnCursorRewriteDetected);

        // Register StopWatcher handler — server sends this to tell us to shut down
        hubConnection.On<string>(
            "StopWatcher",
            reason => {
                Log($"Received StopWatcher signal: {reason}");
                cts.Cancel();
            }
        );

        hubConnection.Reconnecting += ex => {
            Log($"SignalR reconnecting: {ex?.Message}");

            return Task.CompletedTask;
        };

        hubConnection.Reconnected += async connectionId => {
            Log($"SignalR reconnected: {connectionId}");

            // Re-register with server and check if it's behind us (gap recovery)
            try {
                var serverPosition = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cancellationToken: cts.Token);

                if (serverPosition < state.LinesProcessed) {
                    Log($"Server behind ({serverPosition} vs {state.LinesProcessed}), rewinding to resend gap");

                    // hold cursorRewindGate for the whole
                    // rewind so a concurrently-running DrainNewLines (main loop or final drain)
                    // can never interleave with it (see cursorRewindGate's declaration above).
                    // Thin wrapper over the directly-testable GatedApplyReconnectRewindAsync.
                    var rewound = await GatedApplyReconnectRewindAsync(
                        cursorRewindGate, state, serverPosition, sessionId, vendor, transcriptPath, cursorGuard, cts.Token);

                    if (!rewound) {
                        // the server's frontier couldn't be
                        // resolved to an exact local byte offset (the local transcript has fewer
                        // lines than the server acknowledged — truncated/replaced while this watcher
                        // was disconnected). SeedCursorByteOffsetAsync already quarantined the
                        // session rather than seed a bogus baseline; exit the same way a runtime
                        // rewrite detection does — neither the byte nor the line frontier moved.
                        Log("cursor_transcript_rewrite_detected: reconnect frontier beyond local transcript — quarantined, exiting");
                        cts.Cancel();
                    }
                }
            } catch (Exception ex) {
                Log($"Re-register after reconnect failed: {ex.Message}");
            }
        };

        hubConnection.Closed += ex => {
            Log($"SignalR connection closed: {ex?.Message}");
            cts.Cancel();

            return Task.CompletedTask;
        };

        // Connect with retry — server may not be up yet.
        // Retry indefinitely with exponential backoff (cap 30s).
        // The watcher is lightweight when idle and the session will end via session-end hook.
        var connectRetryDelay = TimeSpan.FromSeconds(1);

        while (!cts.Token.IsCancellationRequested) {
            // Task 9: touch every connect-retry iteration too. A server outage at
            // startup (backoff up to 30s) that lasts longer than grace+threshold (~50s) is a
            // healthy-but-reconnecting watcher, NOT a wedged one — without a heartbeat here
            // the hook probe would judge it stale and reap+respawn it repeatedly for the
            // whole outage. Staleness must mean "the loop is wedged", not "the server is down".
            TouchHeartbeat();

            try {
                await hubConnection.StartAsync(cts.Token);

                break;
            } catch (OperationCanceledException) {
                // SIGTERM/SIGINT during connect — exit gracefully
                break;
            } catch (Exception ex) {
                Log($"SignalR connect failed, retrying in {connectRetryDelay.TotalSeconds}s: {ex.Message}");

                // Heartbeat-aware wait: the backoff caps at 30s > the staleness threshold, so a
                // plain Task.Delay here would falsely mark a reconnecting watcher stale.
                if (!await DelayWithHeartbeatAsync(connectRetryDelay)) {
                    break;
                }

                connectRetryDelay = TimeSpan.FromSeconds(Math.Min(connectRetryDelay.TotalSeconds * 2, 30));
            }
        }

        if (cts.Token.IsCancellationRequested) {
            await hubConnection.DisposeAsync();
            await logWriter.DisposeAsync();

            return 0;
        }

        // Register with server and get resume position
        state.LinesProcessed = await hubConnection.InvokeAsync<int>("WatcherConnect", sessionId, agentId, cts.Token);
        Log($"Connected via SignalR, resuming from line {state.LinesProcessed}");
        TouchHeartbeat();

        // seed the Cursor byte frontier to the TRUE byte
        // offset of the resumed line on this INITIAL registration too, not only on a later
        // reconnect rewind (ApplyReconnectRewindAsync already does this for reconnects, via the
        // SAME SeedCursorByteOffsetAsync helper — see below). Without this, a watcher resuming at
        // server line N left CursorByteOffset at its default (0): the ack-to-byte mapping
        // (ByteOffsetForAckedLines) then measures acked lines relative to N but counts their bytes
        // from 0, so a full ack of M resumed lines checkpointed at file offset M instead of N's
        // true offset plus M — a permanent, silent line/byte-frontier misalignment that made the
        // guard re-scan/re-hash old history forever.
        //
        // this resumed line number N is the server's own
        // acknowledged frontier, which can legitimately exceed the local transcript's line count
        // when it was truncated/replaced while this watcher was offline (e.g. across a restart).
        // SeedCursorByteOffsetAsync quarantines the session and returns false in that case rather
        // than seed a bogus (clamped-to-EOF) baseline; exit the same way a runtime rewrite
        // detection does — the loop below never runs against a state that never resolved.
        if (!await SeedCursorByteOffsetAsync(state, state.LinesProcessed, sessionId, vendor, transcriptPath, cursorGuard, cts.Token)) {
            Log("cursor_transcript_rewrite_detected: initial resume frontier beyond local transcript — quarantined, exiting");
            cts.Cancel();
        }

        // Resumed past a tool that opened before the cursor: rebuild the in-flight set so idle-end
        // doesn't mistake a running agent for an idle one. Only reachable on a resume — at line 0
        // the drain delivers the whole file and folds it itself.
        if (state.LinesProcessed > 0) {
            if (TracksClaudeToolCalls(vendor, isSessionWatcher: agentId is null)) {
                await BackfillClaudePendingToolCallsAsync(
                    state.PendingClaudeToolCalls, transcriptPath, state.LinesProcessed, cts.Token);
            }

            if (TracksCodexToolCalls(vendor)) {
                await BackfillCodexWatcherStateAsync(
                    state, transcriptPath, isChildWatcher: agentId is not null, state.LinesProcessed, cts.Token);
            }
        }

        // Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
        // transcripts itself and spawns a child watcher per subagent. Tracks the
        // files already registered + spawned so each is handled exactly once across ticks.
        var seenSubagents = new HashSet<string>(StringComparer.Ordinal);

        // Codex-only sibling cache: rollouts in the shared sessions tree whose header proved
        // they are NOT this session's subagents (top-level sessions, other parents' children).
        // Keeps the per-tick scan to one header read per NEW file; a mid-write header is
        // deliberately never cached so it is retried (see CodexSubagentDiscovery).
        var codexRuledOutRollouts = new HashSet<string>(StringComparer.Ordinal);

        // Codex-only: last mtime ChildRolloutAdvanced fired for, per child rollout — backs the
        // re-ensure gate that respawns a reaped child watcher when its subagent re-engages.
        var codexRolloutMtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        // Every child watcher key this session watcher spawned (#550) — the parent is the only
        // process that knows they exist, so its teardown must stop them.
        var spawnedChildWatcherKeys = new HashSet<string>(StringComparer.Ordinal);

        try {
            while (!cts.Token.IsCancellationRequested) {
                // Task 9: touch every iteration — including no-content drains and
                // while disconnected/reconnecting below — so staleness unambiguously means
                // the loop itself is wedged, not merely idle or mid-reconnect.
                TouchHeartbeat();

                // Skip work while disconnected — SignalR auto-reconnect handles recovery.
                // No point re-reading the file or attempting sends that will fail.
                if (hubConnection.State != HubConnectionState.Connected) {
                    // Freeze the idle clock: record when we went offline so the reconnect path can
                    // subtract the outage from the idle measure.
                    state.DisconnectedSince ??= DateTimeOffset.UtcNow;

                    try {
                        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    } catch (OperationCanceledException) {
                        break;
                    }

                    continue;
                }

                // Reconnected (or never disconnected): fold any accrued outage into the disconnected
                // accumulator so the idle measure ignores it. DisconnectedSince is only ever set while
                // disconnected, so this runs exactly once per outage.
                if (state.DisconnectedSince is { } since) {
                    state.AccumulatedDisconnected += DateTimeOffset.UtcNow - since;
                    state.DisconnectedSince        = null;
                }

                // Periodically refresh repository info (every 60s)
                if (cwd is not null && DateTimeOffset.UtcNow - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
                    var detected = await RepositoryDetection.DetectRepositoryAsync(config, cwd);

                    // An evidence-derived repo may only be replaced by another real detection,
                    // never cleared back to null by a launch-cwd probe that still finds nothing.
                    if (ShouldReplaceRepository(detected, state.RepositoryFromEvidence)) {
                        state.Repository = detected;
                    }

                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                }

                // gated so this can never interleave with a
                // concurrently-running reconnect rewind (see cursorRewindGate's declaration above).
                var drained = await DrainNewLinesGatedAsync(isFinalDrainLocal: false, cts.Token);

                // Live subagent discovery: only the parent (agentId == null) watcher scans;
                // child subagent watchers (agentId != null) just stream their file. Gemini
                // scans its native nested chat files; OpenCode scans the nested dir the
                // kcap plugin writes child {info,parts} into; Codex scans the shared
                // sessions tree for collab subagent rollouts whose session_meta names this
                // session as parent_thread_id (Codex 0.146+ multi-agent v2); Antigravity
                // links subagents from the parent transcript's INVOKE_SUBAGENT steps — the
                // spawn-time signal — drained this tick (nesting only, subagents are
                // captured standalone already).
                if (agentId is null && vendor == "gemini") {
                    await ScanGeminiSubagents(sessionId, transcriptPath, seenSubagents, spawnedChildWatcherKeys, cts.Token);
                } else if (agentId is null && vendor == "opencode") {
                    await ScanOpenCodeSubagents(sessionId, transcriptPath, seenSubagents, spawnedChildWatcherKeys, cts.Token);
                } else if (agentId is null && vendor == "codex") {
                    await ScanCodexSubagents(sessionId, transcriptPath, seenSubagents, codexRuledOutRollouts,
                        codexRolloutMtimes, spawnedChildWatcherKeys, cts.Token);
                } else if (agentId is null && vendor == "antigravity") {
                    await ScanAntigravitySubagentLinks(sessionId, drained, state.PostedSubagentLinks, cts.Token);
                }

                // A Codex collab CHILD posts its own subagent-stop once its rollout's
                // last turn completed (task_complete, no tool call in flight) and the grace
                // window elapsed — without this, the parent's session-end teardown is the only
                // stop, so a finished child's chat card spins for the parent's whole lifetime.
                // One-shot: the server dedupes lifecycle events per (session, agent), so the
                // tracker latches after the first successful POST; a failed POST retries on the
                // CodexSubagentStopRetryInterval cadence (never every 1s tick), and the
                // parent-end teardown remains the backstop either way. The child keeps
                // streaming after the stop — appends are not lifecycle-gated, so a re-engaged
                // child's content still lands in its subsession.
                if (agentId is not null && vendor == "codex"
                 && DateTimeOffset.UtcNow >= codexStopNextAttemptAt
                 && state.CodexSubagentTurn.ShouldPostStop(
                        state.PendingCodexToolCalls.Count, state.LastActivityAt,
                        DateTimeOffset.UtcNow, codexSubagentStopGrace)) {
                    codexChildAgentType ??= ResolveCodexChildAgentType(transcriptPath);

                    if (await PostCodexSubagentStopAsync(sessionId, agentId, codexChildAgentType, transcriptPath, cts.Token)) {
                        state.CodexSubagentTurn.StopPosted = true;
                        Log($"Codex subagent {agentId} ({codexChildAgentType}) turn complete + idle "
                          + $"{codexSubagentStopGrace.TotalMinutes:F0}m; posted subagent-stop");
                    } else {
                        codexStopNextAttemptAt = DateTimeOffset.UtcNow + CodexSubagentStopRetryInterval;
                    }
                }

                // the Cursor idle clock must be the LATER of transcript
                // activity AND the hook heartbeat mtime. Keyed on the CHILD's own session id for
                // a child watcher (agentId), matching how CursorHookCommand actually writes the
                // heartbeat (each hook touches its OWN raw session_id, never remapped to the
                // parent) — not the RunWatch `sessionId` parameter, which for a child watcher is
                // the PARENT id.
                var cursorIdleClockAt = ResolveCursorIdleClock(
                    vendor, state.LastActivityAt,
                    vendor == "cursor" ? WatcherHeartbeat.Read(_markers.HeartbeatPath(agentId ?? sessionId)) : null);

                if (ShouldEndOnIdle(
                        vendor,
                        isSessionWatcher: agentId is null,
                        state.ThresholdReached,
                        cursorIdleClockAt,
                        DateTimeOffset.UtcNow,
                        idleTimeout,
                        // A tool awaiting its result suppresses idle-end: Codex tracks call_ids,
                        // Antigravity counts PLANNER_RESPONSE calls vs result steps, Claude tracks
                        // tool_use ids awaiting their tool_result.
                        toolInFlight: vendor switch {
                            "antigravity" => state.PendingAntigravityToolCalls > 0,
                            "claude"      => state.PendingClaudeToolCalls.Count > 0,
                            _             => state.PendingCodexToolCalls.Count > 0,
                        },
                        disconnectedSinceActivity: state.AccumulatedDisconnected)) {
                    Log($"{vendor} transcript idle for >{idleTimeout.TotalMinutes:F0}m; ending session (idle_timeout)");
                    idleExit = true;
                    cts.Cancel();

                    break;
                }

                try {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } catch (OperationCanceledException) {
            // Expected
        }

        // Final drain before exit
        if (agentId is null && !state.ThresholdReached) {
            // Session watcher never reached threshold — short-lived session.
            // Skip sending buffered lines so the server can clean up the empty session.
            Log($"Session below threshold ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} lines), skipping final drain");
        } else {
            Log("Draining remaining lines...");

            // Shutdown completion signal: the outage/idle-timeout final drain used
            // to disable the half-written-line holdback unconditionally, which could consume a line
            // the agent was still mid-write on. Instead:
            //   1. Bounded-wait (≤2s) to give the writer a chance to finish the final record — a
            //      trailing newline OR a parseable last line, NOT length-stability alone, since a
            //      large write can pause mid-record past the window. This is best-effort ONLY.
            //   2. Run the final drain with ConsumeIfComplete: the completeness decision is re-made
            //      on the EXACT bytes consumed, so a line that resumed growing into an incomplete
            //      record after the wait is still held, never sent-and-advanced (no TOCTOU).
            // If the final line was held back at consume time, flag the session needs-import so
            // `kcap import` can recover the tail rather than dropping a truncated line.
            await WaitForFinalLineCompletionAsync(transcriptPath);

            // gated for the same reason as the main loop's
            // drain call above (a reconnect right at shutdown is unlikely but not impossible).
            var finalDrained = await DrainNewLinesGatedAsync(isFinalDrainLocal: true, CancellationToken.None);

            if (state.FinalDrainHeldIncompleteLine) {
                // Keyed on the canonical server sessionId (not agentId) even for a subagent
                // watcher — TranscriptSpool/LifecycleSpoolDrain's session-id space is the server's,
                // and `kcap import`/session-needs-import operate at the session level.
                Log("Final transcript line still incomplete at consume time; held back and flagging needs-import");
                transcriptSpool.MarkNeedsImport(sessionId, "shutdown final drain: last transcript line never completed (no newline, unparseable)");
            }

            // Task 8: shutdown-during-outage. DrainNewLines above only advances
            // state.LinesProcessed past lines the hub actually CONFIRMED — a failed final-drain send
            // (hub down, OR a HubException while the connection stays Connected — the generic catch
            // below does not change connection state) leaves LinesProcessed unchanged, so any lines
            // between there and EOF are undelivered and this is the LAST chance to act (the process
            // exits right after). Run UNCONDITIONALLY — never gate on connection state: it is a cheap
            // no-op when nothing is undelivered (position already == EOF). Spool the tail into the
            // dedicated TranscriptSpool so the global drain (task 3) replays it after recovery,
            // instead of silently dropping it.
            await SpoolUndeliveredTranscriptTailAsync(
                transcriptSpool, transcriptPath, sessionId, agentId, vendor, state.LinesProcessed, CancellationToken.None);

            // One last subagent-link scan on the way out — the parent may have emitted an
            // INVOKE_SUBAGENT step after the main loop's last tick but before exit, and this is
            // the watcher's final chance to link it.
            if (agentId is null && vendor == "antigravity") {
                await ScanAntigravitySubagentLinks(sessionId, finalDrained, state.PostedSubagentLinks, CancellationToken.None);
            }
        }

        // Signal drain complete to server.
        // cts.Token is already cancelled by the time we reach here (that's the path
        // that exits the main loop), so passing it would throw OperationCanceledException
        // before the call lands. Use a fresh short-lived token so the server actually
        // hears the drain-complete signal and can release StopAndDrainAsync waiters.
        try {
            if (hubConnection.State == HubConnectionState.Connected) {
                using var drainCompleteCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await hubConnection.InvokeAsync("WatcherDrainComplete", sessionId, agentId, cancellationToken: drainCompleteCts.Token);
                Log("Drain complete signaled to server");
            }
        } catch (Exception ex) {
            Log($"Failed to signal drain complete: {ex.Message}");
        }

        // #550: stop spawned child watchers on every parent exit path — SIGTERM-first, so each
        // runs its final drain before the session-end POST below. A SIGKILLed parent skips this;
        // that orphan case is what the codex-child reap ceiling backstops.
        if (spawnedChildWatcherKeys.Count > 0) {
            Log($"Stopping {spawnedChildWatcherKeys.Count} spawned child watcher(s)");
            await _watchers.KillWatchers(spawnedChildWatcherKeys);
        }

        Log($"Done. {state.LinesProcessed} total lines processed.");

        await hubConnection.DisposeAsync();

        // Parent coding-agent died without firing session-end. Take over the role:
        // POST /hooks/session-end so the server writes SessionEnded (otherwise the
        // session stays "active" forever in the read model). Mirrors what the daemon
        // already does for hosted agents via EndAgentSessionAsync. Skipped for:
        //   - agent watchers (agentId != null) — their lifecycle is handled by the
        //     parent session's own session-end hook plus the daemon path.
        //   - below-threshold sessions — no transcript lines were ever sent, so the
        //     server may not have a meaningful session to end.
        // Runs after SignalR dispose so the server's StopAndDrainAsync skips the
        // 10s drain wait (no live watcher connection to signal).
        var endReason = Volatile.Read(ref parentExited)      == 1 ? "parent_exited"
                      : Volatile.Read(ref wedgedCeilingExit) == 1 ? "parent_dead_ceiling"
                      : idleExit                                  ? "idle_timeout"
                      :                                             null;

        // Task 11 (D1): Cursor's idle-ceiling exit must NOT synthesize session-end here —
        // unlike Codex/Antigravity, end synthesis for Cursor has exactly one owner (the
        // sessionEnd hook, or the server-side lease-gated sweep as a backstop). The watcher only
        // exits and final-drains; posting here would race/duplicate whichever of those two
        // eventually fires. Cursor's other exit paths (StopWatcher, parent-exit) are unaffected —
        // this skip is scoped to idleExit specifically.
        var cursorSuppressesEndPost = CursorSuppressesEndPost(vendor, idleExit);

        // Belt-and-suspenders: normally a no-op by now — the final drain above (inside
        // DrainNewLines's isFinalDrain branch) already promoted and applied any read fallback as
        // part of the same batch that reaches the server on a clean exit. This only still matters
        // if that branch didn't run at all (e.g. the final drain found the transcript file gone).
        if (state.EvidenceScanner is { } scanner && await scanner.PromoteReadFallbackAsync() is { } fallbackRepo) {
            ApplyEvidenceRepo(state, fallbackRepo);
        }

        if (endReason is not null && agentId is null && state.ThresholdReached && !cursorSuppressesEndPost) {
            await PostSessionEndOnParentExitAsync(sessionId, transcriptPath, cwd, vendor, state.Repository, endReason);
        }

        // Graceful exit: retire this incarnation's pid file so no later teardown/cleanup can act
        // on a recycled pid (KillWatcher's token guard is the crash-exit backstop).
        _watchers.RemoveOwnPidFile(
            agentId is null ? sessionId : $"{sessionId}-{agentId}", Environment.ProcessId);

        await logWriter.DisposeAsync();

        return 0;
    }

    /// <summary>
    /// Gemini fires no subagent hooks, so the parent watcher discovers nested subagent
    /// transcripts itself (<c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>). On first
    /// sight of a file it registers the subagent (<c>subagent-start</c>, fail-closed) then
    /// spawns a detached child watcher that streams it with the subagent's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <paramref name="seen"/>;
    /// deterministic server-side lifecycle ids make re-registration safe.
    /// </summary>
    async Task ScanGeminiSubagents(
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = GeminiSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        IReadOnlyDictionary<string, string>? types = null;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var subId = Path.GetFileNameWithoutExtension(subFile);
            if (!Guid.TryParse(subId, out _)) continue; // not a <subId>.jsonl

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(subId);
            types       ??= GeminiSubagentDiscovery.ResolveAgentTypes(transcriptPath);
            var agentType = types.GetValueOrDefault(subId) ?? "subagent";

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostSubagentStartAsync(sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await _watchers.EnsureWatcherRunning(key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "gemini");
            spawnedChildKeys.Add($"{sessionId}-{agentId}");

            Log($"Gemini subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    async Task<bool> PostSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = GeminiSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// OpenCode fires no subagent hooks; its subagents live in its SQLite db, so the kcap
    /// plugin fetches each child session via the SDK and writes its <c>{info,parts}</c> JSONL
    /// into <c>&lt;cacheDir&gt;/&lt;parentSid&gt;/&lt;childSid&gt;.jsonl</c>. The parent watcher discovers
    /// those files (mirroring <see cref="ScanGeminiSubagents"/>): on first sight it registers
    /// the subagent (<c>subagent-start</c>, fail-closed) then spawns a detached child watcher
    /// that streams it with the canonical agentId (= childSid) → <c>AgentSubsession-*</c>, which
    /// lines up with the agentId the server surfaced from the parent's <c>task</c> tool call.
    /// Idempotent across ticks via <paramref name="seen"/>; deterministic server-side lifecycle
    /// ids make re-registration safe.
    /// </summary>
    async Task ScanOpenCodeSubagents(
            string              sessionId,
            string              transcriptPath,
            HashSet<string>     seen,
            ICollection<string> spawnedChildKeys,
            CancellationToken   ct
        ) {
        IReadOnlyList<string> subFiles;
        try {
            subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        if (subFiles.Count == 0) return;

        foreach (var subFile in subFiles) {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(subFile)) continue; // already registered + spawned

            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (!await PostOpenCodeSubagentStartAsync(sessionId, agentId, agentType, subFile, ct)) {
                seen.Remove(subFile);
                continue;
            }

            await _watchers.EnsureWatcherRunning(key: $"{sessionId}-{agentId}", transcriptPath: subFile,
                agentId: agentId, sessionIdOverride: sessionId, vendor: "opencode");
            spawnedChildKeys.Add($"{sessionId}-{agentId}");

            Log($"OpenCode subagent {agentId} ({agentType}) registered + child watcher spawned");
        }
    }

    async Task<bool> PostOpenCodeSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = OpenCodeSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Codex fires no subagent hooks; its collab subagents (Codex 0.146+, multi-agent v2) each
    /// fork into their OWN rollout in the shared <c>~/.codex/sessions</c> tree, linked back via
    /// the child <c>session_meta</c>'s <c>parent_thread_id</c> / <c>thread_source: "subagent"</c>.
    /// The parent watcher scans that tree (day dirs from the parent's date forward, one header
    /// read per new file — see <see cref="CodexSubagentDiscovery.EnumerateSubagentRollouts"/>):
    /// on first sight it registers the subagent (<c>subagent-start</c>, fail-closed) then spawns
    /// a detached child watcher streaming the child rollout under the child's canonical agentId
    /// (→ <c>AgentSubsession-*</c>). Idempotent across ticks via <c>seen</c>;
    /// deterministic server-side lifecycle ids make re-registration safe. Disk enumeration (not
    /// the parent's in-band <c>sub_agent_activity</c> events) so a restarted parent watcher
    /// still recovers already-spawned children.
    /// </summary>
    /// <summary>
    /// #550 companion to the codex-child reap ceiling: fires on first sight of a rollout and
    /// thereafter only when its mtime advances, recording the mtime it fired for — so a reaped
    /// child stays reaped while its rollout is quiet but is re-ensured the moment its subagent
    /// re-engages (the fresh watcher resumes from the server frontier). Pure for testability.
    /// </summary>
    internal static bool ChildRolloutAdvanced(Dictionary<string, DateTime> lastMtimes, string filePath, DateTime currentMtime) {
        if (lastMtimes.TryGetValue(filePath, out var prev) && prev == currentMtime) return false;

        lastMtimes[filePath] = currentMtime;

        return true;
    }

    async Task ScanCodexSubagents(
            string                       sessionId,
            string                       transcriptPath,
            HashSet<string>              seen,
            HashSet<string>              ruledOut,
            Dictionary<string, DateTime> rolloutMtimes,
            ICollection<string>          spawnedChildKeys,
            CancellationToken            ct
        ) {
        List<CodexSubagentDiscovery.SubagentRollout> subs;
        try {
            subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(transcriptPath, sessionId, ruledOut);
        } catch {
            return; // discovery is best-effort — never break the main drain loop
        }

        foreach (var sub in subs) {
            if (ct.IsCancellationRequested) return;

            // Both gates evaluate before branching so first sight also records the mtime. An
            // already-seen rollout that hasn't grown keeps its watcher state as-is — alive and
            // streaming, or reaped-idle — rather than respawning a reaped child for a finished rollout.
            var advanced = ChildRolloutAdvanced(rolloutMtimes, sub.FilePath, File.GetLastWriteTimeUtc(sub.FilePath));
            var isNew    = seen.Add(sub.FilePath);

            if (!isNew && !advanced) continue;

            var childAgentId = sub.ChildDashlessId;
            var agentType    = CodexSubagentDiscovery.AgentTypeFrom(sub.AgentPath, sub.AgentNickname);

            // Fail-closed: register the subagent (→ SubagentStarted) before its child watcher
            // streams content. On POST failure, drop from `seen` so the next tick retries.
            if (isNew && !await PostCodexSubagentStartAsync(sessionId, childAgentId, agentType, sub.FilePath, ct)) {
                seen.Remove(sub.FilePath);
                continue;
            }

            await _watchers.EnsureWatcherRunning(key: $"{sessionId}-{childAgentId}", transcriptPath: sub.FilePath,
                agentId: childAgentId, sessionIdOverride: sessionId, vendor: "codex");
            spawnedChildKeys.Add($"{sessionId}-{childAgentId}");

            if (isNew) Log($"Codex subagent {childAgentId} ({agentType}) registered + child watcher spawned");
        }
    }

    async Task<bool> PostCodexSubagentStartAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = CodexSubagentDiscovery.BuildStartPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-start", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Overall budget for one live child-stop POST attempt (auth client creation + the POST
    /// itself). Must stay well inside <see cref="WatcherHeartbeat.Threshold"/> (20s): the
    /// main loop touches the heartbeat once per iteration and awaits this call inline, so a
    /// slow attempt on the default 30s retry budget could mark the watcher stale and churn
    /// it exactly at turn completion. Retries ride later loop iterations instead, on the
    /// <see cref="CodexSubagentStopRetryInterval"/> cadence.
    /// </summary>
    static readonly TimeSpan CodexSubagentStopPostBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Minimum spacing between live child-stop POST attempts, so a persistent failure
    /// (outage, auth trouble) never hammers <c>/hooks/subagent-stop</c> at the loop's 1s
    /// cadence. Deliberately no terminal give-up: an auth refresh can heal a 401 and the
    /// parent-end teardown dedupes whatever this never manages to post.
    /// </summary>
    static readonly TimeSpan CodexSubagentStopRetryInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The live per-child stop, posted by the CHILD watcher itself once its turn
    /// completed and idled — the same wire shape <see cref="CodexSubagentTeardown"/> posts at
    /// parent end (which stays the backstop; the server dedupes the duplicate stop via its
    /// deterministic lifecycle event ids). Bounded by
    /// <see cref="CodexSubagentStopPostBudget"/>; failures are logged and reported to the
    /// caller, never thrown.
    /// </summary>
    async Task<bool> PostCodexSubagentStopAsync(
        string sessionId, string agentId, string agentType, string subFile, CancellationToken ct
    ) {
        try {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(CodexSubagentStopPostBudget);

            using var client  = await http.ForBackgroundAsync(budgetCts.Token);
            var       payload = CodexSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/subagent-stop", content, timeout: CodexSubagentStopPostBudget, ct: budgetCts.Token);

            if (!resp.IsSuccessStatusCode) {
                Log($"Codex subagent {agentId} stop POST returned {(int)resp.StatusCode}; "
                  + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");
            }

            return resp.IsSuccessStatusCode;
        } catch (Exception ex) {
            Log($"Codex subagent {agentId} stop POST failed: {ex.Message}; "
              + $"retrying in {CodexSubagentStopRetryInterval.TotalSeconds:F0}s");

            return false;
        }
    }

    /// <summary>
    /// Agent type for a child watcher's own stop payload, read from the child rollout's
    /// <c>session_meta</c> header — the child watcher isn't handed the type at spawn (only the
    /// parent's scan derives it), and <see cref="CodexSubagentDiscovery.ReadHeader"/> never
    /// throws, so a still-unreadable header just yields the generic fallback.
    /// </summary>
    static string ResolveCodexChildAgentType(string transcriptPath) {
        var (_, meta) = CodexSubagentDiscovery.ReadHeader(transcriptPath);

        return CodexSubagentDiscovery.AgentTypeFrom(meta.AgentPath, meta.AgentNickname);
    }

    /// <summary>
    /// Codex's counterpart to <see cref="BackfillClaudePendingToolCallsAsync"/>, same window and
    /// fail-soft properties. Both roles need the pending set — a <c>function_call</c> that opened
    /// before the cursor writes nothing until its output lands, so a session watcher would read
    /// <c>toolInFlight</c> false and idle-end a live session. Turn state is child-only: nothing
    /// else re-delivers the acknowledged <c>task_complete</c> that arms the live stop.
    /// </summary>
    internal static async Task BackfillCodexWatcherStateAsync(
            WatchState state, string transcriptPath, bool isChildWatcher, int upToLine, CancellationToken ct) {
        if (ct.IsCancellationRequested) return;

        var firstParsedLine = Math.Max(0, upToLine - ToolBackfillWindowLines);

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var       reader = new StreamReader(stream);

            for (var lineNumber = 0; lineNumber < upToLine; lineNumber++) {
                if (await reader.ReadLineAsync(ct) is not { } line) break;
                if (lineNumber < firstParsedLine) continue;

                UpdateCodexPendingToolCalls(state.PendingCodexToolCalls, line);

                if (isChildWatcher) state.CodexSubagentTurn.Observe(line);
            }
        } catch (OperationCanceledException) {
            // Shutdown racing startup — expected, and not worth a log line.
        } catch (Exception ex) {
            Log($"Codex watcher-state backfill skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Whitelist of vendor values accepted by the server's session-end route.
    /// Used to reject unexpected --vendor input before interpolating into the URL
    /// path (defence-in-depth against path traversal even though the CLI runs locally).
    /// </summary>
    static readonly HashSet<string> KnownVendors = new(StringComparer.Ordinal) { "claude", "codex", "copilot", "gemini", "kiro", "pi", "opencode", "antigravity", "cursor" };

    /// <summary>
    /// Total time budget for the parent-exit session-end POST. Covers /auth/config
    /// discovery + retrying POST. Short by design — this runs on the watcher's
    /// shutdown path; a stalled server must not block process termination, the whole
    /// point of the parent-PID watchdog is to self-terminate within ~5s.
    /// </summary>
    static readonly TimeSpan ParentExitPostBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Codex has no session-end hook and the desktop app's single long-lived
    /// `codex app-server` process is the watchdog's parent PID for EVERY
    /// conversation, so the parent-exit watchdog never fires per-conversation —
    /// sessions stay "active" until the whole app quits. This idle window is the
    /// fallback: if the rollout file gets no new lines for this long, the watcher
    /// POSTs session-end (reason "idle_timeout"). Self-correcting — Codex fires a
    /// Stop hook per turn that re-spawns the watcher and the read model
    /// reactivates the session, so a resumed conversation comes back to life.
    /// </summary>
    internal static readonly TimeSpan DefaultCodexIdleTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Resolves the Codex idle timeout from KCAP_CODEX_IDLE_MINUTES, falling back
    /// to <see cref="DefaultCodexIdleTimeout"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveCodexIdleTimeout(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCodexIdleTimeout;

    /// <summary>
    /// Task 11 (D1) — Cursor has no shell hooks that reliably fire a per-conversation
    /// parent-exit signal (the same class of gap Codex/Antigravity have), so an idle transcript
    /// is likewise the fallback session-end signal. Its own knob (default 60 min, mirroring
    /// <see cref="DefaultCodexIdleTimeout"/>) so tenants can tune it independently of the two
    /// GUI vendors.
    /// </summary>
    internal static readonly TimeSpan DefaultCursorIdleCeiling = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Resolves the Cursor idle ceiling from KCAP_CURSOR_IDLE_CEILING_MINUTES, falling back to
    /// <see cref="DefaultCursorIdleCeiling"/> for unset/blank/non-numeric/non-positive values.
    /// Pure so the parsing is unit-testable (mirrors <see cref="ResolveCodexIdleTimeout"/>).
    /// </summary>
    internal static TimeSpan ResolveCursorIdleCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCursorIdleCeiling;

    /// <summary>
    /// Reap ceiling for a Claude CHILD (subagent) watcher, whose only other exits are the
    /// SubagentStop-driven StopWatcher signal and the parent-exit watchdog — so a missed
    /// SubagentStop leaks it for the life of the parent session.
    ///
    /// Far longer than the 60-minute vendor ceilings because it is a leak backstop, not an
    /// end-of-conversation detector: a child watcher posts no session-end, so reaping late costs
    /// nothing while reaping a LIVE subagent drops the rest of its transcript.
    /// </summary>
    internal static readonly TimeSpan DefaultClaudeSubagentIdleCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the Claude subagent idle ceiling from KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES, falling
    /// back to <see cref="DefaultClaudeSubagentIdleCeiling"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveClaudeSubagentIdleCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultClaudeSubagentIdleCeiling;

    /// <summary>
    /// Reap ceiling for a Codex collab CHILD watcher (#550), the backstop for a child orphaned by
    /// a hard-killed parent (no parent-pid watchdog, and StopWatcher only reaches the session
    /// watcher). Same sizing rationale as <see cref="DefaultClaudeSubagentIdleCeiling"/>: a leak
    /// backstop, not an end-of-conversation detector — reaping late costs nothing, reaping a LIVE
    /// subagent drops the rest of its transcript.
    /// </summary>
    internal static readonly TimeSpan DefaultCodexSubagentReapCeiling = TimeSpan.FromHours(6);

    /// <summary>
    /// Resolves the Codex subagent reap ceiling from KCAP_CODEX_SUBAGENT_REAP_MINUTES — its own
    /// knob because KCAP_CODEX_SUBAGENT_IDLE_MINUTES already tunes the subagent-stop grace.
    /// Falls back to <see cref="DefaultCodexSubagentReapCeiling"/> for unset/blank/non-numeric/
    /// non-positive values. Pure so the parsing is unit-testable.
    /// </summary>
    internal static TimeSpan ResolveCodexSubagentReapCeiling(string? envValue) =>
        int.TryParse(envValue, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : DefaultCodexSubagentReapCeiling;

    /// <summary>
    /// The idle window <see cref="RunWatch"/> measures against, per vendor and watcher role — each
    /// vendor on its OWN knob. <paramref name="env"/> is injected to keep the mapping testable.
    /// The role argument exists for Claude, whose CHILD watchers get the subagent backstop rather
    /// than a conversation-idle window.
    /// </summary>
    internal static TimeSpan ResolveIdleWindow(string vendor, bool isSessionWatcher, Func<string, string?> env) =>
        vendor switch {
            "antigravity"                     => ResolveCodexIdleTimeout(env("KCAP_ANTIGRAVITY_IDLE_MINUTES")),
            "cursor"                          => ResolveCursorIdleCeiling(env("KCAP_CURSOR_IDLE_CEILING_MINUTES")),
            "claude" when !isSessionWatcher   => ResolveClaudeSubagentIdleCeiling(env("KCAP_CLAUDE_SUBAGENT_IDLE_MINUTES")),
            "codex" when !isSessionWatcher    => ResolveCodexSubagentReapCeiling(env("KCAP_CODEX_SUBAGENT_REAP_MINUTES")),
            _                                 => ResolveCodexIdleTimeout(env("KCAP_CODEX_IDLE_MINUTES")),
        };

    /// <summary>
    /// Whether the watcher should self-terminate because the vendor's transcript file has gone
    /// idle. Pure so the policy is unit-testable. Gated to the vendor/role combinations whose
    /// parent-exit watchdog can't fire per-conversation — see the switch below for which, and
    /// why each one qualifies. Session watchers additionally require threshold-reached
    /// (below-threshold short-lived sessions have no server session to end); CHILD (subagent)
    /// watchers are eligible WITHOUT the threshold gate — they never buffer, so ThresholdReached
    /// never flips true for them; see <see cref="RunWatch"/>'s call site for the
    /// heartbeat-aware idle clock. Uses strictly-greater-than so the boundary tick is not yet
    /// considered idle. Also suppressed when a tool call is in flight
    /// (<paramref name="toolInFlight"/> true): a long-running shell command / custom tool
    /// legitimately produces no new transcript lines between its start and completion records —
    /// ending while it's running would falsely terminate an active session. No hard ceiling on
    /// tool duration for the vendors with a live parent-exit watchdog; for Claude subagents the
    /// generous <see cref="DefaultClaudeSubagentIdleCeiling"/> is itself the second layer.
    ///
    /// For Cursor specifically the caller must NOT follow this up with a session-end POST —
    /// unlike Codex/Antigravity, end synthesis for Cursor has exactly one owner (the sessionEnd
    /// hook, or the server-side lease-gated sweep as a backstop); the watcher only exits and
    /// final-drains (see the <c>endReason</c> resolution in <see cref="RunWatch"/>).
    /// </summary>
    internal static bool ShouldEndOnIdle(
            string         vendor,
            bool           isSessionWatcher,
            bool           thresholdReached,
            DateTimeOffset lastActivityAt,
            DateTimeOffset now,
            TimeSpan       idleTimeout,
            bool           toolInFlight              = false,
            TimeSpan       disconnectedSinceActivity = default
        ) {
        // Which vendors may idle-exit, and in which role:
        //   codex             — both. Session: the idle window is the fallback session-end signal
        //                       (no per-conversation parent-exit hook). CHILD: the reap ceiling
        //                       is a leak backstop (#550) — no parent-pid watchdog, and the
        //                       server's StopWatcher only reaches the session watcher, so a child
        //                       that outlives the parent's teardown would otherwise never exit.
        //                       Server-side finalization stays with the child's own live
        //                       subagent-stop POST or the parent-end teardown, never this exit.
        //   antigravity       — session only; it spawns no child watchers at all.
        //   cursor            — both; no shell hook fires a reliable parent-exit signal either.
        //   claude            — CHILD only: nothing else finalizes it (a missed SubagentStop
        //                       leaks it for the life of the parent). Its SESSION watcher has a
        //                       working watchdog AND a sessionEnd hook, so a ceiling there could
        //                       end a live session out from under a thinking user.
        var eligible = vendor switch {
            "codex" or "cursor" => true,
            "antigravity"       => isSessionWatcher,
            "claude"            => !isSessionWatcher,
            _                   => false,
        };

        if (!eligible) return false;

        // A CHILD (subagent) watcher never buffers and so never sets ThresholdReached
        // (WatchState.ThresholdReached only ever flips true on the agentId==null buffering-flush
        // branch in DrainNewLines) — requiring it here would make every child watcher permanently
        // ineligible. Session watchers keep the gate: a below-threshold session was never flushed
        // to the server, so there's nothing to end.
        if (isSessionWatcher && !thresholdReached) return false;

        return now - lastActivityAt - disconnectedSinceActivity > idleTimeout && !toolInFlight;
    }

    /// <summary>
    /// Task 13 — pure extraction of the end-synthesis suppression <see cref="RunWatch"/>
    /// applies to its own idle-ceiling exit. Cursor's end-of-session synthesis has exactly one
    /// owner (the <c>sessionEnd</c> hook, or the server-side lease-gated sweep as a backstop);
    /// unlike Codex/Antigravity, the watcher posting <c>session-end</c> itself on an idle-ceiling
    /// exit would race/duplicate whichever of those two eventually fires. Scoped to
    /// <paramref name="idleExit"/> specifically — Cursor's other exit paths (<c>StopWatcher</c>,
    /// parent-exit) still post normally through the same call site this guards.
    /// </summary>
    internal static bool CursorSuppressesEndPost(string vendor, bool idleExit) => vendor == "cursor" && idleExit;

    /// <summary>
    /// vendors whose sessionStart hook (or, for Antigravity, an
    /// equivalent pre-spawn POST) commits the session server-side BEFORE the tailing watcher
    /// itself ever reads a transcript line, so the generic below-threshold buffer (which exists
    /// solely to avoid polluting the server with a session it has never heard of) does not apply.
    /// Before this fix, only Antigravity got this treatment — a top-level Cursor watcher re-added
    /// its still-unread lines to <see cref="WatchState.BufferedLines"/> every poll (the line
    /// cursor never advances while buffering) until they eventually flushed as duplicates, and a
    /// watcher that exited before crossing the artificial threshold skipped its final drain AND
    /// shutdown spool entirely, and was permanently ineligible for the Cursor idle ceiling
    /// (<see cref="ShouldEndOnIdle"/>'s <c>isSessionWatcher</c> branch requires
    /// <see cref="WatchState.ThresholdReached"/>). Pure so it's unit-testable; see
    /// <see cref="RunWatch"/>'s call site.
    /// </summary>
    internal static bool SkipsThresholdBuffering(string vendor) => vendor is "antigravity" or "cursor";

    /// <summary>
    /// the idle clock <see cref="ShouldEndOnIdle"/> measures against for
    /// Cursor must be the LATER of transcript activity (<paramref name="lastActivityAt"/>) and the
    /// hook heartbeat mtime (<paramref name="hookHeartbeatAt"/>): every Cursor hook invocation —
    /// including telemetry-only ones — touches <c>CursorMarkers.HeartbeatPath</c> independent of
    /// whether the tailing watcher itself observes new transcript content, so a session Cursor is
    /// still actively firing hooks for (but which happens to produce no new transcript lines)
    /// must not idle-exit underneath the user. For every other vendor <paramref name="hookHeartbeatAt"/>
    /// is always null (the caller only reads the heartbeat file for vendor == "cursor"), so this
    /// degrades to plain <paramref name="lastActivityAt"/> — unchanged behavior. Pure so it's
    /// unit-testable without a real heartbeat file.
    /// </summary>
    internal static DateTimeOffset ResolveCursorIdleClock(
            string          vendor,
            DateTimeOffset  lastActivityAt,
            DateTimeOffset? hookHeartbeatAt
        ) =>
        vendor == "cursor" && hookHeartbeatAt is { } hb && hb > lastActivityAt
            ? hb
            : lastActivityAt;

    /// <summary>
    /// Updates <c>pending</c> based on a single Codex rollout line.
    /// A <c>function_call</c> or <c>custom_tool_call</c> response_item adds its
    /// <c>call_id</c> to the set; the matching <c>_output</c> variant removes it.
    /// All other lines and malformed JSON are silently ignored so this is safe to
    /// call unconditionally for every line of any vendor transcript.
    /// </summary>
    /// <summary>
    /// Which watchers feed <see cref="UpdateCodexPendingToolCalls"/>. It reads RAW drain lines,
    /// which are unbounded, so — unlike when it read the 64 KiB-bounded redacted list — running it
    /// for every vendor would make an unrelated watcher parse a multi-megabyte line it can never
    /// match. Not gated on watcher role: a collab child needs it for ShouldPostSubagentStop.
    /// </summary>
    internal static bool TracksCodexToolCalls(string vendor) => vendor == "codex";

    internal static void UpdateCodexPendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return;

            var payload = root.Obj("payload");

            if (payload is not { } p) return;

            var callId = p.Str("call_id");

            if (callId is null) return;

            switch (p.Str("type")) {
                case "function_call"
                  or "custom_tool_call":
                    pending.Add(callId);
                    break;

                case "function_call_output"
                  or "custom_tool_call_output":
                    pending.Remove(callId);
                    break;
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    /// <summary>
    /// Claude's counterpart to <see cref="UpdateCodexPendingToolCalls"/>, guarding the subagent
    /// idle ceiling: a <c>tool_use</c> block adds its <c>id</c>, the matching <c>tool_result</c>
    /// removes it via <c>tool_use_id</c>. While the set is non-empty the subagent has a tool
    /// running and transcript silence is expected, so the ceiling is suppressed.
    ///
    /// Keyed off the content blocks rather than the record's top-level <c>type</c>: the block
    /// types are what actually carry the ids, so this stays correct regardless of how the
    /// envelope is labelled.
    /// </summary>
    /// <summary>
    /// Which watchers feed <see cref="UpdateClaudePendingToolCalls"/>. Must stay in step with the
    /// watchers <see cref="ShouldEndOnIdle"/> gives a ceiling to: one that is reaped on idle but
    /// never tracks its tool calls has no in-flight suppression and would end a live subagent
    /// mid-build.
    /// </summary>
    internal static bool TracksClaudeToolCalls(string vendor, bool isSessionWatcher) =>
        vendor == "claude" && !isSessionWatcher;

    /// <summary>
    /// How far back from the resume cursor the tool-call backfills parse. An unfinished tool sits
    /// at the tail by construction — nothing is appended between the call and its result — so older
    /// lines are settled and parsing them only costs startup latency. Bounding also stops a stale
    /// unmatched call from an interrupted turn stranding an id and suppressing idle-end forever.
    /// Shared by the Claude and Codex backfills: same rationale, and nothing about the number is
    /// vendor-specific.
    /// </summary>
    internal const int ToolBackfillWindowLines = 512;

    /// <summary>
    /// Rebuilds the in-flight set from the transcript lines before a resume cursor. A watcher that
    /// reconnects mid-tool starts draining at the server's line position and never sees the
    /// <c>tool_use</c> that opened before it, so the set would come up empty and the ceiling would
    /// treat a live subagent as idle. Parses only the last <see cref="ToolBackfillWindowLines"/>
    /// lines before <paramref name="upToLine"/>; everything from the cursor on arrives through the
    /// normal drain. Opened <c>FileShare.ReadWrite</c>: the agent is still writing this file.
    /// Failure is a no-op, never fatal — losing the backstop beats failing a watcher's startup.
    /// </summary>
    internal static async Task BackfillClaudePendingToolCallsAsync(
            HashSet<string> pending, string transcriptPath, int upToLine, CancellationToken ct) {
        if (ct.IsCancellationRequested) return;

        var firstParsedLine = Math.Max(0, upToLine - ToolBackfillWindowLines);

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            for (var lineNumber = 0; lineNumber < upToLine; lineNumber++) {
                if (await reader.ReadLineAsync(ct) is not { } line) break;

                // Lines before the window are still read (there is no cheap seek to line N) but
                // never parsed — the JSON parse is what actually costs.
                if (lineNumber >= firstParsedLine) UpdateClaudePendingToolCalls(pending, line);
            }
        } catch (OperationCanceledException) {
            // Shutdown racing startup — expected, and not worth a log line.
        } catch (Exception ex) {
            Log($"Claude tool-call backfill skipped: {ex.Message}");
        }
    }

    internal static void UpdateClaudePendingToolCalls(HashSet<string> pending, string line) {
        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Obj("message")?.Arr("content") is not { } content) return;

            foreach (var block in content.EnumerateArray()) {
                switch (block.Str("type")) {
                    case "tool_use" when block.Str("id") is { } id:
                        pending.Add(id);
                        break;

                    case "tool_result" when block.Str("tool_use_id") is { } resultId:
                        pending.Remove(resultId);
                        break;
                }
            }
        } catch {
            // Ignore malformed / non-JSON lines — never break the drain loop
        }
    }

    internal async Task PostSessionEndOnParentExitAsync(
            string             sessionId,
            string             transcriptPath,
            string?            cwd,
            string             vendor,
            RepositoryPayload? repository,
            string             reason = "parent_exited"
        ) {
        if (!KnownVendors.Contains(vendor)) {
            Log($"Parent-exit session-end skipped: unknown vendor '{vendor}'");
            return;
        }

        // Gemini fires no subagent-stop hook and the child watchers spawned in ScanGeminiSubagents
        // carry no parent-pid watchdog, so when the parent process dies WITHOUT the session-end
        // hook this is the only place that finalizes live subagents. Mirror the hook path: kill
        // each child watcher, drain its tail, POST subagent-stop — capped so a slow drain can't
        // block the watchdog's self-termination, and run BEFORE the session-end POST so
        // SubagentCompleted lands ahead of SessionEnded. No-op when none were spawned.
        if (vendor == "gemini") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => new GeminiSubagentTeardown(config, profiles, http).DrainAsync(sessionId, transcriptPath),
                    GeminiSubagentTeardown.DrainCap);

                if (!finalized) {
                    Log("Parent-exit Gemini subagent teardown cap elapsed; "
                      + "unfinalized subagents recover via: kcap import --gemini");
                }
            } catch (Exception ex) {
                Log($"Parent-exit Gemini subagent teardown failed: {ex.Message}");
            }
        }

        // Codex likewise fires no subagent-stop hook and its child watchers (spawned in
        // ScanCodexSubagents) carry no parent-pid watchdog, so this session-end synthesis —
        // which for Codex covers idle_timeout AND parent-exit, there being no session-end
        // hook — is the only place that finalizes them. Same shape as the Gemini teardown;
        // runs BEFORE the session-end POST so SubagentCompleted lands ahead of SessionEnded.
        // No-op when none were spawned.
        if (vendor == "codex") {
            try {
                var finalized = await TimeBudget.RunCappedAsync(
                    () => new CodexSubagentTeardown(config, profiles, http).DrainAsync(sessionId, transcriptPath),
                    CodexSubagentTeardown.DrainCap);

                if (!finalized) {
                    Log("Parent-exit Codex subagent teardown cap elapsed; "
                      + "unfinalized subagents recover via: kcap import --codex");
                }
            } catch (Exception ex) {
                Log($"Parent-exit Codex subagent teardown failed: {ex.Message}");
            }
        }

        // OpenCode likewise fires no subagent-stop hook (the plugin-written child files are
        // discovered + streamed by ScanOpenCodeSubagents, with no parent-pid watchdog on the
        // child watchers), so the parent exit is the only place that finalizes them. Same
        // shape as the Gemini teardown; runs BEFORE the session-end POST so SubagentCompleted
        // lands ahead of SessionEnded. No-op when none were spawned.
        if (vendor == "opencode") {
            // DrainAsync is self-bounding (per-step caps + a shared cleanup deadline + a hard
            // overall ceiling), so it needs no outer time cap here — wrapping it in one risked
            // cutting later children's SubagentCompleted. It attempts subagent-stop for every child
            // WITHIN the overall budget; under a pathological/huge child count it stops at the
            // ceiling and returns how many were left unfinalized (logged below — OpenCode has no
            // historical import to recover a missed stop).
            try {
                var unfinalized = await new OpenCodeSubagentTeardown(config, profiles, http).DrainAsync(sessionId, transcriptPath);
                if (unfinalized > 0) {
                    Log($"Parent-exit OpenCode subagent teardown hit the {OpenCodeSubagentTeardown.OverallBudget.TotalSeconds:0}s ceiling; "
                      + $"{unfinalized} subagent(s) left without SubagentCompleted");
                }
            } catch (Exception ex) {
                Log($"Parent-exit OpenCode subagent teardown failed: {ex.Message}");
            }
        }

        using var budgetCts = new CancellationTokenSource(ParentExitPostBudget);

        try {
            var endHook = new JsonObject {
                ["session_id"]      = sessionId,
                ["transcript_path"] = transcriptPath,
                ["cwd"]             = cwd ?? "",
                ["hook_event_name"] = "session_end",
                ["reason"]          = reason,
                // Stamp the exit time so the server can scope the SessionEnded id
                // per run. Vendors with no session-end hook (Kiro) end ONLY via this
                // path, so without a timestamp a resumed session's second exit would
                // dedupe against the first and the session would stay Active.
                // Computed once here and reused across POST retries, so it stays
                // idempotent for a single exit.
                ["ended_at"]        = DateTimeOffset.UtcNow.ToString("O"),
            };

            if (repository is not null) {
                endHook["repository"] = JsonNode.Parse(
                    JsonSerializer.Serialize(repository, CapacitorJsonContext.Default.RepositoryPayload)
                );
            }

            using var httpClient = await http.ForBackgroundAsync(budgetCts.Token);
            using var content    = new StringContent(endHook.ToJsonString(), Encoding.UTF8, "application/json");

            var url = $"{Url}/hooks/session-end/{vendor}";
            using var response = await httpClient.PostWithRetryAsync(url, content, timeout: ParentExitPostBudget, ct: budgetCts.Token);

            if (!response.IsSuccessStatusCode) {
                Log($"Parent-exit session-end POST returned HTTP {(int)response.StatusCode}");
                return;
            }

            Log("Parent-exit session-end POST succeeded");

            try {
                var body = await response.Content.ReadAsStringAsync(budgetCts.Token);
                var node = JsonNode.Parse(body);

                if (node?["generate_whats_done"]?.GetValue<bool>() == true) {
                    _watchers.SpawnWhatsDoneGenerator(sessionId, vendor);
                }
            } catch (Exception ex) {
                Log($"Parent-exit session-end response parse failed: {ex.Message}");
            }
        } catch (OperationCanceledException) {
            Log($"Parent-exit session-end POST timed out after {ParentExitPostBudget.TotalSeconds:F0}s");
        } catch (Exception ex) {
            Log($"Parent-exit session-end POST failed: {ex.Message}");
        }
    }

    static readonly Regex CommandNameRegex = CommandNameRx();
    static          bool  parseErrorLogged;

    // review fix #2/#3 — internal (not private) so the Cursor rewrite-guard wiring
    // (shrink detection, the periodic full-prefix cadence, and the acked-byte-offset checkpoint)
    // is directly regression-testable: every path exercised by those tests trips the guard and
    // returns BEFORE ever touching `hubConnection`, so an unconnected/never-started HubConnection
    // instance is sufficient — no live SignalR server needed.
    internal async Task<IReadOnlyList<string>> DrainNewLines(
            HubConnection      hubConnection,
            string             sessionId,
            string             transcriptPath,
            string?            agentId,
            WatchState         state,
            string             vendor,
            CancellationToken  ct,
            bool               isFinalDrain            = false,
            CursorRewriteGuard? cursorGuard             = null,
            Action?            onCursorRewriteDetected = null
        ) {
        try {
            if (!File.Exists(transcriptPath)) {
                return [];
            }

            // Task 11 (D1) — a Cursor session already given up on (quarantined by the
            // runtime rewrite guard) must never have more transcript lines delivered by the
            // watcher either; and while an ordering-sensitive hook's side-effect barrier is
            // pending, HOLD delivery entirely this poll (retry next tick) rather than risk
            // normalizing a transcript line ahead of the attachment it depends on (the watcher's
            // half of the barrier Task 8 introduced and Task 10 wired into the backfill).
            if (vendor == "cursor") {
                if (_markers.IsQuarantined(sessionId)) {
                    return [];
                }

                if (_markers.BarrierPending(sessionId, DateTimeOffset.UtcNow, CursorMarkers.DefaultBarrierBound)) {
                    return [];
                }
            }

            // Task 11 (D0/D3) — the runtime two-zone rewrite guard (Task 7) verifies the
            // byte range this poll is about to send hasn't been rewritten underneath the watcher.
            // Record the new range's hash NOW (right after the line-based read snapshot below) and
            // re-verify the SAME range, freshly re-read from disk, immediately before send below —
            // a rewrite racing between the two reads is exactly what VerifyNewRange/VerifyPriorZone
            // are built to catch. A trip discards the unsent batch and quarantines the session (the
            // guard does that itself); the caller (RunWatch) exits via onCursorRewriteDetected.
            var cursorGuardOldOffset    = state.CursorByteOffset;
            var priorLineCursorForGuard = state.LinesProcessed;

            // decide BEFORE reading whether this poll needs
            // the (rare, amortized) periodic full-prefix re-hash, which genuinely needs the whole
            // file, or can use a BOUNDED read starting near the guard's own prior-tail zone. Moved
            // ahead of the read (previously decided only after) so ReadNewCompleteLinesAsync can be
            // told where to start — an idle poll with nothing new then allocates ~nothing instead
            // of a file-sized buffer every second, which the previous unconditional whole-file
            // `captureRawBytes` read did even for large, mostly-unchanged Cursor transcripts.
            var cursorFullPrefixPollNow  = false;
            var cursorBoundedReadFrom    = 0L;
            var cursorGuardPollCountPeek = 0;

            if (vendor == "cursor" && cursorGuard is not null) {
                // Periodic full-prefix re-hash: CursorRewriteGuard.VerifyFullPrefix existed (D0)
                // but was never called from anywhere until review fix #2 wired it in on the Nth
                // poll. Review fix #3 seeds it on the very FIRST poll too (not only lazily via the
                // guard's own first call, which without this never happens before poll N) — without
                // an early seed, a mid-region rewrite landing anywhere in polls 1..N-1 has no
                // baseline to be caught against until poll N seeds the ALREADY-rewritten file as if
                // it were the original, valid one. Every Nth poll after that still compares.
                //
                // PEEK the would-be next count for this
                // poll's cadence decision WITHOUT committing it to state yet. The old code
                // incremented state.CursorGuardPollCount right here, unconditionally — if the
                // guarded read below then hit a transient IOException (caught by this method's
                // outer catch), the counter was already advanced even though VerifyFullPrefix was
                // never reached for this poll, so the NEXT successful poll saw a cadence value one
                // past due and silently skipped the full-prefix baseline it was supposed to
                // perform — recreating the polls-1..N-1 blind window this cadence exists to close.
                // The counter is committed (see below, right after the read actually succeeds)
                // ONLY once the read this decision fed has actually completed.
                cursorGuardPollCountPeek = state.CursorGuardPollCount + 1;
                cursorFullPrefixPollNow = cursorGuardPollCountPeek == 1
                    || cursorGuardPollCountPeek % CursorFullPrefixVerifyEveryNPolls == 0;

                if (!cursorFullPrefixPollNow) {
                    cursorBoundedReadFrom = Math.Max(0, cursorGuardOldOffset - cursorGuard.TrailingBytes);
                }
            }

            // Read only newline-TERMINATED lines. A final line still being written by the agent
            // (no trailing '\n' yet) is held back — sending its truncated prefix and advancing the
            // position past it permanently drops the completed line (dropped Read results;
            // large tool_result lines are slow to flush and get caught mid-write). The next drain
            // re-reads it once complete.
            NewTranscriptLines drainRead;
            await using (var stream = new FileStream(
                    transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                // Shutdown final drain: consume an unterminated final line only if
                // the exact bytes read parse as a complete JSON record (re-validated at consume time,
                // so no TOCTOU with the bounded pre-wait); a still-growing/unparseable tail is held.
                // Every live drain always holds an unterminated final line.
                //
                // Cursor additionally captures the raw byte buffer this
                // read decodes from (captureRawBytes), so the rewrite guard below can hash the
                // EXACT bytes that produced `newLines` instead of a separately reopened read.
                // rawBytesReadFrom/newRangeByteOffset bound
                // that capture instead of always reading the whole file (see above).
                drainRead = await ReadNewCompleteLinesAsync(
                    stream, state.LinesProcessed,
                    isFinalDrain ? IncompleteFinalLinePolicy.ConsumeIfComplete : IncompleteFinalLinePolicy.Hold,
                    ct, captureRawBytes: vendor == "cursor",
                    rawBytesReadFrom: cursorBoundedReadFrom,
                    newRangeByteOffset: vendor == "cursor" ? cursorGuardOldOffset : null);
            }

            // commit the cadence counter now that the guarded
            // read above actually completed without throwing. A failure never reaches this line, so
            // the next poll re-peeks from the SAME starting count — if THIS poll was full-prefix-due
            // but failed before reading, the next successful poll is still due (nothing was skipped).
            if (vendor == "cursor" && cursorGuard is not null) {
                state.CursorGuardPollCount = cursorGuardPollCountPeek;
            }

            // Surface whether the final drain held back an incomplete (unterminated/unparseable)
            // final line so the shutdown path can flag the session needs-import instead of dropping
            // a truncated tail. Only meaningful for the final drain; live drains hold routinely.
            if (isFinalDrain) {
                state.FinalDrainHeldIncompleteLine = drainRead.HeldIncompleteFinalLine;
            }

            // cursorGuardNewLength is the SAME capped byte length
            // ReadNewCompleteLinesAsync sampled while building drainRead above
            // (drainRead.SnapshotByteLength), NOT a fresh FileInfo.Length re-sample. Re-sampling
            // here raced an append between that line-based read and this call: any bytes appended
            // in that window would be hashed/checkpointed below as part of "the batch just sent"
            // even though drainRead (and therefore newLines/newLineNumbers) never saw them — a
            // silent, permanent skip on the next poll, since the guard's next prior-zone start
            // would already be past them.
            var cursorGuardNewLength   = drainRead.SnapshotByteLength;
            var cursorGuardSnapshot    = drainRead.SnapshotBytes;
            var cursorGuardSnapshotAt  = drainRead.SnapshotStartOffset;
            byte[]? cursorGuardVerifiedRange = null;

            if (vendor == "cursor" && cursorGuard is not null && cursorGuardSnapshot is not null) {
                // every hash below is derived from cursorGuardSnapshot, the
                // raw byte buffer ReadNewCompleteLinesAsync captured during the SAME capped read
                // that decoded drainRead.Lines (above) — never from a separate file reopen. The
                // previous wiring reopened the file HERE to hash the prior zone and record the new
                // range, which left a TOCTOU window: a rewrite landing between the decode read and
                // this reopen produced a hash for a snapshot the batch never actually came from,
                // and the later pre-send VerifyNewRange re-read (still a genuine fresh reopen,
                // right before the RPC — unchanged below) would then agree with THIS hash (both
                // saw the same, but stale, later snapshot) and never trip.
                //
                // A shrink must trip the guard even though nothing "new" was read — VerifyNotShrunk
                // owns writing the quarantine marker itself (mirroring every other Verify* method).
                if (!cursorGuard.VerifyNotShrunk(cursorGuardNewLength, cursorGuardOldOffset)) {
                    onCursorRewriteDetected?.Invoke();
                    return [];
                }

                // Prior-zone check runs unconditionally (independent of growth): an in-place
                // rewrite of already-checkpointed bytes that doesn't change the file's length is
                // caught here even when cursorGuardNewLength == cursorGuardOldOffset. A single
                // check suffices now — there is no separate "before/after" read of this snapshot to
                // race against (it was captured once, atomically, above). cursorGuardSnapshotAt
                // (review fix r3 #3) is non-zero on a bounded (non-full-prefix) poll — HashPriorZone
                // clips its window to whatever the snapshot actually covers.
                if (!cursorGuard.VerifyPriorZone(cursorGuard.HashPriorZone(cursorGuardSnapshot, cursorGuardSnapshotAt))) {
                    onCursorRewriteDetected?.Invoke();
                    return [];
                }

                if (cursorGuardNewLength > cursorGuardOldOffset) {
                    var rangeLength = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                    var rangeBytes  = cursorGuardSnapshot.AsSpan((int)(cursorGuardOldOffset - cursorGuardSnapshotAt), rangeLength);
                    cursorGuard.RecordNewRangeRead(cursorGuardOldOffset, rangeLength, rangeBytes);
                }

                // Periodic full-prefix re-hash — cadence decided up front (cursorFullPrefixPollNow)
                // so the expensive whole-file read/hash only ever happens on its own cadence; a
                // bounded (non-cadence) poll's snapshot never starts at byte 0, so it could never
                // correctly feed VerifyFullPrefix anyway.
                if (cursorFullPrefixPollNow) {
                    var sample = new CursorAppendOnlyProbe.Sample(
                        cursorGuardSnapshot.Length, CursorAppendOnlyProbe.Sha256Hex(cursorGuardSnapshot));

                    if (!cursorGuard.VerifyFullPrefix(sample, cursorGuardSnapshot)) {
                        onCursorRewriteDetected?.Invoke();
                        return [];
                    }
                }
            }

            var newLineNumbers = drainRead.LineNumbers;
            var newLines       = drainRead.Lines.Select(SecretRedactor.RedactLine).ToList();
            var linesRead      = drainRead.NextPosition;

            // Track Antigravity in-flight tool calls + the latest step timestamp from this
            // drain's transcript lines (BEFORE appending USAGE lines, which aren't transcript
            // steps). The created_at anchors USAGE recency; the pending-call count suppresses a
            // premature idle session-end while a long command runs (no line between call/result).
            if (vendor == "antigravity") {
                foreach (var l in newLines) {
                    UpdateAntigravityPendingToolCalls(state, l);
                    if (TryGetAntigravityCreatedAt(l) is { } ca) state.LastAntigravityCreatedAt = ca;
                }
            }

            // Antigravity keeps per-generation tokens/model in the sibling conversation .db
            // (derived from the transcript path — the session id here is the canonical dashless
            // form, but the transcript path carries the real conversation id), not the JSONL;
            // poll for newly-appended gen_metadata rows and stream them as synthetic USAGE
            // lines (server → AntigravityUsageBackfilledEvent). Only outside the below-threshold
            // buffering phase (a session watcher that hasn't reached threshold buffers without
            // sending — injecting there would either dupe on re-read or be lost on a flush
            // failure); subagent watchers (agentId != null) never buffer. The gen watermark
            // advances only after a successful send (below), so a failed batch re-reads the rows.
            var antigravityGenMax = -1L;
            if (vendor == "antigravity" && (agentId is not null || state.ThresholdReached)) {
                antigravityGenMax = AppendAntigravityUsageLines(state, newLines, newLineNumbers, transcriptPath, state.LastAntigravityCreatedAt);
            }

            // Task 10: Kiro's per-turn credits/context% live in the sibling {id}.json, not
            // the .jsonl (see KiroUsage docs) — by the time Kiro flushes that sidecar, a live drain
            // has usually already sent the anchor's AssistantMessage line, so import-style inline
            // enrichment can't reach it. Backfill it instead as a synthetic KiroUsageBackfilled line
            // (server Task 12 folds these into a backfill event, keyed on the anchor). Same
            // buffering/agentId gating as the Antigravity block above; staged anchors are committed
            // into state.KiroUsageEmittedAnchors only after a successful send (below), so a failed
            // batch re-reads and re-stages the same anchors next drain.
            if (vendor == "kiro" && (agentId is not null || state.ThresholdReached)) {
                AppendKiroUsageBackfillLines(state, newLines, newLineNumbers, transcriptPath);
            }

            if (newLines.Count > 0) {
                state.LastActivityAt          = DateTimeOffset.UtcNow;
                // New activity restarts the idle measure, so discard disconnected time accrued
                // before it (draining only happens while connected, so DisconnectedSince is null).
                state.AccumulatedDisconnected = TimeSpan.Zero;
            }

            // Codex tool calls in flight, from RAW drainRead.Lines: an oversized
            // function_call_output redacts to a placeholder with no call_id, stranding it and
            // pinning toolInFlight true forever (#528). Never gated on title phase or threshold.
            if (TracksCodexToolCalls(vendor)) {
                foreach (var line in drainRead.Lines) {
                    UpdateCodexPendingToolCalls(state.PendingCodexToolCalls, line);
                }
            }

            // Claude subagent idle-ceiling guard. Reads drainRead.Lines, NOT the redacted newLines:
            // RedactLine swaps any line over 64 KiB for a placeholder with no tool ids, and an
            // oversized tool_result (a big file read, a build log) would then never clear its id —
            // pinning toolInFlight true forever and disabling the ceiling entirely. Only ids are
            // read here; no raw content leaves the process.
            if (TracksClaudeToolCalls(vendor, isSessionWatcher: agentId is null)) {
                foreach (var line in drainRead.Lines) {
                    UpdateClaudePendingToolCalls(state.PendingClaudeToolCalls, line);
                }
            }

            // A Codex collab CHILD watcher additionally folds its own rollout's turn state
            // (task_complete vs renewed activity) so the polling loop can post a live
            // subagent-stop once the child is done + idle. Raw lines here too, in the other
            // direction: a redacted response_item reads as no activity, so a re-engaged child
            // would be reported stopped while working. Matches BackfillCodexWatcherStateAsync.
            if (vendor == "codex" && agentId is not null) {
                foreach (var line in drainRead.Lines) {
                    state.CodexSubagentTurn.Observe(line);
                }
            }

            // Capture first user text (needed for title generation)
            if (state is { TitleGenerated: false, FirstUserText: null } && agentId is null) {
                // If we resumed from a later position, scan from the beginning of the file
                if (state is { LinesProcessed: > 0, FullFileScanDone: false }) {
                    Log("Scanning full file for first user text (resumed from later position)");

                    try {
                        await using var scanStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var       scanReader = new StreamReader(scanStream);

                        while (await scanReader.ReadLineAsync(ct) is { } scanLine) {
                            if (string.IsNullOrWhiteSpace(scanLine)) {
                                continue;
                            }

                            var userText = TryExtractUserText(scanLine, vendor);

                            if (userText is null) {
                                continue;
                            }

                            SetFirstUserText(state, userText);

                            if (state.FirstUserText is not null) {
                                break;
                            }
                        }

                        state.FullFileScanDone = true;
                    } catch (Exception ex) {
                        Log($"Full file scan for user text failed: {ex.Message}");
                    }
                }

                // Also check new lines (normal path)
                if (state.FirstUserText is null && newLines.Count > 0) {
                    foreach (var userText in newLines.Select(line => TryExtractUserText(line, vendor)).OfType<string>()) {
                        SetFirstUserText(state, userText);

                        if (state.FirstUserText is not null) {
                            break;
                        }
                    }
                }

                if (state.FirstUserText is not null) {
                    Log($"First user text captured ({state.FirstUserText.Length} chars)");
                }

                // Send truncated user text as the initial title immediately
                // (deferred until threshold is reached for session watchers)
                if (state is { InitialTitleSent: false, FirstUserText: not null, ThresholdReached: true } && agentId is null) {
                    state.InitialTitleSent = true;
                    _                      = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                }
            }

            // Count events and capture assistant context for LLM title generation
            if (state is { TitleGenerated: false } && agentId is null && newLines.Count > 0) {
                foreach (var line in newLines) {
                    if (IsEvent(line, vendor)) {
                        state.EventCount++;
                    }

                    if (state.FirstAssistantText is null) {
                        var assistantText = TryExtractAssistantText(line, vendor);

                        if (assistantText is not null) {
                            state.FirstAssistantText = assistantText.Length > 300 ? assistantText[..300] : assistantText;
                            Log($"First assistant text captured ({state.FirstAssistantText.Length} chars)");
                        }
                    }
                }
            }

            // Generate LLM title after enough events have accumulated
            // (deferred until threshold is reached for session watchers)
            if (state is { TitleGenerated: false, TitleInFlight: false, TitleAttempts: < 5, ThresholdReached: true }
             && agentId is null
             && state.FirstUserText is not null
             && state.EventCount >= 5) {
                Log($"Triggering LLM title generation (attempt {state.TitleAttempts + 1}/5, events: {state.EventCount})");
                state.TitleInFlight = true;
                state.TitleAttempts++;
                _ = GenerateTitleAsync(hubConnection, sessionId, state, vendor);
            }

            switch (agentId) {
                // ── Buffering for session watchers ──────────────────────────────
                // Hold lines until threshold is reached to avoid polluting the server
                // with short-lived sessions (e.g. <local-command-caveat> prompts).
                // Subagent watchers (agentId != null) skip buffering entirely.
                case null when !state.ThresholdReached && newLines.Count > 0: {
                    state.BufferedLines.AddRange(newLines);
                    state.BufferedLineNumbers.AddRange(newLineNumbers);
                    state.LinesReadAhead = linesRead;

                    if (state.BufferedLines.Count < WatchState.TranscriptThreshold) {
                        Log($"Buffering {newLines.Count} line(s) ({state.BufferedLines.Count}/{WatchState.TranscriptThreshold} threshold)");

                        return newLines;
                    }

                    // Threshold reached — flush the entire buffer
                    Log($"Threshold reached ({state.BufferedLines.Count} lines), flushing buffer");
                    state.ThresholdReached = true;
                    newLines               = [..state.BufferedLines];
                    newLineNumbers         = [..state.BufferedLineNumbers];
                    linesRead              = state.LinesReadAhead;
                    state.BufferedLines.Clear();
                    state.BufferedLineNumbers.Clear();

                    // Send the initial title now that we're flushing
                    if (state is { InitialTitleSent: false, FirstUserText: not null }) {
                        state.InitialTitleSent = true;
                        _                      = SendInitialTitleAsync(hubConnection, sessionId, TruncateForTitle(state.FirstUserText, 80));
                    }

                    break;
                }
                case null when !state.ThresholdReached:
                    // No new content lines while buffering — track file position
                    state.LinesReadAhead = linesRead;

                    return newLines;
            }

            // Stamp Kiro's live context-% onto AssistantMessage lines at flush so it's present on the
            // first send — it can't be backfilled (the server dedupes Kiro events by canonical id).
            // Best-effort; rationale in the design spec.
            if (vendor == "kiro" && newLines.Count > 0) {
                newLines = EnrichKiroContextUsage(newLines, transcriptPath);
            }

            // Evidence-based repo detection for a session launched outside any repo — extracted
            // to ApplyEvidenceScanAsync so the final-drain fallback delivery (the actual send
            // path, not just the promotion) is directly unit-testable with a fake scanner.
            await ApplyEvidenceScanAsync(state, vendor, newLines, isFinalDrain);

            // Only include repository info when it has changed since last send
            var repoToSend = RepoPayloadChanged(state.Repository, state.LastSentRepository)
                ? state.Repository
                : null;

            // keep the
            // BYTE frontier in lockstep with the LINE frontier on EVERY path that advances
            // state.LinesProcessed past blank/whitespace-only lines WITHOUT a send that returns an
            // ack. Before this, such a poll (newLines.Count == 0, but linesRead — the new
            // NextPosition — > priorLineCursorForGuard, the position this poll STARTED from) advanced
            // ONLY state.LinesProcessed, leaving state.CursorByteOffset and the guard's own checkpoint
            // at the OLD byte boundary. The NEXT poll's bounded decoder then re-reads those SAME
            // already-"processed" blank bytes (its read starts from CursorByteOffset) but seeds its
            // base line number from the ALREADY-ADVANCED LinesProcessed (ReadNewCompleteLinesAsync's
            // newRangeByteOffset branch derives lineIndex from `linesProcessed`) — so an idle poll
            // re-inflates LinesProcessed every time, and the next REAL line is sent under a wrong
            // line number with its ack mapped to the wrong byte offset. This mirrors the ack path's
            // own ByteOffsetForAckedLines + Checkpoint composition (same helper, same guard-checkpoint
            // discipline) — cursorGuardSnapshot is this poll's already-captured/verified read, so no
            // extra I/O. A local function so the two call sites (the "no content, no repo change"
            // early return AND the repo-only-send-failed catch branch) stay byte-for-byte identical.
            void AdvanceCursorBlankByteFrontierInLockstep() {
                if (vendor != "cursor" || cursorGuard is null || cursorGuardSnapshot is null
                 || linesRead <= priorLineCursorForGuard || cursorGuardNewLength <= cursorGuardOldOffset) {
                    return;
                }

                var blankLineCount     = linesRead - priorLineCursorForGuard;
                var rangeLength        = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                var rangeBytes         = cursorGuardSnapshot.AsSpan((int)(cursorGuardOldOffset - cursorGuardSnapshotAt), rangeLength);
                var advancedByteOffset = ByteOffsetForAckedLines(rangeBytes, cursorGuardOldOffset, blankLineCount);

                state.CursorByteOffset = advancedByteOffset;

                var trailingLength = (int)Math.Min(cursorGuard.TrailingBytes, advancedByteOffset);
                var trailingHash   = trailingLength <= 0
                    ? CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty)
                    : CursorAppendOnlyProbe.Sha256Hex(
                          cursorGuardSnapshot.AsSpan((int)(advancedByteOffset - trailingLength - cursorGuardSnapshotAt), trailingLength));

                cursorGuard.Checkpoint(advancedByteOffset, trailingHash);
            }

            if (newLines.Count == 0 && repoToSend is null) {
                // advance the byte frontier together with the
                // line frontier (see AdvanceCursorBlankByteFrontierInLockstep above).
                AdvanceCursorBlankByteFrontierInLockstep();

                // No content lines and no repo changes — safe to advance past blank/whitespace lines
                state.LinesProcessed = linesRead;

                return newLines;
            }

            try {
                // Task 11 (D0) — pre-send re-verify: re-read the SAME byte range fresh
                // from disk immediately before this call and compare to the hash recorded above.
                // A rewrite racing between the read (above) and this send is exactly what this
                // last check catches; a length shrink or hash mismatch discards the unsent batch
                // (never advances state) and hands off to the caller's exit path.
                if (vendor == "cursor" && cursorGuard is not null && cursorGuardNewLength > cursorGuardOldOffset) {
                    var rangeLength = (int)(cursorGuardNewLength - cursorGuardOldOffset);
                    var rangeBuffer = new byte[rangeLength];

                    await using (var verifyStream = new FileStream(
                            transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                        verifyStream.Position = cursorGuardOldOffset;
                        var rangeRead = await ReadFullyAsync(verifyStream, rangeBuffer, ct);

                        if (!cursorGuard.VerifyNewRange(rangeBuffer.AsSpan(0, rangeRead), cursorGuardOldOffset, rangeLength)) {
                            onCursorRewriteDetected?.Invoke();

                            return newLines;
                        }
                    }

                    // this is the freshest, just-re-verified copy of the
                    // exact bytes about to be sent; stash it so the checkpoint logic below can map
                    // the server's acked LINE count to a precise byte offset within it, instead of
                    // assuming the whole range was delivered.
                    cursorGuardVerifiedRange = rangeBuffer;
                }

                // SendTranscriptBatch takes a single TranscriptBatch record (arity 1) —
                // SignalR matches on argument COUNT and does NOT auto-supply C# optional
                // defaults (PR #576 / v0.4.0 incident), so a parameter object keeps the
                // contract stable: adding a field stays backward-compatible (this client
                // omits a null vendor; servers ignore unknown fields). This calls the
                // record-based `SendTranscriptBatch2` added in (the legacy
                // positional `SendTranscriptBatch` stays on the server for older CLIs),
                // so it requires a server deployed with that method — server-before-CLI.
                // Cursor uses the ACKED variant instead — its return carries the
                // server's source-acknowledgement frontier, which the watcher's local cursor
                // tracks (see below) rather than the raw count of lines just sent.
                var batch = new TranscriptBatch {
                    SessionId   = sessionId,
                    AgentId     = agentId,
                    Lines       = newLines.ToArray(),
                    LineNumbers = newLineNumbers.ToArray(),
                    Repository  = repoToSend,
                    Vendor      = vendor,
                };

                int? cursorAckNextLine = null;

                if (vendor == "cursor") {
                    // re-check both markers IMMEDIATELY at the delivery
                    // boundary. The early checks at the top of this method ran before the guard's
                    // file reads/re-verification above (which can take real, if small, wall-clock
                    // time), so a beforeSubmitPrompt barrier created — or a quarantine written by
                    // a concurrent process — in that window must still be caught here, never sent.
                    // Hold (never advance state) so the next poll re-evaluates from scratch.
                    if (_markers.IsQuarantined(sessionId)
                     || _markers.BarrierPending(sessionId, DateTimeOffset.UtcNow, CursorMarkers.DefaultBarrierBound)) {
                        return newLines;
                    }

                    var ack = await hubConnection.InvokeAsync<TranscriptBatchAck>("SendTranscriptBatchAcked", batch, ct);
                    cursorAckNextLine = ack.NextLineNumber;
                } else {
                    await hubConnection.InvokeAsync("SendTranscriptBatch2", batch, ct);
                }

                if (newLines.Count > 0) {
                    Log(cursorAckNextLine is { } nextLine
                        ? $"Sent {newLines.Count} line(s) via SignalR (acked next-line {nextLine})"
                        : $"Sent {newLines.Count} line(s) via SignalR");
                }

                if (repoToSend is not null) {
                    Log("Sent updated repository info via SignalR");
                }

                // Only advance position after successful send — if send fails,
                // the next drain cycle will re-read and resend the same lines.
                // KurrentDB event IDs are deterministic (from transcript UUIDs),
                // so re-sending is idempotent. Cursor's local cursor is the server's ACKED
                // frontier, not the raw line count sent — a retry-blocked or persist-blocked
                // line re-delivers next poll and an ignored (no-event) line still advances past.
                state.LinesProcessed = cursorAckNextLine ?? linesRead;

                if (cursorAckNextLine is not null) {
                    // checkpoint only the bytes the ack actually covers.
                    // A partially-disposed batch (D3's "halt-at-the-gap" policy) acks fewer lines
                    // than were sent; the previous code advanced CursorByteOffset to the full
                    // capped snapshot length regardless, silently checkpointing the unacked tail
                    // as if it had been delivered — those bytes would never be re-sent, yet the
                    // server never durably disposed of them. Map the acked LINE count (an absolute
                    // frontier, same numbering as priorLineCursorForGuard) to a byte offset within
                    // the freshly-verified range; fall back to the full snapshot length only when
                    // nothing grew this poll (cursorGuardVerifiedRange is null exactly then, and
                    // cursorGuardNewLength == cursorGuardOldOffset in that case anyway).
                    var ackedLineCount  = cursorAckNextLine.Value - priorLineCursorForGuard;
                    var ackedByteOffset = cursorGuardVerifiedRange is { } verifiedRange
                        ? ByteOffsetForAckedLines(verifiedRange, cursorGuardOldOffset, ackedLineCount)
                        : cursorGuardNewLength;

                    state.CursorByteOffset = ackedByteOffset;

                    if (cursorGuard is not null) {
                        // derive the checkpoint's trailing hash from
                        // cursorGuardSnapshot, the bytes captured during THIS poll's single capped
                        // read (already re-verified against fresh disk re-reads at both the guard
                        // check above and the pre-send VerifyNewRange step), instead of reopening
                        // the file again here. The previous wiring re-read mutable disk bytes AFTER
                        // the RPC had already returned — a rewrite landing while the ack was in
                        // flight would be blessed as the new checkpoint baseline instead of caught
                        // (this is the same TOCTOU class review fix #1 closes for the read side).
                        // cursorGuardSnapshot may now start at
                        // cursorGuardSnapshotAt (a bounded, non-zero offset) rather than always byte
                        // 0; the slice below is relative to the snapshot buffer, so subtract it.
                        var trailingLength = (int)Math.Min(cursorGuard.TrailingBytes, ackedByteOffset);
                        var trailingHash   = trailingLength <= 0 || cursorGuardSnapshot is null
                            ? CursorAppendOnlyProbe.Sha256Hex(ReadOnlySpan<byte>.Empty)
                            : CursorAppendOnlyProbe.Sha256Hex(
                                  cursorGuardSnapshot.AsSpan((int)(ackedByteOffset - trailingLength - cursorGuardSnapshotAt), trailingLength));

                        cursorGuard.Checkpoint(ackedByteOffset, trailingHash);
                    }
                }

                // Commit the Antigravity gen_metadata watermark ONLY now (after the batch
                // carrying its USAGE lines landed); a failed send above leaves it unchanged
                // so the rows re-read next drain instead of being skipped forever.
                if (antigravityGenMax > state.LastAntigravityGenIdx)
                    state.LastAntigravityGenIdx = antigravityGenMax;

                // Commit the Kiro usage-backfill anchors ONLY now (after the batch carrying their
                // synthetic lines landed); a failed send above leaves KiroUsageEmittedAnchors
                // unchanged so AppendKiroUsageBackfillLines re-stages the same anchors next drain
                // instead of losing them.
                foreach (var anchor in state.KiroUsagePendingAnchors)
                    state.KiroUsageEmittedAnchors.Add(anchor);

                if (repoToSend is not null) {
                    state.LastSentRepository = repoToSend;
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                if (newLines.Count > 0) {
                    Log($"SendTranscriptBatch failed, will retry from line {state.LinesProcessed}: {ex.Message}");
                } else {
                    // Repo-only batch failed — no transcript lines at risk, so advance
                    // position and defer retry to the next 60s repo detection cycle.
                    //
                    // this branch reaches the send path only because a
                    // repo change was pending (repoToSend != null); newLines.Count == 0 here means
                    // any lines this poll DID read were blank/whitespace-only. Advancing
                    // LinesProcessed alone would leave CursorByteOffset/checkpoint behind exactly as
                    // the "no content, no repo change" early return did before finding #1's fix — so
                    // advance the byte frontier in the SAME lockstep, via the shared helper, before
                    // moving the line cursor. (No ack came back — the send threw — so this is the
                    // blank-line path, not the acked path; the helper maps the consumed blank-line
                    // count to its true byte offset and re-checkpoints from the same snapshot.)
                    AdvanceCursorBlankByteFrontierInLockstep();

                    state.LinesProcessed    = linesRead;
                    state.LastRepoDetection = DateTimeOffset.UtcNow;
                    Log($"Repo info send failed, will retry in 60s: {ex.Message}");
                }
            }

            return newLines;
        } catch (IOException ex) {
            Log($"Error reading file: {ex.Message}");
        } catch (OperationCanceledException) {
            // Expected during shutdown
        }

        return [];
    }

    /// <summary>
    /// Pure builder for the JSON payload spooled into <see cref="TranscriptSpool"/>
    /// at shutdown when the hub is down and the final drain's still-undelivered tail cannot be sent
    /// live. Mirrors the <see cref="TranscriptBatch"/> construction in <see cref="DrainNewLines"/>
    /// (the live SignalR send) so the shape the global drain (task 3) later POSTs to
    /// <c>/hooks/transcript</c> on replay is identical to a normal live batch.
    /// </summary>
    internal static string BuildTranscriptSpoolBatch(
            string                sessionId,
            string?               agentId,
            string                vendor,
            IReadOnlyList<string> lines,
            IReadOnlyList<int>    lineNumbers
        ) => JsonSerializer.Serialize(
            new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = lines.ToArray(),
                LineNumbers = lineNumbers.ToArray(),
                Vendor      = vendor,
            },
            CapacitorJsonContext.Default.TranscriptBatch);

    /// <summary>
    /// Task 8: called from the shutdown path only when the hub is NOT connected at the point
    /// the final drain finishes. Re-reads the transcript from <paramref name="linesProcessed"/> (the
    /// last line the server actually confirmed, per <see cref="DrainNewLines"/>'s
    /// "only advance position after successful send" rule) to EOF, using the same
    /// <see cref="IncompleteFinalLinePolicy.ConsumeIfComplete"/> decision as the final drain itself so
    /// a still-growing/unparseable last line is never spooled prematurely. Any resulting lines are the
    /// tail the outage prevented from being delivered live; spools them via
    /// <see cref="TranscriptSpool.Append"/> so the global drain (task 3) replays them once the hub
    /// recovers, rather than dropping them when this process exits.
    /// Returns <c>null</c> when there is nothing undelivered (nothing spooled), otherwise the
    /// <see cref="TranscriptSpool.AppendResult"/> from the spool write.
    /// </summary>
    internal async Task<TranscriptSpool.AppendResult?> SpoolUndeliveredTranscriptTailAsync(
            TranscriptSpool   transcriptSpool,
            string            transcriptPath,
            string            sessionId,
            string?           agentId,
            string            vendor,
            int               linesProcessed,
            CancellationToken ct
        ) {
        if (!File.Exists(transcriptPath)) return null;

        // a Cursor session already quarantined by the runtime rewrite
        // guard must never have its tail re-read and spooled here either: the bytes past
        // `linesProcessed` ARE the exact corrupted batch the guard just discarded (the discard
        // never advanced state.LinesProcessed), so without this check a later global drain
        // (LifecycleSpoolDrain) would replay from the spool precisely what the guard existed to
        // block. No needs-import marker either — D0's quarantine is a deliberate, permanent,
        // diagnosable stop (see CursorRewriteGuard), and `kcap import` also refuses a quarantined
        // session (review fix #7), so a needs-import marker here would just be inert.
        if (vendor == "cursor" && _markers.IsQuarantined(sessionId)) {
            Log($"Cursor session {sessionId} is quarantined; skipping shutdown-tail spool "
              + "(no line-number path may keep feeding a corrupted cursor)");

            return null;
        }

        NewTranscriptLines tail;
        try {
            await using var stream = new FileStream(
                transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            tail = await ReadNewCompleteLinesAsync(
                stream, linesProcessed, IncompleteFinalLinePolicy.ConsumeIfComplete, ct);
        } catch (IOException ex) {
            Log($"Shutdown-tail spool: failed to read transcript for {sessionId}: {ex.Message}");

            return null;
        }

        if (tail.Lines.Count == 0) return null;

        // Redact secrets exactly as the live drain does (DrainNewLines: drainRead.Lines.Select(
        // SecretRedactor.RedactLine)) — otherwise the spooled tail lands on disk raw and is POSTed
        // unredacted on replay, leaking secrets the live path would have stripped.
        var redacted = tail.Lines.Select(SecretRedactor.RedactLine).ToList();

        var batch  = BuildTranscriptSpoolBatch(sessionId, agentId, vendor, redacted, tail.LineNumbers);
        var result = transcriptSpool.Append(sessionId, batch);

        switch (result) {
            case TranscriptSpool.AppendResult.Appended:
                Log($"Spooled {tail.Lines.Count} undelivered transcript line(s) at shutdown for "
                  + $"{sessionId} (hub down) — will replay on the next global drain");

                break;
            case TranscriptSpool.AppendResult.MarkedNeedsImport:
                Log($"Undelivered transcript tail for {sessionId} could not be spooled (cap exhausted "
                  + "or write failed); session flagged needs-import — recover via `kcap import`");

                break;
        }

        return result;
    }

    static void SetFirstUserText(WatchState state, string userText) {
        var cmdMatch = CommandNameRegex.Match(userText);

        if (cmdMatch.Success) {
            // Skip slash commands — wait for the next real user prompt to generate the title
            Log($"Skipping slash command /{cmdMatch.Groups[1].Value} for title generation");

            return;
        }

        state.FirstUserText = userText;
    }

    internal static string? TryExtractAssistantText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"    => TryExtractCodexAssistantText(line),
            "copilot"  => TryExtractCopilotAssistantText(line),
            "kiro"     => TryExtractKiroAssistantText(line),
            "pi"       => TryExtractPiAssistantText(line),
            "opencode" => TryExtractOpenCodeText(line, "assistant"),
            "antigravity" => TryExtractAntigravityText(line, "assistant"),
            _          => TryExtractClaudeAssistantText(line)
        };

    static string? TryExtractClaudeAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant") {
                return null;
            }

            if (root.Obj("message")?.Arr("content") is not { } content) {
                return null;
            }

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("type") == "text") {
                    var text = block.Str("text")?.Trim();

                    if (!string.IsNullOrEmpty(text)) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractCodexAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "assistant") return null;

            return TitleGenerator.ExtractCodexBlockText(payload.Value, "output_text")?.Trim() is { Length: > 0 } text ? text : null;
        } catch {
            return null;
        }
    }

    internal static bool IsEvent(string line, string vendor = "claude") {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (vendor == "copilot") {
                // user.message / assistant.message are the only conversational
                // envelopes; everything else (hook.*, system.*, tool.*,
                // session.*) is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("type") is "user.message" or "assistant.message";
            }

            if (vendor == "kiro") {
                // Prompt / AssistantMessage are the conversational lines;
                // ToolResults is plumbing and must not count toward the
                // title-generation event threshold.
                return root.Str("kind") is "Prompt" or "AssistantMessage";
            }

            if (vendor == "codex") {
                // Codex rolls everything into a top-level response_item envelope;
                // a "message" payload (user or assistant) is the analog of Claude's
                // user/assistant event for title-threshold purposes. User-role
                // payloads that are codex-injected preludes
                // (<environment_context>, AGENTS.md, <turn_aborted>) must NOT
                // count — otherwise a fresh session with no real prompts can
                // reach the 5-event threshold and produce a low-context title.
                if (root.Str("type") != "response_item") return false;

                var payload = root.Obj("payload");

                if (payload?.Str("type") != "message") return false;

                var role = payload.Value.Str("role");

                return role switch {
                    "assistant" => true,
                    "user"      => TitleGenerator.ExtractCodexBlockText(payload.Value, "input_text") is { } text
                                && !TitleGenerator.IsCodexInjectedUserPrelude(text),
                    _           => false
                };
            }

            if (vendor == "pi") {
                // Pi conversational envelopes are type:"message" with
                // message.role user/assistant (Pi has no top-level user/assistant
                // type). Gate on CONTENT, not just role: an empty user/assistant
                // envelope produces no canonical event (the server's NormalizeUser
                // returns null for empty content, NormalizeAssistant for zero
                // parts), so it must not count toward the title-event threshold
                // either. Mirrors PiImportSource.IsImportRelevantLine.
                if (root.Str("type") != "message") return false;
                if (root.Obj("message") is not { } piMsg) return false;

                return piMsg.Str("role") switch {
                    "user"      => PiContent.HasUserContent(piMsg),
                    "assistant" => PiContent.HasAssistantContent(piMsg),
                    _           => false
                };
            }

            if (vendor == "opencode") {
                // OpenCode envelopes are {info,parts} with info.role user/assistant.
                // Gate on text content: a turn with no non-hidden text (e.g. tool-only)
                // yields no titleable text, so it must not count toward the title-event
                // threshold. Mirrors the Pi content gate.
                if (root.Obj("info")?.Str("role") is not ("user" or "assistant")) return false;

                return OpenCodeTextParts(root.Arr("parts")) is not null;
            }

            if (vendor == "antigravity") {
                // Antigravity transcript_full.jsonl lines carry a `type`; only the two
                // conversational steps count toward the title-event threshold, and only
                // when they have text (a tool-only PLANNER_RESPONSE or an empty prompt
                // yields no titleable text). Everything else (RUN_COMMAND, VIEW_FILE,
                // GENERIC, CHECKPOINT, INVOKE_SUBAGENT, SYSTEM_*) is plumbing.
                return root.Str("type") switch {
                    "USER_INPUT"       => TryExtractAntigravityText(line, "user")      is not null,
                    "PLANNER_RESPONSE" => TryExtractAntigravityText(line, "assistant") is not null,
                    _                  => false
                };
            }

            return root.Str("type") is "user" or "assistant";
        } catch {
            return false;
        }
    }

    internal static string? TryExtractUserText(string line, string vendor = "claude") =>
        vendor switch {
            "codex"    => TryExtractCodexUserText(line),
            "copilot"  => TryExtractCopilotUserText(line),
            "kiro"     => TryExtractKiroUserText(line),
            "pi"       => TryExtractPiUserText(line),
            "opencode" => TryExtractOpenCodeText(line, "user"),
            "antigravity" => TryExtractAntigravityText(line, "user"),
            _          => TryExtractClaudeUserText(line)
        };

    // ── OpenCode extractors ───────────────────────────────────────────
    //
    // OpenCode transcript lines are {info,parts} with info.role user/assistant. Title
    // text is the joined non-hidden text parts — mirrors the server's
    // OpenCodeTranscriptNormalizer.ExtractText so the watcher's titles match ingest.
    static string? TryExtractOpenCodeText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Obj("info")?.Str("role") != role) return null;

            return OpenCodeTextParts(root.Arr("parts"));
        } catch {
            return null;
        }
    }

    static string? OpenCodeTextParts(JsonElement? parts) {
        if (parts is not { } arr) return null;

        var pieces = new List<string>();
        foreach (var part in arr.EnumerateArray()) {
            if (part.Str("type") != "text") continue;
            if (part.TryGetProperty("synthetic", out var syn) && syn.ValueKind == JsonValueKind.True) continue;
            if (part.TryGetProperty("ignored",   out var ign) && ign.ValueKind == JsonValueKind.True) continue;
            if (part.Str("text")?.Trim() is { Length: > 0 } t) pieces.Add(t);
        }

        return pieces.Count > 0 ? string.Join("\n", pieces) : null;
    }

    // ── Antigravity extractors ────────────────────────────────────────
    //
    // Antigravity writes brain/<id>/.system_generated/logs/transcript_full.jsonl as
    // {step_index, source, type, status, content, thinking, tool_calls, …} lines.
    // USER_INPUT (source USER_EXPLICIT) carries the prompt wrapped in
    // <USER_REQUEST>…</USER_REQUEST> plus trailing <ADDITIONAL_METADATA>/
    // <USER_SETTINGS_CHANGE> blocks; PLANNER_RESPONSE (source MODEL) carries the
    // assistant's visible `content`. Mirrors the server's
    // AntigravityTranscriptNormalizer so watcher titles match ingest.
    static string? TryExtractAntigravityText(string line, string role) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            var want = role == "user" ? "USER_INPUT" : "PLANNER_RESPONSE";
            if (root.Str("type") != want) return null;

            if (root.Str("content")?.Trim() is not { Length: > 0 } content) return null;

            return role == "user" ? StripAntigravityUserWrapper(content) : content;
        } catch {
            return null;
        }
    }

    // Pull the human prompt out of the <USER_REQUEST> envelope, dropping the trailing
    // metadata blocks Antigravity appends. Falls back to the raw text when no envelope
    // is present.
    static string? StripAntigravityUserWrapper(string content) {
        const string open = "<USER_REQUEST>", close = "</USER_REQUEST>";
        var start = content.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return content;

        start += open.Length;
        var end   = content.IndexOf(close, start, StringComparison.Ordinal);
        var inner = (end < 0 ? content[start..] : content[start..end]).Trim();

        return inner.Length > 0 ? inner : null;
    }

    // Synthetic USAGE line numbers live in a high band so they never collide with real
    // transcript line numbers (which start at 0). The server derives the USAGE event id
    // from line CONTENT (which carries gen_row), so the line number is only an ordering
    // hint — a stable, non-colliding value keeps re-sends idempotent.
    const long AntigravityUsageLineBase = 1_000_000_000L;

    /// <summary>
    /// Appends synthetic USAGE lines for gen_metadata rows past the committed watermark, and
    /// returns the max row idx staged (-1 if none). It does NOT advance the watermark — the
    /// caller commits it only after the batch send succeeds, so a failed send re-reads the
    /// rows next drain. The db is the sibling of the transcript (its path carries the real
    /// conversation id, even when the session id is the canonical dashless form).
    /// </summary>
    static long AppendAntigravityUsageLines(WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string? createdAt) {
        var maxIdx = -1L;
        try {
            if (AntigravityPaths.ConversationDbFromTranscript(transcriptPath) is not { } dbPath) return -1L;
            var rows = AntigravityGenMetadataDb.ReadUsageLines(dbPath, state.LastAntigravityGenIdx, createdAt);

            foreach (var (idx, line) in rows) {
                newLines.Add(line);
                newLineNumbers.Add((int)(AntigravityUsageLineBase + idx));
                if (idx > maxIdx) maxIdx = idx;
            }
        } catch (Exception ex) {
            // Cost is always best-effort — never let a db read break the drain.
            Log($"Antigravity usage poll failed: {ex.Message}");
        }
        return maxIdx;
    }

    // Task 10: Kiro's per-turn credits/context% live in the sidecar {id}.json, not the
    // .jsonl the live watcher tails (see KiroUsage docs) — the import path enriches the anchor
    // AssistantMessage line inline because it reads the whole file up front, but a live drain has
    // already sent that line by the time Kiro flushes the sidecar. So the live path emits a
    // synthetic KiroUsageBackfilled line per turn anchor instead (server: Task 12 folds it into a
    // backfill event, deterministic id keyed on the anchor — never the same shape/idea as a real
    // transcript line, hence its own kind). High band keeps its line numbers clear of both real
    // transcript lines and Antigravity's synthetic USAGE band (AntigravityUsageLineBase).
    const long KiroUsageLineBase = 2_000_000_000L;

    /// <summary>
    /// Builds the synthetic JSONL line the server recognizes as a Kiro usage backfill for one
    /// turn anchor (the turn's final <c>message_id</c>). <paramref name="contextPct"/> is omitted
    /// when absent, mirroring <see cref="KiroUsage.EnrichLine"/>'s optional-field handling.
    /// </summary>
    internal static string BuildKiroUsageBackfillLine(string anchor, double credits, double? contextPct) {
        var data = new JsonObject {
            ["message_id"] = anchor,
            ["credits"]    = credits,
        };
        if (contextPct is { } pct) data["context_usage_percentage"] = pct;

        var root = new JsonObject {
            ["kind"] = "KiroUsageBackfilled",
            ["data"] = data,
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// Reads the sidecar <c>{id}.json</c> next to <paramref name="transcriptPath"/> and appends a
    /// synthetic <see cref="BuildKiroUsageBackfillLine"/> line for each turn anchor NOT already in
    /// <see cref="WatchState.KiroUsageEmittedAnchors"/>, staging the newly-seen anchors on
    /// <see cref="WatchState.KiroUsagePendingAnchors"/> for the caller to commit after a successful
    /// send (mirrors <see cref="AppendAntigravityUsageLines"/>'s watermark-after-send contract, but
    /// keyed on anchor strings rather than a monotonic row index). Returns the count staged (0 on a
    /// missing/malformed sidecar or when every turn's anchor is already emitted) — never throws;
    /// usage is always best-effort.
    /// </summary>
    internal static long AppendKiroUsageBackfillLines(WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath) {
        state.KiroUsagePendingAnchors.Clear();
        var staged = 0L;
        try {
            var metaPath = Path.ChangeExtension(transcriptPath, ".json");
            if (!File.Exists(metaPath)) return 0;

            var anchors = KiroUsage.AnchorMap(File.ReadAllTextShared(metaPath)); // never lock Kiro out of its own sidecar
            var offset  = 0;

            foreach (var (anchor, usage) in anchors) {
                if (state.KiroUsageEmittedAnchors.Contains(anchor)) { offset++; continue; }

                // Task 10 scope is credits/context% only — tokens stay upstream-blocked,
                // so a turn with only (dormant, zero) token counts has nothing to backfill yet.
                if (usage.Credits == 0 && usage.ContextPct is null) { offset++; continue; }

                newLines.Add(BuildKiroUsageBackfillLine(anchor, usage.Credits, usage.ContextPct));
                newLineNumbers.Add((int)(KiroUsageLineBase + offset));
                state.KiroUsagePendingAnchors.Add(anchor);
                staged++;
                offset++;
            }
        } catch (Exception ex) {
            // Usage is always best-effort — never let a sidecar read break the drain.
            Log($"Kiro usage backfill poll failed: {ex.Message}");
        }
        return staged;
    }

    /// <summary>
    /// Kiro live context-%: returns <paramref name="lines"/> with each Kiro AssistantMessage line
    /// stamped with <c>data._kcap_usage.context_usage_percentage</c> from the sibling <c>{id}.json</c>
    /// (reusing <see cref="KiroUsage.AnchorMap"/> + <see cref="KiroUsage.EnrichLine"/>). The sibling is
    /// derived from the transcript path (dashed on disk), not the dashless <c>sessionId</c>.
    /// Best-effort, order-preserving — never throws, never drops a line. See the design spec.
    /// </summary>
    internal static List<string> EnrichKiroContextUsage(List<string> lines, string transcriptPath) {
        // Nothing to enrich unless the batch has an AssistantMessage line — skip the file read entirely.
        if (!lines.Any(static l => l.Contains("AssistantMessage", StringComparison.Ordinal))) return lines;
        try {
            var siblingJson = Path.ChangeExtension(transcriptPath, ".json");
            if (!File.Exists(siblingJson)) return lines;

            var anchors = KiroUsage.AnchorMap(File.ReadAllTextShared(siblingJson)); // never lock Kiro out of its own sidecar
            if (anchors.Count == 0) return lines;

            return lines.Select(l => KiroUsage.EnrichLine(l, anchors)).ToList();
        } catch (Exception ex) {
            // Log the first failure only — a persistently unreadable/malformed sibling would otherwise
            // spam one line per flush and drown out real warnings.
            if (!_kiroEnrichWarned) {
                _kiroEnrichWarned = true;
                Log($"Kiro context-% enrichment failed (further failures suppressed): {ex.Message}");
            }
            return lines;
        }
    }

    static bool _kiroEnrichWarned;

    /// <summary>
    /// Subagent-orchestration tool calls whose result arrives ASYNCHRONOUSLY via a separate
    /// conversation (the child reports back through <c>brain/&lt;parent&gt;/.system_generated/
    /// messages</c>, not as a result STEP in the parent's own transcript). They therefore never
    /// produce a decrement step, so counting them as "in flight" would pin
    /// <see cref="WatchState.PendingAntigravityToolCalls"/> above zero forever and permanently
    /// suppress the parent's idle-end — a subagent-invoking conversation would only end when
    /// Antigravity quits. They do not block the parent's idleness, so they are excluded
    /// from the pending count.
    /// </summary>
    static readonly HashSet<string> AsyncAntigravityToolCalls =
        new(StringComparer.OrdinalIgnoreCase) { "invoke_subagent", "define_subagent" };

    /// <summary>
    /// Tracks Antigravity tool calls in flight from a transcript line: a PLANNER_RESPONSE adds
    /// its tool_calls count (excluding async subagent-orchestration calls, see
    /// <see cref="AsyncAntigravityToolCalls"/>); a result step (RUN_COMMAND/VIEW_FILE/
    /// LIST_DIRECTORY/CODE_ACTION) removes one. Safe to call for any line — non-matching /
    /// malformed lines are ignored — so the count reflects whether a (possibly long-running)
    /// tool is awaiting its in-transcript result.
    /// </summary>
    internal static void UpdateAntigravityPendingToolCalls(WatchState state, string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            switch (root.Str("type")) {
                case "PLANNER_RESPONSE":
                    if (root.Arr("tool_calls") is { } calls)
                        state.PendingAntigravityToolCalls += calls.EnumerateArray()
                            .Count(tc => tc.Str("name") is { } name && !AsyncAntigravityToolCalls.Contains(name));
                    break;
                case "RUN_COMMAND" or "VIEW_FILE" or "LIST_DIRECTORY" or "CODE_ACTION":
                    if (state.PendingAntigravityToolCalls > 0) state.PendingAntigravityToolCalls--;
                    break;
            }
        } catch {
            // Never break the drain loop on a malformed line.
        }
    }

    static string? TryGetAntigravityCreatedAt(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Str("created_at") : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Links subagents from the parent transcript's INVOKE_SUBAGENT steps (the
    /// spawn-time signal), replacing the messages/*.json scan. For each child id not already
    /// posted, POST the link once via <paramref name="post"/> (returns true on success); a failed
    /// POST is left un-posted so a later scan retries. Pure over its inputs for unit testing.
    /// </summary>
    internal static async Task ExtractAndPostSubagentLinks(
            IEnumerable<string> lines, HashSet<string> posted, Func<string, Task<bool>> post) {
        foreach (var line in lines) {
            var children = AntigravitySubagents.ChildConversationIdsFromLine(line);
            if (children.Count == 0) {
                if (AntigravitySubagents.IsInvokeSubagentLine(line))
                    Log("Antigravity INVOKE_SUBAGENT step had no parseable child conversationId (format drift?)");
                continue;
            }
            foreach (var child in children) {
                if (posted.Contains(child)) continue;
                if (await post(child)) posted.Add(child);   // fail-open: leave un-posted to retry
            }
        }
    }

    /// <summary>
    /// Live subagent nesting: links a subagent from its parent transcript's
    /// INVOKE_SUBAGENT steps — the spawn-time signal — rather than waiting for the child to
    /// report back. <paramref name="drainedLines"/> are the lines the watcher just drained from
    /// the parent transcript this tick. Each newly-seen child is POSTed to
    /// <c>/hooks/antigravity/subagent-link</c> and only added to <paramref name="posted"/> on
    /// success, so a failed POST retries on the next scan (fail-open — never breaks the drain
    /// loop).
    /// </summary>
    Task ScanAntigravitySubagentLinks(
            string sessionId, IReadOnlyList<string> drainedLines,
            HashSet<string> posted, CancellationToken ct) =>
        ExtractAndPostSubagentLinks(drainedLines, posted,
            child => PostAntigravitySubagentLinkAsync(sessionId, child, ct));

    async Task<bool> PostAntigravitySubagentLinkAsync(
        string sessionId, string childId, CancellationToken ct
    ) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = new JsonObject {
                ["hook_event_name"] = "subagent-link",
                ["session_id"]      = sessionId,
                ["agent_id"]        = childId,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{Url}/hooks/antigravity/subagent-link", content, ct: ct);

            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Log($"Antigravity subagent-link POST failed for child {childId}: {ex.Message}");
            return false;
        }
    }

    // ── Kiro extractors ───────────────────────────────────────────
    //
    // Kiro CLI writes ~/.kiro/sessions/cli/{id}.jsonl as {"version","kind","data"}
    // lines: kind "Prompt" (user) / "AssistantMessage" / "ToolResults", whose
    // data.content[] holds {"kind":"text"|"toolUse"|"toolResult","data":…} blocks.
    // For titles we want the first user prompt and the first assistant text.

    static string? TryExtractKiroUserText(string line) => KiroLineText(line, "Prompt");

    static string? TryExtractKiroAssistantText(string line) => KiroLineText(line, "AssistantMessage");

    static string? KiroLineText(string line, string kind) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("kind") != kind) return null;
            if (root.Obj("data")?.Arr("content") is not { } content) return null;

            foreach (var block in content.EnumerateArray()) {
                if (block.Str("kind") == "text" && block.Str("data")?.Trim() is { Length: > 0 } text)
                    return text;
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    // ── Copilot extractors ────────────────────────────────────────
    //
    // Copilot's events.jsonl envelope: {type, data, id, timestamp, parentId,
    // agentId?}. Subagent-tagged events (agentId set) are skipped — a
    // subagent's user.message is the parent's task prompt, not something the
    // human typed, and using it for the title would mislabel the session.

    static string? TryExtractCopilotUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user.message") return null;
            if (root.Str("agentId") is not null) return null;

            // data.content is the clean prompt; transformedContent wraps it in
            // injected <current_datetime>/<system_reminder> noise.
            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    static string? TryExtractCopilotAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "assistant.message") return null;
            if (root.Str("agentId") is not null) return null;

            var text = root.Obj("data")?.Str("content")?.Trim();

            return string.IsNullOrEmpty(text) ? null : text;
        } catch {
            return null;
        }
    }

    // ── Pi extractors ─────────────────────────────────────────────
    //
    // Pi emits type:"message" with message.role user/assistant — NOT Claude's
    // top-level type:"user"/"assistant" — so the watcher title path needs its
    // own branch (mirrors the server PiTranscriptNormalizer mapping). Content is
    // a string or an array of {type:"text",text} blocks.

    static string? TryExtractPiUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "user") return null;

            if (msg.Str("content")?.Trim() is { Length: > 0 } strContent) return strContent;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractPiAssistantText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "message") return null;
            if (root.Obj("message") is not { } msg) return null;
            if (msg.Str("role") != "assistant") return null;

            if (msg.Arr("content") is { } content) {
                foreach (var block in content.EnumerateArray()) {
                    if (block.Str("type") == "text" && block.Str("text")?.Trim() is { Length: > 0 } text) {
                        return text;
                    }
                }
            }
        } catch {
            // Ignore parse errors
        }

        return null;
    }

    static string? TryExtractClaudeUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "user") {
                return null;
            }

            // Skip system-injected meta messages (e.g. <local-command-caveat>)
            if (root.TryGetProperty("isMeta", out var metaProp) && metaProp.ValueKind == JsonValueKind.True) {
                return null;
            }

            var msg = root.Obj("message");

            // message.content can be a string or an array
            if (msg?.Str("content") is { } strContent) {
                return strContent.StartsWith("<local-command-stdout>") ? null : StripSystemInstructions(strContent);
            }

            if (msg?.Arr("content") is { } arrContent) {
                foreach (var element in arrContent.EnumerateArray()) {
                    if (element.Str("type") == "text" && element.Str("text") is { } text
                     && !text.StartsWith("<local-command-stdout>")) {
                        return StripSystemInstructions(text);
                    }
                }
            }
        } catch (Exception ex) {
            if (!parseErrorLogged) {
                parseErrorLogged = true;
                Log($"TryExtractUserText parse error (further errors suppressed): {ex.Message}");
            }
        }

        return null;
    }

    static string? TryExtractCodexUserText(string line) {
        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "response_item") return null;

            var payload = root.Obj("payload");

            if (payload?.Str("type") != "message" || payload.Value.Str("role") != "user") return null;

            // Skip codex-injected wrappers (environment_context, AGENTS.md, turn_aborted).
            // These role:"user" payloads precede the real prompt and would otherwise be
            // used as the title context. Prelude list and block extractor live in
            // TitleGenerator so the offline-import and live-watch paths stay in sync.
            var text = TitleGenerator.ExtractCodexBlockText(payload.Value, "input_text");

            return text is null || TitleGenerator.IsCodexInjectedUserPrelude(text) ? null : text;
        } catch (Exception ex) {
            if (!parseErrorLogged) {
                parseErrorLogged = true;
                Log($"TryExtractUserText (codex) parse error (further errors suppressed): {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    /// Strips XML-like system instruction blocks from user text so they don't pollute title generation.
    /// Removes content between tags like &lt;system_instructions&gt;, &lt;system-reminder&gt;, etc.
    /// </summary>
    internal static string? StripSystemInstructions(string? text) {
        if (text is null) {
            return null;
        }

        var stripped = SystemInstructionsRegex.Replace(text, "").Trim();

        return stripped.Length > 0 ? stripped : null;
    }

    [GeneratedRegex(@"<(system_instructions|system-instructions|system-reminder|system_reminder|SYSTEM_INSTRUCTIONS)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex SystemInstructionsRx();

    static readonly Regex SystemInstructionsRegex = SystemInstructionsRx();

    async Task GenerateTitleAsync(HubConnection hubConnection, string sessionId, WatchState state, string vendor) {
        try {
            var result = await TitleGeneration.GenerateAsync(state.FirstUserText!, state.FirstAssistantText, Log, profiles.Resolution.Profile, home, vendor);

            if (result is null) {
                Log($"Title generation attempt {state.TitleAttempts}/5 returned no usable result (CLI failure, refusal-like output, or empty title)");
                state.TitleInFlight = false;

                return;
            }

            Log($"Title usage: model={result.Model} input={result.InputTokens} output={result.OutputTokens} cost=${result.CostUsd:F4}");

            await PostTitleAsync(
                hubConnection,
                sessionId,
                result.Result,
                result.Model,
                result.InputTokens,
                result.OutputTokens,
                result.CacheReadTokens,
                result.CacheWriteTokens,
                state
            );
        } catch (Exception ex) {
            Log($"Title generation failed: {ex.Message}");
            state.TitleInFlight = false;
        }
    }

    static async Task SendInitialTitleAsync(HubConnection hubConnection, string sessionId, string title) {
        try {
            await hubConnection.InvokeAsync("SendTitle", sessionId, title, null, 0L, 0L, 0L, 0L);
            Log($"Initial title sent: {title}");
        } catch (Exception ex) {
            Log($"Initial title send failed: {ex.Message}");
        }
    }

    static async Task PostTitleAsync(
            HubConnection hubConnection,
            string        sessionId,
            string        title,
            string?       model,
            long          inputTokens,
            long          outputTokens,
            long          cacheReadTokens,
            long          cacheWriteTokens,
            WatchState    state
        ) {
        try {
            await hubConnection.InvokeAsync("UpdateTitle", sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens);
            Log($"LLM title generated: {title}");
            state.TitleGenerated = true;
        } catch (Exception ex) {
            Log($"LLM title send failed: {ex.Message}");
        }

        state.TitleInFlight = false;
    }

    internal static bool RepoPayloadChanged(RepositoryPayload? current, RepositoryPayload? lastSent) {
        if (current is null) {
            return false;
        }

        if (lastSent is null) {
            return true;
        }

        return current.Owner  != lastSent.Owner
         || current.RepoName  != lastSent.RepoName
         || current.Branch    != lastSent.Branch
         || current.PrNumber  != lastSent.PrNumber
         || current.PrUrl     != lastSent.PrUrl
         || current.PrTitle   != lastSent.PrTitle
         || current.PrHeadRef != lastSent.PrHeadRef;
    }

    // An evidence-derived repo (from RepoEvidenceScanner, for a session launched outside any
    // checkout) has no cwd to re-probe, so a periodic cwd-based refresh finding nothing must
    // never be allowed to clear it back to null.
    internal static bool ShouldReplaceRepository(RepositoryPayload? detected, bool repositoryFromEvidence) =>
        detected is not null || !repositoryFromEvidence;

    // Shared by every EvidenceScanner call site — the scanner already resolved and validated
    // completeness (RepoEvidenceScanner's isComplete), so this just records the result.
    static void ApplyEvidenceRepo(WatchState state, RepositoryPayload repo) {
        state.Repository             = repo;
        state.RepositoryFromEvidence = true;
    }

    // Extracted out of DrainNewLines so the final-drain fallback DELIVERY (not just
    // RepoEvidenceScanner's own promotion, which was already covered) is unit-testable with a
    // fake scanner and no HubConnection: a caller can assert state.Repository directly after
    // isFinalDrain: true, and that a non-final drain leaves it untouched. No-op when
    // state.EvidenceScanner is null (every non-Claude / already-in-repo session) or already Done.
    internal static async Task ApplyEvidenceScanAsync(WatchState state, string vendor, IReadOnlyList<string> newLines, bool isFinalDrain) {
        if (state.EvidenceScanner is not { Done: false } scanner) return;

        foreach (var line in newLines) {
            if (await scanner.OnLineAsync(vendor, line) is { } repo) {
                ApplyEvidenceRepo(state, repo);

                return;
            }
        }

        if (isFinalDrain && await scanner.PromoteReadFallbackAsync() is { } fallback) {
            ApplyEvidenceRepo(state, fallback);
        }
    }

    static string TruncateForTitle(string text, int maxLength) {
        // Take first line only, then truncate
        var firstLine  = text.AsSpan();
        var newlineIdx = firstLine.IndexOfAny('\r', '\n');

        if (newlineIdx >= 0) {
            firstLine = firstLine[..newlineIdx];
        }

        if (firstLine.Length <= maxLength) {
            return firstLine.ToString().Trim();
        }

        // Find last word boundary before maxLength
        var truncated = firstLine[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');

        if (lastSpace > maxLength / 2) {
            truncated = truncated[..lastSpace];
        }

        return $"{truncated.ToString().Trim()}...";
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [watch] {message}");

    /// <summary>
    /// Result of a transcript drain read: the new non-blank <see cref="Lines"/> (beyond the
    /// caller's processed position), their 0-based <see cref="LineNumbers"/>, the
    /// <see cref="NextPosition"/> the caller should advance its watermark to, and
    /// <see cref="HeldIncompleteFinalLine"/> — true when a non-blank, unterminated (or, under
    /// <see cref="IncompleteFinalLinePolicy.ConsumeIfComplete"/>, unparseable) final line was held
    /// back rather than consumed. The shutdown final drain uses this to flag the session
    /// needs-import instead of silently dropping a truncated tail. <see cref="SnapshotBytes"/> is
    /// the raw byte buffer captured during THIS same capped read, starting at absolute file offset
    /// <see cref="SnapshotStartOffset"/> (0 by default — the true file start) and running
    /// <see cref="SnapshotByteLength"/> - <see cref="SnapshotStartOffset"/> bytes — populated only
    /// when the caller opts in via <c>captureRawBytes</c> (<see cref="ReadNewCompleteLinesAsync"/>)
    /// — so the Cursor rewrite guard can hash the EXACT bytes that decoded <see cref="Lines"/>
    /// instead of a later, separately-reopened disk read (review fix #1: the previous
    /// two-read wiring left a TOCTOU window between decoding the batch and hashing it).
    ///
    /// <see cref="SnapshotStartOffset"/> is non-zero whenever
    /// the caller asked for a BOUNDED capture (<c>rawBytesReadFrom</c> on
    /// <see cref="ReadNewCompleteLinesAsync"/>): <see cref="SnapshotBytes"/> then only spans the
    /// guard's prior-tail zone plus the new range, not the whole file, so a poll with little/no new
    /// content allocates ~nothing instead of a file-sized buffer every second.
    /// </summary>
    public readonly record struct NewTranscriptLines(
        List<string> Lines,
        List<int>    LineNumbers,
        int          NextPosition,
        bool         HeldIncompleteFinalLine = false,
        long         SnapshotByteLength      = 0,
        byte[]?      SnapshotBytes           = null,
        long         SnapshotStartOffset     = 0);

    /// <summary>
    /// Policy for an unterminated (no trailing newline) final transcript line at drain time.
    /// </summary>
    public enum IncompleteFinalLinePolicy {
        /// <summary>Every normal live drain: ALWAYS hold an unterminated final line — the agent is
        /// mid-write of it, and consuming its truncated prefix would permanently drop the completed
        /// line.</summary>
        Hold,

        /// <summary>The shutdown final drain: consume the unterminated final line
        /// ONLY IF the exact bytes read parse as a complete JSON record; otherwise hold it. The
        /// parseable-JSON check runs on the SAME bytes being consumed, so there is no TOCTOU with a
        /// separate pre-read completeness probe — a line that resumed growing into an incomplete
        /// record after any earlier probe is still held, never sent-and-advanced.</summary>
        ConsumeIfComplete
    }

    /// <summary>
    /// Split <paramref name="fileText"/> into the new complete (newline-terminated) transcript
    /// lines beyond <paramref name="linesProcessed"/>. A final line that is NOT newline-terminated
    /// means the agent is mid-write of it: it is held back — excluded from the batch AND from
    /// <see cref="NewTranscriptLines.NextPosition"/> — so a later drain re-reads it once complete.
    /// Consuming it would send a truncated line that fails to normalize server-side and permanently
    /// drop the completed line (the "endless reads" bug; large Read tool_result lines were
    /// the common victim). Blank lines are skipped from the output but still advance the position,
    /// matching the long-standing drain behaviour.
    /// </summary>
    public static NewTranscriptLines SplitNewCompleteLines(
            string                    fileText,
            int                       linesProcessed,
            IncompleteFinalLinePolicy policy = IncompleteFinalLinePolicy.Hold
        ) {
        var newLines       = new List<string>();
        var newLineNumbers = new List<int>();

        var endsWithNewline = fileText.Length == 0 || fileText[^1] == '\n';

        using var reader    = new StringReader(fileText);
        var       lineIndex = 0;

        while (reader.ReadLine() is { } line) {
            if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                newLines.Add(line);
                newLineNumbers.Add(lineIndex);
            }

            lineIndex++;
        }

        // this string-based helper has no independent byte-length sample
        // of its own (its caller already materialized the whole file into `fileText` elsewhere),
        // so SnapshotByteLength stays at its default (0, unused here) — only
        // ReadNewCompleteLinesAsync below (the Cursor watcher's actual read path) populates it.
        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, policy);
    }

    /// <summary>
    /// Task 11 (D0/D3) — reads exactly <paramref name="buffer"/>'s length worth of bytes
    /// from <paramref name="stream"/>'s CURRENT position, looping until the buffer is full or EOF
    /// is hit. Used by the Cursor watcher's runtime rewrite-guard byte-range checks, which need
    /// a definite byte count (a single <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/>
    /// call is not guaranteed to fill the buffer). Returns the number of bytes actually read.
    /// </summary>
    static async Task<int> ReadFullyAsync(FileStream stream, byte[] buffer, CancellationToken ct) {
        var total = 0;

        while (total < buffer.Length) {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    /// <summary>
    /// maps a server-acknowledged LINE count within the Cursor guard's
    /// freshly-verified new-range byte buffer to the byte offset immediately after that many
    /// lines' terminating newlines. <paramref name="range"/> spans file bytes starting at
    /// <paramref name="rangeStartOffset"/>; <paramref name="ackedLineCount"/> is how many of the
    /// lines within it the server's source-acknowledgement frontier actually disposed of (D3's
    /// per-line halt-at-the-gap — fewer than were sent whenever a line is retry-blocked or
    /// persist-blocked). Used to advance the guard's byte checkpoint only that far, never the
    /// whole capped range just because more bytes happened to be present in the same poll's read.
    /// Pure so it's unit-testable without a real file.
    /// </summary>
    internal static long ByteOffsetForAckedLines(ReadOnlySpan<byte> range, long rangeStartOffset, int ackedLineCount) {
        if (ackedLineCount <= 0) return rangeStartOffset;

        var seen = 0;

        for (var i = 0; i < range.Length; i++) {
            if (range[i] != (byte)'\n') continue;

            seen++;

            if (seen == ackedLineCount) return rangeStartOffset + i + 1;
        }

        // Acked at least as many lines as this range's newlines account for (a full ack) — the
        // whole verified range was consumed.
        return rangeStartOffset + range.Length;
    }

    /// <summary>
    /// scans <paramref name="transcriptPath"/> from byte 0 to find the byte
    /// offset immediately after the <paramref name="lineNumber"/>-th newline. Returns 0 for a
    /// non-positive <paramref name="lineNumber"/> (nothing to resolve). Returns null when the file
    /// is missing, or has fewer lines than requested — <b> review fix (r4, finding #5)</b>:
    /// this used to silently CLAMP to EOF instead, on the stated assumption that the reconnect path
    /// only ever rewinds to a line count the server itself acknowledged, which "by construction"
    /// can't exceed what the file has ever contained. That assumption breaks on an initial resume
    /// after the transcript was truncated/replaced while the watcher was offline (or, symmetrically,
    /// on a reconnect after the same happened mid-session): the server can legitimately hand back a
    /// line number N that the CURRENT local file — fewer than N lines — can no longer produce.
    /// Clamping there seeded a rewrite guard baseline against the WRONG (truncated) file while the
    /// bounded decoder went on labelling subsequent local lines starting at N — exactly the
    /// history-rewrite the guard exists to catch, let through by the seed path instead. Callers
    /// (<see cref="SeedCursorByteOffsetAsync"/>) must treat null as "cannot resolve this frontier
    /// exactly" and quarantine rather than seed a bogus baseline.
    ///
    /// <para>
    /// finding #5's guard was slightly too strict: a COMPLETE final
    /// record with no trailing newline yet (exactly what this watcher's own shutdown final drain,
    /// <see cref="IncompleteFinalLinePolicy.ConsumeIfComplete"/>, sends — and what historical
    /// import's <c>StreamReader</c>-based line splitting also sends for a file with no final
    /// newline) is a legitimately resolvable "last line", not a truncation. Let C be the number of
    /// <c>'\n'</c> bytes in the file. For <paramref name="lineNumber"/> &lt;= C this still resolves
    /// to the byte offset right after that newline (unchanged). For
    /// <paramref name="lineNumber"/> == C + 1, if the bytes after the last newline (or from byte 0,
    /// if there is no newline at all) form a complete JSON record (the same
    /// <see cref="IsCompleteJsonRecord"/> predicate <see cref="ApplyPartialLineHoldback"/> uses to
    /// gate consuming an unterminated final line), that record IS line C + 1 and is resolvable —
    /// matching <see cref="ByteOffsetForAckedLines"/>, which already treats a full ack beyond the
    /// range's newline count as consuming through EOF. Any other case (no trailing content, or a
    /// trailing tail that doesn't parse, or <paramref name="lineNumber"/> &gt; C + 1) is a genuine
    /// local shortfall and still returns null so the caller quarantines.
    /// </para>
    ///
    /// <para>
    /// the C + 1 case no longer resolves to EOF paired with the
    /// unchanged <paramref name="lineNumber"/>. Seeding <see cref="WatchState.CursorByteOffset"/>
    /// at EOF while <see cref="WatchState.LinesProcessed"/> stayed at C + 1 (the r5 behaviour) put
    /// the byte cursor exactly where Cursor's OWN later-arriving terminator for this SAME
    /// already-acknowledged record lands (Cursor normally writes that <c>'\n'</c> before appending
    /// the next JSONL record). When it arrives, the bounded reader
    /// (<see cref="ReadNewCompleteLinesAsync"/>) starts reading right there and — because it seeds
    /// its own line index from <see cref="WatchState.LinesProcessed"/>, already at C + 1 — treats
    /// the leading terminator as closing a NEW, phantom empty line, then labels every real line
    /// after it one too high, permanently: the server is left waiting at the true frontier while
    /// the watcher repeatedly resends from the stale offset.
    /// </para>
    ///
    /// <para>
    /// The fix rewinds instead of seeding at EOF: the C + 1 case now returns
    /// (<c>lastNewlineEnd</c>, <paramref name="lineNumber"/> - 1) — the byte offset the final
    /// record itself STARTS at, paired with a line count that does NOT count that record as
    /// processed. The record is then re-read (and re-sent) on the next poll exactly as if it had
    /// never been acknowledged — harmless, because Cursor's normalizer emits deterministic event
    /// ids, so the server's source-ack frontier dedupes a resend at/behind it rather than treating
    /// it as new. When the terminator later lands, the record is read and counted normally as line
    /// C (0-based), and the following record lands at line C + 1 — no phantom line, no drift. This
    /// keeps the byte/line frontier in lockstep exactly like every other seed path in this file.
    /// </para>
    /// </summary>
    internal static async Task<(long ByteOffset, int LineNumber)?> ResolveByteOffsetForLineAsync(string transcriptPath, int lineNumber, CancellationToken ct) {
        if (lineNumber <= 0) return (0, lineNumber);
        if (!File.Exists(transcriptPath)) return null;

        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[81920];
        var seen   = 0;
        long offset = 0;
        long lastNewlineEnd = 0;

        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) {
            for (var i = 0; i < read; i++) {
                offset++;

                if (buffer[i] != (byte)'\n') continue;

                seen++;
                if (seen == lineNumber) return (offset, lineNumber);
                lastNewlineEnd = offset;
            }
        }

        // Fewer newline-terminated lines than requested. The one remaining possibility is that the
        // requested line IS the final, unterminated-but-complete record — never a shortfall further
        // back than that.
        if (lineNumber != seen + 1 || offset <= lastNewlineEnd) return null;

        var trailingLength = (int)(offset - lastNewlineEnd);
        var trailing        = new byte[trailingLength];
        stream.Position      = lastNewlineEnd;
        var trailingRead     = await ReadFullyAsync(stream, trailing, ct);

        if (trailingRead != trailingLength) return null; // file shrank underneath us — don't guess

        // rewind to the record's own start (lastNewlineEnd) and drop
        // LineNumber by one (not yet "processed" — it will be re-read/re-sent) instead of
        // resolving at EOF with LineNumber unchanged. See the method doc for why the EOF seed
        // drifted line numbering once Cursor's own terminator for this record later arrived.
        return IsCompleteJsonRecord(Encoding.UTF8.GetString(trailing))
            ? (lastNewlineEnd, lineNumber - 1)
            : null;
    }

    /// <summary>
    /// applies a reconnect-discovered rewind (the server's acknowledged
    /// line frontier, <paramref name="serverPosition"/>, is behind <paramref name="state"/>'s own
    /// <see cref="WatchState.LinesProcessed"/>) atomically: for Cursor, resolves and rewinds
    /// <see cref="WatchState.CursorByteOffset"/> to the TRUE byte offset of
    /// <paramref name="serverPosition"/> and resets the guard's checkpoint BEFORE the line cursor
    /// itself moves, so no intermediate state is ever observed where the line cursor has rewound
    /// but the byte guard has not (or vice versa). Extracted out of <see cref="RunWatch"/>'s
    /// <c>Reconnected</c> handler so the atomicity is unit-testable without a live SignalR
    /// reconnect. A no-op byte-side rewind for every non-Cursor vendor (no guard to keep in sync).
    ///
    /// <para>
    /// returns false, WITHOUT touching
    /// <see cref="WatchState.LinesProcessed"/> or <see cref="WatchState.CursorByteOffset"/> at all,
    /// when <paramref name="serverPosition"/> cannot be resolved to an exact local byte offset (the
    /// session has already been quarantined by <see cref="SeedCursorByteOffsetAsync"/> in that case).
    /// The caller (RunWatch's <c>Reconnected</c> handler) must treat false the same way it treats a
    /// runtime rewrite detection — discard and exit — rather than proceed with a rewind that never
    /// actually happened.
    /// </para>
    ///
    /// <para>
    /// <see cref="WatchState.LinesProcessed"/> is no longer set to
    /// <paramref name="serverPosition"/> unconditionally here; <see cref="SeedCursorByteOffsetAsync"/>
    /// now owns that assignment (for every vendor) so it can substitute a REWOUND line count for
    /// the r5 complete-unterminated-final-record case, keeping it in lockstep with the byte offset
    /// it seeds in the same call. See that method's doc for why.
    /// </para>
    /// </summary>
    internal async Task<bool> ApplyReconnectRewindAsync(
            WatchState          state,
            int                 serverPosition,
            string              sessionId,
            string              vendor,
            string              transcriptPath,
            CursorRewriteGuard? cursorGuard,
            CancellationToken   ct
        ) {
        // shares the byte-side seed with RunWatch's INITIAL
        // WatcherConnect registration (see SeedCursorByteOffsetAsync's own doc) so both paths that
        // resume at a server-given line number map it to the true byte offset identically.
        //
        // SeedCursorByteOffsetAsync now sets state.LinesProcessed itself
        // (possibly rewound), so this no longer duplicates that assignment afterward.
        return await SeedCursorByteOffsetAsync(state, serverPosition, sessionId, vendor, transcriptPath, cursorGuard, ct);
    }

    /// <summary>
    /// resolves <paramref name="lineNumber"/> to its TRUE
    /// byte offset in <paramref name="transcriptPath"/> and seeds <see cref="WatchState.CursorByteOffset"/>
    /// with it, resetting the guard's checkpoint so the two-zone checks start clean from that
    /// offset (exactly as if this were the guard's very first poll). A no-op byte-side seed for
    /// every non-Cursor vendor (no guard to keep in sync) — but see r6 below, which now also owns
    /// <see cref="WatchState.LinesProcessed"/> for every vendor, not just Cursor.
    ///
    /// Shared by two call sites that both resume the watcher at a server-given line number and
    /// must keep the byte/line frontier aligned from the very first poll: <see cref="ApplyReconnectRewindAsync"/>
    /// (a reconnect discovers the server is behind the client) and RunWatch's INITIAL
    /// <c>WatcherConnect</c> registration (the server resumes the watcher at line N on a fresh
    /// process start — e.g. after a restart). Before this fix, only the reconnect path seeded the
    /// byte offset; the initial-registration path left <see cref="WatchState.CursorByteOffset"/> at
    /// its default (0) while <see cref="WatchState.LinesProcessed"/> was already N, so the
    /// ack-to-byte mapping (<see cref="ByteOffsetForAckedLines"/>) measured acked lines relative to
    /// N but counted their bytes from 0 — a permanent, silent line/byte-frontier misalignment.
    ///
    /// <para>
    /// <paramref name="lineNumber"/> is the server's own
    /// acknowledged frontier, so it is normally exactly resolvable; when it is NOT (fewer local
    /// lines exist than the server claims — a transcript truncated/replaced while the watcher was
    /// offline), seeding CursorByteOffset to a clamped EOF offset would establish the rewrite
    /// guard's baseline against the wrong (truncated) file while the line cursor still advanced to
    /// N — exactly the rewrite the guard exists to catch, let through by the seed path itself.
    /// Returns false and quarantines <paramref name="sessionId"/> via <see cref="CursorMarkers.Quarantine"/>
    /// in that case, WITHOUT touching <see cref="WatchState.CursorByteOffset"/> or the guard's
    /// checkpoint — callers must not advance <see cref="WatchState.LinesProcessed"/> either.
    /// </para>
    ///
    /// <para>
    /// <see cref="ResolveByteOffsetForLineAsync"/> now returns a
    /// (byte offset, line number) PAIR rather than a bare byte offset, because the r5
    /// complete-unterminated-final-record case resolves to a REWOUND pair (the record's own start,
    /// paired with <paramref name="lineNumber"/> - 1) instead of EOF paired with
    /// <paramref name="lineNumber"/> unchanged — see that method's doc for the full rationale (a
    /// terminator Cursor appends for this SAME already-acked record later would otherwise be
    /// misread as a phantom empty line, permanently shifting every following line's number by
    /// one). This method now assigns BOTH halves of that pair to <see cref="WatchState.CursorByteOffset"/>
    /// and <see cref="WatchState.LinesProcessed"/> together, for every vendor — including
    /// non-Cursor, which previously relied on both call sites to separately set
    /// <see cref="WatchState.LinesProcessed"/> themselves (now removed; see
    /// <see cref="ApplyReconnectRewindAsync"/>) — so the pair is assigned atomically from one
    /// source of truth instead of the byte offset and line count being written by two different
    /// call paths that could disagree.
    /// </para>
    /// </summary>
    internal async Task<bool> SeedCursorByteOffsetAsync(
            WatchState          state,
            int                 lineNumber,
            string              sessionId,
            string              vendor,
            string              transcriptPath,
            CursorRewriteGuard? cursorGuard,
            CancellationToken   ct
        ) {
        if (vendor != "cursor" || cursorGuard is null) {
            state.LinesProcessed = lineNumber;
            return true;
        }

        var resolved = await ResolveByteOffsetForLineAsync(transcriptPath, lineNumber, ct);

        if (resolved is null) {
            _markers.Quarantine(
                sessionId,
                $"cursor_transcript_rewrite_detected: session {sessionId} zone=resume_frontier — "
              + $"server-acknowledged line {lineNumber} exceeds the local transcript's line count");

            return false;
        }

        state.CursorByteOffset = resolved.Value.ByteOffset;
        state.LinesProcessed   = resolved.Value.LineNumber;
        cursorGuard.ResetCheckpoint();

        return true;
    }

    /// <summary>
    /// runs <see cref="ApplyReconnectRewindAsync"/> under
    /// <paramref name="gate"/> (RunWatch's <c>cursorRewindGate</c> — null for every non-Cursor
    /// vendor) so it can never interleave with a concurrently-running <see cref="DrainNewLines"/>
    /// (see <see cref="GatedDrainNewLinesAsync"/>, held under the SAME gate instance). Both mutate
    /// <see cref="WatchState.CursorByteOffset"/>/<see cref="WatchState.LinesProcessed"/> and the
    /// guard's checkpoint; without a shared mutual-exclusion gate, a rewind's three writes (byte
    /// offset, checkpoint reset, line cursor) could land in the middle of a drain that already
    /// snapshotted the PRE-rewind offsets — re-creating the byte/line-frontier divergence review
    /// fix #2 (r2) closed, just via a different interleaving. Extracted as a standalone testable
    /// method (rather than inlined in RunWatch's Reconnected handler) so the gate composition
    /// itself — not just <see cref="ApplyReconnectRewindAsync"/>'s own atomicity — is directly
    /// unit-testable without a live SignalR reconnect.
    ///
    /// Returns false (review fix r4, finding #5) when the rewind could not resolve the
    /// server's frontier exactly and quarantined the session instead — the caller must exit the
    /// same way it would for a runtime rewrite detection.
    /// </summary>
    internal async Task<bool> GatedApplyReconnectRewindAsync(
            SemaphoreSlim?      gate,
            WatchState          state,
            int                 serverPosition,
            string              sessionId,
            string              vendor,
            string              transcriptPath,
            CursorRewriteGuard? cursorGuard,
            CancellationToken   ct
        ) {
        if (gate is null) {
            return await ApplyReconnectRewindAsync(state, serverPosition, sessionId, vendor, transcriptPath, cursorGuard, ct);
        }

        await gate.WaitAsync(ct);
        try {
            return await ApplyReconnectRewindAsync(state, serverPosition, sessionId, vendor, transcriptPath, cursorGuard, ct);
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// runs <see cref="DrainNewLines"/> under
    /// <paramref name="gate"/> (RunWatch's <c>cursorRewindGate</c> — null for every non-Cursor
    /// vendor), the exact counterpart to <see cref="GatedApplyReconnectRewindAsync"/>: the SAME
    /// gate instance serializes both, so a drain can never observe a half-applied reconnect
    /// rewind (or vice versa — a rewind can never observe/clobber a half-applied drain ack).
    /// </summary>
    internal async Task<IReadOnlyList<string>> GatedDrainNewLinesAsync(
            SemaphoreSlim?      gate,
            HubConnection       hubConnection,
            string              sessionId,
            string              transcriptPath,
            string?             agentId,
            WatchState          state,
            string              vendor,
            CancellationToken   ct,
            bool                isFinalDrain            = false,
            CursorRewriteGuard? cursorGuard             = null,
            Action?             onCursorRewriteDetected = null
        ) {
        if (gate is null) {
            return await DrainNewLines(
                hubConnection, sessionId, transcriptPath, agentId, state, vendor, ct,
                isFinalDrain: isFinalDrain, cursorGuard: cursorGuard, onCursorRewriteDetected: onCursorRewriteDetected);
        }

        await gate.WaitAsync(ct);
        try {
            return await DrainNewLines(
                hubConnection, sessionId, transcriptPath, agentId, state, vendor, ct,
                isFinalDrain: isFinalDrain, cursorGuard: cursorGuard, onCursorRewriteDetected: onCursorRewriteDetected);
        } finally {
            gate.Release();
        }
    }

    /// <summary>
    /// Streams the new complete transcript lines from <paramref name="stream"/> WITHOUT materializing
    /// the whole file (Qodo #291 #2): only lines beyond <paramref name="linesProcessed"/> are retained.
    /// The file length is sampled once and the end-of-file newline is read from the last byte, then the
    /// read is CAPPED at that length — so a concurrent append after the sample can't make an as-yet
    /// unterminated final line look complete and get consumed (which would re-drop it).
    /// Opened by the caller with FileShare.ReadWrite (Qodo #291 #1) so the writing agent is never blocked.
    /// </summary>
    /// <param name="rawBytesReadFrom">
    /// only meaningful when <paramref name="captureRawBytes"/>
    /// is true: the absolute file offset the captured buffer starts at. 0 (the default) captures
    /// the whole file, exactly as before. A caller that knows it only needs a bounded window (the
    /// Cursor guard's prior-tail zone plus the new range) passes the true start offset instead, so
    /// the buffer allocated is bounded by how much is actually new/relevant, not file size.
    /// </param>
    /// <param name="newRangeByteOffset">
    /// only meaningful when <paramref name="captureRawBytes"/>
    /// is true: the absolute file offset of the first NEW line (line number <paramref name="linesProcessed"/>).
    /// Bytes in the captured buffer before this offset (the prior-tail zone, present only for the
    /// guard's hash) are never decoded as lines. When null (the default), the legacy behaviour
    /// applies: every line in the buffer is decoded and filtered by absolute index — required for
    /// every caller that doesn't (yet) know this offset, and pinned by
    /// <c>ReadNewCompleteLinesAsyncTests</c>. The caller is responsible for ensuring this offset
    /// lands exactly on a line boundary (true by construction for the Cursor watcher's own
    /// <c>CursorByteOffset</c> — see <see cref="ByteOffsetForAckedLines"/>).
    /// </param>
    internal static async Task<NewTranscriptLines> ReadNewCompleteLinesAsync(
            FileStream                stream,
            int                       linesProcessed,
            IncompleteFinalLinePolicy policy,
            CancellationToken         ct,
            bool                      captureRawBytes    = false,
            long                      rawBytesReadFrom   = 0,
            long?                     newRangeByteOffset = null) {
        var length = stream.Length;

        bool endsWithNewline;
        if (length == 0) {
            endsWithNewline = true;
        } else {
            stream.Seek(length - 1, SeekOrigin.Begin);
            endsWithNewline = stream.ReadByte() == '\n';
            stream.Seek(0, SeekOrigin.Begin);
        }

        var newLines           = new List<string>();
        var newLineNumbers     = new List<int>();
        var lineIndex          = 0;
        byte[]? rawBytes       = null;
        var     snapshotStart  = 0L;

        // when the caller (the Cursor watcher) needs it, capture the raw
        // bytes of this SAME capped read into a buffer and decode lines from THAT buffer, rather
        // than streaming lines directly off `stream`. This is what lets the runtime rewrite guard
        // hash the EXACT bytes that produced `newLines` below — the previous wiring decoded lines
        // here, closed the stream, and only reopened the file later to hash "the new range",
        // leaving a window where a rewrite between the two reads went undetected (both the record
        // and the pre-send re-verify would see the same, but stale, later snapshot). Every other
        // vendor keeps the original zero-buffering streaming path (captureRawBytes defaults false).
        if (captureRawBytes) {
            // bound the capture to [rawBytesReadFrom, EOF)
            // instead of always materializing the whole file. The caller (DrainNewLines) only ever
            // passes a non-zero rawBytesReadFrom on a poll that doesn't need the periodic
            // full-prefix re-hash, so an idle/small poll allocates a buffer sized to the guard's
            // small trailing-tail zone plus whatever actually grew — not file length.
            snapshotStart = Math.Min(rawBytesReadFrom, length);
            if (snapshotStart > 0) stream.Position = snapshotStart;

            var buffer = new byte[length - snapshotStart];
            var total  = 0;

            while (total < buffer.Length) {
                var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (n == 0) break;
                total += n;
            }

            // A concurrent shrink between the Length sample above and this read could leave
            // `total` short of `length` — trim to what was ACTUALLY read so SnapshotByteLength/
            // SnapshotBytes never claim bytes that were never really on disk.
            rawBytes = total == buffer.Length ? buffer : buffer[..total];
            length   = snapshotStart + rawBytes.Length;

            if (newRangeByteOffset is { } nro) {
                // Every byte at/after this position in the buffer is NEW (the offset is guaranteed
                // to land on a line boundary — see the parameter doc); bytes before it are the
                // prior-tail zone, captured only so the guard can hash it, never decoded as lines.
                var decodeFrom = (int)Math.Clamp(nro - snapshotStart, 0, rawBytes.Length);
                lineIndex = linesProcessed;

                using var bytesReader = new StreamReader(
                    new MemoryStream(rawBytes, decodeFrom, rawBytes.Length - decodeFrom, writable: false));
                while (await bytesReader.ReadLineAsync(ct) is { } line) {
                    if (!string.IsNullOrWhiteSpace(line)) {
                        newLines.Add(line);
                        newLineNumbers.Add(lineIndex);
                    }

                    lineIndex++;
                }
            } else {
                // Legacy path: the caller doesn't know the new range's exact byte offset (every
                // caller other than the live Cursor watcher), so the whole buffer (necessarily the
                // whole file — rawBytesReadFrom stays 0 with no newRangeByteOffset) is decoded and
                // filtered by absolute line index, exactly as before this fix.
                using var bytesReader = new StreamReader(new MemoryStream(rawBytes, writable: false));
                while (await bytesReader.ReadLineAsync(ct) is { } line) {
                    if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                        newLines.Add(line);
                        newLineNumbers.Add(lineIndex);
                    }

                    lineIndex++;
                }
            }
        } else {
            using var reader = new StreamReader(new LengthLimitedReadStream(stream, length), leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { } line) {
                if (lineIndex >= linesProcessed && !string.IsNullOrWhiteSpace(line)) {
                    newLines.Add(line);
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }
        }

        // `length` is the exact byte boundary this read was capped at
        // (sampled once, above, before any concurrent append could grow the file further) —
        // threaded through as SnapshotByteLength so the Cursor rewrite guard can use THIS value
        // instead of re-sampling FileInfo.Length later and risking a race with an append that
        // landed in between the two samples.
        return ApplyPartialLineHoldback(
            newLines, newLineNumbers, nextPosition: lineIndex, linesProcessed, endsWithNewline, policy,
            snapshotByteLength: length, snapshotBytes: rawBytes, snapshotStartOffset: snapshotStart);
    }

    // Decide the fate of a still-being-written final line (no trailing newline yet). Under
    // Hold (every live drain) it is always held back: the position stays before it and it is dropped
    // from this batch, so a later drain re-reads it once newline-terminated — consuming its truncated
    // prefix would permanently drop the completed line. Under ConsumeIfComplete (the
    // shutdown final drain — task 7) it is consumed ONLY IF the exact bytes read parse as a
    // complete JSON record; a still-growing/unparseable tail is held (never sent-and-advanced) and
    // signalled via HeldIncompleteFinalLine so the caller can flag needs-import. Because the parse
    // check runs on the bytes actually being consumed here, there is no TOCTOU with any earlier
    // completeness probe. Shared by the string helper (SplitNewCompleteLines) and the streaming
    // reader (ReadNewCompleteLinesAsync).
    static NewTranscriptLines ApplyPartialLineHoldback(
            List<string>              newLines,
            List<int>                 newLineNumbers,
            int                       nextPosition,
            int                       linesProcessed,
            bool                      endsWithNewline,
            IncompleteFinalLinePolicy policy,
            long                      snapshotByteLength  = 0,
            byte[]?                   snapshotBytes       = null,
            long                      snapshotStartOffset = 0) {
        if (endsWithNewline || nextPosition <= linesProcessed) {
            return new NewTranscriptLines(
                newLines, newLineNumbers, nextPosition,
                SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
                SnapshotStartOffset: snapshotStartOffset);
        }

        var partialIndex = nextPosition - 1;
        var hasPartial   = newLineNumbers.Count > 0 && newLineNumbers[^1] == partialIndex;

        // Consume the unterminated final line only when the policy allows it AND the exact bytes
        // read form a complete JSON record. Anything else (Hold, blank/whitespace partial, or an
        // unparseable tail) is held back.
        var consume = policy == IncompleteFinalLinePolicy.ConsumeIfComplete
                      && hasPartial
                      && IsCompleteJsonRecord(newLines[^1]);

        if (consume) {
            return new NewTranscriptLines(
                newLines, newLineNumbers, nextPosition,
                SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
                SnapshotStartOffset: snapshotStartOffset);
        }

        if (hasPartial) {
            newLines.RemoveAt(newLines.Count - 1);
            newLineNumbers.RemoveAt(newLineNumbers.Count - 1);
        }

        // Only a real (non-blank) held line is "incomplete content" the caller must surface; a
        // whitespace-only trailing partial carries nothing to lose. SnapshotByteLength/SnapshotBytes
        // still reflect the FULL capped read (including the held-back tail's bytes) — the guard
        // must still verify those bytes weren't rewritten even though they weren't sent this poll.
        return new NewTranscriptLines(
            newLines, newLineNumbers, partialIndex, HeldIncompleteFinalLine: hasPartial,
            SnapshotByteLength: snapshotByteLength, SnapshotBytes: snapshotBytes,
            SnapshotStartOffset: snapshotStartOffset);
    }

    /// <summary>True if <paramref name="line"/> (a single, newline-less transcript line) parses as a
    /// complete JSON document. Used to gate consuming an unterminated final line at shutdown.</summary>
    static bool IsCompleteJsonRecord(string line) {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        try {
            using var _ = JsonDocument.Parse(trimmed);
            return true;
        } catch (JsonException) {
            return false;
        }
    }

    /// <summary>Read-only view of <c>inner</c> that reports EOF after <c>limit</c> bytes, so a
    /// StreamReader over it can't read past a length sampled before a concurrent append.</summary>
    internal sealed class LengthLimitedReadStream(Stream inner, long limit) : Stream {
        long _consumed;

        public override int Read(byte[] buffer, int offset, int count) {
            var allowed = (int)Math.Min(count, limit - _consumed);
            if (allowed <= 0) return 0;
            var n = inner.Read(buffer, offset, allowed);
            _consumed += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            var allowed = (int)Math.Min(buffer.Length, limit - _consumed);
            if (allowed <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..allowed], ct);
            _consumed += n;
            return n;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => limit;

        public override long Position {
            get => _consumed;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static int CountFileLines(string path) {
        try {
            if (!File.Exists(path)) {
                return 0;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var       count  = 0;

            while (reader.ReadLine() is not null) {
                count++;
            }

            return count;
        } catch {
            return 0;
        }
    }

    [GeneratedRegex("<command-name>(.*?)</command-name>", RegexOptions.Compiled)]
    private static partial Regex CommandNameRx();

    /// <summary>
    /// Retries SignalR reconnection indefinitely with exponential backoff capped at 30s.
    /// The watcher is lightweight when idle — it should never give up while the session is active.
    /// </summary>
    sealed class InfiniteRetryPolicy : IRetryPolicy {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount switch {
                0 => TimeSpan.Zero,
                1 => TimeSpan.FromSeconds(2),
                2 => TimeSpan.FromSeconds(10),
                _ => TimeSpan.FromSeconds(30),
            };
    }
}
