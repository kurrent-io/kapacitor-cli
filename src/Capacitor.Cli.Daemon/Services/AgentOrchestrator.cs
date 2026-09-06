using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <paramref name="Repository"/> is the main repository behind the checkout the worktree was
/// made from — a linked worktree's own .git file names it, so a borrowed reviewer and the
/// primary whose worktree it borrowed resolve to the same repository. <paramref name="Worktree"/>
/// is the checkout root the agent runs in; <paramref name="BorrowedFrom"/> the checkout root a
/// reviewer borrowed, null unless borrowed. A snapshot reviewer runs in its own copy, so its
/// Worktree and BorrowedFrom differ.
/// </summary>
internal sealed record AgentCheckout(string Repository, string Worktree, string? BorrowedFrom) {
    public static AgentCheckout Resolve(WorktreeInfo worktree, WorkLocation work, string? borrowedSnapshotSource) {
        var borrowed   = work == WorkLocation.BorrowedCwd || borrowedSnapshotSource is not null;
        // The source is a cwd for a borrow, which may sit below its checkout root.
        var sourceRoot = GitRepository.FindRoot(worktree.SourceRepo) ?? worktree.SourceRepo;
        var repository = GitRepository.ResolveMainRepoRoot(sourceRoot);
        var runsIn     = work == WorkLocation.BorrowedCwd ? sourceRoot : worktree.SnapshotRoot ?? worktree.Path;

        return new AgentCheckout(repository, runsIn, borrowed ? sourceRoot : null);
    }
}

internal record AgentInstance(
        string                  Id,
        string?                 Prompt,
        // Nullable because "no model is being reported" is a real, intentional state: a runtime that
        // cannot APPLY a caller-supplied model must not report one (see ModelSelectionLaunchPolicy).
        // Declaring it non-null while storing null there would let a consumer dereference it with no
        // warning on exactly the launches where it is absent.
        string?                 Model,
        string?                 Effort,
        string                  RepoPath,
        string                  Vendor,
        IHostedAgentRuntime     Runtime,
        WorktreeInfo            Worktree,
        CancellationTokenSource ReadCts
    ) {
    public string?              SessionId         { get; set; }
    public string               Status            { get; set; } = "Starting";
    public DateTime             CreatedAt         { get; init; } = DateTime.UtcNow;
    public DateTime             LastOutputAt      { get; set; } = DateTime.UtcNow;
    public bool                 HasReceivedOutput { get; set; }
    public TerminalOutputBuffer OutputBuffer      { get; } = new();

    /// The agent's own transcript — Claude's project .jsonl or Codex's rollout — resolved once
    /// by discovery and cached: the status payload and the Codex send-path probe both read it,
    /// and neither may scan a directory to do so. Null until discovery lands, and forever for a
    /// runtime that writes nothing the daemon locates.
    public string? TranscriptPath { get; set; }

    bool _titleComputed;
    string? _title;
    /// <summary>The status payload's display title, computed ONCE from the immutable Prompt
    /// (SnapshotAgentsForStatus re-runs for every agent on every status pulse — re-parsing an
    /// invariant there is pure waste, same reasoning as TranscriptPath's cache).</summary>
    public string? Title {
        get {
            if (!_titleComputed) { _title = TitleFromPrompt(Prompt); _titleComputed = true; }
            return _title;
        }
    }

    /// <summary>A title resolved after launch — native extraction, local generation, or the
    /// server's — which the status payload prefers over the prompt seed. Written only through
    /// <see cref="AgentOrchestrator.SetResolvedTitle"/> so the pulse cannot be forgotten.</summary>
    public string? ResolvedTitle { get; set; }

    /// First non-blank line of the launch prompt, trimmed, capped at 80 chars total (ellipsis when
    /// cut, never splitting a surrogate pair) — the status payload is re-sent on every revision,
    /// so the full prompt never rides it.
    internal static string? TitleFromPrompt(string? prompt) {
        if (prompt is null) return null;
        foreach (var raw in prompt.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Length <= 80) return line;
            var cut = char.IsHighSurrogate(line[78]) ? 78 : 79;
            return line[..cut] + "…";
        }
        return null;
    }

    /// <summary>Codex turn diagnostic: monotonic per-agent round generation for the post-send
    /// rollout-growth probe. Bumped (under the send gate, BEFORE each round's input is delivered) so
    /// a later round instantly invalidates any earlier round's probe — a probe emits a verdict only
    /// while its captured generation is still the latest, so a later round's growth can never be
    /// attributed to an earlier round even during the earlier probe's own poll. A plain field for
    /// <see cref="System.Threading.Interlocked.Increment(ref long)"/>.</summary>
    public long CodexTurnProbeGen;

    /// <summary>This agent's monotonic activity clock. One instance per launch — a relaunch gets a
    /// fresh one, never inheriting the predecessor's idle window. Defaults to a real-time instance so
    /// existing test constructions keep compiling; a production launch builds it explicitly, before
    /// this record exists, so the ACP runtime and the permission bridge share the same instance.</summary>
    public AgentActivityClock ActivityClock { get; init; } = new(TimeProvider.System);

    /// <summary>Phase B (D2): the launch kind + (for a ReviewFlow launch) the flow identity,
    /// captured from <see cref="LaunchAgentCommand"/> at construction. Reported in
    /// <c>LiveAgents</c>/<c>DaemonStatusReport</c> so a restarted server can associate a surviving
    /// unassigned reviewer with its role. Defaults preserve pre-D2 behavior for any non-D2 launch path.</summary>
    public LaunchKind           Kind              { get; init; } = LaunchKind.Default;
    public string?              FlowRunId         { get; init; }
    public string?              FlowRole          { get; init; }

    /// <summary>The server-sent per-run inactivity bound
    /// (<see cref="LaunchAgentCommand.InactivityBoundSeconds"/>), captured verbatim at launch. Null
    /// for a non-review-flow launch, a local spawn, and a launch from a server predating this field.
    ///
    /// <para><b>Nothing on the daemon enforces this</b> — it is round-scoped and this daemon is
    /// round-agnostic. Stored only because it is a live wire field; removing it would be a wire
    /// change. <see cref="AgentOrchestrator.FindReviewersToReap"/> deliberately never reads it — see
    /// that method for the regression that rule prevents. Do not reintroduce a reap keyed on it.</para></summary>
    public int?                 InactivityBoundSeconds { get; init; }

    /// <summary>Who asked for this launch (server-stamped requester user id). Null for old
    /// servers and local spawns — the supervision payload renders null as unknown.</summary>
    public string?              RequesterUserId   { get; init; }

    /// <summary>The server-stamped human-readable name for <see cref="RequesterUserId"/>. Null for
    /// old servers, local spawns, and a PID-record recovery (never persisted, best-effort only) —
    /// the supervision payload falls back to <see cref="RequesterUserId"/>, then "unknown".</summary>
    public string?              RequesterDisplay  { get; init; }

    /// <summary>The applied Codex sandbox/approval pair — the values actually passed to the vendor
    /// CLI, whether caller-selected or derived. Set only for an interactive Codex launch on a
    /// daemon-owned worktree; null everywhere else. Stored HERE (not recomputed at each send) so the
    /// initial registration and every reconnect re-registration report the same pair, which is what
    /// lets the value survive a server restart.</summary>
    public string?              SandboxPolicy     { get; init; }
    public string?              ApprovalPolicy    { get; init; }

    /// <summary>The applied ACP permission preset for a hosted interactive ACP launch that carried
    /// one; null everywhere else. Stored here (like the posture pair above) so initial registration
    /// and every reconnect re-registration report the same value.</summary>
    public string?              PermissionPreset  { get; init; }

    /// <summary>The approval policy bound once at launch, from the worktree this agent runs in plus
    /// the daemon user's own file. Held for the run so every permission this agent raises is judged
    /// against the same documents the launch was reported with, whatever the files later say.
    /// Null when no provider was wired, or when the build failed.</summary>
    public PolicySnapshot?      PolicySnapshot    { get; init; }

    /// <summary>The runtime transport this agent actually launched on, reported to the server on
    /// AgentRegistered so it can validate its launch decision. Each runtime owns its own transport
    /// (<see cref="IHostedAgentRuntime.RuntimeTransport"/>), so this reports the same value on initial
    /// registration and every reconnect re-registration (the same instance survives a reconnect).</summary>
    public string RuntimeTransport => Runtime.RuntimeTransport;

    /// <summary>Phase B (D1): single-flight teardown latch — a plain field (not a property) so
    /// <see cref="System.Threading.Interlocked.CompareExchange(ref int,int,int)"/> can gate it. Exactly
    /// one teardown runs even if the launch-catch and the read-loop's finally race.</summary>
    public int CleanupStarted;

    /// <summary>Design spec §3.3: single-flight guard for reporting a published ACP launch-window
    /// reap verdict to the server — claimed (0→1) by the FIRST path that reports it, via
    /// <see cref="System.Threading.Interlocked.CompareExchange(ref int,int,int)"/>, so dedupe is
    /// structural rather than dependent on today's call-site ordering. Set by
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/>'s verdict arm; read (via a plain
    /// <see cref="System.Threading.Volatile"/> read, no claim) by
    /// <see cref="AgentOrchestrator.StopAgentCoreAsync"/> to suppress a racing stop's non-failure
    /// "Completed" transition once a verdict has already been reported — the factory's
    /// pre-registration reclassification (design spec §3.2) has no <see cref="AgentInstance"/> to
    /// share it with (see that method's remarks) — but it is the canonical claim any future
    /// verdict-reporting call site for this agent must take before sending.</summary>
    public int LaunchFailureVerdictReported;

    /// <summary>Phase B (D4): the child's exact start-identity captured ONCE at spawn (the
    /// <c>ProcessStartToken</c>). Teardown uses THIS stored token — never a freshly-recaptured one — so a
    /// pid recycled after the child exited can't be adopted and later killed. Null only when the pid was
    /// never capturable (a non-live/degenerate pid — nothing to track).</summary>
    public string? StartIdentity { get; set; }

    /// <summary>Temp MCP config path written for hosted PR reviews; deleted on cleanup.</summary>
    public string? McpConfigPath { get; set; }

    /// <summary>The per-reviewer LocalPermissionBridge token URL minted for an unattended review-flow
    /// launch (null otherwise). Revoked on cleanup so the auto-approve path dies with the reviewer.</summary>
    public string? ReviewerBridgeToken { get; init; }

    /// <summary>
    /// Reason string sent to the server when ending the AgentSession. Defaults to
    /// "agent_exited" (claude exited on its own); HandleStopAgent flips it to
    /// "agent_stopped" so a user-initiated stop is still attributed correctly even
    /// if HandleStopAgent's own EndAgentSessionAsync call fails and the read-loop's
    /// finally-block call is the only one that lands.
    /// </summary>
    public string PendingEndReason { get; set; } = "agent_exited";

    /// <summary>True while a park ack is outstanding; a sweep skips an agent already parking; reset
    /// on ambiguous ack to allow retry.</summary>
    public bool ParkAttemptInFlight { get; set; }

    // ── Local terminal attach (Phase 1) ──────────────────────────────────
    // Internal: these expose the daemon-internal ITerminalSink, so they can't be public
    // on this public record (CS0053). They're only touched inside the daemon assembly.
    /// <summary>Local-terminal clients attached over the control socket.</summary>
    internal List<ITerminalSink> LocalSinks { get; } = [];
    internal Lock                SinksLock  { get; } = new();
    /// <summary>Each attached local client's last-reported size, for the resize min-clamp.</summary>
    internal Dictionary<ITerminalSink, Dim> ClientDims { get; } = [];
    public readonly record struct Dim(ushort Cols, ushort Rows);

    /// <summary>The server-aggregated min size across all web viewers (one value per agent,
    /// computed server-side from per-connection web dims), folded into the same min-clamp as the
    /// local clients so a small web viewer and a large local terminal share the one PTY at the
    /// smallest size — tmux semantics across surfaces. <c>null</c> when no web viewer is
    /// attached, so the clamp grows back to the local-only size. Guarded by <see cref="SinksLock"/>.</summary>
    internal Dim? WebDims { get; set; }

    /// <summary>Tripped when the agent terminates (CleanupAgentAsync) so an attached local
    /// client that's blocked waiting on the user's keystrokes wakes, flushes the last output,
    /// and sends an Exited frame instead of hanging.</summary>
    internal CancellationTokenSource ExitedCts { get; } = new();

    /// <summary>
    /// True for locally-launched agents: the orchestrator makes no per-agent server call
    /// and does not attach the SignalR sink. An explicit share (Phase 2) clears this.
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>
    /// True for agents started from a local terminal (`kcap agent start`), whether registered or
    /// `--private`. Such an agent has a live local terminal as its primary surface, so the read
    /// loop streams to the server <b>non-blocking</b> (drop+count on a full backlog) rather than
    /// back-pressuring the PTY on a remote tunnel stall — the local terminal must not freeze when
    /// the cloud hiccups. Hosted agents (server is the only consumer) keep lossless back-pressure.
    /// </summary>
    public bool IsLocalSpawned { get; init; }

    /// <summary>Owned worktree (daemon-created — safe to remove on cleanup) vs borrowed cwd
    /// (the user's own checkout — never removed).</summary>
    public WorkLocation Work { get; init; } = WorkLocation.OwnedWorktree;

    /// <summary>Authorized live checkout mirrored into this owned worktree for a runtime that
    /// cannot safely execute in-place. Refreshed before each later review round.</summary>
    public string? BorrowedSnapshotSource { get; init; }

    AgentCheckout? _checkout;
    /// <summary>Where the work lives, as the status wire reports it. Resolved once: it reads
    /// .git entries, and neither the worktree nor the work location changes after launch.</summary>
    public AgentCheckout Checkout => _checkout ??= AgentCheckout.Resolve(Worktree, Work, BorrowedSnapshotSource);

    /// <summary>The per-agent critical section. Named for its original duty (serializing the borrowed-
    /// checkout refresh against a concurrent send) but it has always wrapped the ENTIRE
    /// <see cref="AgentOrchestrator.HandleSendInput"/> body for EVERY vendor, borrowed or not — so it
    /// is simply "the delivery section", and that is its second, load-bearing purpose:
    ///
    /// <para><b>The delivery/reap fence (round-dispatch grace §3).</b> The reaper's
    /// validate-and-claim (<see cref="AgentOrchestrator.TryClaimReapAsync"/>) runs inside this same
    /// section, so a delivery's clock advance and a reap claim are mutually exclusive and exactly one
    /// side wins: a delivery that completes first advances <see cref="AgentActivityClock.ActivitySeq"/>
    /// past the generation captured at selection, and the claim aborts; a claim that lands first sets
    /// <see cref="ReapClaimed"/>, and the next delivery refuses. Selection alone can never be the
    /// decision — <see cref="AgentOrchestrator.FindReviewersToReap"/> reads a snapshot, and any recheck
    /// outside this section leaves a recheck-to-stop window.</para>
    ///
    /// <para><b>A waiter whose own action is needed to unblock the holder must bound its wait.</b> It
    /// is held across a delivery, and a delivery can block for an unbounded time on a healthy-looking
    /// transport — the PTY path ends in <c>UnixPtyProcess.WriteAsync</c>, a raw <c>write(2)</c> on the
    /// pty master with no timeout and no cancellation, which parks forever if the child stops draining
    /// its stdin. The graceful-stop wait in <see cref="AgentOrchestrator.StopAgentCoreAsync"/> is exactly this case: it
    /// is waiting on the very process that only its OWN next action (terminate) can unblock, so that
    /// wait is bounded. The delivery's own ENTRY wait onto this gate (<see
    /// cref="AgentOrchestrator.HandleSendInput"/>) is exempt from this rule — it is the holder class,
    /// not the bounded class: it is unblocked by whatever the current holder is itself waiting on (the
    /// child exiting, or a stop completing), never by an action the entry waiter must take. See <see
    /// cref="AgentOrchestrator.TryClaimReapAsync"/>.</para>
    ///
    /// <para><b>Lock ordering.</b> This gate is OUTERMOST relative to
    /// <see cref="AgentActivityClock"/>'s internal lock (holders read the clock while holding it) and
    /// must NEVER be held across an acquisition of
    /// <c>AgentOrchestrator._statusReportOrderingGate</c> — that gate is held across a whole hub send
    /// and takes the clock itself, so a per-agent → ordering edge would put an unbounded network wait
    /// underneath a per-agent lock, on top of the write(2) hazard above. The permitted edges are
    /// per-agent → clock and ordering → clock, never per-agent → ordering.</para>
    ///
    /// <para>Deliberately never disposed: a reap claim and a delivery can both be parked here while
    /// teardown runs, and an <see cref="ObjectDisposedException"/> on either would surface on a task
    /// nobody observes.</para></summary>
    public SemaphoreSlim BorrowedSnapshotGate { get; } = new(1, 1);

    /// <summary>0/1 single-flight latch claimed by the reaper that WON this agent, via
    /// <see cref="System.Threading.Interlocked.CompareExchange(ref int,int,int)"/> — the same shape as
    /// <see cref="CleanupStarted"/>, and interlocked rather than gate-guarded ON PURPOSE: the absolute-
    /// lifetime reap must be able to claim WITHOUT <see cref="BorrowedSnapshotGate"/> when a parked
    /// delivery is holding it (see <see cref="AgentOrchestrator.TryClaimReapAsync"/>), so the two claim
    /// paths cannot share a lock and must share a CAS instead.
    ///
    /// <para>Read via <see cref="IsReapClaimed"/> by <see cref="AgentOrchestrator.HandleSendInput"/>,
    /// which refuses to deliver to a condemned agent. Effectively write-once: a claimed agent is on its
    /// way down, and <see cref="AgentOrchestrator.StopAgentCoreAsync"/> flipping the status to
    /// "Completed" already takes it out of the reap candidate set permanently, so the latch adds no new
    /// terminality — it only makes the losing side of the race observable to the delivery path BEFORE
    /// teardown has physically closed the transport.</para>
    ///
    /// <para>The SOLE reset is §2.7 B6 arm-A's <see cref="AgentOrchestrator.ParkReviewerAsync"/> on a
    /// <see cref="ParkAck.Ambiguous"/> ack: a park that got no definite reply tears down NOTHING, so it
    /// must un-condemn the still-Running agent (release the latch) for a later sweep to re-claim — a
    /// claim that neither parks nor reaps may not permanently strand a live reviewer. That reset runs
    /// while the agent is still Running (no teardown began) and before the in-flight guard is cleared,
    /// so it never races a path that already assumed terminality.</para></summary>
    public int ReapClaimed;

    /// <summary><see cref="ReapClaimed"/> as a bool, through a
    /// <see cref="System.Threading.Volatile"/> read — the un-gated absolute-lifetime claim
    /// path writes it outside any lock, so a plain field read is not guaranteed to observe it.</summary>
    public bool IsReapClaimed => Volatile.Read(ref ReapClaimed) != 0;

    /// <summary>Current PTY dimensions — the single source of truth for every dims send
    /// (registration, reconnect). Updated by every resize path (local clamp + web resize).
    /// Hosted agents initialise these to the fixed HostedPtyCols/Rows; ushort read/write is
    /// atomic, and stale-by-one-resize is harmless for best-effort dims.</summary>
    public ushort CurrentCols { get; set; }
    public ushort CurrentRows { get; set; }

    /// <summary>
    /// The live ACP transcript forwarder for this agent, set once
    /// <see cref="AgentOrchestrator.HandleLaunchAgent"/>'s post-registration bind
    /// (<c>AcpSessionStarted</c>) succeeds and the forwarder is constructed. <see langword="null"/>
    /// for every PTY agent (claude/codex — <see cref="HostedRuntimeStart.Transcript"/> is null for
    /// them) and for an ACP agent whose initial bind failed (nothing to drain in that case — see
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/>). Read by
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> to run the bounded final-drain before
    /// ending the session.
    /// </summary>
    public AcpForwarderHandle? AcpForwarder { get; set; }

    /// <summary>
    /// Per-agent/per-setup <see cref="CancellationTokenSource"/>, linked to the daemon's shutdown
    /// token, created in <see cref="AgentOrchestrator.HandleLaunchAgent"/> BEFORE the fire-and-forget
    /// <see cref="AgentOrchestrator.StartAcpForwardingAsync"/> call — so it exists immediately,
    /// independent of whether the bind ever resolves. Both the bind/setup task and (once started) the
    /// forwarder's run task use ITS token rather than the raw daemon-wide shutdown token, so
    /// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can cancel just this agent's ACP work
    /// (on drain-timeout, and unconditionally at finalize) without touching any other agent or the
    /// daemon's own shutdown gate. <see langword="null"/> for every PTY agent (claude/codex) and set
    /// exactly once, at launch, for every ACP-capable runtime — never re-created for the same agent.
    /// </summary>
    public CancellationTokenSource? AcpCts { get; set; }
}

/// <summary>
/// Pairs a started <see cref="AcpTranscriptForwarder"/> with its fire-and-forget run task
/// (<see cref="AgentOrchestrator.ForwardAcpTranscriptAsync"/>'s return
/// value) so <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> can await the SAME task (bounded)
/// at teardown without re-deriving or re-wrapping it.
/// </summary>
internal sealed record AcpForwarderHandle(AcpTranscriptForwarder Forwarder, Task RunTask);

/// <summary>Ring buffer that keeps the last 2 MB of terminal output.</summary>
public class TerminalOutputBuffer {
    readonly List<byte[]> _chunks = [];
    int                   _totalBytes;
    const int             MaxBytes = 2 * 1024 * 1024;

    public void Append(byte[] data) {
        lock (_chunks) {
            _chunks.Add(data);
            _totalBytes += data.Length;

            while (_totalBytes > MaxBytes && _chunks.Count > 1) {
                _totalBytes -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }
    }

    public List<byte[]> GetAll() {
        lock (_chunks) { return [.._chunks]; }
    }

    /// <summary>Flattens the retained ring into one buffer for a one-time replay to a
    /// newly-attached client (bounded by <see cref="MaxBytes"/>).</summary>
    public byte[] Snapshot() {
        lock (_chunks) {
            var ms = new MemoryStream(_totalBytes);
            foreach (var c in _chunks) ms.Write(c);

            return ms.ToArray();
        }
    }
}

internal partial class AgentOrchestrator : IAsyncDisposable {
    readonly ConcurrentDictionary<string, AgentInstance>       _agents = new();

    // The change-generation counter behind the DaemonStatus push. Optional ctor param so the
    // existing direct-construction sites (and DI, which resolves an optional parameter to a
    // registered singleton when one exists) keep compiling unchanged.
    readonly DaemonStatusNotifier _statusNotifier;

    readonly PolicySnapshotProvider? _policySnapshots;

    /// <summary>
    /// Relays a borrowed reviewer's flow submission to the server under the DAEMON's credential.
    ///
    /// <para>The daemon runs unsandboxed with the real HOME, so its token store resolves; the
    /// reviewer's does not. <paramref name="apiPath"/> comes from the bridge's own fixed route table,
    /// never from the request, so a sandboxed child cannot steer this at an arbitrary API path.</para>
    /// </summary>
    async Task<(int Status, string Body)> ForwardFlowSubmissionAsync(
            string apiPath, string body, CancellationToken ct) {
        using var http = await _http.ForBackgroundAsync(ct);
        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(
            $"{_config.ServerUrl.TrimEnd('/')}{apiPath}", content, ct);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Test seam: exposes which notifier this orchestrator actually pulses into, so a DI
    /// wiring test can pin that the registered singleton — not a private fallback nobody
    /// subscribes to — is the one every agent mutation reaches (see DaemonStatusWiringTests).</summary>
    internal DaemonStatusNotifier StatusNotifierForTest => _statusNotifier;

    int _discoveryStarts;
    internal int DiscoveryStartsForTest => Volatile.Read(ref _discoveryStarts);

    // Phase B (D4): durable PID records + this daemon's logical identity/epoch for
    // crash-survivor reaping. Initialized in the ctor from config.
    readonly AgentPidRecordStore? _pidRecords;
    readonly AgentKillQuarantine? _quarantine;
    readonly OrphanReaper?        _orphanReaper;

    // Tail-of-PTY capture for a FAILED launch, under the same per-daemon record root as the PID
    // records ({state}/{name}/agents/failed/) — survives worktree teardown for post-mortem.
    readonly FailedLaunchLog?     _failedLaunchLog;
    readonly string               _daemonId    = "";
    readonly string               _daemonEpoch = "";

    // Phase B2-b (sequenced-settlement design §4.2.4): the durable positive-death-evidence outbox.
    // Fed at every CONFIRMED-gone seam — the OrphanReaper record pass (Hook A), the quarantine drain
    // (Hook B), and the StopAgent-fallback reap (Hook C) — always ledger-append BEFORE source-delete so
    // a crash between the two re-derives from the leftover source and Upsert (idempotent on the
    // source-stable (AgentId, OldEpoch) key) collapses onto the committed entry. Lives in the same
    // state dir as _pidRecords, under the same atomic temp+rename discipline.
    readonly ResolvedCandidatesLedger? _resolvedLedger;

    // Phase B2-b (sequenced-settlement design §4.2.4): the durable marker-candidate source store for a
    // RECORDLESS prior-epoch survivor (Hook D). The env-marker scan persists a source BEFORE the kill and
    // resolves it through the (a)/(b)/(c) matrix; on confirmed death it emits into _resolvedLedger with a
    // NULL flow (the env is untrusted) unless a co-existing durable RECORD supplies a trusted flow.
    readonly MarkerCandidateStore? _markerCandidates;

    // Phase B (D4): the per-daemon record root ("{stateDir}/{name}") every durable per-daemon store shares.
    // Retained so the next-boot handoff can be exercised over the same root a restarted daemon would use.
    readonly string _pidRecordRoot;

    // Phase B2-b (sequenced-settlement design §4.2.3): the durable coverage boot-chain verdict,
    // folded in DaemonRunner (before Connect) and stashed on config. Advertised on the enriched
    // DaemonConnect payload; a Linux/macOS value is inert (the server consumes it only on Windows).
    readonly bool        _recordlessSurvivorsImpossible;

    // Phase B2-b (sequenced-settlement design §4.2.2): the epoch-scoped sequenced-command handler.
    // Owns the contiguous-prefix watermark + the daemon's single server-command execution lane; injected
    // with this orchestrator's ReadLiveness / stop-admission probe / the server's CommandAck +
    // CommandRejected sends so it stays unit-testable without a live hub.
    //
    // §3.3 (one execution domain): un-sequenced launches and stops no longer bypass it — they are
    // committed onto the SAME serial lane via SubmitUnsequenced, so cross-format arrival order holds by
    // construction and nothing is ever refused for its FORMAT (the server mixes formats permanently by
    // design: the sequenced tuple rides only the review-flow settlement lane, while ordinary launches and
    // every stop are un-sequenced). NOT readonly, because publication is a guarded transition rather than
    // a plain field write (see PublishSequencedProcessor) — but still single-assignment for the process
    // lifetime, since the daemon epoch is a per-boot GUID pinned before services are built: exactly one
    // null->live transition, never a replacement or reset. Read through Processor.
    SequencedCommandProcessor? _processor;

    // §3.3 transition barrier ("no dual domain, ever"). ONE orchestrator-owned lock shared by un-sequenced
    // handler admission and processor publication: a handler takes it to snapshot _processor and, on null,
    // RESERVES the inline slot before invoking the core; publication takes the same lock to install the
    // processor and capture that reservation, which the lane awaits before executing its first item.
    // Snapshot+reserve is therefore atomic with publication — a handler that saw null cannot start inline
    // work after the lane has begun, and the lane cannot begin while a reserved inline item exists.
    // In production the null window does not exist at all (publication happens in this constructor, before
    // any handler is wired), so the barrier is defence-in-depth for the shape the spec describes; the
    // deferred-publication test seam is what makes it observable.
    readonly object _domainLock = new();
    int _inlineInFlight;
    TaskCompletionSource? _inlineDrained;

    // §3.3: the un-sequenced payload CLASSES. The legacy StopAgent hub method carries only an agent id, so
    // its payload key is a constant and every un-sequenced stop for one target coalesces onto one queued
    // entry per launch segment. Launches never coalesce (two launches for one id are two distinct
    // instances), so their key is a fixed placeholder the processor ignores for that kind.
    const string UnsequencedStopPayloadKey   = "stop";
    const string UnsequencedLaunchPayloadKey = "launch";

    // §3.3: the only thing a caller can say when the lane has stopped accepting. There is no reply surface
    // for an un-sequenced stop (§1.8), so this covers launches only.
    const string ShutdownRefusedLaunchReason =
        "daemon_shutting_down: the daemon is tearing down and did not start this launch";

    // Phase B (D4 §6.4(2a)/(3)): single-flight latches so a slow sweep (each survivor consumes a
    // ~5s TERM grace sequentially) can't overlap itself when the next heartbeat tick fires — otherwise
    // sweeps accumulate, double-signal, and re-scan /proc concurrently. A tick whose prior sweep is still
    // running is simply skipped. 0 = idle, 1 = running (Interlocked-gated).
    int _orphanSweepRunning;
    int _quarantineSweepRunning;
    readonly DaemonConfig                                      _config;
    readonly ConfigRoot                                        _configRoot;
    readonly UserHome                                          _home;
    // The vendors this daemon sees, resolved once for its lifetime: an override cannot change under
    // a running process, and the inventory refresh would otherwise re-resolve all nine per TTL.
    readonly HarnessRegistry                                   _harnesses;
    readonly ServerConnection                                  _server;
    readonly WorktreeManager                                   _worktreeManager;
    readonly RepoMatcher                                       _repoMatcher;
    readonly IPtyProcessFactory                                _ptyFactory;
    readonly IHttpClientFactory                                _httpClientFactory;
    readonly ICapacitorHttpClient                              _http;
    readonly TokenStore                                        _tokens;
    readonly LocalPermissionBridge                             _permissionBridge;
    readonly PermissionPromptBroker                            _permissionBroker;
    readonly IReadOnlyDictionary<string, IHostedAgentLauncher> _launchers;
    readonly IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> _runtimeFactories;
    readonly ILogger<AgentOrchestrator>                        _logger;

    /// <summary>Serialises + coalesces the background capability refresh fired after a certification
    /// rejection. See SingleFlightRefresh for why bare fire-and-forget was unsafe here.</summary>
    readonly SingleFlightRefresh _capabilityRefresh = new();

    // Hosted-agent PTYs are spawned at a fixed size and never resized. The daemon
    // reports these dims to the server right after the agent registers (and on
    // reconnect) so the read-only viewers (web/desktop xterm) lock to exactly the
    // width Claude drew for — otherwise the viewer auto-fits its panel and the
    // mismatched columns garble the TUI. PtyDefaults is the single source
    // of truth, shared with IPtyProcessFactory.Spawn's defaults so they can't drift.
    const ushort HostedPtyCols = PtyDefaults.Cols;
    const ushort HostedPtyRows = PtyDefaults.Rows;

    /// <summary>The per-agent heartbeat/reap sweep period. Named rather than inlined because
    /// <see cref="ReapClaimGateWait"/> is sized against it — that relation is asserted by a test, so a
    /// change here cannot silently weaken the one-claim-waiter-per-agent property.</summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    readonly PeriodicTimer _heartbeatTimer = new(HeartbeatInterval);

    // heartbeat tightened from 60 s SendAsync to round-trip Ping.
    // tick halved (15 → 7 s) and deadline halved (10 → 5 s) so a
    // displaced-slot mismatch or a hung transport is caught within ~10 s
    // instead of ~25 s. This is independent of SignalR's transport timeout
    // (which stays at the 30 s default) — the heartbeat is the daemon's
    // application-level liveness probe.
    readonly PeriodicTimer _daemonHeartbeat = new(TimeSpan.FromSeconds(7));

    static readonly TimeSpan PingDeadline = TimeSpan.FromSeconds(5);

    // proactively refresh the active profile's auth token ahead of expiry so a
    // continuously-running daemon keeps a WorkOS sliding-inactivity session alive (up to its
    // absolute lifetime) rather than forcing a `kcap login` after an idle period. The tick is
    // cheap (a token-file read + expiry compare) and only calls the refresh endpoint when the
    // token is within ProactiveRefreshWindow of expiry; TokenRefreshLoop further rate-limits
    // attempts to at most one per ProactiveRefreshMinInterval, so refresh traffic stays bounded
    // even for a failing refresh or a short-lived token that keeps re-entering the window.
    readonly PeriodicTimer _tokenRefresh = new(TimeSpan.FromSeconds(60));

    // Task 12: periodic sweep of the cross-vendor lifecycle + transcript spools. Covers
    // backlogs left behind by vendors whose session-end never fires another `kcap` hook process
    // (Kiro/OpenCode watcher-owned session-end, Antigravity/Codex-desktop GUI idle/parent-exit) —
    // see SpoolDrainLoop's doc comment. 60s mirrors the reaper-style cadence of the other timers;
    // the drain's own per-tick budget keeps a slow/unreachable server from stalling the daemon.
    readonly PeriodicTimer _spoolDrain = new(TimeSpan.FromSeconds(60));

    // Title resolution ladder (native transcript title → server title → one local generation).
    // 60s: a title is display convenience — the lanes it drives are either cheap (a transcript
    // scan) or explicitly rate-limited by TitleResolveLoop itself.
    readonly PeriodicTimer _titleResolve = new(TimeSpan.FromSeconds(60));

    // Refresh once the token is within this much of its expiry. Comfortably above the 60 s tick
    // so the window is never stepped over.
    static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromMinutes(5);

    // Hit the refresh endpoint at most once per this interval (see TokenRefreshLoop). Small
    // enough that a healthy token issued with a short lifetime is still renewed before it
    // lapses during idle; large enough that a dead/rotated refresh token isn't re-hit every
    // tick.
    static readonly TimeSpan ProactiveRefreshMinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long <see cref="FinalizeAgentRunAsync"/> waits on the (reconnect-retrying)
    /// EndAgentSession call before proceeding with local cleanup regardless. Covers a
    /// typical transient SignalR blip with margin; a longer outage proceeds to cleanup
    /// while the retry continues in the background. Settable so tests don't wait 30s.
    /// </summary>
    internal TimeSpan EndAgentSessionBudget { get; set; } = TimeSpan.FromSeconds(30);

    // Linked to IHostApplicationLifetime.ApplicationStopping so the shutdown gate
    // trips as soon as the host begins stopping — the same instant ServerConnection's
    // _ct (also ApplicationStopping) cancels SignalR calls. Otherwise there's a
    // window between ApplicationStopping firing and DisposeAsync running where
    // server calls would still throw TaskCanceledException unguarded.
    readonly CancellationTokenSource _shutdownCts;
    readonly LaunchConsentGate _consentGate;

    /// <summary>Guards <see cref="DisposeAsync"/> so its body runs exactly once — the DI
    /// container tracks this singleton AND <c>DaemonRunner</c> disposes it explicitly, so
    /// DisposeAsync runs twice by construction on every shutdown. Without the guard, disposing
    /// <see cref="_shutdownCts"/> would make the second pass throw ObjectDisposedException into
    /// DI teardown (NativeAOT: unhandled → abort()).</summary>
    int _disposeOnce;

    /// <summary>Counts entries into the <see cref="DisposeAsync"/> body (post-guard). Test seam:
    /// proves durably that a second dispose did NOT re-enter the body.</summary>
    int _disposeBodyRuns;

    internal int DisposeBodyRuns => Volatile.Read(ref _disposeBodyRuns);

    /// <summary>Test seam: the shutdown CTS, so tests can assert it ends cancelled AND disposed
    /// after the first dispose pass (removal of the Dispose call fails the suite).</summary>
    internal CancellationTokenSource ShutdownCtsForTests => _shutdownCts;

    public AgentOrchestrator(
            DaemonConfig                                      config,
            ConfigRoot                                        configRoot,
            UserHome                                          home,
            ServerConnection                                  server,
            WorktreeManager                                   worktreeManager,
            RepoMatcher                                       repoMatcher,
            IPtyProcessFactory                                ptyFactory,
            IHttpClientFactory                                httpClientFactory,
            ICapacitorHttpClient                              http,
            TokenStore                                       tokens,
            LocalPermissionBridge                             permissionBridge,
            IReadOnlyDictionary<string, IHostedAgentLauncher> launchers,
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories,
            IHostApplicationLifetime                          lifetime,
            ILogger<AgentOrchestrator>                        logger,
            LaunchConsentGate                                 consentGate,
            // §3.3 test-only: leave the sequenced processor unpublished so a test can drive the
            // pre-settlement inline arm and the publication barrier explicitly (see
            // PublishSequencedProcessorForTest). Production ALWAYS publishes here, before any handler is
            // wired, so the null window this exposes never exists in a running daemon.
            bool                                              deferProcessorPublication = false,
            // Null in every pre-existing construction site — those daemons just get a private
            // notifier nobody subscribes to. DaemonRunner passes the DI-registered singleton so a
            // StatusSubscribe waiter sees every mutation this orchestrator makes.
            DaemonStatusNotifier?                             statusNotifier = null,
            // Null in every pre-existing construction site — those get a private broker nobody else
            // reaches. DaemonRunner passes the DI-registered singleton so the permission bridge and
            // local IPC attribute against the same pending-request set this orchestrator withdraws from.
            PermissionPromptBroker?                           permissionBroker = null,
            // Null in every pre-existing construction site — those launches carry no policy snapshot
            // at all. DaemonRunner's bare AddSingleton lets DI fill this in production.
            PolicySnapshotProvider?                           policySnapshots = null
        ) {
        _shutdownCts       = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _config            = config;
        _configRoot        = configRoot;
        _home              = home;
        _tokens            = tokens;
        _harnesses         = HarnessRegistry.FromEnvironment(home);
        _server            = server;
        _worktreeManager   = worktreeManager;
        _repoMatcher       = repoMatcher;
        _ptyFactory        = ptyFactory;
        _httpClientFactory = httpClientFactory;
        _http              = http;
        _permissionBridge  = permissionBridge;
        _permissionBroker  = permissionBroker ?? new();
        _launchers         = launchers;
        _runtimeFactories  = runtimeFactories;
        _logger            = logger;
        _consentGate       = consentGate;
        _statusNotifier    = statusNotifier ?? new();
        _policySnapshots   = policySnapshots;

        // Phase B (D4): per-daemon PID-record store + this daemon's logical id + boot epoch.
        // Records live under "{stateDir}/{name}/agents" so they are unambiguously THIS daemon's own
        // (the startup reap only touches its own leftovers). DaemonId is a stable per-name identity;
        // DaemonEpoch is fresh per boot so the env-marker scan can tell a prior incarnation's
        // survivors from the current incarnation's live children.
        var recordRoot = config.Store.StateDirectory(config.Name);
        _pidRecordRoot = recordRoot;
        _pidRecords  = new AgentPidRecordStore(recordRoot, logger);
        _failedLaunchLog = new FailedLaunchLog(recordRoot);
        _quarantine  = new AgentKillQuarantine(logger);
        _daemonId    = ComputeDaemonId(config.Name);
        _daemonEpoch = config.DaemonEpoch ?? Guid.NewGuid().ToString("N");
        _recordlessSurvivorsImpossible = config.RecordlessSurvivorsImpossible;

        // Phase B2-b (sequenced-settlement design §4.2.4): the resolved-candidates ledger shares the PID
        // record root so all durable per-daemon state lives together. The OrphanReaper's record-pass
        // callback (Hook A) emits into it before the source delete; the drain (Hook B) and StopAgent
        // fallback (Hook C) below emit directly. All three are append-before-delete + idempotent.
        _resolvedLedger = new ResolvedCandidatesLedger(recordRoot, logger);
        // Phase B2-b (sequenced-settlement design §4.2.4): the marker-candidate source store (Hook D)
        // shares the same record root. A recordless survivor resolves with a NULL flow (env untrusted);
        // a co-existing durable record's TRUSTED flow is routed through onRecordResolved by EmitAndClear.
        _markerCandidates = new MarkerCandidateStore(recordRoot, logger);
        _orphanReaper = new OrphanReaper(_pidRecords, _daemonId, _daemonEpoch, logger,
            onRecordResolved: (a, e, fr, role) => _resolvedLedger?.Upsert(a, e, fr, role),
            markerStore: _markerCandidates,
            onMarkerResolved: (a, e) => _resolvedLedger?.Upsert(a, e, null, null));

        // Phase B2-b (sequenced-settlement design §4.2.2) + §3.3: publish the epoch-scoped processor
        // BEFORE any handler is wired, so no command can ever observe a null one in production.
        if (!deferProcessorPublication) PublishSequencedProcessor();

        // Wire up server commands
        _server.OnLaunchAgent            += HandleLaunchAgent;
        // §3.3 (one execution domain): the un-sequenced legacy stop is committed onto the same serial lane
        // as everything else rather than executed inline on the pump. Internal reaping and local-socket
        // stops deliberately keep calling HandleStopAgent directly (§1.11) — routing them through the lane
        // would let a parked consent prompt delay reviewer reaping, the exact inversion of its purpose.
        _server.OnStopAgent              += HandleUnsequencedStopAgent;
        _server.OnSendInput              += HandleSendInput;
        _server.OnSendSpecialKey         += HandleSendSpecialKey;
        _server.OnResizeTerminal         += HandleResizeTerminal;
        _server.ReRegisterAgentsHook          =  ReRegisterAgentsAsync;
        // Settlement lost-ack redelivery (D1): re-deliver unretired terminal acks POST-registration
        // (readiness restored) — inside ReRegisterAgentsHook the CommandAckAsync IsReady gate would drop
        // them. Fires on every (re)connect + heartbeat re-register.
        _server.OnRegisteredHook              =  () => { Processor?.RedeliverUnretiredProcessedAcks(); return Task.CompletedTask; };
        _server.FindRepoForRemoteHandler      =  HandleFindRepoForRemote;
        _permissionBridge.AttributeHandler    =  HandleAttributePermission;
        _server.ProbeBorrowSourceHandler      =  HandleProbeBorrowSource;
        // Task 8: the side-effect-free reviewer-model preflight. Pure resolution over the
        // advertised resolvers — no subprocess/worktree/config side effects.
        _server.ResolveReviewerModelHandler   =  req => Task.FromResult(HandleResolveReviewerModel(req));

        // Phase B2-b (sequenced-settlement design §4.2.4): the server prunes the resolved-candidates
        // ledger per-entry via AckResolvedCandidates (synchronous void handler); the connect payload
        // re-advertises the un-acked snapshot alongside the periodic DaemonStatusReport.
        _server.OnAckResolvedCandidates       += HandleAckResolvedCandidates;
        _server.GetResolvedStartupCandidates  =  () => [.. _resolvedLedger?.Snapshot() ?? []];
        // Phase B2-b (sequenced-settlement design §5.5): advertise the ledger's monotonic high-water on
        // the connect payload (BuildStatusReport carries it on the periodic self-report) so the server
        // learns the generation frontier even after sparse acks prune entries.
        _server.GetHighestResolutionGeneration =  () => _resolvedLedger?.HighestResolutionGeneration;

        // Phase B2-b (sequenced-settlement design): mirror the per-platform startup-completeness signals
        // into the DaemonConnect payload (the periodic DaemonStatusReport carries them via
        // BuildStatusReport). Additive/inert until the paired server PR consumes them; finalized
        // alongside the sequenced counters in a later task.
        _server.GetStartupReapComplete         =  ComputeStartupReapComplete;
        _server.GetUnresolvedStartupCandidates =  () => [.. _orphanReaper?.BlockedCandidates() ?? []];
        _server.GetStartupDiscovery            =  () => _orphanReaper?.CurrentDiscovery;

        // Phase B2-b (sequenced-settlement design §4.2.2): route the sequenced-command receive seams and
        // mirror the watermark counters + kill-quarantine snapshot onto the connect payload. StopAgentV2
        // goes through the processor's serial lane; AckProcessedPrefix retires identity-cache entries;
        // RequestStatusReport is answered by an immediate out-of-band DaemonStatusReport.
        _server.OnStopAgentV2          += HandleStopAgentV2;
        _server.OnAckProcessedPrefix   += ack => _processor?.AckPrefix(ack);
        _server.OnRequestStatusReport  += () => SendDaemonStatusReportOnceAsync();
        _server.OnRequestStatusReport2 += nonce => SendDaemonStatusReportOnceAsync(nonce);
        _server.GetHighestAcceptedSeq  =  () => _processor?.HighestAcceptedSeq;
        _server.GetLastProcessedSeq    =  () => _processor?.LastProcessedSeq;
        _server.GetQuarantined         =  () => [.. QuarantineSnapshot()];
        // Phase B2-b (sequenced-settlement design): the DaemonConnect epoch reads THIS orchestrator's
        // per-boot _daemonEpoch (the same source the processor is scoped to), so the advertised epoch
        // can't diverge from it even if config.DaemonEpoch were left unpinned (tests).
        _server.GetDaemonEpoch         =  () => _daemonEpoch;

        _server.GetLiveAgentIds = () => [
            .. _agents
                .Where(kvp => (kvp.Value.Status is "Starting" or "Running") && !kvp.Value.IsPrivate)
                .Select(kvp => kvp.Key)
        ];

        // Phase B (D2): richer live-agent metadata (kind + flow identity) alongside the ids.
        _server.GetLiveAgents = () => [.. BuildLiveAgents()];

        // Start heartbeat loops
        _ = RunHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunDaemonHeartbeatLoopAsync(_shutdownCts.Token);
        _ = RunTokenRefreshLoopAsync(_shutdownCts.Token);
        _ = RunSpoolDrainLoopAsync(_shutdownCts.Token);
        _ = RunTitleResolveLoopAsync(_shutdownCts.Token);
        _ = RunDaemonStatusReportLoopAsync(_shutdownCts.Token); // Phase B (D2): periodic self-report
    }

    /// <summary>§3.3: the published processor, snapshotted under the transition lock so no command handler
    /// can act on a stale null (which for a sequenced command would fail it closed as a malformed tuple).</summary>
    SequencedCommandProcessor? Processor { get { lock (_domainLock) return _processor; } }

    /// <summary>§3.3: build and publish the sequenced processor. Runs exactly once — the daemon epoch is a
    /// per-boot GUID pinned before services are built, so this is a single null-&gt;live transition with no
    /// replacement or reset case. The start barrier is created BEFORE the processor (its lane awaits it),
    /// and completed only once any inline slot reserved by a handler that saw null has drained: that is
    /// what makes "the lane cannot begin while a reserved inline item exists" a mechanism rather than a
    /// claim.</summary>
    void PublishSequencedProcessor() {
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // ReadLiveness gives it the confirmed-death-precedence liveness read a duplicate CommandAck needs;
        // IsKnownStopTarget is the §3.3 un-sequenced stop-admission probe; the two server sends are its
        // ack/reject channels.
        var processor = new SequencedCommandProcessor(
            _daemonEpoch, ReadLiveness, _server.CommandAckAsync, _server.CommandRejectedAsync, _logger,
            isKnownStopTarget: IsKnownStopTarget, startBarrier: startGate.Task);

        Task? inlineDrained;
        lock (_domainLock) {
            _processor = processor;
            inlineDrained = _inlineDrained?.Task; // the reservation, if a null-snapshot handler is mid-flight
        }

        if (inlineDrained is null) { startGate.SetResult(); return; }

        _ = inlineDrained.ContinueWith(_ => startGate.TrySetResult(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>§3.3 stop-admission probe: whether this id is a real un-sequenced stop target OUTSIDE the
    /// processor's own in-flight-launch set. Called INSIDE the processor's critical section, so it must stay
    /// cheap and non-blocking — a registry hit (lock-free) answers the overwhelmingly common case, and only
    /// a miss reaches the single existence check on the durable PID record.
    ///
    /// <para>The PID-record arm is load-bearing, not belt-and-braces: <see cref="HandleStopAgent"/> falls
    /// back to <see cref="TryStopByPidRecordAsync"/> for an id this incarnation never registered, which is
    /// how the server's registry-independent physical stop reaps a prior incarnation's survivor. Such a stop
    /// is NOT the no-op that justifies dropping unknown targets, so admission has to see it.</para></summary>
    bool IsKnownStopTarget(string agentId) =>
        !string.IsNullOrEmpty(agentId) && (_agents.ContainsKey(agentId) || (_pidRecords?.Exists(agentId) ?? false));

    /// <summary>§3.3: snapshot the processor and, when it is still null, RESERVE the inline slot in the SAME
    /// critical section publication uses. Returns null to mean "run inline, then call
    /// <see cref="ReleaseInlineSlot"/>" — the release is the caller's obligation in a <c>finally</c>.</summary>
    SequencedCommandProcessor? SnapshotProcessorReservingInlineSlot() {
        lock (_domainLock) {
            if (_processor is { } live) return live;

            _inlineInFlight++;
            _inlineDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return null;
        }
    }

    /// <summary>§3.3: release a reserved inline slot. Counted, so concurrent inline handlers (the pump
    /// serializes them today, but nothing here depends on that) all have to finish before the lane's first
    /// item may run.</summary>
    void ReleaseInlineSlot() {
        lock (_domainLock) {
            if (--_inlineInFlight > 0) return;

            var drained = _inlineDrained;
            _inlineDrained = null;
            drained?.TrySetResult();
        }
    }

    internal int ActiveCount => _agents.Count(a => a.Value.Status is "Starting" or "Running");

    /// Mutation FIRST, Pulse() second — always (a pulse published before its mutation lets a
    /// subscriber read the new version, snapshot the OLD state, and wait forever). These
    /// helpers are the only writers of agent status and registry membership, so the ordering
    /// cannot be forgotten at a call site.
    internal void SetAgentStatus(AgentInstance agent, string status) {
        agent.Status = status;
        _statusNotifier.Pulse();
    }

    internal void PublishAgent(AgentInstance agent) {
        _agents[agent.Id] = agent;
        _statusNotifier.Pulse();
    }

    internal void UnpublishAgent(string agentId) {
        _agents.TryRemove(agentId, out _);
        _statusNotifier.Pulse();
    }

    internal void SetResolvedTitle(AgentInstance agent, string title) {
        if (agent.ResolvedTitle == title) return;
        agent.ResolvedTitle = title;
        _statusNotifier.Pulse();
    }

    /// The attribution ladder: the payload's agent id (raw, then canonical GUID), the resolved
    /// vendor session id, the worktree path — each rung only on exactly one live match. Live is
    /// "present in _agents"; teardown withdraws whatever was attributed during that window.
    internal AttributedAgent? HandleAttributePermission(PermissionAttribution query) {
        var canonicalSession = PermissionWire.Canonical(query.SessionId);
        if (canonicalSession is null) return null;

        var live = _agents.Values.ToList();

        if (query.AgentId is { Length: > 0 } rawId) {
            var raw = live.Where(a => string.Equals(a.Id, rawId, StringComparison.Ordinal)).ToList();
            if (raw.Count == 1) return new AttributedAgent(raw[0].Id, raw[0].PolicySnapshot);
            if (PermissionWire.Canonical(rawId) is { } canonicalId) {
                var canon = live.Where(a => PermissionWire.Canonical(a.Id) == canonicalId).ToList();
                if (canon.Count == 1) return new AttributedAgent(canon[0].Id, canon[0].PolicySnapshot);
            }
        }

        var bySession = live.Where(a => a.SessionId is { } s && PermissionWire.Canonical(s) == canonicalSession).ToList();
        if (bySession.Count == 1) return new AttributedAgent(bySession[0].Id, bySession[0].PolicySnapshot);

        if (query.Cwd is { Length: > 0 } cwd) {
            var wanted = Path.TrimEndingDirectorySeparator(cwd);
            var byCwd = live.Where(a => string.Equals(
                Path.TrimEndingDirectorySeparator(a.Worktree.Path), wanted, RepoPathStore.PathComparison)).ToList();
            if (byCwd.Count == 1) return new AttributedAgent(byCwd[0].Id, byCwd[0].PolicySnapshot);
        }

        return null;
    }

    internal PermissionPromptBroker PermissionBrokerForTest => _permissionBroker;
    internal void UnpublishAgentForTest(string agentId) => WithdrawAndUnpublish(agentId);

    void WithdrawAndUnpublish(string agentId) {
        _permissionBroker.WithdrawForAgent(agentId);
        UnpublishAgent(agentId);
    }

    /// <summary>Phase B (D3): clock seam so the reviewer-TTL heartbeat check is testable with a
    /// fixed time. Production uses the real UTC clock.</summary>
    internal Func<DateTime> ClockUtc { get; set; } = () => DateTime.UtcNow;

    /// <summary>The ReviewFlow agents the heartbeat should reap now — the daemon's coarse backstop for
    /// a dead or disconnected server. Only Running ReviewFlow agents; pure, so the heartbeat and tests
    /// share one decision.
    ///
    /// <para><b>Decision table</b>, in this order. A <see cref="TimeSpan.Zero"/> config value disables
    /// its own rule. The lifetime cap (<see cref="DaemonConfig.ReviewerMaxLifetime"/>, 6h) is
    /// two-tier: past lifetime + one <see cref="DaemonConfig.ReviewerTurnWedgeCeiling"/> it is
    /// absolute and unfenced (a runaway turn or output stream must stay mortal, and a disabled
    /// wedge ceiling collapses this tier onto the lifetime); in between it selects only a
    /// visibly-at-rest reviewer — a held turn defers it, and so does activity within
    /// <see cref="LifetimeReapQuietWindow"/>, because reaping mid-round burns the dispatched round
    /// plus the reviewer's context, and a just-delivered round may not have flagged its turn yet
    /// (the busy signal is asynchronous — e.g. Pi's <c>agent_start</c>). The wedge rule
    /// (<c>turn_wedged</c>) and the idle rule (<c>reviewer_idle_expired</c>) are unchanged. A PTY
    /// vendor never sets <c>TurnInFlight</c>; mid-round it relies on the quiet window and the
    /// activity fence alone.</para>
    ///
    /// <para><b>The server's <see cref="AgentInstance.InactivityBoundSeconds"/> must never appear in
    /// that table.</b> It is ROUND-SCOPED — the server applies it only while a round is in flight, and
    /// stops the participant itself. This method runs on the heartbeat regardless of round state, so
    /// reading the bound here turns it into a LIFETIME idle rule and reaps a healthy reviewer BETWEEN
    /// rounds while the driver spends twenty minutes fixing round 1's findings. This supersedes spec
    /// decision 6 ("daemon and server enforce one number") for the daemon's idle rule only.</para>
    ///
    /// <para>Idle comes from <see cref="AgentActivityClock"/>, never <see cref="AgentInstance.LastOutputAt"/>
    /// (PTY-only, so it sits frozen at launch for every ACP vendor, degenerating "2h idle" into a hard
    /// 2h cap) and never a <see cref="DateTime"/> delta (a wall-clock jump would reap a healthy
    /// reviewer).</para>
    ///
    /// <para>This is SELECTION, never the decision: the result is a snapshot, and the agent it names
    /// can become active in the very next instant. Each candidate therefore carries the activity
    /// generation it was selected against, which <see cref="TryClaimReapAsync"/> re-validates inside
    /// the per-agent fence.</para></summary>

    /// <summary>§2.7 B6 arm-A: the end/park reason for a resumable reviewer parked (not reaped). This
    /// exact string is BOTH the ack reason sent to the server's <c>ReportParticipantParked</c> AND the
    /// <see cref="AgentInstance.PendingEndReason"/> that tells <see cref="FinalizeAgentRunAsync"/> to
    /// SUPPRESS the hosted session-end — so it must match on both sides. Do not vary it.</summary>
    internal const string ReviewerParkedResumableReason = "reviewer_parked_resumable";

    /// <summary>§2.7 B6 arm-A: the end reason stamped when the server REJECTS a park — the reviewer is
    /// then ended normally (session-end fires) rather than parked, so this must NOT be
    /// <see cref="ReviewerParkedResumableReason"/> (which would suppress that end).</summary>
    const string ReviewerParkRejectedReason = "reviewer_park_rejected";

    /// <summary>§2.7 B6 arm-A resume-capability predicate: true only for an app-server hosted Codex
    /// reviewer, whose <c>thread/start</c> handshake bound a Codex thread id that a later
    /// <c>thread/resume</c> can reopen — the one runtime that reports the app-server transport
    /// (<see cref="Harness.Codex.CodexAppServerHostedAgentRuntime"/>; every other runtime reports the
    /// "pty" default). A PTY / non-Codex reviewer has no resumable thread and never matches. Read off
    /// the transport label rather than type-switching the runtime — the same value the server validates
    /// its launch decision against on registration.</summary>
    static bool IsResumableReviewer(AgentInstance a) =>
        a.RuntimeTransport == Harness.Codex.CodexTransportDecision.AppServer;

    internal IReadOnlyList<ReapCandidate> FindReviewersToReap() {
        var result = new List<ReapCandidate>();

        // IsPrivate is excluded explicitly, matching the stuck-Starting sweep below rather than resting
        // on the (true today, but unenforced) fact that a local `kcap agent start --private` can never
        // carry LaunchKind.ReviewFlow. A private agent has no server-side row and no flow to heal, so
        // a daemon-internal backstop must not act on one.
        foreach (var a in _agents.Values) {
            if (a.Kind != LaunchKind.ReviewFlow || a.Status != "Running" || a.IsPrivate) continue;

            // One snapshot, one lock acquisition: the seq the claim is later fenced on belongs to the
            // same instant as the idle/age/turn fields the rule below decides from.
            var clock = a.ActivityClock.Snapshot();

            if (_config.ReviewerMaxLifetime > TimeSpan.Zero && clock.AgeMs > (ulong) _config.ReviewerMaxLifetime.TotalMilliseconds) {
                var graceMs = _config.ReviewerTurnWedgeCeiling > TimeSpan.Zero
                    ? (ulong) _config.ReviewerTurnWedgeCeiling.TotalMilliseconds
                    : 0UL;

                if (clock.AgeMs > (ulong) _config.ReviewerMaxLifetime.TotalMilliseconds + graceMs) {
                    // The hard ceiling is deliberately NOT activity-fenced: it fires regardless of how
                    // active the reviewer is (that is what "absolute" means here), so a delivery racing
                    // it must not abort it.
                    result.Add(new ReapCandidate(a, "reviewer_ttl_expired", clock.ActivitySeq, FencedOnActivity: false));
                    continue;
                }

                if (!clock.TurnInFlight && clock.IdleForMs > (ulong) LifetimeReapQuietWindow.TotalMilliseconds) {
                    result.Add(new ReapCandidate(a, "reviewer_ttl_expired", clock.ActivitySeq, FencedOnActivity: true));
                    continue;
                }
                // Held turn, or activity inside the quiet window (a delivered round whose busy
                // signal has not landed yet): deferred to the hard ceiling. Fall through so the
                // wedge rule still owns a frozen turn.
            }

            if (clock.TurnInFlight) {
                if (_config.ReviewerTurnWedgeCeiling > TimeSpan.Zero
                 && clock.IdleForMs > (ulong) _config.ReviewerTurnWedgeCeiling.TotalMilliseconds) {
                    result.Add(new ReapCandidate(a, "turn_wedged", clock.ActivitySeq, FencedOnActivity: true));
                }

                continue; // a held turn suppresses the plain idle rule outright
            }

            // §2.7 B6 arm-A: a RESUMABLE hosted reviewer (app-server Codex, whose thread survives a
            // process teardown for a later thread/resume) that has been idle past the SHORT resumable
            // bound — and is not already mid-park — is PARKED rather than reaped: its slot is freed but
            // its Codex thread is kept. Checked here, BEFORE the 2h arm-B idle rule and inside the same
            // !clock.TurnInFlight region, so a resumable reviewer parks at ~10min instead of reaping at
            // 2h. A non-resumable reviewer (PTY / non-Codex) never satisfies the transport gate and
            // falls through to arm-B unchanged.
            //
            // No separate "channel-drained" clause (Task 0): !TurnInFlight already means no active turn,
            // every input enqueue / turn transition / notification advances this same activity clock (so
            // idle past the bound already implies the input queue and transcript are quiescent — the
            // forwarder's unacked buffer flushes within its <=30s retry cadence, far under the bound),
            // and FencedOnActivity: true re-checks at claim that nothing has advanced since selection.
            if (IsResumableReviewer(a) && !a.ParkAttemptInFlight
                && _config.ReviewerResumableIdleTimeout > TimeSpan.Zero
                && clock.IdleForMs > (ulong) _config.ReviewerResumableIdleTimeout.TotalMilliseconds) {
                result.Add(new ReapCandidate(a, ReviewerParkedResumableReason, clock.ActivitySeq, FencedOnActivity: true) { Park = true });
                continue;
            }

            if (_config.ReviewerIdleTimeout > TimeSpan.Zero && clock.IdleForMs > (ulong) _config.ReviewerIdleTimeout.TotalMilliseconds)
                result.Add(new ReapCandidate(a, "reviewer_idle_expired", clock.ActivitySeq, FencedOnActivity: true));
        }

        return result;
    }

    /// <summary>One reap target as SELECTED, plus the evidence <see cref="TryClaimReapAsync"/> revalidates
    /// the selection against inside the per-agent fence.</summary>
    /// <param name="Agent">The instance selected — carried, not just its id, so the claim can prove the
    /// id still resolves to THIS incarnation rather than a relaunch that reused the name.</param>
    /// <param name="Reason">Which rule fired; becomes <see cref="AgentInstance.PendingEndReason"/> on a
    /// won claim, so server-side end attribution names the rule.</param>
    /// <param name="ActivityGeneration"><see cref="AgentActivityClock.ActivitySeq"/> at selection.</param>
    /// <param name="FencedOnActivity">Whether an activity advance since selection ABORTS this reap —
    /// true for the idle and wedge rules (both are "nothing has happened" claims, which a delivery
    /// falsifies), false for the absolute lifetime cap (which holds regardless of activity).</param>
    internal readonly record struct ReapCandidate(
            AgentInstance Agent, string Reason, ulong ActivityGeneration, bool FencedOnActivity) {
        public string Id => Agent.Id;

        /// <summary>§2.7 B6 arm-A discriminator. When true, the heartbeat routes this candidate to the
        /// resumable-PARK path (<see cref="AgentOrchestrator.ParkReviewerAsync"/>) instead of the reap
        /// path: its daemon slot is freed like a reap, but its Codex app-server thread is kept alive
        /// (the hosted session-end is SUPPRESSED) for a later <c>thread/resume</c>. False for every
        /// reap rule (TTL / wedge / idle), which keep flowing to
        /// <see cref="AgentOrchestrator.ReapReviewerAsync"/> unchanged.</summary>
        public bool Park { get; init; }
    }

    // Surface 3: cached machine inventory, recomputed on a 6h in-memory cadence. Deliberately never
    // claims the on-disk nudge throttle stamp — that would starve the hook/CLI nudge surfaces.
    // BuildStatusReport only reads the cache (stays pure); the send path refreshes.
    static readonly TimeSpan HarnessInventoryTtl = TimeSpan.FromHours(6);
    readonly object _harnessInventoryGate = new();
    Capacitor.Cli.Core.Setup.HarnessInventory? _harnessInventory;
    DateTimeOffset _harnessInventoryEvaluatedAt;

    /// <summary>Recomputes the cached harness inventory if it's never been evaluated or is older than
    /// <see cref="HarnessInventoryTtl"/>. Single-flight: the whole check-evaluate-publish runs under
    /// the gate, so overlapping report calls (periodic loop, OnRequestStatusReport, launch-stage,
    /// delivered-input) can't each probe, and a slower probe can't overwrite a newer one and reset the
    /// TTL to stale content. The evaluation is a handful of dir/PATH stats + a small JSON read, so
    /// holding the gate across it is cheap. Never throws, and a probe failure does NOT advance the
    /// timestamp — it retries on the next report rather than waiting a full TTL.</summary>
    void RefreshHarnessInventoryIfStale() {
        lock (_harnessInventoryGate) {
            if (_harnessInventory is not null &&
                DateTimeOffset.UtcNow - _harnessInventoryEvaluatedAt < HarnessInventoryTtl) return;
            try {
                _harnessInventory = HarnessInventory.EvaluateCurrent(_configRoot, _harnesses);
            } catch (Exception ex) {
                // Keep the last cached value (or null); inventory must never break the report path.
                _logger.LogDebug(ex, "Harness inventory evaluation failed — keeping last cached");
            }
            // Advance on success AND failure: a persistently-failing environment (e.g. a read-only
            // config dir) then backs off to the TTL instead of re-probing on every 60s send. The
            // evaluation's sub-probes are already defensive, so a throw here is rare/environmental.
            _harnessInventoryEvaluatedAt = DateTimeOffset.UtcNow;
        }
    }

    Capacitor.Cli.Core.Setup.HarnessInventory? CurrentHarnessInventory() {
        lock (_harnessInventoryGate) return _harnessInventory;
    }

    /// <summary>Phase B (D2): the daemon's self-report snapshot — its authoritative
    /// <see cref="ActiveCount"/> plus the live-agent metadata (and, once D4/Task 8 lands, the
    /// kill-quarantine). Pure; the send loop + tests share it.</summary>
    internal DaemonStatusReport BuildStatusReport(string? echoNonce = null) =>
        new(ActiveCount, [.. BuildLiveAgents()], [.. QuarantineSnapshot()],
            // Phase B2-b (sequenced-settlement design §4.2.4): re-advertise the durable resolved-
            // candidates ledger on every self-report until the server prunes it per-entry via
            // AckResolvedCandidates. Epoch is the shipped per-boot _daemonEpoch.
            Epoch: _daemonEpoch,
            // Phase B2-b (sequenced-settlement design §4.2.2): the sequenced-command watermark counters
            // from the processor (LastProcessedSeq = contiguous terminal prefix; HighestAcceptedSeq =
            // highest accepted). Null before the processor exists; 0 on a fresh epoch (nothing accepted).
            LastProcessedSeq: _processor?.LastProcessedSeq,
            HighestAcceptedSeq: _processor?.HighestAcceptedSeq,
            // Phase B2-b (sequenced-settlement design): the per-platform startup-completeness signals.
            // StartupReapComplete is a computed roll-up; UnresolvedStartupCandidates always lists the
            // blocked known-id set so a completion-false report carries its reason; StartupDiscovery
            // surfaces the recordless-survivor marker-scan state (Pending/Complete/Failed on Linux,
            // NotApplicable off it). Additive/inert until the paired server PR consumes them.
            StartupReapComplete: ComputeStartupReapComplete(),
            ResolvedStartupCandidates: [.. _resolvedLedger?.Snapshot() ?? []],
            UnresolvedStartupCandidates: [.. _orphanReaper?.BlockedCandidates() ?? []],
            StartupDiscovery: _orphanReaper?.CurrentDiscovery,
            // Phase B2-b (sequenced-settlement design §5.5): the resolved-candidates ledger's monotonic
            // high-water, so once sparse acks prune entries the server still knows the generation frontier.
            HighestResolutionGeneration: _resolvedLedger?.HighestResolutionGeneration,
            // Surface 3 (new-harness detection): the last cached machine inventory (null until the first
            // send refreshes it); the server raises the "installed but not configured" notification from it.
            HarnessInventory: CurrentHarnessInventory(),
            EchoNonce: echoNonce);

    /// <summary>Phase B2-b (sequenced-settlement design): the per-platform startup-reap-complete
    /// roll-up. A blocked known-id candidate (pending_marker / legacy_unresolvable /
    /// identity_unresolvable) always keeps it false. Otherwise completion is platform-specific:
    /// <list type="bullet">
    /// <item><b>Linux</b> — needs BOTH no blocked candidates AND one clean env-marker-scan pass
    /// (<see cref="MarkerScanState.Complete"/>): the scan is the only proof a recordless survivor was
    /// enumerated.</item>
    /// <item><b>Windows with <c>RecordlessSurvivorsImpossible</c></b> — trivially complete once the
    /// record pass leaves nothing blocked (the boot-chain attestation proves no recordless class
    /// exists).</item>
    /// <item><b>pre-W1 Windows + macOS</b> — record-pass-only completion (a weakened proof: there is no
    /// scan and no boot-chain guarantee, so "no blocked record-tracked candidates" is the best available
    /// signal).</item>
    /// </list></summary>
    internal bool ComputeStartupReapComplete() {
        var blocked = _orphanReaper?.BlockedCandidates().Count ?? 0;
        if (blocked > 0) return false;
        if (OperatingSystem.IsLinux())
            return _orphanReaper?.CurrentDiscovery.MarkerScanState == MarkerScanState.Complete;
        if (OperatingSystem.IsWindows() && _recordlessSurvivorsImpossible) return true;
        return true; // pre-W1 Windows / macOS: record-pass-only completion (no blocked record-tracked candidates)
    }

    /// <summary>Phase B2-b (sequenced-settlement design §5.5/§4.2.2): the single lifecycle-state read
    /// (confirmed-death precedence Live &gt; Quarantined &gt; Dead) over the same collections
    /// <see cref="CleanupAgentAsync"/> + <see cref="AgentKillQuarantine"/> mutate. The design mandates that a
    /// duplicate CommandAck's CurrentState be read so a teardown racing the read can NEVER surface a transient
    /// false Dead. This read is lock-free (it does not take the per-agent lifecycle lock) and is SOUND ONLY
    /// BECAUSE OF THE SHIPPED CleanupAgentAsync ORDERING INVARIANT: the confirmed-death teardown adds the
    /// surviving child to <c>_quarantine</c> BEFORE removing it from <c>_agents</c> (AgentOrchestrator.cs —
    /// "Add to quarantine BEFORE removing from _agents so EffectiveCount never dips"), so an agent is
    /// CONTINUOUSLY present in <c>_agents ∪ _quarantine</c> from spawn until its quarantine entry is drained
    /// (RetryQuarantineOnceAsync) — there is no window where a live/tearing-down agent is absent from both,
    /// hence no transient false Dead. Dead is returned only after the genuine drain (confirmed death). If that
    /// ordering invariant is ever broken, this must instead take the per-agent lifecycle lock. NotFound
    /// collapses to Dead here (see the appendix note) — both satisfy confirmed-absence.</summary>
    /// <summary>Evaluates the reviewer certification arm-by-arm so a rejection can NAME the one that
    /// failed. Extracted and internal so the arms are unit-testable — the previous inline expression
    /// collapsed four distinguishable conditions into one boolean and one message, and that message
    /// actively misdirected: it reported the certification revision (which matches on every one of
    /// these paths) and told the operator to update a CLI that was usually fine.</summary>
    internal static (bool Ok, string Reason) EvaluateReviewerCertification(
            string vendor, string? probedVersion, string? currentConnectionId,
            ReviewerCertificationRequirement certification) {
        if (!string.Equals(certification.Vendor, vendor, StringComparison.Ordinal))
            return (false, $"the launch is for '{vendor}' but the certification is for '{certification.Vendor}'");

        if (!string.Equals(currentConnectionId, certification.ExpectedDaemonConnectionId, StringComparison.Ordinal))
            return (false, "this daemon reconnected after the certification was issued " +
                           "(connection id changed) — retry the flow");

        if (!string.Equals(certification.RequiredLauncherPolicyVersion,
                DaemonRunner.ClaudeLauncherPolicyVersion, StringComparison.Ordinal))
            return (false, $"the server requires launcher policy '{certification.RequiredLauncherPolicyVersion}' " +
                           $"but this daemon implements '{DaemonRunner.ClaudeLauncherPolicyVersion}' — " +
                           "update kcap and restart the daemon");

        // A NULL advertised version means the registration-time probe failed — a transient condition,
        // not evidence the CLI changed. This arm exists to catch a CLI SWAP between advertisement and
        // launch, and null-vs-value is not a swap. Treating it as one rejected every launch for the
        // daemon's lifetime, and restarting on a loaded host merely re-poisoned the advertisement.
        // A null advertised value falls through to the range check below, which is the real gate.
        // A failed LAUNCH probe is its own condition, classified before the swap arm: otherwise a
        // timeout reads as "the CLI changed, restart", when nothing changed and restarting repeats
        // it. Fails closed either way; only the diagnosis and remedy differ.
        if (string.IsNullOrEmpty(probedVersion))
            return (false, $"could not read the installed {vendor} CLI version (the version probe " +
                           "failed or timed out) — this is usually transient under load; retry the flow");

        // Range BEFORE swap: an out-of-range replacement would otherwise be told only to restart,
        // and restarting re-advertises the same out-of-range version.
        if (!DaemonRunner.CliVersionAllowed(probedVersion, certification.AllowedCliRanges))
            return (false, $"the installed {vendor} CLI '{probedVersion}' is outside the " +
                           $"server's allowed range '{certification.AllowedCliRanges}'");

        // A missing advertised version means the registration probe failed — not evidence of a CLI
        // swap, which is all this arm exists to catch. It falls through to the range check above.
        // Null or empty: declared non-nullable, but populated from a probe returning null over JSON.
        if (!string.IsNullOrEmpty(certification.ExpectedCliVersion) &&
            !string.Equals(probedVersion, certification.ExpectedCliVersion, StringComparison.Ordinal))
            return (false, $"the installed {vendor} CLI is '{probedVersion}' but this daemon " +
                           $"advertised '{certification.ExpectedCliVersion}' at registration — " +
                           "restart the daemon so it re-advertises");

        return (true, "");
    }

    internal AgentLiveness ReadLiveness(string agentId) {
        // Order matters: check _agents first (Live/Quarantined-by-status), then _quarantine, then Dead.
        // The add-to-quarantine-before-remove-from-_agents invariant makes this ordering false-Dead-free.
        if (_agents.TryGetValue(agentId, out var a))
            return a.Status is "Starting" or "Running" ? AgentLiveness.Live : AgentLiveness.Quarantined;
        if (_quarantine?.IsQuarantined(agentId) == true) return AgentLiveness.Quarantined;
        return AgentLiveness.Dead;
    }

    /// <summary>Phase B (D4 §6.4(2a)): the kill-quarantine snapshot for the status report.</summary>
    internal IReadOnlyList<QuarantinedAgentInfo> QuarantineSnapshot() => _quarantine?.Snapshot() ?? [];

    /// <summary>Phase B (D4 §6.4(2a)): the daemon's admission gate — EVERY live registry entry
    /// (not just Starting/Running — a Completed/Failed agent still mid-teardown holds its slot until
    /// CleanupAgentAsync's count-preserving remove) PLUS unconfirmed-death quarantined ones. Using the
    /// full <c>_agents.Count</c> (rather than <see cref="ActiveCount"/>, whose Starting/Running meaning is
    /// the wire contract) keeps a slot reserved across the whole teardown, so a concurrent launch can't
    /// observe a transiently-freed slot and over-admit. A persistent kill/record-write failure shrinks
    /// admission (fails closed) rather than minting processes beyond the budget.</summary>
    internal int EffectiveCount => _agents.Count + (_quarantine?.Count ?? 0);

    /// <summary>Phase B (D4): this daemon's stable logical id = a hash of its name, written
    /// into each child's <c>KCAP_DAEMON_ID</c> marker. Per-name so a different daemon under the same
    /// user is never mistaken for ours by the env-marker scan.</summary>
    static string ComputeDaemonId(string name) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name ?? "")))
        [..16].ToLowerInvariant();

    /// <summary>Phase B (D4 §6.4(2)/(2a)); L1-managed(b) extends this for Unix: capture the child's
    /// EXACT start-identity ONCE, store it on the agent (teardown reuses it — never re-captures a
    /// possibly-recycled pid), and persist the durable PID record FAIL-CLOSED. A write failure (I/O)
    /// or a live-but-unidentifiable child THROWS — caught by the post-insert single-flight cleanup, so
    /// a spawned child we cannot durably track never stays admitted holding capacity. A non-live/
    /// degenerate pid (already gone, or pid&lt;=0) has nothing to track, so it returns cleanly rather
    /// than failing an already-doomed launch.
    ///
    /// <paramref name="capturedStartIdentity"/> is the runtime's own natively-captured identity
    /// (<see cref="IHostedAgentRuntime.StartIdentity"/>) — non-null on Unix (post-L1): the shim
    /// captures (or definitively fails to capture) identity INSIDE pty_spawn, immediately post-fork,
    /// which is the ONLY correct place to read it (the capture-binding rule — a post-hoc re-capture
    /// here could adopt an unrelated process if the pid was already recycled by the time we get here).
    /// Null means the runtime never captures this way (Windows; the ACP runtime has no PTY at all) —
    /// that path falls back to the ORIGINAL post-hoc <see cref="ProcessIdentity.Capture"/>, unchanged
    /// from before this task.</summary>
    void PersistPidRecordOrThrow(AgentInstance agent, int pid, string? capturedStartIdentity) {
        if (_pidRecords is null) return;

        if (capturedStartIdentity is not null) {
            // Unix (post-L1): NEVER re-capture — capturedStartIdentity is already the exact token
            // read by the shim immediately after the child existed. "" is a deliberate,
            // well-formed "identity_unavailable" record (capture was attempted and failed), NOT a
            // launch failure — see UnixPtyProcess's design note for why agent.StartIdentity is set
            // to "" rather than left null: CleanupAgentAsync's teardown check
            // (`agent.StartIdentity is { } startIdentity && ... MatchesTri(pid, startIdentity) != false`)
            // treats "" as permanently uncomparable (MatchesTri returns null — no ':' scheme
            // separator), which is exactly "ambiguity never kills": a still-alive
            // identity-unavailable agent gets quarantined and retried, never silently dropped.
            //
            // M1-A (spec §4.3): the record's identity_kind makes "" a well-formed, distinguishable
            // IdentityUnavailable marker rather than an inferred-from-emptiness convention.
            agent.StartIdentity = capturedStartIdentity; // "" is intentional, not a bug

            var identityKind = capturedStartIdentity.Length == 0
                ? PidIdentityKind.IdentityUnavailable
                : PidIdentityKind.Present;

            _pidRecords.Write(new AgentPidRecord(
                agent.Id, pid, capturedStartIdentity, identityKind, agent.Kind.ToString(), agent.Vendor,
                agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));

            return;
        }

        // Legacy path (Windows / ACP runtimes with no shim-based capture): unchanged behavior.
        var identity = ProcessIdentity.Capture(pid);
        if (identity is null) {
            if (ProcessIdentity.IsAlive(pid))
                throw new InvalidOperationException(
                    $"Could not capture start-identity for live agent {agent.Id} (pid {pid}) — failing launch closed");

            return; // no live capturable process → nothing to record or leak
        }

        agent.StartIdentity = identity;

        // Write throws on I/O failure → propagates → post-insert single-flight cleanup (fail closed):
        // a spawned, capturable child we cannot durably record must not stay admitted without a record.
        // This legacy path only reaches here with a non-null capture (see the guard above) — always Present.
        _pidRecords.Write(new AgentPidRecord(
            agent.Id, pid, identity, PidIdentityKind.Present, agent.Kind.ToString(), agent.Vendor,
            agent.FlowRunId, agent.FlowRole, _daemonId, _daemonEpoch, DateTimeOffset.UtcNow));
    }

    /// <summary>Delete an agent's PID record after its death is confirmed (teardown / confirmed reap).</summary>
    void DeletePidRecord(string agentId) => _pidRecords?.Delete(agentId);

    /// <summary>Test seams (this daemon's PID-record store) so a unit test can seed/inspect records
    /// without a real launch. Never used in production.</summary>
    internal void WritePidRecordForTest(AgentPidRecord record)     => _pidRecords?.Write(record);
    internal IReadOnlyList<AgentPidRecord> PidRecordsForTest()      => _pidRecords?.ReadAll() ?? [];

    /// <summary>Test-only: the per-daemon record root, so a test can build the store a NEXT BOOT would
    /// build over the same state dir (§3.3's shutdown-orphan handoff).</summary>
    internal string PidRecordRootForTest                            => _pidRecordRoot;
    internal string DaemonIdForTest                                 => _daemonId;
    internal string DaemonEpochForTest                             => _daemonEpoch;
    internal bool   RecordlessSurvivorsImpossibleForTest           => _recordlessSurvivorsImpossible;

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): the resolved-candidates ledger's
    /// un-acked snapshot, so a test can assert the confirmed-gone hooks (quarantine drain / StopAgent
    /// fallback / record pass) emitted positive per-id death evidence. Never used in production.</summary>
    internal IReadOnlyList<ResolvedStartupCandidate> ResolvedLedgerSnapshotForTest => _resolvedLedger?.Snapshot() ?? [];

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): honor the server's per-entry
    /// AckResolvedCandidates prune (sparse, deliver-once). SYNCHRONOUS — the ledger's <c>Ack</c> is a
    /// synchronous void, so this stays void (a <c>void</c> event/receive seam is never awaited).</summary>
    internal void HandleAckResolvedCandidates(AckResolvedCandidates ack) => _resolvedLedger?.Ack(ack.Entries ?? []);

    /// <summary>Test seam: seed a resolved-candidate ledger entry so a test can drive the advertise/ack
    /// prune path without a real confirmed-death hook. Never used in production.</summary>
    internal ResolvedStartupCandidate SeedResolvedCandidateForTest(string agentId, string oldEpoch)
        => _resolvedLedger!.Upsert(agentId, oldEpoch, null, null);

    /// <summary>Test seam: route an ack through the SYNCHRONOUS <see cref="HandleAckResolvedCandidates"/>
    /// handler (no await — the ledger Ack is void). Never used in production.</summary>
    internal void HandleAckResolvedCandidatesForTest(AckResolvedCandidates ack) => HandleAckResolvedCandidates(ack);

    /// <summary>Test seam: seed a kill-quarantine entry so a test can drive the drain hook without a
    /// real launch+teardown. Mirrors <see cref="WritePidRecordForTest"/>; never used in production.</summary>
    internal void QuarantineForTest(AgentKillQuarantine.Entry entry) => _quarantine?.Add(entry);

    /// <summary>Phase B2-b (sequenced-settlement design): test seam — persist a marker-candidate source
    /// so a test can assert it surfaces as a <c>pending_marker</c> blocked candidate (keeping
    /// <see cref="ComputeStartupReapComplete"/> false) without a real recordless survivor. The pid is
    /// irrelevant: <see cref="OrphanReaper.BlockedCandidates"/> lists every persisted source WITHOUT a
    /// liveness check, and the assertion reads the blocked surface directly via
    /// <see cref="BuildStatusReport"/> (no scan runs, so the dead pid is never resolved away). Never used
    /// in production.</summary>
    internal void SeedPendingMarkerCandidateForTest(string agentId, string oldEpoch) =>
        _markerCandidates!.Write(new MarkerCandidate(agentId, _daemonId, oldEpoch, 999_999));

    /// <summary>Looks up a persisted PID record by agent id, or null if none exists. Read-only and
    /// policy-free — shared by the reap below and by the local-stop protection check in
    /// AgentOrchestrator.LocalIpc.cs, which decides whether to reap at all before this ever runs.</summary>
    internal AgentPidRecord? FindPidRecord(string agentId) {
        if (_pidRecords is null) return null;

        var record = _pidRecords.ReadAll().FirstOrDefault(r => r.AgentId == agentId);

        return record.AgentId == agentId ? record : null;
    }

    /// <summary>Phase B (D4 §6.4(3) StopAgent fallback): the caller had no in-memory agent for
    /// this id — consult the PID record and, if a live process still matches its EXACT identity (and,
    /// on Unix, carries the expected <c>KCAP_AGENT_ID</c> env — ambiguity spares), reap it by identity
    /// and delete the record on confirmed death. This makes the server's registry-independent S2 stop
    /// effective even against a NEW daemon incarnation that never knew the agent in memory.</summary>
    async Task<bool> TryStopByPidRecordAsync(string agentId) {
        if (FindPidRecord(agentId) is not { } record) return false;

        var confirmedGone = await ProcessReaper.ReapByRecordAsync(record, _logger, _shutdownCts.Token);
        if (confirmedGone) {
            // Phase B2-b (sequenced-settlement design §4.2.4) Hook C: ledger-append the positive per-id
            // death evidence (from the TRUSTED record — its epoch + flow identity) BEFORE deleting the
            // source record. A crash between the two leaves a committed entry + leftover record; the next
            // boot's OrphanReaper record pass re-derives it and Upsert (idempotent on the source-stable
            // (AgentId, OldEpoch) key) collapses onto the committed entry, then completes the delete.
            _resolvedLedger?.Upsert(agentId, record.DaemonEpoch, record.FlowRunId, record.FlowRole);
            _pidRecords?.Delete(agentId); // delete ONLY on confirmed death (spec §6.4(2))
        }

        return confirmedGone;
    }

    /// <summary>Phase B (D4 §6.4(3)): run the startup orphan reap once — called by DaemonRunner
    /// at boot under the daemon lock (next to WorktreeManager.CleanupOrphanedAsync), and re-run on each
    /// heartbeat tick. SINGLE-FLIGHT: if a prior sweep is still running (a long /proc scan + sequential
    /// TERM graces can outlast the 30s heartbeat, and the ctor-started heartbeat can overlap the boot
    /// call), this tick is skipped rather than piling on. Best-effort: a reaper fault is logged and
    /// swallowed, never faulting the caller.</summary>
    internal async Task ReapOrphansOnceAsync(CancellationToken ct = default) {
        if (_orphanReaper is null) return;
        if (Interlocked.CompareExchange(ref _orphanSweepRunning, 1, 0) != 0) return; // a sweep is already in flight

        try { await _orphanReaper.ReapOnceAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "OrphanReaper sweep faulted — continuing"); }
        finally { Interlocked.Exchange(ref _orphanSweepRunning, 0); }
    }

    /// <summary>Phase B (D4 §6.4(2a)): retry the kill-quarantine once — SINGLE-FLIGHT, mirroring
    /// <see cref="ReapOrphansOnceAsync"/>, so a slow retry (each entry a ~5s TERM grace) can't overlap the
    /// next heartbeat tick. Skipped when empty or already running.</summary>
    async Task RetryQuarantineOnceAsync(CancellationToken ct) {
        if (_quarantine is not { Count: > 0 }) return;
        if (Interlocked.CompareExchange(ref _quarantineSweepRunning, 1, 0) != 0) return;

        try {
            // Delete the durable PID record of every agent whose death the retry CONFIRMED — teardown
            // retained it (with the current epoch) while the child was quarantined, so without this it
            // would be skipped by the orphan sweep and leak until the next daemon restart.
            //
            // Phase B2-b (sequenced-settlement design §4.2.4) Hook B: for each drained (confirmed-dead)
            // entry, ledger-append its positive per-id death evidence BEFORE deleting the record. The
            // shipped _quarantine is current-incarnation only, so its entries carry the CURRENT epoch —
            // emit (AgentId, _daemonEpoch, flow…). That same-epoch id is harmless per outbox idempotency
            // (prior-epoch proofs come from the record pass + marker scan) and gives the server id-level
            // absence proof. Append-before-delete: a crash between the two leaves a committed entry + the
            // retained record (its DaemonEpoch == _daemonEpoch), which the next boot's OrphanReaper record
            // pass reconciles on the source-stable (AgentId, OldEpoch) key (single emit) then deletes.
            foreach (var e in await _quarantine.RetryAllAsync(ct)) {
                _resolvedLedger?.Upsert(e.AgentId, _daemonEpoch, e.FlowRunId, e.FlowRole);
                DeletePidRecord(e.AgentId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogWarning(ex, "quarantine retry sweep faulted — continuing"); }
        finally { Interlocked.Exchange(ref _quarantineSweepRunning, 0); }
    }

    /// <summary>Round-dispatch grace: the per-daemon ordering section for status-report build+send.
    ///
    /// <para><b>Invariant.</b> Wire content must be non-decreasing in per-agent
    /// <see cref="AgentActivityClock.ActivitySeq"/> — the server's flow-participant fold treats ANY
    /// regression as permanent corruption (latches "regressed", disables that agent's liveness
    /// supervision for good). <see cref="BuildStatusReport"/> runs inside this section, and the hub-send
    /// is INVOKED inside it too, so invocation order — and with it SignalR's per-connection wire order
    /// — is fixed by acquisition order regardless of how long any one send takes to finish (see
    /// <see cref="SendDaemonStatusReportOnceAsync"/>'s bounded wait).</para>
    ///
    /// <para><b>Rule.</b> EVERY status-report emission site MUST route through
    /// <see cref="SendDaemonStatusReportOnceAsync"/>; a direct <c>_server.DaemonStatusReportAsync</c>
    /// call bypasses the ordering and can regress the fold.</para>
    ///
    /// <para><b>Lock order.</b> ordering → clock (via <see cref="BuildStatusReport"/>). MUST NEVER be
    /// acquired while a per-agent <see cref="AgentInstance.BorrowedSnapshotGate"/> is held —
    /// <see cref="HandleSendInput"/> offloads its emission for exactly this reason.</para>
    ///
    /// <para>Never disposed: emissions are fire-and-forget, so a waiter parked here during teardown
    /// must not fault on an <see cref="ObjectDisposedException"/> nobody observes.</para>
    ///
    /// See docs/superpowers/specs/2026-08-10-ai1842-round-dispatch-grace-design.md (kcap-server) for
    /// rationale.</summary>
    readonly SemaphoreSlim _statusReportOrderingGate = new(1, 1);

    /// <summary>Bound on <see cref="SendDaemonStatusReportOnceAsync"/>'s in-gate wait for the hub send
    /// to complete. A parked send (dead/degraded connection) must not hold every other emission behind
    /// it forever; see that method for why releasing the WAIT here is safe. Settable so tests don't
    /// wait the real 30s.</summary>
    internal TimeSpan StatusReportSendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Phase B (D2): build + send one status report, one-way, swallowing errors (an old
    /// server has no handler). Snapshot build AND the send's INVOCATION happen inside
    /// <see cref="_statusReportOrderingGate"/> — see that field for the ordering invariant. The WAIT for
    /// completion is bounded by <see cref="StatusReportSendTimeout"/> and released on timeout without
    /// waiting further: invocation order, not completion order, is what the invariant needs, so
    /// dropping the wait here is safe while dropping the invocation itself out from under the gate
    /// would not be.</summary>
    internal async Task SendDaemonStatusReportOnceAsync(string? echoNonce = null) {
        // Surface 3: refresh the machine inventory before building the report. Cheap and self-throttled
        // (recomputes at most once per 6h); the first send is the effective startup evaluation.
        RefreshHarnessInventoryIfStale();

        try {
            await _statusReportOrderingGate.WaitAsync(_shutdownCts.Token);
        } catch (OperationCanceledException) {
            return; // shutdown must not strand a waiter behind a gate nobody will release
        }

        try {
            // BuildStatusReport() is evaluated INSIDE the section, not passed in from outside it —
            // a snapshot captured before acquisition is exactly the stale content the section exists
            // to keep off the wire. The invocation itself is inside this try too (not just the await):
            // a double that throws synchronously instead of returning a faulted Task must be swallowed
            // the same as an awaited failure.
            Task? send = null;
            try {
                send = _server.DaemonStatusReportAsync(BuildStatusReport(echoNonce));
                await send.WaitAsync(StatusReportSendTimeout, _shutdownCts.Token);
            } catch (TimeoutException) {
                // The invocation already happened under the gate (see the gate's own doc), so wire
                // FIFO on this single connection still holds even though we stop waiting here.
                LogStatusReportSendTimedOut(StatusReportSendTimeout.TotalSeconds);
                ObserveAbandonedStatusReportSend(send!);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "DaemonStatusReport send failed — ignoring");
            }
        } finally {
            _statusReportOrderingGate.Release();
        }

        // Settlement lost-ack redelivery (D1): after advertising the watermarks, re-deliver any UNRETIRED terminal acks — a lost
        // ack in a reconnect window is re-elicited here instead of waiting for the server to retransmit
        // the whole command (the server tolerates the duplicate per D2″). No-op when nothing is
        // unretired; sends are contained + one-way, so this can never fault the report path. This covers
        // the periodic tick, the server's on-request report, and the activity-triggered report — every
        // status-report moment is also a re-sync moment. A validated AckProcessedPrefix stops the re-sends.
        // Runs OUTSIDE the ordering section above: it re-sends acks, not the report, so serializing it
        // would only extend the section's hold without ordering anything the server folds by seq.
        //
        // Round-dispatch grace added a FOURTH trigger — the delivered-input report — which therefore
        // also re-drives this sweep at input rate rather than at the 60s cadence. Benign in both
        // directions: the unretired set is normally empty (so this is a no-op), and a duplicate ack is
        // explicitly tolerated by the server per D2″ — the same tolerance the on-request trigger
        // already relies on.
        Processor?.RedeliverUnretiredProcessedAcks();
    }

    /// <summary>Observes an abandoned status-report send (see
    /// <see cref="SendDaemonStatusReportOnceAsync"/>) so its eventual completion — or fault, once it
    /// finally lands or the connection reclaims it — is not an unobserved task exception.</summary>
    void ObserveAbandonedStatusReportSend(Task send) =>
        _ = send.ContinueWith(
            t => LogStatusReportSendFailedInBackground(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>The out-of-cycle report fired on a launch-stage transition, wired in
    /// <see cref="CreateActivityClock"/>. Shares <see cref="SendDaemonStatusReportOnceAsync"/>'s
    /// swallow-on-failure behavior — a failed send must never fail a launch. Returns the Task so a
    /// test can await it; the clock callback fires it fire-and-forget.</summary>
    internal Task SendStatusReportNowAsync() => SendDaemonStatusReportOnceAsync();

    async Task RunDaemonStatusReportLoopAsync(CancellationToken ct) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct)) {
            try { await SendDaemonStatusReportOnceAsync(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "DaemonStatusReport loop tick faulted — continuing"); }
        }
    }

    /// <summary>Phase B (D2): one <see cref="LiveAgentInfo"/> per currently-live (Starting or
    /// Running), non-private agent, carrying its kind + flow identity. Mirrors the
    /// <see cref="ServerConnection.GetLiveAgentIds"/> filter (private-local agents excluded).
    ///
    /// <para>Also carries the agent's activity attestation (spec §0/§2).
    /// <c>ActivitySeq</c>/<c>IdleForMs</c>/<c>TurnInFlight</c> go unconditionally; <c>LaunchStage</c>
    /// ONLY while <c>Status == "Starting"</c> — the clock also clears its own stage, but this gate is
    /// the contractual guarantee rather than an accident of when that runs.</para>
    ///
    /// <para>ALSO reports every in-flight launch not yet published (<see cref="TrackPendingLaunch"/>).
    /// Without that arm the stage-evidence lane is INERT: the ACP handshake runs entirely inside
    /// <c>runtimeFactory.StartAsync</c>, which returns before the <c>AgentInstance</c> exists, so a
    /// stage-triggered report would describe an <c>_agents</c> map that never contained the agent
    /// being staged.</para>
    ///
    /// <para>A pending entry is emitted only when <c>_agents</c> does not already hold its id, checked
    /// AFTER the published entries are materialized. That ordering makes a racing publish cost at most
    /// a one-cycle OMISSION (benign — no server consumer infers absence from an omission) rather than
    /// a DUPLICATE id, which would double-count against the server's capacity tally.</para></summary>
    internal IReadOnlyList<LiveAgentInfo> BuildLiveAgents() {
        var live = _agents.Values
            .Where(a => a.Status is "Starting" or "Running" && !a.IsPrivate)
            .Select(a => new LiveAgentInfo(
                a.Id, a.Kind.ToString(), a.CreatedAt, a.FlowRunId, a.FlowRole,
                ActivitySeq:  a.ActivityClock.ActivitySeq,
                IdleForMs:    a.ActivityClock.IdleForMs,
                TurnInFlight: a.ActivityClock.TurnInFlight,
                LaunchStage:  a.Status == "Starting" ? a.ActivityClock.LaunchStage : null))
            .ToList();

        foreach (var p in _pendingLaunches.Values) {
            if (_agents.ContainsKey(p.Id)) continue; // published — the real entry above is authoritative

            live.Add(new LiveAgentInfo(
                p.Id, p.Kind.ToString(), p.CreatedAt, p.FlowRunId, p.FlowRole,
                ActivitySeq:  p.ActivityClock.ActivitySeq,
                IdleForMs:    p.ActivityClock.IdleForMs,
                TurnInFlight: p.ActivityClock.TurnInFlight,
                // A pending launch IS "Starting" by definition — there is no other status it can hold —
                // so the Status gate above is satisfied unconditionally here.
                LaunchStage:  p.ActivityClock.LaunchStage));
        }

        return live;
    }

    /// <summary>An in-flight launch: the runtime is starting (for an ACP vendor, the whole handshake)
    /// but no <see cref="AgentInstance"/> exists yet. Carries the SAME <see cref="AgentActivityClock"/>
    /// instance the eventual <c>AgentInstance</c> will own, so a stage stamped during the handshake is
    /// readable through this record the instant it is stamped.</summary>
    internal sealed record PendingLaunch(
        string             Id,
        LaunchKind         Kind,
        DateTime           CreatedAt,
        string?            FlowRunId,
        string?            FlowRole,
        AgentActivityClock ActivityClock);

    readonly ConcurrentDictionary<string, PendingLaunch> _pendingLaunches = new(StringComparer.Ordinal);

    /// <summary>Registers an in-flight launch so <see cref="BuildLiveAgents"/> can describe it during
    /// the handshake, returning a scope that removes it again. <c>using</c>-scoped rather than a
    /// hand-written call per exit — that is what makes no-leak structural across success, coded
    /// failure, cleanup-and-return and a thrown handshake. Removal after <see cref="PublishAgent"/>
    /// leaves no window: <see cref="BuildLiveAgents"/> suppresses a pending entry already in
    /// <c>_agents</c>.</summary>
    internal IDisposable TrackPendingLaunch(
            string agentId, LaunchKind kind, string? flowRunId, string? flowRole, AgentActivityClock clock) {
        _pendingLaunches[agentId] = new PendingLaunch(agentId, kind, DateTime.UtcNow, flowRunId, flowRole, clock);

        return new PendingLaunchScope(this, agentId);
    }

    sealed class PendingLaunchScope(AgentOrchestrator owner, string agentId) : IDisposable {
        public void Dispose() => owner._pendingLaunches.TryRemove(agentId, out _);
    }

    /// <summary>One activity clock per launch, wired so a genuine
    /// <see cref="AgentActivityClock.SetLaunchStage"/> transition fires
    /// <see cref="SendStatusReportNowAsync"/>. EVERY launch path must route through here — a path that
    /// builds its own clock silently loses the stage-report wiring. Shared with
    /// <see cref="SeedAgentForTest"/> so tests exercise the same wiring, never a test-only hookup.</summary>
    AgentActivityClock CreateActivityClock() =>
        new(TimeProvider.System) {
            OnLaunchStageChanged = () => _ = SendStatusReportNowAsync(),
            OnTurnEnded          = () => _ = SendStatusReportNowAsync(),
        };

    /// <summary>Phase B: test-only seam — insert a minimal <see cref="AgentInstance"/> (Noop
    /// PTY runtime, no real process/worktree) so unit tests can exercise <see cref="BuildLiveAgents"/>
    /// / status-report / reviewer-TTL logic without a live launch. Never called in production.</summary>
    internal AgentInstance SeedAgentForTest(
            string id, LaunchKind kind = LaunchKind.Default, string status = "Running",
            string? flowRunId = null, string? flowRole = null,
            DateTime? createdAt = null, DateTime? lastOutputAt = null, bool isPrivate = false,
            IPtyProcess? pty = null, string? startIdentity = null, string? requester = null,
            string? requesterDisplay = null, string? model = "default", int? inactivityBoundSeconds = null,
            string? prompt = null,
            // A test that needs to control the agent's monotonic age/idle constructs its own
            // AgentActivityClock over a FakeTimeProvider and passes it here — the same wiring
            // CreateActivityClock() gives a real launch, with a controllable time source.
            AgentActivityClock? activityClock = null,
            WorktreeInfo? worktree = null, WorkLocation work = WorkLocation.OwnedWorktree,
            string? borrowedSnapshotSource = null) {
        var agent = new AgentInstance(
            id, prompt, model, null, "/repo", "codex",
            new PtyHostedAgentRuntime("codex", pty ?? NoopPtyProcess.Instance),
            worktree ?? new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Kind = kind, FlowRunId = flowRunId, FlowRole = flowRole, IsPrivate = isPrivate,
            CreatedAt = createdAt ?? DateTime.UtcNow, StartIdentity = startIdentity,
            RequesterUserId = requester,
            RequesterDisplay = requesterDisplay,
            InactivityBoundSeconds = inactivityBoundSeconds,
            ActivityClock = activityClock ?? CreateActivityClock(),
            Work = work,
            BorrowedSnapshotSource = borrowedSnapshotSource
        };
        agent.Status = status;
        if (lastOutputAt is { } lo) agent.LastOutputAt = lo;
        PublishAgent(agent);
        return agent;
    }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2/§5.5): route a launch. A fully-Seq'd command
    /// (Epoch + Seq + CommandId ALL present) goes through the processor's serial lane — accepted exactly-in-order,
    /// SUBMITTED (not executed) synchronously on this call, and eventually turned into a terminal
    /// CommandAck/CommandRejected from the <see cref="CommandOutcome"/> the core returns. An un-Seq'd command
    /// (NONE of the three — old server) runs the legacy unsequenced lane directly — inline-awaited, unchanged —
    /// and never advances the watermark. Anything in between (a PARTIAL tuple, or the processor somehow
    /// missing) is a malformed sequenced command and FAILS CLOSED with a LaunchFailed — never the legacy lane,
    /// whose retry could be re-accepted on the sequenced lane and double-create the generation.
    ///
    /// <para>§3.3 (one execution domain): NO handler awaits launch or stop EXECUTION any more. A sequenced
    /// command's ACCEPTANCE is still decided synchronously on the pump
    /// (<see cref="SequencedCommandProcessor.SubmitAsync"/> resolves accept/reject under lock before
    /// returning, so acceptance ordering still depends only on pump serialization), but the returned
    /// execution-completion task is handed to <see cref="ObserveDetachedExecution"/> — fault logging only.
    /// Terminal CommandAck/CommandRejected emission was always the lane's own duty, unaffected by whether
    /// anyone awaits that task. An un-sequenced launch is committed onto the SAME lane, so a consent prompt
    /// inside one launch delays subsequent launch/stop EXECUTIONS exactly as the shipped pump serialized
    /// them — relocated off the pump, never refused for its format.</para></summary>
    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var anySeq = cmd.Epoch is not null || cmd.Seq is not null || cmd.CommandId is not null;

        if (!anySeq) {
            await DispatchUnsequencedLaunchAsync(cmd);
            return;
        }

        // Phase B2-b (sequenced-settlement design §5.5): a capable server sends ALL of
        // Epoch/Seq/CommandId. Anything less (a partial tuple, or the processor somehow missing) is a
        // malformed sequenced command and must FAIL CLOSED — never the unwatermarked un-sequenced route,
        // whose retry could be re-accepted on the sequenced lane and double-create the generation
        // (at-most-once-per-generation).
        if (Processor is { } proc && cmd.Epoch is { } epoch && cmd.Seq is { } seq && cmd.CommandId is { } cmdId) {
            var execution = proc.SubmitAsync(
                new SequencedItem(SequencedKind.Launch, epoch, seq, cmdId, cmd.AgentId),
                () => HandleLaunchAgentCore(cmd));
            _ = ObserveDetachedExecution(execution, cmd.AgentId);
            return;
        }

        await _server.LaunchFailedAsync(cmd.AgentId, "Malformed sequenced launch: partial Epoch/Seq/CommandId");
    }

    /// <summary>§3.3: an un-sequenced (no Epoch/Seq/CommandId) launch — the shape EVERY ordinary dashboard,
    /// hosted-agent and PR-review launch has, since the sequenced tuple rides only the review-flow
    /// settlement lane (§1.9). It is committed onto the same serial lane as sequenced traffic so arrival
    /// order holds across formats, and the pump is free the instant the commit returns.</summary>
    async Task DispatchUnsequencedLaunchAsync(LaunchAgentCommand cmd) {
        var proc = SnapshotProcessorReservingInlineSlot();

        if (proc is null) {
            // Pre-settlement server (no processor published): the SHIPPED inline await, unchanged. The
            // inline await IS the backpressure — no queue exists — and no sequenced traffic can exist
            // against such a server, so the single execution domain is trivially preserved. One accepted,
            // narrow cost carried from the consent deadline-discipline change: a shutdown/teardown
            // cancellation racing this await propagates an uncaught OperationCanceledException out of
            // HandleLaunchAgentCore, which ServerConnection.SafeInvoke logs and swallows — so no
            // LaunchFailed reaches the server for a launch torn down mid-prompt by shutdown. That is
            // shutdown-only and the server's own reconciliation lanes are the backstop; do NOT re-add one.
            try { await HandleLaunchAgentCore(cmd); }
            finally { ReleaseInlineSlot(); }
            return;
        }

        // Committed -> the lane owns execution AND fault containment (no handler-side continuation).
        // Coalesced/DroppedUnknownTarget are stop-only outcomes: launches never coalesce and are never
        // admission-checked (a launch CREATES its target).
        var outcome = proc.SubmitUnsequenced(new UnsequencedItem(
            UnsequencedKind.Launch, cmd.AgentId, UnsequencedLaunchPayloadKey, () => HandleLaunchAgentCore(cmd)));

        if (outcome is not SubmitOutcome.Refused) return;

        // Refused == the lane stopped accepting, i.e. daemon shutdown. The CALLER owns the consequence, and
        // its own send failure is swallowed: the daemon is exiting, nothing here can act on it, and this
        // notification must never be able to fault the pump.
        try { await _server.LaunchFailedAsync(cmd.AgentId, ShutdownRefusedLaunchReason); }
        catch (Exception ex) { LogRefusedLaunchNotifyFailed(ex, cmd.AgentId); }
    }

    /// <summary>§3.3: the only job of this continuation is to observe/log a fault on a detached sequenced
    /// execution task so it never becomes an unobserved task exception. It must NEVER convert the fault into
    /// any answer of its own — that is <see cref="SequencedCommandProcessor.RunLaneAsync"/>'s job, via its
    /// own per-item catch, which already classifies an execution fault (including an
    /// <see cref="OperationCanceledException"/> from a torn-down consent prompt — the sequenced lane's
    /// "lane failure" settlement) as <see cref="CommandOutcomeKind.InternalError"/> plus a terminal
    /// <see cref="CommandRejected"/>. In normal operation this task completes successfully — RunLaneAsync's
    /// own finally always resolves it — so reaching the catch below is itself a defensive, not expected,
    /// path.</summary>
    async Task ObserveDetachedExecution(Task execution, string agentId) {
        try { await execution; }
        catch (Exception ex) { LogDetachedCommandFault(ex, agentId); }
    }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): the shipped launch body, now returning the
    /// terminal <see cref="CommandOutcome"/> the sequenced lane needs (the legacy caller ignores it). Every
    /// shipped pre-flight rejection maps to <c>LaunchRejected</c> — capacity to <c>daemon_capacity</c>, all
    /// other validations to <c>semantic</c> — so the sequenced lane emits a CommandRejected alongside the
    /// unchanged LaunchFailed; a spawn/registration failure that was cleaned up maps to
    /// <c>launch_failed_cleaned</c>; a registered agent maps to <c>launch_executed</c>. The shipped
    /// LaunchFailed / worktree-teardown / cleanup side effects are UNCHANGED — only the return value is added.</summary>
    async Task<CommandOutcome> HandleLaunchAgentCore(LaunchAgentCommand cmd) {
        var agentId       = cmd.AgentId;
        var prompt        = cmd.Prompt;
        var model         = cmd.Model;
        // A protocol-v3 explicit-reviewer-model launch pins the server-resolved LaunchModel VERBATIM.
        // Compute the effective model ONCE here so every site that records/reports the model this
        // launch REQUESTS — the launch log and the runtime start context — reads the same value.
        // (What the server sees registered is registeredModel below: for an ACP runtime the
        // handshake-confirmed model narrows this request; PTY runtimes register it as-is.)
        // Null block ⇒ legacy path, cmd.Model unchanged.
        // Explicitly string?, not var: ModelSelectionLaunchPolicy below clears this when the selected
        // runtime cannot apply a model, and "no model reported" must be representable in the type.
        string? effectiveModel = cmd.ExplicitReviewerModel?.LaunchModel ?? model;
        var effort        = cmd.Effort;
        var repoPath      = cmd.RepoPath;
        var tools         = cmd.Tools;
        var attachmentIds = cmd.AttachmentIds;
        var isReview      = cmd.Kind == LaunchKind.Review;
        var isReviewFlow  = cmd.Kind == LaunchKind.ReviewFlow;

        // A caller-selected ACP permission preset is a pure, side-effect-free rejection (wrong-vendor /
        // non-interactive / borrowed / unknown token), validated BEFORE the consent gate: an ineligible
        // preset must fail the launch WITHOUT first prompting the owner or holding the serialized command
        // lane for the consent timeout on a launch that can never proceed.
        if (AcpPermissionPresetPolicy.RejectionReason(cmd) is { } presetRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, presetRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // Before consent for the same reason as the preset guard above.
        if (ClaudePermissionModePolicy.RejectionReason(cmd) is { } modeRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, modeRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // Owner consent gate. Server-driven launches only — the local 0600 socket path
        // (HandleLocalSpawnAsync) is the owner's by construction and never consults this.
        // NOTE: in prompt mode this can hold the sequenced slot up to PromptTimeoutSeconds (≤300s,
        // default 45s ≤ the server's 60s launch-admission patience); commands queued behind it wait
        // — and, because SignalR dispatches server→client invocations sequentially, other server-relayed
        // messages to this daemon queue behind the prompt for the same window.
        var consentInput = new LaunchConsentInput(
            cmd.RequesterUserId, cmd.RequesterIsOwner ?? false,
            LaunchConsentEngine.KindToken(cmd.Kind), cmd.RepoPath, cmd.Vendor, cmd.RequesterDisplay);
        var consent = await _consentGate.DecideAsync(cmd.AgentId, consentInput, _shutdownCts.Token);
        if (!consent.Allowed) {
            _logger.LogWarning("Launch {AgentId} denied by consent policy ({Source})", cmd.AgentId, consent.Source);
            await _server.LaunchFailedAsync(cmd.AgentId,
                $"{LaunchConsentGate.DeniedReasonPrefix}: {consent.Detail}");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // Guard for a null/blank vendor before the dictionary lookup: LaunchAgentCommand crosses
        // the SignalR boundary where the non-null annotation isn't enforced, and Dictionary
        // .TryGetValue(null) throws ArgumentNullException — which SafeInvoke would swallow, dropping
        // the launch with no LaunchFailed reaching the server. (The removed vendor allowlist used to
        // absorb this incidentally.)
        if (string.IsNullOrWhiteSpace(cmd.Vendor) || !_runtimeFactories.TryGetValue(cmd.Vendor, out var runtimeFactory)) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // fail an unattended (review-flow) launch fast when the selected vendor's
        // runtime can't run unattended — before creating a worktree, so there's nothing to
        // clean up. This guards every newly registered runtime through the same capability seam.
        if (UnattendedLaunchPolicy.RejectionReason(cmd.Vendor, runtimeFactory.SupportsUnattended, isReviewFlow) is { } unattendedRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, unattendedRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // A runtime that cannot APPLY a model must not REPORT one. effectiveModel above feeds both
        // RuntimeStartContext.Model (where a no-op selector discards it) and the AgentInstance the
        // server sees — so without this, such a vendor runs its default while the live model chip and
        // hosted_agent_started analytics both claim the requested model is live.
        var modelDisposition = ModelSelectionLaunchPolicy.Evaluate(
            effectiveModel, runtimeFactory.SupportsModelSelection, cmd.ExplicitReviewerModel is not null);

        string? droppedModelPick = null;

        if (modelDisposition != ModelSelectionDisposition.Honor) {
            // Non-null by construction: Evaluate returns Honor whenever the requested model is
            // null/blank, so reaching here means a model really was asked for. Captured before the
            // clear below so the diagnostics can name it.
            var unhonorableModel = effectiveModel!;

            if (modelDisposition == ModelSelectionDisposition.Reject) {
                await _server.LaunchFailedAsync(cmd.AgentId,
                    ModelSelectionLaunchPolicy.RejectionReason(cmd.Vendor, unhonorableModel));

                return new CommandOutcome(
                    CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            LogModelSelectionUnsupported(cmd.Vendor, unhonorableModel);
            droppedModelPick = unhonorableModel;
            effectiveModel   = null;
        }

        if (isReviewFlow && cmd.Borrowed && !runtimeFactory.SupportsBorrowedReviewFlow) {
            await _server.LaunchFailedAsync(cmd.AgentId,
                $"Borrowed review flows are not certified for '{cmd.Vendor}'; retry with an owned review worktree.");

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        // A caller-selected Codex posture is validated here — before the repo checks and the worktree
        // creation below, so a rejection leaves nothing to clean up. Selection is only ever valid for
        // an interactive daemon-owned-worktree launch: a posture on a borrowed, review-flow or
        // PR-review launch would defeat a containment invariant, so it fails the launch outright
        // rather than being silently dropped or silently honoured.
        if (CodexPosturePolicy.RejectionReason(cmd) is { } postureRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, postureRejection);

            return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
        }

        if (isReviewFlow && cmd.ReviewerCertification is { } certification) {
            var version = string.Equals(cmd.Vendor, "claude", StringComparison.Ordinal)
                ? DaemonRunner.ProbeCliVersionForLaunch(_config.ClaudePath)
                : null;
            var certificationCheck = EvaluateReviewerCertification(
                cmd.Vendor, version, _server.CurrentConnectionId, certification);
            if (!certificationCheck.Ok) {
                // Codex review round 2 (P1): tell the caller FIRST. Recomputing capabilities re-runs
                // the REGISTRATION probe — three 10s attempts plus backoff — so doing it before the
                // notification meant a failed launch probe cost ~10s here and then ~30.75s more
                // before the caller heard anything. The single-attempt launch probe did not bound the
                // launch path at all while this sat in front of the rejection; my claim that it did
                // was wrong.
                await _server.LaunchFailedAsync(cmd.AgentId,
                    $"reviewer_certification_changed: {certificationCheck.Reason}.");

                // The self-heal (recompute + re-advertise) is genuinely useful — a certification
                // mismatch usually means the advertisement is stale — but nothing waits on it, and
                // the caller already has its answer.
                //
                // SINGLE-FLIGHT, not bare fire-and-forget. Codex review round 3: concurrent rejected
                // launches each starting an independent refresh reintroduces this PR's own bug — a
                // slow failing refresh can complete AFTER a fast successful one and overwrite valid
                // capabilities with a failed-probe null, durably disabling the reviewer again.
                // Serialising publication is necessary; coalescing keeps a burst of rejections from
                // queueing a refresh each, and the rerun pass guarantees the LAST write is the
                // NEWEST computation.
                _capabilityRefresh.Trigger(
                    async () => {
                        _config.UnattendedVendorCapabilities =
                            DaemonRunner.ComputeUnattendedVendorCapabilities(_runtimeFactories.Values, _config);
                        await _server.ReRegisterAsync();
                    },
                    ex => _logger.LogDebug(ex,
                        "Capability recompute after a certification rejection failed; the next " +
                        "registration or launch re-evaluates it."));
                return new CommandOutcome(
                    CommandOutcomeKind.LaunchRejected,
                    agentId,
                    RejectReason: CommandRejectedReason.Semantic);
            }
        }

        WorktreeInfo? worktree      = null;
        string?       mcpConfigPath = null;

        // Declared OUTSIDE the try so it is in scope in the catch blocks below: the failed-launch
        // cleanup must consult it to decide whether the worktree is ours to remove. A borrowed cwd
        // is the user's real checkout — never removed on any path (spec's top safety invariant).
        var snapshotBorrow = cmd.Borrowed && runtimeFactory.BorrowedReviewRequiresIndependentSnapshot;
        var work = cmd.Borrowed && !snapshotBorrow
            ? WorkLocation.BorrowedCwd
            : WorkLocation.OwnedWorktree;
        string? borrowedSnapshotSource = null;

        // The per-reviewer bridge token URL (if this is an unattended review-flow launch), hoisted to
        // method scope so the failure catch can revoke it when no AgentInstance was created to carry it.
        string? reviewerToken = null;

        // Created here, ahead of the reviewer-token mint and the AgentInstance that will own it, so the
        // SAME instance reaches the permission-bridge grant, the ACP runtime and the AgentInstance —
        // one clock per launch, never three.
        var activityClock = CreateActivityClock();

        try {
            if (EffectiveCount >= _config.MaxConcurrentAgents) {
                await _server.LaunchFailedAsync(agentId, $"At max capacity ({_config.MaxConcurrentAgents} agents)");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.DaemonCapacity);
            }

            if (!_config.IsRepoAllowed(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path not allowed: {repoPath}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            if (!Directory.Exists(repoPath)) {
                await _server.LaunchFailedAsync(agentId, $"Repo path does not exist: {repoPath}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            if (isReview) {
                if (cmd.Review is not { } review) {
                    await _server.LaunchFailedAsync(agentId, "Review launch missing PR info");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                // Final guard: re-validate that the chosen path's origin really
                // matches the PR's repo. The match the UI saw could have moved
                // (remote renamed, repo moved) between picker and launch.
                var actual = await GetOriginRemoteAsync(repoPath);

                if (actual is null) {
                    await _server.LaunchFailedAsync(agentId, $"No origin remote at {repoPath}");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                var expected = $"github.com/{review.Owner}/{review.Repo}";

                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) {
                    await _server.LaunchFailedAsync(agentId, $"Repo at {repoPath} no longer matches {review.Owner}/{review.Repo} (origin: {actual})");

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }
            }

            // "auto" means let the CLI decide — don't pass --effort at all
            if (string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
                effort = null;
            }

            // Validate effort level before expensive worktree setup
            if (!string.IsNullOrEmpty(effort) && !ValidEffortLevels.Contains(effort)) {
                await _server.LaunchFailedAsync(agentId, $"Invalid effort level: {effort}");

                return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
            }

            LogLaunching(agentId, repoPath, cmd.Vendor, effort ?? "default", effectiveModel);

            // Review launches base the worktree on the PR head ref so the agent
            // works against the PR's actual state, not the local HEAD.
            var baseRef = isReview && cmd.Review is { } reviewInfo
                ? $"refs/pull/{reviewInfo.PrNumber}/head"
                : cmd.BaseRef;

            if (cmd.Borrowed) {
                // Defense-in-depth re-authorization: the server already probed this cwd, but the
                // daemon NEVER borrows a path just because the server said so (TOCTOU-safe). The
                // reason is surfaced verbatim to the server via the catch → LaunchFailedAsync below;
                // Phase B (server) keys off the `borrow_auth_failed:` prefix.
                var auth = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(cmd.BorrowCwd ?? "");

                if (!auth.Allowed) {
                    throw new InvalidOperationException($"borrow_auth_failed: {auth.Reason}");
                }

                if (snapshotBorrow) {
                    borrowedSnapshotSource = auth.CanonicalCwd
                        ?? throw new InvalidOperationException("borrow_auth_failed: canonical_cwd_missing");
                    var snapshotGitRoot = auth.CanonicalGitRoot
                        ?? throw new InvalidOperationException("borrow_auth_failed: not_a_git_repository");
                    worktree = await _worktreeManager.CreateBorrowedSnapshotAsync(
                        snapshotGitRoot, borrowedSnapshotSource, $"borrowed-{agentId}", _shutdownCts.Token);
                } else {
                    // Direct borrowed runtimes have their own certified read-only boundary.
                    worktree = WorktreeInfo.Borrowed(auth.CanonicalCwd!);
                }
            } else {
                worktree = await _worktreeManager.CreateAsync(repoPath, baseRef: baseRef);
            }

            // The checkout the agent runs in is what the session's repo policy file means — the same
            // trust boundary the local seams read. Bound once here so the whole run is judged against
            // one set of documents. A policy that cannot be built is not a launch failure: the launch
            // proceeds with no snapshot, and the permission seam falls back to prompting.
            PolicySnapshot? policySnapshot = null;
            try {
                policySnapshot = _policySnapshots?.BuildFor(worktree.Path);
            } catch (Exception ex) {
                LogPolicySnapshotBuildFailed(ex, agentId);
            }

            // The documents this run is judged against, enqueued BEFORE the runtime starts: the ACP
            // runtime queues its opening prompt inside StartAsync, so an immediate
            // session/request_permission would otherwise put a decision event ahead of the snapshot
            // it names, and the event lane preserves insertion order. Keyed by the agent id — no
            // vendor session id exists yet.
            if (policySnapshot is { IsEmpty: false } uploadable) {
                if (uploadable.Degraded)
                    LogPolicySnapshotDegraded(agentId,
                        uploadable.Degradations.Count > 0 ? uploadable.Degradations[0] : "unspecified");
                _ = _server.AppendAgentRunEventAsync(agentId, PolicyWire.ToUpload(agentId, uploadable));
            }

            if (work == WorkLocation.OwnedWorktree) {
                // Download attachments into worktree (best-effort)
                if (attachmentIds is { Length: > 0 }) {
                    try {
                        var paths = await DownloadAttachmentsAsync(worktree.Path, attachmentIds);

                        if (paths.Count > 0) {
                            var suffix = $"\n\n[Attached files: {string.Join(", ", paths)}]";
                            prompt = string.IsNullOrEmpty(prompt) ? suffix.TrimStart() : prompt + suffix;
                        }
                    } catch (Exception ex) {
                        LogAttachmentDownloadFailed(ex, agentId);
                    }
                }
            }

            // An unattended review-flow reviewer must auto-approve its kcap tool calls (no human is
            // present): mint a dedicated bridge token bound to the launch's read-only kcap allowlist
            // and hand the reviewer that token's URL as KCAP_DAEMON_URL. An invalid allowlist fails
            // the launch FAST rather than falling back to a prompt that would hang.
            var daemonBridgeUrl    = _permissionBridge.BaseUrl;
            var effectiveAllowlist = cmd.McpAllowlist;
            string? reviewContextCapabilityUrl = null;
            string? flowResultCapabilityUrl    = null;

            // Whether this reviewer's kcap-flow-result channel must deliver through a daemon-brokered
            // capability instead of authenticating for itself. The channel resolves its credential
            // from its own config root, which hangs off HOME — so the question is whether the launch
            // runs under the daemon user's HOME, and there are two independent causes of its not doing
            // so, one owned by the launch and one by the runtime:
            //
            //   • a borrowed snapshot, whose OS sandbox moves HOME into a per-launch state dir, and
            //   • a runtime that isolates HOME on every review it serves (Antigravity), which borrows
            //     nothing at all and is therefore unreachable through the first.
            //
            // The borrowed arm stays unconditional rather than being narrowed to the sandboxed vendors:
            // the snapshot lane has delivered through the broker since #488, and a snapshot runtime that
            // keeps its own HOME (Cursor) losing that is a behaviour change nothing here asks for.
            //
            // Computed ONCE and used for the mint, the forwarder and the context, so the grant that
            // serves the submit path and the env the channel is launched with cannot disagree.
            var brokeredResultDelivery = snapshotBorrow
                                      || (isReviewFlow && runtimeFactory.ReviewFlowRedirectsHome);

            // A token record is minted for the union of three independent authorities: Codex's
            // unattended permission allowlist, a borrowed snapshot's immutable review context, and a
            // brokered result channel's submit forwarder. Direct Codex keeps its permissions-only
            // grant; every other reviewer here gets an empty permission set plus whichever capability
            // it needs. One record means one revocation cannot drift.
            var codexReviewer = isReviewFlow &&
                string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase);
            if (codexReviewer || brokeredResultDelivery) {
                if (daemonBridgeUrl is null)
                    throw new InvalidOperationException(
                        "An unattended reviewer's capabilities require the local permission bridge.");

                string[] reviewerServers = [];
                if (codexReviewer &&
                    !KcapMcpRegistry.TryResolveReviewFlowAllowlist(
                        cmd.McpAllowlist, out reviewerServers, out var rejected)) {
                    await _server.LaunchFailedAsync(agentId,
                        $"Review-flow reviewer MCP allowlist contains a server that is not auto-approvable: '{rejected}'.");

                    if (work == WorkLocation.OwnedWorktree) {
                        try { await WorktreeManager.RemoveAsync(worktree); } catch { /* best-effort */ }
                    }

                    return new CommandOutcome(CommandOutcomeKind.LaunchRejected, agentId, RejectReason: CommandRejectedReason.Semantic);
                }

                var reviewGeneration = snapshotBorrow
                    ? worktree.ReviewContextGeneration
                      ?? throw new InvalidOperationException(
                          "borrowed_snapshot_review_context_missing")
                    : null;
                var reviewerUrl = _permissionBridge.RegisterReviewerToken(
                    reviewerServers, reviewGeneration, activityClock,
                    // Only a reviewer that cannot reach its own token store gets a submit forwarder.
                    // Every other reviewer authenticates for itself and must NOT be handed a
                    // daemon-credentialed relay it has no need for. Withholding it also keeps the
                    // endpoint 404 rather than merely unused.
                    brokeredResultDelivery ? ForwardFlowSubmissionAsync : null);
                reviewerToken = reviewerUrl; // the URL doubles as the revoke handle
                if (codexReviewer) {
                    daemonBridgeUrl = reviewerUrl;
                    effectiveAllowlist = reviewerServers;
                }
                if (snapshotBorrow)
                    reviewContextCapabilityUrl = reviewerUrl +
                        "/review-context/workspace-mcp-configs";
                // The capability IS the reviewer grant, so revoking that one token closes the submit
                // path in the same operation that closes the read path a borrowed launch also holds.
                // A separately-minted token could outlive the first and leave a live submit path
                // after the reviewer is gone.
                if (brokeredResultDelivery) flowResultCapabilityUrl = reviewerUrl;
            }

            var runtimeCtx = new RuntimeStartContext(
                AgentId: agentId,
                Vendor: cmd.Vendor,
                SourceRepoPath: repoPath,
                Worktree: worktree,
                Prompt: prompt,
                // Task 8: for an explicit-model reviewer launch, launch with the server-pinned
                // LaunchModel VERBATIM (the launcher passes ctx.Model straight to the argument list —
                // never recanonicalized). effectiveModel (computed once above) resolves this. The
                // registered AgentInstance below reads the same local for PTY runtimes; an ACP
                // runtime instead registers the handshake-confirmed model (see registeredModel),
                // which can only narrow this request, never substitute a different one.
                Model: effectiveModel,
                DroppedModelPick: droppedModelPick,
                Effort: effort,
                Tools: tools,
                IsReview: isReview,
                IsReviewFlow: isReviewFlow,
                Review: cmd.Review,
                Cols: HostedPtyCols,
                Rows: HostedPtyRows,
                ServerUrl: _config.ServerUrl,
                DaemonBridgeUrl: daemonBridgeUrl,
                CapacitorPath: _config.CapacitorPath,
                McpAllowlist: effectiveAllowlist,
                Work: work,
                DaemonId: _daemonId,       // Phase B (D4 §6.4(3)): child env markers for the OrphanReaper scan
                DaemonEpoch: _daemonEpoch,
                IsBorrowedSnapshot: snapshotBorrow,
                ReviewContextCapabilityUrl: reviewContextCapabilityUrl,
                FlowResultCapabilityUrl: flowResultCapabilityUrl,
                RequiresBrokeredResultDelivery: brokeredResultDelivery,
                CodexPosture: cmd.CodexPosture,
                // Handed to the factory so it can wire the clock onto the runtime BEFORE StartAsync —
                // assigning it after that call returns silently defeats every handshake stage stamp.
                ActivityClock: activityClock,
                // Carried verbatim; the ACP factory resolves it (non-review-flow launches only) into the
                // interaction bridge's preset.
                AcpPermissionPreset: cmd.AcpPermissionPreset,
                // Carried verbatim; the Codex app-server factory branches thread/start -> thread/resume
                // on it for a parked reviewer relaunch. Ignored by every other runtime.
                ResumeSessionId: cmd.ResumeSessionId,
                PermissionMode: cmd.PermissionMode,
                // The same instance the AgentInstance below carries, so a factory that judges an
                // action during its own startup uses the documents this launch was reported with.
                PolicySnapshot: policySnapshot
            );

            HostedRuntimeStart start;

            // Captured BEFORE the spawn so the transcript-based session-id fallback
            // (DetectSessionIdAsync) can filter the shared project/rollout dir to files
            // written by THIS agent's process, not the user's earlier sessions.
            var spawnedAtUtc = DateTime.UtcNow;

            // Make this launch describable for the duration of the handshake below: the AgentInstance
            // does not exist until StartAsync returns, so without this the out-of-cycle report each
            // SetLaunchStage fires would omit the very agent it is reporting a stage for.
            using var pendingLaunch = TrackPendingLaunch(
                agentId, cmd.Kind, cmd.FlowRunId, cmd.FlowRole, activityClock);

            try {
                start = await runtimeFactory.StartAsync(runtimeCtx, _shutdownCts.Token);
            } catch (Exception ex) when (ex is CodexHooksNotInstalledException or CodexReviewerMcpIsolationException
                                            or CodexUnsupportedWindowsException) {
                // CodexHooksNotInstalledException: hooks preflight failed in Prepare.
                // CodexReviewerMcpIsolationException: the review-flow reviewer's inherited MCP
                // servers could not be authoritatively enumerated, so the recursion guard cannot be
                // proven — fail the launch CLOSED rather than spawn a reviewer that might inherit a
                // flow-starting server.
                // CodexUnsupportedWindowsException: Windows build older than 10.0.17763, where
                // Codex's Windows sandbox does not exist — carries the version + doc link.
                // All map to the same cleanup path.
                // §3.5: DescribeLaunchFailure covers a null/whitespace ex.Message with the typed
                // fallback; these three exception types always carry a real message, but every
                // LaunchFailed send site routes through the same fallback rather than assuming so.
                await _server.LaunchFailedAsync(agentId, AcpHostedAgentRuntimeFactory.DescribeLaunchFailure(ex));

                // No AgentInstance was created, so CleanupAgentAsync won't run — revoke the reviewer
                // token here (if we minted one) so it doesn't leak into the live-token set.
                if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

                // Still need to clean up the worktree before returning — but ONLY if we own it.
                // A borrowed cwd is the user's real checkout; removing it here would `git worktree
                // remove` the user's tree (spec's top safety invariant; mirrors CleanupAgentAsync).
                if (work == WorkLocation.OwnedWorktree) {
                    try { await WorktreeManager.RemoveAsync(worktree); } catch {
                        /* best-effort */
                    }
                }

                return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
            }

            mcpConfigPath = start.McpConfigPath;
            var runtime = start.Runtime;

            // No clock assignment here, deliberately: the ACP factory already wired the SAME instance
            // onto the runtime before the handshake ran. Assigning it post-hoc at this point would
            // leave it null for the whole handshake and defeat every stage stamp.

            LogAgentSpawned(agentId, runtime.Pid, worktree.Path, runtimeFactory.Vendor);

            var cts = new CancellationTokenSource();

            // Applied-posture echo, stamped only for an interactive Codex launch on a daemon-owned
            // worktree. A review-flow or PR-review launch reports nothing (its posture is the
            // containment invariant, not a choice), and neither does a borrowed one. Both borrow
            // conditions are tested: `work` alone is not enough, because a snapshot-backed borrow
            // maps to OwnedWorktree while still being a borrow the caller never chose a posture for.
            (string Sandbox, string Approval)? appliedPosture =
                string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)
             && cmd.Kind == LaunchKind.Default
             && !cmd.Borrowed
             && work == WorkLocation.OwnedWorktree
                    ? CodexPosturePolicy.Resolve(work, isReviewFlow, cmd.CodexPosture)
                    : null;

            // The permission bridge can never prompt under `never`, and danger-full-access reaches
            // outside the worktree entirely. The dashboard warns the user before launch; this is the
            // operator-side record of what actually ran.
            if (appliedPosture is { } applied
             && (string.Equals(applied.Approval, "never", StringComparison.Ordinal)
              || string.Equals(applied.Sandbox, "danger-full-access", StringComparison.Ordinal))) {
                LogBridgeDefeatingPosture(agentId, applied.Sandbox, applied.Approval);
            }

            // An ACP runtime confirms model application during its StartAsync handshake:
            // Transcript.ResolvedModel is the id actually applied, or null when the request did not
            // take (no availableModels match / the agent rejected the option — the vendor's default
            // runs in every null case). Register the CONFIRMED value, never the request: agent.Model
            // feeds AgentRegisteredAsync (live model chip + hosted_agent_started analytics),
            // AgentRunStarted (agent_runs), every reconnect re-registration, and the local
            // supervision status payload (SnapshotAgentsForStatus). Same requested-vs-running rule
            // as ModelSelectionLaunchPolicy, applied per-request instead of per-capability. PTY
            // runtimes have no confirmation seam (Transcript is null) and keep reporting
            // effectiveModel.
            var registeredModel = start.Transcript is { } confirmed ? confirmed.ResolvedModel : effectiveModel;

            var agent = new AgentInstance(agentId, prompt, registeredModel, effort, repoPath, cmd.Vendor, runtime, worktree, cts) {
                ActivityClock       = activityClock,
                McpConfigPath       = mcpConfigPath,
                CurrentCols         = HostedPtyCols,
                CurrentRows         = HostedPtyRows,
                Work                = work,
                SandboxPolicy       = appliedPosture?.Sandbox,
                ApprovalPolicy      = appliedPosture?.Approval,
                // Echoed on AgentRegistered for the dashboard chip; carried verbatim from the command
                // so a reconnect re-registration reports the same value. Only ever set for an
                // interactive ACP launch (the policy above rejects any other shape).
                PermissionPreset    = cmd.AcpPermissionPreset,
                PolicySnapshot      = policySnapshot,
                ReviewerBridgeToken = reviewerToken,
                BorrowedSnapshotSource = borrowedSnapshotSource,
                Kind                = cmd.Kind,       // Phase B (D2): flow identity + kind for LiveAgents/status report
                FlowRunId           = cmd.FlowRunId,
                FlowRole            = cmd.FlowRole,
                RequesterUserId     = cmd.RequesterUserId,
                RequesterDisplay    = cmd.RequesterDisplay,
                InactivityBoundSeconds = cmd.InactivityBoundSeconds
            };
            PublishAgent(agent);

            // Phase B (D4 §6.4(2)): capture the start-identity + write the durable PID record
            // immediately after the process exists (before registration) so a daemon crash right after
            // this leaves a reapable record. FAIL-CLOSED: a write/identity failure throws → the catch
            // routes it through the single-flight cleanup (the agent is already in _agents).
            PersistPidRecordOrThrow(agent, runtime.Pid, runtime.StartIdentity);

            await RegisterAgentAsync(agent);

            // A runtime with no terminal output (ACP/cursor) has no output-chunk signal to flip
            // Starting→Running on — ReadAgentOutputAsync's read loop never yields a byte for such
            // a runtime, so without this the agent would sit in "Starting" until the heartbeat's
            // StartupTimeout auto-stops it as stuck (Fix B/E). Flip to Running immediately:
            // the runtime factory's StartAsync already completed the ACP initialize/session-new
            // handshake by the time we get here, so the session really is established. PTY
            // runtimes are unaffected — they keep the existing on-first-chunk flip in
            // ReadAgentOutputAsync unchanged.
            if (!runtime.EmitsTerminalOutput) {
                SetAgentStatus(agent, "Running");
                agent.HasReceivedOutput = true;
                if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);

                // LaunchStage is Starting-only; cleared at the exact instant this agent leaves it.
                agent.ActivityClock.ClearLaunchStage();
            }

            // Bind + start live transcript forwarding for any runtime that exposes an ACP
            // transcript source (Cursor today; null for every PTY runtime — no branch taken for
            // claude/codex). Fire-and-forget from here, exactly like ReadAgentOutputAsync below: the
            // bind call is IsReady-gated and can block across a reconnect outage (ConnectionRetry),
            // and HandleLaunchAgent must never stall on it — a stalled launch would queue every OTHER
            // inbound hub command behind it on this daemon's single SignalR connection.
            // StartAcpForwardingAsync itself still enforces the load-bearing ordering (bind strictly
            // after RegisterAgentAsync above, strictly before any AcpSessionEvents) by awaiting the
            // bind before constructing the forwarder.
            if (start.Transcript is { } transcript) {
                // Create + store the per-agent CTS BEFORE firing the setup task, so it exists for
                // FinalizeAgentRunAsync to cancel even if the agent finalizes before the bind below
                // ever resolves (see AgentInstance.AcpCts).
                var acpCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
                agent.AcpCts = acpCts;
                // §2.5: a runtime that HOLDS its first turn (envelope-sourced hosted Codex) is
                // driven through the deferred-first-turn source-claim sequence — durable claim BEFORE the
                // first turn, then forward without re-binding, then confirm. Every other transcript
                // runtime (Cursor) keeps the single-phase bind-then-forward path. Dormant until the
                // factory sets deferFirstTurn (the activation slice); the fake test runtime exercises it.
                if (start.Runtime.RequiresSourceClaimBeforeFirstTurn)
                    _ = StartEnvelopeSourcedSessionAsync(agent, start.Runtime, transcript, cmd.Vendor, acpCts);
                else
                    _ = StartAcpForwardingAsync(agent, transcript, cmd.Vendor, acpCts);
            }

            // Reconnect PID-record seam (reconnect spec §6.2 step 1): a resume candidate's pid is
            // durably recorded at its spawn — before any handshake — through the SAME record + agent
            // identity machinery as the original launch (PersistPidRecordOrThrow refreshes
            // agent.StartIdentity too, so teardown's identity check tracks the live incarnation).
            // The record write THROWS on failure by contract; the runtime treats that as the
            // attempt failing and disposes the candidate. Wired here, after registration, because
            // no reconnect can begin before the launch path completes.
            if (runtime is AcpHostedAgentRuntime { ReconnectSupport: { } reconnectSupport }) {
                reconnectSupport.PidCallbacks = new AcpPidRecordCallbacks(
                    Record: pid => PersistPidRecordOrThrow(agent, pid, null),
                    Clear:  () => DeletePidRecord(agent.Id));
            }

            // The same seam at the exec-per-turn cadence: agy runs each round as its own short-lived
            // child, so the one-shot record above names turn 1's pid and nothing after it. Recording
            // per turn (and clearing on a confirmed turn exit) is what keeps the fail-closed contract
            // true for round 2 onward — the env-marker fallback cannot cover them, since
            // ComputeStartupReapComplete gates that scan on Linux and this reviewer is POSIX-only,
            // meaning macOS in practice. Wired here, after registration, for the same reason the ACP
            // branch is.
            if (runtime is AntigravityHostedAgentRuntime antigravity) {
                antigravity.PidCallbacks = new AgyPidRecordCallbacks(
                    Record: pid => PersistPidRecordOrThrow(agent, pid, null),
                    Clear:  () => DeletePidRecord(agent.Id));
            }

            // Start reading output
            _ = ReadAgentOutputAsync(agent);

            // Fallback session-id discovery: the primary link (the spawned harness's
            // session-start hook POSTing agent_host_id to /hooks/session-start) silently
            // breaks when the hook can't authenticate (e.g. expired kcap token → 401) or
            // doesn't land in time (an unattended/borrowed reviewer), leaving the agent
            // without a session id for correlation/display. The daemon can discover the id
            // itself from the transcript/rollout the harness writes and report it over its
            // own authenticated connection. Vendor-dispatched — Claude reads its per-worktree
            // project transcript, Codex reads its ~/.codex/sessions rollout; vendors without a
            // daemon-side locator no-op (the hook stays their only source). Best-effort
            // background task, cancelled with the agent — the server converges incarnations on
            // daemon liveness, so a missing id never blocks a launch.
            _ = DetectSessionIdAsync(agent, cmd.Vendor, spawnedAtUtc);

            // Report the resolved model so the server can display / validate the real model the agent
            // is running. Best-effort: never let a report failure break the launch.
            if (cmd.ExplicitReviewerModel is { } explicitReviewerModel) {
                // Task 8: a protocol-v3 explicit-model reviewer launch reports the CONCRETE
                // resolved model via the dedicated ReportExplicitReviewerModelResolved channel, keyed by
                // the durable LaunchAttemptId, so the server's one-shot waiter can validate + re-price it.
                ReportExplicitReviewerModel(agentId, cmd.Vendor, explicitReviewerModel, runtimeFactory);
            } else if (string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase)) {
                // Legacy path (name/arity/behavior UNCHANGED): the dispatched `model` may be the "default"
                // no-override sentinel, in which case Codex resolves the model from ~/.codex/config.toml.
                // Codex-only — Claude/other agents never call the ReportAgentResolvedModel hub.
                ReportResolvedModel(agentId, cmd.Vendor, model);
            }

            // Phase B2-b (sequenced-settlement design §4.2.2): the launch executed — the agent is registered.
            // The sequenced lane turns this into a terminal CommandAck(Processed) with a LIVE CurrentState read
            // at ack time; a fire-and-forget read loop that already finalized+cleaned the agent (e.g. an
            // immediate-exit runtime) reads as launch_failed_cleaned instead. Legacy callers ignore the value.
            return _agents.TryGetValue(agentId, out var launched)
                ? new CommandOutcome(CommandOutcomeKind.LaunchExecuted, agentId, launched.SessionId)
                : new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
        } catch (Exception ex) {
            LogLaunchFailed(ex, agentId);

            // Phase B (D1): a post-insert failure (agent already in _agents — e.g. a throwing
            // RegisterAgentAsync) routes teardown through the single-flight CleanupAgentAsync so it
            // can't strand a live child; a pre-insert failure falls through to the transient cleanup below.
            if (_agents.ContainsKey(agentId)) {
                await CleanupAgentAsync(agentId);
                // §3.5: never forward a null/whitespace ex.Message raw.
                await _server.LaunchFailedAsync(agentId, AcpHostedAgentRuntimeFactory.DescribeLaunchFailure(ex));
                return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
            }

            // If a reviewer token was minted before the failure and no AgentInstance was created to
            // own it, revoke it here so it can't linger in the bridge's live-token set.
            if (reviewerToken != null) _permissionBridge.RevokeReviewerToken(reviewerToken);

            // Only tear down a worktree we OWN. A borrowed cwd is the user's real checkout — never
            // remove it, its branch, or its Claude project symlink on a failed launch (spec's top
            // safety invariant; mirrors the normal-stop guard in CleanupAgentAsync). For a borrowed
            // launch there is nothing daemon-created to clean up anyway (no CreateAsync, no mirror,
            // no attachments), and StartAsync throwing means mcpConfigPath was never assigned.
            if (worktree != null && work == WorkLocation.OwnedWorktree) {
                if (_launchers.TryGetValue(cmd.Vendor, out var launcherForCleanup)) {
                    try {
                        // Build a transient AgentInstance with a no-op PTY just so launcher.Cleanup
                        // can run its symlink/mcp-config teardown without a live agent.
                        var transient = new AgentInstance(
                            agentId,
                            prompt,
                            model,
                            effort,
                            repoPath,
                            cmd.Vendor,
                            new PtyHostedAgentRuntime(cmd.Vendor, NoopPtyProcess.Instance),
                            worktree,
                            new CancellationTokenSource()
                        ) {
                            McpConfigPath = mcpConfigPath
                        };
                        launcherForCleanup.Cleanup(transient);
                    } catch (Exception cleanupEx) {
                        LogCleanupStepFailed(cleanupEx, "launcher.Cleanup (failed-launch)", agentId);
                    }
                }

                try { await WorktreeManager.RemoveAsync(worktree); } catch {
                    /* best-effort */
                }
            }

            // §3.5: this is the landing site for a factory pre-StartAsync failure — including a
            // reclassified AcpReviewerReapedException (design spec §3.2) — thrown before any
            // AgentInstance exists, so DescribeLaunchFailure's fallback is the ONLY cover a blank
            // message gets here.
            await _server.LaunchFailedAsync(agentId, AcpHostedAgentRuntimeFactory.DescribeLaunchFailure(ex));

            // Phase B2-b (sequenced-settlement design §4.2.2): a pre-insert failure — the worktree (if any)
            // was torn down and no agent was ever registered; terminal for the sequenced lane.
            return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, agentId);
        }
    }

    /// <summary>
    /// Best-effort: report the model the agent will actually run to the server so the UI can
    /// show the real model instead of the dispatched value. Codex-only — the hub contract
    /// (ReportAgentResolvedModel) and the config resolution are Codex-specific, so the caller
    /// gates this on <c>vendor == "codex"</c>. The dispatched <paramref name="model"/> may be
    /// the "default" no-override sentinel (or empty), in which case Codex resolves the model from
    /// <c>~/.codex/config.toml</c> — we resolve the same value here. Never throws: a resolve/report
    /// failure must not break launch.
    /// </summary>
    void ReportResolvedModel(string agentId, string vendor, string model) {
        try {
            var isDefault = string.IsNullOrEmpty(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);

            var resolved = isDefault && string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase)
                ? CodexResolvedModel(model)
                : model;

            if (string.IsNullOrEmpty(resolved)) return;

            _ = _server.ReportAgentResolvedModelAsync(agentId, resolved);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Task 8: reports the CONCRETE resolved model for an explicit-model reviewer launch via the
    /// dedicated <c>ReportExplicitReviewerModelResolved</c> channel. The concrete model is the exact,
    /// server-pinned <c>LaunchModel</c> the daemon launched with (never a recanonicalized or date-suffixed
    /// value — for Codex the slug-level equivalence key is date-SENSITIVE, so a date-suffixed
    /// session-metadata model would drift it; for Claude the family-level key absorbs any date, so the
    /// launch model's key still matches the pinned family anchor). The reported equivalence key is DERIVED
    /// from that concrete model via the vendor's own resolver so the server's key-equality validation is
    /// meaningful. Best-effort: a resolver-absent / unresolvable model or a send failure never breaks the
    /// launch — the server's report waiter then times out and fails the attempt CLOSED.
    /// </summary>
    void ReportExplicitReviewerModel(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IHostedAgentRuntimeFactory runtimeFactory) {
        try {
            var report = BuildExplicitReviewerModelReport(agentId, vendor, block, runtimeFactory.ReviewerModelResolver);

            if (report is null) {
                LogExplicitReviewerModelUnreportable(agentId, vendor, block.LaunchModel);
                return;
            }

            _ = _server.ReportExplicitReviewerModelResolvedAsync(report);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Reads the top-level <c>model = "…"</c> from <c>~/.codex/config.toml</c> (honouring
    /// <c>CODEX_HOME</c> via <see cref="CodexPaths"/>); falls back to <paramref name="fallback"/>
    /// when the file is missing/unreadable or has no top-level model key.
    /// </summary>
    string CodexResolvedModel(string fallback) {
        var fromConfig = CodexConfigToml.ReadTopLevelModel(_harnesses.Of<CodexHarness>().Paths.ConfigToml);

        return string.IsNullOrWhiteSpace(fromConfig) ? fallback : fromConfig;
    }

    static readonly TimeSpan GitGuardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads <c>git remote get-url origin</c> at <paramref name="repoPath"/>
    /// and normalises it to <c>host/owner/repo</c> form (or null if missing
    /// or if git times out / blocks on a credential prompt). Used as a final
    /// guard before a hosted PR review is launched, so it must never hang the
    /// launch path.
    /// </summary>
    static async Task<string?> GetOriginRemoteAsync(string repoPath) {
        try {
            var psi = new ProcessStartInfo("git", ["remote", "get-url", "origin"]) {
                WorkingDirectory       = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                Environment = {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GCM_INTERACTIVE"]     = "Never"
                }
            };

            using var proc = Process.Start(psi);

            if (proc is null) return null;

            using var cts = new CancellationTokenSource(GitGuardTimeout);

            try {
                await proc.WaitForExitAsync(cts.Token);
            } catch (OperationCanceledException) {
                try { proc.Kill(true); } catch {
                    /* best-effort */
                }

                return null;
            }

            if (proc.ExitCode != 0) return null;

            var raw = (await proc.StandardOutput.ReadToEndAsync()).Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : RemoteMatcher.NormalizeRemoteUrl(raw);
        } catch {
            return null;
        }
    }

    async Task ReadAgentOutputAsync(AgentInstance agent) {
        // The terminal-output enqueue back-pressures (awaits) when the send queue is
        // full. Tie that await to BOTH this agent's stop (ReadCts) and daemon shutdown
        // so HandleStopAgent releasing ReadCts unblocks the read loop — otherwise a
        // stop mid-outage would leave the finally-block finalization/cleanup stalled
        // until the whole daemon exits.
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);

        // An UNATTENDED reviewer (bypassPermissions, no human present) can wedge forever on a
        // one-time consent/trust dialog — the original silent failure. Watch its PTY stream for the
        // known banners and fail the launch fast with an actionable reason instead of dying at the
        // server's session-id timeout. Only for unattended review-flow agents: an interactive agent's
        // human viewer can dismiss the prompt themselves, so we must not fail-fast for them.
        var dialogDetector = agent is { Kind: LaunchKind.ReviewFlow, Runtime.EmitsTerminalOutput: true }
            ? new ConsentDialogDetector()
            : null;

        try {
            await foreach (var data in agent.Runtime.ReadOutputAsync(agent.ReadCts.Token)) {
                agent.LastOutputAt      = DateTime.UtcNow;
                agent.HasReceivedOutput = true;
                // PTY output IS the activity signal for a PTY-hosted agent — no turn gate applies.
                agent.ActivityClock.Advance();

                if (agent.Status == "Starting") {
                    SetAgentStatus(agent, "Running");
                    if (!agent.IsPrivate) _ = _server.AgentStatusChangedAsync(agent.Id, "Running", agent.SessionId);
                }

                // Consent/trust dialogs are a PRE-SESSION concern: they render once at startup, before
                // any session exists. Once the session is live (SessionId resolved from the transcript
                // by DetectSessionIdAsync) the dialog phase is over — stop scanning so ordinary
                // reviewer/tool output that merely quotes a banner phrase (e.g. a reviewer reading the
                // detector's own source) can't latch a false wedge and kill a healthy reviewer.
                if (dialogDetector is not null) {
                    if (agent.SessionId is not null) {
                        dialogDetector = null; // session live — release the detector + its window
                    } else if (dialogDetector.Observe(data) is { } wedgeReason) {
                        await FailWedgedLaunchAsync(agent, data, wedgeReason);
                        return; // stop reading — the finally runs finalize + cleanup (kills the wedged PTY)
                    }
                }

                // Append to the replay buffer AND fan out to local sinks atomically under
                // SinksLock — paired with attach taking its snapshot + subscribing under the
                // same lock, so a chunk can't land in both a new client's replay and its live
                // stream (duplication), nor in neither (gap). TryEnqueue is non-blocking so the
                // lock is held only briefly; a slow client force-detaches inside TryEnqueue.
                lock (agent.SinksLock) {
                    agent.OutputBuffer.Append(data);
                    foreach (var sink in agent.LocalSinks) sink.TryEnqueue(data);
                }

                if (!agent.IsPrivate) {
                    var base64 = Convert.ToBase64String(data);

                    if (agent.IsLocalSpawned) {
                        // Local-first: a registered local agent has a live local terminal as its
                        // primary surface, so NEVER block the PTY read loop on a remote tunnel
                        // stall. Enqueue non-blocking; a full backlog (sustained outage) drops +
                        // counts the chunk and the web mirror re-syncs from the server's own buffer
                        // on reconnect. Keeps the local terminal responsive when the cloud hiccups.
                        _server.TrySendTerminalOutput(agent.Id, base64);
                    } else {
                        // Hosted: the server is the only consumer, so back-pressure here when the
                        // queue is full (slow/down transport) — a chunk is never dropped, since
                        // losing one byte garbles the whole redraw-TUI mirror. sendCts
                        // releases this await on agent stop or daemon shutdown.
                        await _server.SendTerminalOutputAsync(agent.Id, base64, sendCts.Token);
                    }
                }
            }
        } catch (OperationCanceledException) {
            /* expected on stop */
        } catch (Exception ex) {
            LogOutputReadError(ex, agent.Id);
        } finally {
            // Daemon shutdown: ServerConnection's _ct (= lifetime.ApplicationStopping)
            // is cancelled and the hub is being disposed, so every server call here
            // would throw TaskCanceledException. DisposeAsync owns the local cleanup
            // path for in-flight agents; the server detects the daemon disconnection
            // and ends its sessions on its own. Skip to avoid noisy warnings.
            if (!_shutdownCts.IsCancellationRequested) {
                await FinalizeAgentRunAsync(agent);
            }
        }
    }

    /// <summary>
    /// Fails a launch that the PTY dialog detector caught wedged on a consent/trust dialog: reports
    /// an actionable <c>LaunchFailed</c> (so the server surfaces it instead of timing out silently),
    /// persists the terminal tail for post-mortem, terminates the wedged (still-alive) process, and
    /// cancels the read loop so its finally runs the normal finalize + cleanup. Sets Status="Failed"
    /// FIRST so FinalizeAgentRunAsync skips its own startup-failure classification (no double report).
    /// </summary>
    async Task FailWedgedLaunchAsync(AgentInstance agent, byte[] triggeringChunk, string reason) {
        LogConsentDialogWedge(agent.Id, reason);

        // The detector inspects a chunk BEFORE the read loop appends it to OutputBuffer, so append the
        // triggering chunk now — otherwise a banner delivered in the first chunk (the common case) is
        // absent from the persisted tail, defeating the very capture this log exists to preserve.
        // TerminalOutputBuffer.Append is self-synchronized; the read loop has stopped feeding it.
        agent.OutputBuffer.Append(triggeringChunk);

        // Capture the banner before termination/cleanup discards the buffer.
        PersistFailedLaunchLog(agent, reason);

        SetAgentStatus(agent, "Failed");
        agent.PendingEndReason = "consent_dialog_wedge";

        if (!agent.IsPrivate) {
            _ = _server.LaunchFailedAsync(agent.Id, reason);
            _ = _server.AgentStatusChangedAsync(agent.Id, "Failed", agent.SessionId);
            _ = _server.AppendAgentRunEventAsync(agent.Id, new AgentRunStopped("failed", null));
        }

        // The wedged process is still ALIVE on the dialog (unlike an ordinary startup failure, which
        // has already exited) — actively terminate it so it can't hold a daemon slot.
        try { await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) { LogStopError(ex, agent.Id); }

        // Cancel the read loop's token so the fallback session-id poll (DetectSessionIdAsync)
        // stops too; the loop itself exits via the `return` at the call site.
        try { await agent.ReadCts.CancelAsync(); } catch { /* best-effort */ }
    }

    /// <summary>Best-effort: persist the tail of an agent's PTY output to the retained failed-launch
    /// log. Never throws (FailedLaunchLog swallows I/O errors) — a diagnostic write must not disturb
    /// teardown.</summary>
    void PersistFailedLaunchLog(AgentInstance agent, string reason) {
        var path = _failedLaunchLog?.Persist(agent.Id, agent.OutputBuffer.Snapshot(), reason);
        if (path is not null) LogFailedLaunchCaptured(agent.Id, path);
    }

    /// <summary>
    /// True when a published launch-window reap verdict (or its already-claimed report flag) forbids
    /// any NON-failure <c>AgentStatusChanged</c> for this agent — such a transition clears the
    /// <c>FailureReason</c> the verdict's <c>LaunchFailed</c> set server-side (design spec §3.3). The
    /// single source of that rule, consulted by every non-failure status emitter that can race the
    /// finalizer's report: the finalizer's own exit-code classification, the stop gate, and reconnect
    /// re-registration (finding 2).
    ///
    /// <para>Both signals are read with proper cross-thread ordering — the flag via
    /// <see cref="System.Threading.Volatile"/>, the verdict via the runtime's
    /// lock-synchronised <see cref="AcpHostedAgentRuntime.ReadVerdict"/> — and ORed because the
    /// verdict is PUBLISHED (at reap-claim time) strictly before the finalizer's CAS flips the flag,
    /// so a same-tick emitter could otherwise read the flag as still 0 while the verdict already
    /// exists. Post-window reaps leave both false, preserving byte-identical teardown for that
    /// case.</para>
    /// </summary>
    static bool VerdictForbidsNonFailureStatus(AgentInstance agent) =>
        Volatile.Read(ref agent.LaunchFailureVerdictReported) != 0
     || (agent.Runtime is AcpHostedAgentRuntime runtime
         && runtime.ReadVerdict() is { ReapedInsideLaunchWindow: true });

    async Task FinalizeAgentRunAsync(AgentInstance agent) {
        try {
            // Design spec §3.3: the registered-agent report seam, and the FIRST action here —
            // strictly before the process-exit wait below. The runtime's Verdict is published at
            // reap-CLAIM time (TryStartReap), not at process exit, so waiting on exit first would
            // only widen the race against the server's reviewer-readiness wait for no benefit.
            // Post-window reaps (ReapedInsideLaunchWindow == false) are deliberately NOT reported
            // here — today's teardown for that case must stay byte-identical (documented residual,
            // spec §1).
            // ReadVerdict (not the plain Verdict property): the reap that produced this verdict can be
            // racing us on another thread, publishing it at the tail of a critical section its own
            // _cts.Cancel() drove us into — reading the plain property there sees null and skips the
            // report permanently (finding 1). ReadVerdict blocks until the claim has committed.
            if (agent.Runtime is AcpHostedAgentRuntime acpRuntime
                && acpRuntime.ReadVerdict() is { ReapedInsideLaunchWindow: true } verdict
                && Interlocked.CompareExchange(ref agent.LaunchFailureVerdictReported, 1, 0) == 0) {
                // Force terminal Failed BEFORE the report await, not after (finding 2). A concurrent
                // status emitter — a reconnect re-registration, a racing stop — must observe the
                // failure state so it cannot emit a non-failure AgentStatusChanged that clears the
                // FailureReason the LaunchFailed below is about to set server-side. Setting it here
                // also makes the exit-code-driven classification further down a no-op for this agent.
                // The CAS above (already committed) and this status are both visible before we await.
                SetAgentStatus(agent, "Failed");

                if (!agent.IsPrivate) {
                    // Own try/catch, deliberately separate from this method's own outer one below —
                    // a report fault must never skip CleanupAgentAsync or the normal unregister
                    // (capacity decrement + the durable death record ride AgentUnregistered and must
                    // still run exactly once).
                    try {
                        await _server.LaunchFailedAsync(
                            agent.Id,
                            MapLaunchFailureReason(verdict.Reason, nameof(AcpHostedAgentRuntime.TerminationVerdict)));
                    } catch (Exception ex) {
                        LogVerdictReportFailed(ex, agent.Id);
                    }
                }
            }

            // PTY output can end before waitpid reports the child as exited.
            // Wait briefly for the process to finalize so we get a real exit code.
            await agent.Runtime.WaitForExitAsync(TimeSpan.FromSeconds(5));

            var exitCode = agent.Runtime.ExitCode;

            var status = agent.Runtime.HasExited
                ? exitCode is null or 0 ? "Completed" : "Failed"
                : "Failed";

            if (agent.Status is not "Completed" and not "Failed") {
                // A startup failure means the process exited before establishing
                // a real interactive session (CLI config error, auth issue, immediate
                // crash). A real session keeps producing output throughout its
                // lifetime, so the gap between CreatedAt and LastOutputAt is the
                // discriminator: tiny gap → startup failure; sustained → real session.
                //
                // We avoid agent.Status because the first output chunk flips it to
                // "Running" — a one-line error banner triggers that flip too. We
                // also avoid wall-clock since spawn: a user who types /exit shortly
                // after starting produces a short-but-real session that must not be
                // flagged as a launch failure. HasReceivedOutput guards
                // against a no-output process whose CreatedAt/LastOutputAt
                // initializers happened to straddle a long pause.
                //
                // This whole heuristic is output-stream-centric and only applies to a runtime
                // whose ReadOutputAsync yields real terminal bytes (PTY). A no-terminal runtime
                // (ACP/cursor) never has output to key off, so gate the check on
                // EmitsTerminalOutput — such a runtime is Completed/Failed purely by exit code
                // (Fix B/E), never misclassified as a startup failure just for having
                // produced no output.
                if (agent.Runtime.EmitsTerminalOutput && IsStartupFailure(agent.CreatedAt, agent.LastOutputAt, agent.HasReceivedOutput)) {
                    var output = ExtractTerminalText(agent.OutputBuffer);

                    var reason = !string.IsNullOrWhiteSpace(output)
                        ? output
                        : exitCode is null or 0
                            ? "Process exited before establishing a session"
                            : $"Process exited immediately (exit code {exitCode})";

                    status = "Failed";

                    LogStartupFailed(agent.Id, exitCode, reason);

                    // Persist the PTY tail before cleanup drops the in-memory buffer and removes the
                    // worktree, so a startup failure is diagnosable post-mortem (see FailedLaunchLog).
                    PersistFailedLaunchLog(agent, reason);

                    if (!agent.IsPrivate) _ = _server.LaunchFailedAsync(agent.Id, reason);
                }

                SetAgentStatus(agent, status);

                // PrivateLocal agents make no per-agent server calls (deny-all).
                if (!agent.IsPrivate) {
                    await _server.AgentStatusChangedAsync(agent.Id, status, agent.SessionId);

                    var stopReason = status == "Completed" ? "exited" : "failed";

                    await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunStopped(stopReason, exitCode));
                }
            }

            LogAgentExited(agent.Id, exitCode);

            // For an ACP agent with a live forwarder, give the transcript a bounded chance to drain
            // BEFORE ending the session — this must NEVER pin shutdown (see
            // FinalDrainAcpTranscriptAsync's remarks); it always returns within AcpFinalDrainBudget
            // regardless of outcome. PTY agents have no AcpForwarder and take none of this path — the
            // runtime is disposed exactly where it always was, inside CleanupAgentAsync below.
            if (agent.AcpForwarder is { } acpForwarder) {
                await FinalDrainAcpTranscriptAsync(agent, acpForwarder);
            }

            // Cancel the per-agent ACP CTS unconditionally here — not only inside
            // FinalDrainAcpTranscriptAsync's own timeout branch above — so a bind/setup task that's
            // STILL in flight (the agent exited before its bind ever completed, so AcpForwarder is
            // still null and the drain step above never ran at all) observes cancellation now and can
            // abort at its liveness check (StartAcpForwardingAsync) before it ever registers a
            // binding for an agent that is finalizing right now. Runs BEFORE EndAgentSessionAsync so
            // any forwarder is fully stopped before the binding goes terminal server-side (the same
            // ordering the drain above already protects). Idempotent/harmless if already cancelled by
            // the drain step.
            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort */ }
            }

            // Tell the server to end the AgentSession. Claude doesn't reliably fire
            // its own session-end hook on SIGTERM/exit, so without this call the
            // session would stay "active" forever in the read model. Server-side is
            // idempotent — if claude did fire session-end first, this is a no-op.
            // Reason is read from agent.PendingEndReason so a user-initiated stop is
            // recorded as "agent_stopped" rather than "agent_exited".
            //
            // EndAgentSessionAsync retries across SignalR reconnects, so it can block
            // for the length of an outage. We must NOT let that stall local cleanup
            // (worktree/process disposal, removing the agent from _agents), so bound how
            // long we WAIT on it to EndAgentSessionBudget. The retry keeps running in the
            // background — a reconnect shortly after still lands the session-end — and a
            // genuinely long outage falls back to server-side daemon-disconnect reconcile.
            //
            // PrivateLocal agents have no server-side session to end (deny-all).
            //
            // §2.7 B6 arm-A: a PARKED reviewer (ParkReviewerAsync got a durable Parked ack, which stamped
            // ReviewerParkedResumableReason here) completes EVERY local-teardown step EXCEPT this hosted
            // session-end — its Codex app-server thread must survive for a later thread/resume, and the
            // server's B5 close authority owns the eventual close. Every other teardown step (the ACP
            // final-drain above, UnregisterAcpBinding + CleanupAgentAsync below) still runs, so the slot
            // is freed exactly as a reap frees it.
            if (!agent.IsPrivate && agent.PendingEndReason != ReviewerParkedResumableReason) {
                var endTask = _server.EndAgentSessionAsync(agent.Id, agent.PendingEndReason);

                try {
                    var result = await endTask.WaitAsync(EndAgentSessionBudget, _shutdownCts.Token);

                    // The daemon doesn't track sessionId on its own (only agentId), so
                    // the server returns it in the result. Spawn what's-done locally
                    // when the server says yes.
                    if (result is { GenerateWhatsDone: true, SessionId: not null }) {
                        SpawnWhatsDoneGenerator(result.SessionId);
                    }
                } catch (TimeoutException) {
                    // Outage outlasted the budget. Don't block cleanup; the retry continues
                    // in the background (observed below so a later fault isn't unobserved).
                    LogEndSessionTimedOut(agent.Id, EndAgentSessionBudget.TotalSeconds);
                    ObserveEndSessionInBackground(endTask, agent.Id);
                } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) {
                    // Shutdown fired mid-wait — connection is being torn down. Server
                    // detects daemon disconnection independently. No warning needed.
                } catch (Exception ex) {
                    LogEndSessionFailed(ex, agent.Id);
                }
            }

            // Drop the reconnect re-bind registration now that EndAgentSessionAsync above has
            // (best-effort) made this binding terminal server-side — a later reconnect must not try
            // to re-bind a session that's already ended. Unconditional — NOT gated on AcpForwarder
            // having ever been set — so a binding a late/racing setup managed to register despite the
            // cancellation above (StartAcpForwardingAsync's liveness check narrows but can't fully
            // eliminate that window) still gets cleaned up here; UnregisterAcpBinding is a no-op when
            // nothing was ever registered, so this call is always safe to make unconditionally.
            _server.UnregisterAcpBinding(agent.Id);

            // Clean up worktree and unregister from server. Runs unconditionally — even
            // when end-session timed out and is still retrying in the background — so a
            // prolonged outage can never pin the agent in _agents or leak its worktree.
            await CleanupAgentAsync(agent.Id);
        } catch (Exception ex) {
            LogCleanupError(ex, agent.Id);
        }
    }

    /// <summary>Budget for the graceful phase of a stop — applied SEPARATELY to sending the stop and
    /// to waiting for the exit it asks for, because sending it is itself unbounded work on some
    /// runtimes (see <see cref="StopAgentCoreAsync"/>). Settable only so a test need not spend it for
    /// real; production never changes it.</summary>
    internal TimeSpan GracefulExitWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): the sequenced stop. Routes through the
    /// processor's serial lane (accepted exactly-in-order, executed once, terminal StopExecuted outcome →
    /// CommandAck). Falls back to a direct <see cref="HandleStopAgent"/> call if the processor is absent
    /// (never happens in production — publication precedes handler wiring in the ctor).
    ///
    /// <para>§3.3: the returned execution-completion task is NOT awaited — no handler awaits launch or stop
    /// execution any more. Acceptance is still answered synchronously on the pump; the stop's EXECUTION
    /// queues behind whatever the lane is already running, exactly as the shipped pump serialized it.</para></summary>
    Task HandleStopAgentV2(StopAgentV2 cmd) {
        if (Processor is { } proc) {
            var execution = proc.SubmitAsync(
                new SequencedItem(SequencedKind.Stop, cmd.Epoch, cmd.Seq, cmd.CommandId, cmd.AgentId),
                async () => {
                    await HandleStopAgent(cmd.AgentId);
                    return new CommandOutcome(CommandOutcomeKind.StopExecuted, cmd.AgentId);
                });
            _ = ObserveDetachedExecution(execution, cmd.AgentId);
            return Task.CompletedTask;
        }

        return HandleStopAgent(cmd.AgentId); // no processor — direct, as shipped
    }

    /// <summary>§3.3: the UN-SEQUENCED stop command — the shape every server stop actually has (user Stop,
    /// admin stop, the registry-independent physical stop / retry-until-gone reaper), since the sequenced
    /// tuple rides only the review-flow settlement lane (§1.9). Committed onto the same serial lane as
    /// launches so a stop can never overtake a launch that arrived before it, and never refused for its
    /// format.
    ///
    /// <para>No reply surface exists for this command (§1.8), so every non-committed outcome is a log and
    /// nothing more: <c>Coalesced</c> means an identical stop is already queued and unstarted;
    /// <c>DroppedUnknownTarget</c> is already logged by the processor, which owns that drop; <c>Refused</c>
    /// means the daemon is tearing down, and its own teardown kills the registered children.</para></summary>
    async Task HandleUnsequencedStopAgent(string agentId) {
        var proc = SnapshotProcessorReservingInlineSlot();

        if (proc is null) {
            try { await HandleStopAgent(agentId); }
            finally { ReleaseInlineSlot(); }
            return;
        }

        var outcome = proc.SubmitUnsequenced(new UnsequencedItem(
            UnsequencedKind.Stop, agentId, UnsequencedStopPayloadKey, () => HandleStopAgent(agentId)));

        if (outcome is SubmitOutcome.Refused) LogUnsequencedStopRefused(agentId);
    }

    /// <summary>How long a reap claim waits for the per-agent delivery section before giving up on it.
    /// Sized UNDER <see cref="HeartbeatInterval"/> on purpose: a delivery parked longer than this leaves
    /// at most one claim waiter alive per agent, instead of accumulating one per tick for as long as the
    /// parked write lasts. Settable only so a test need not spend a real heartbeat proving the timeout
    /// arms behave; production never changes it.
    ///
    /// <para>Note it is SHORTER than a delivery's own worst-case in-section time (the borrowed-snapshot
    /// refresh is budgeted 30s), so the unfenced timeout arm fires against healthy deliveries too — by
    /// design, and handled by <see cref="HandleSendInput"/>'s pre-write re-read of the claim latch
    /// rather than by inflating this wait, which would only restore the deadlock it exists to
    /// prevent.</para></summary>
    internal TimeSpan ReapClaimGateWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a mid-band lifetime candidate (past <see cref="DaemonConfig.ReviewerMaxLifetime"/>,
    /// under the hard ceiling, no turn held) must have been quiet before selection. One heartbeat:
    /// long enough for a just-delivered round's asynchronous busy signal to land, short enough that
    /// a genuinely between-rounds reviewer is reaped on the next sweep.</summary>
    internal TimeSpan LifetimeReapQuietWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Execute one selected reap: claim it under the per-agent fence, and stop the agent only
    /// if the claim was WON. Fire-and-forget from the heartbeat, so it contains its own faults —
    /// <see cref="StopAgentCoreAsync"/> already swallows the stop's, leaving only the gate wait.</summary>
    async Task ReapReviewerAsync(ReapCandidate candidate) {
        try {
            if (!await TryClaimReapAsync(candidate)) return;

            await StopClaimedReapAsync(candidate);
        } catch (Exception ex) {
            LogReapFailed(ex, candidate.Id, candidate.Reason);
        }
    }

    /// <summary>§2.7 B6 arm-A: PARK a resumable reviewer instead of reaping it — free its daemon slot
    /// while keeping its Codex app-server thread alive for a later <c>thread/resume</c> (B4). Mirrors
    /// <see cref="ReapReviewerAsync"/>'s claim fence, then, on a DURABLE-park ack, takes the same local
    /// teardown a reap does (<see cref="StopClaimedReapAsync"/> → stop process, unregister, free slot)
    /// MINUS the hosted <c>EndAgentSession</c> emission: <see cref="FinalizeAgentRunAsync"/> suppresses
    /// that when <see cref="AgentInstance.PendingEndReason"/> is <see cref="ReviewerParkedResumableReason"/>,
    /// because the server's B5 close authority owns the eventual close and a resume needs the thread
    /// intact. Fire-and-forget from the heartbeat, so — like <see cref="ReapReviewerAsync"/> — it
    /// contains its own faults, and it never strands <see cref="AgentInstance.ParkAttemptInFlight"/>.
    ///
    /// <para>Three-way on the ack (<see cref="ServerConnection.ReportParticipantParkedAsync"/> never
    /// throws): <see cref="ParkAck.Parked"/> completes the park teardown (session-end suppressed);
    /// <see cref="ParkAck.Rejected"/> keeps the won claim but restores a normal end reason and ends the
    /// reviewer through the same stop path (so a refused park is cleaned up, never left dangling — and
    /// NOT re-claimed, which could now abort on activity and strand it); <see cref="ParkAck.Ambiguous"/>
    /// tears down NOTHING and RESTORES the pre-attempt state (releasing the reap latch the claim took —
    /// the one park-specific exception to <see cref="AgentInstance.ReapClaimed"/> being write-once,
    /// since a claim that neither parks nor reaps must not condemn a live agent) so the next sweep
    /// re-selects and retries.</para></summary>
    async Task ParkReviewerAsync(ReapCandidate candidate) {
        var a = candidate.Agent;

        // One park attempt at a time per agent (a sweep skips an agent already parking); reset on an
        // ambiguous ack / lost claim / fault below so a later sweep can retry.
        a.ParkAttemptInFlight = true;
        try {
            // Claim/revalidate under the per-agent fence EXACTLY as the reap path does: incarnation +
            // status + the activity generation captured at selection (FencedOnActivity: true — a
            // delivery since selection has advanced the seq and the park aborts here). A lost claim
            // tears down nothing; clear the guard so the next sweep can retry.
            if (!await TryClaimReapAsync(candidate)) {
                a.ParkAttemptInFlight = false;

                return;
            }

            // The canonical session id the server keys the park (and a later resume) on: the app-server
            // thread id, exposed by the runtime's IAcpTranscriptSource facet.
            var canonicalSessionId = (a.Runtime as IAcpTranscriptSource)?.AcpSessionId ?? "";

            // Never throws: a transport error / timeout / unknown-method folds to Ambiguous rather than a
            // false Rejected, so an uncertain reply never triggers a destructive teardown.
            var ack = await _server.ReportParticipantParkedAsync(
                a.Id, canonicalSessionId, ReviewerParkedResumableReason, _shutdownCts.Token);

            switch (ack) {
                case ParkAck.Parked:
                    // Durable park acked. Stamp ReviewerParkedResumableReason NOW — this is the
                    // authoritative set (the claim deliberately left a neutral default so a child-exit
                    // during the ack await above could not suppress an unconfirmed park; see
                    // TryLatchClaim). It both attributes the teardown AND drives FinalizeAgentRunAsync to
                    // SUPPRESS the hosted session-end so the thread survives — and it is set strictly
                    // before StopClaimedReapAsync below, whose stop is what triggers finalization.
                    // Complete every other local-teardown step through the shared stop path.
                    a.PendingEndReason = ReviewerParkedResumableReason;
                    LogReviewerParked(a.Id, canonicalSessionId);
                    await StopClaimedReapAsync(candidate);
                    break;

                case ParkAck.Rejected:
                    // The server refused the park (e.g. a terminal close already won). End the reviewer
                    // normally instead of leaving it dangling: keep the won claim, but restore a normal
                    // end reason so FinalizeAgentRunAsync's session-end FIRES (it is suppressed only for
                    // the park reason), then run the same stop path.
                    a.PendingEndReason = ReviewerParkRejectedReason;
                    LogReviewerParkRejected(a.Id);
                    await StopClaimedReapAsync(candidate);
                    break;

                case ParkAck.Ambiguous:
                    // No definite reply. Tear down NOTHING; restore the pre-attempt state so the next
                    // sweep re-selects and re-claims. Order matters: clear the reap-reason stamp and
                    // RELEASE the reap latch (so the retry's claim can win) BEFORE clearing the in-flight
                    // guard LAST, so no concurrent sweep re-selects a half-restored agent.
                    a.PendingEndReason = "agent_exited";
                    Interlocked.Exchange(ref a.ReapClaimed, 0);
                    LogReviewerParkAmbiguous(a.Id);
                    a.ParkAttemptInFlight = false;
                    break;
            }
        } catch (Exception ex) {
            // Fire-and-forget from the heartbeat (matching ReapReviewerAsync): contain the fault, and
            // NEVER strand the in-flight guard on an unexpected throw.
            a.ParkAttemptInFlight = false;
            LogParkFailed(ex, candidate.Id, candidate.Reason);
        }
    }

    /// <summary>The stop half of a WON claim, split out from <see cref="ReapReviewerAsync"/> so the
    /// re-check below is independently testable. <see cref="HandleStopAgent"/> re-resolves
    /// <paramref name="candidate"/>'s id from <c>_agents</c> rather than taking the claimed instance
    /// directly, so a relaunch that reused the id between the claim and this call would stop the FRESH
    /// incarnation on the claimed one's evidence. Unreachable today — ids are unique per launch — but
    /// this makes it structural rather than assumed: re-validate the map entry is still the claimed
    /// instance immediately before handing off, and abort (same "agent_gone" cause as the claim's own
    /// incarnation check) on mismatch instead of stopping whatever now answers to the id.</summary>
    async Task StopClaimedReapAsync(ReapCandidate candidate) {
        if (!_agents.TryGetValue(candidate.Id, out var current) || !ReferenceEquals(current, candidate.Agent)) {
            LogReapAborted(candidate.Id, candidate.Reason, "agent_gone");
            return;
        }

        await HandleStopAgent(candidate.Id);
    }

    /// <summary>The atomic reap claim (round-dispatch grace §3). Runs inside
    /// <see cref="AgentInstance.BorrowedSnapshotGate"/> — the same per-agent section every
    /// <see cref="HandleSendInput"/> delivery holds across its clock advance — so the claim and a
    /// delivery are mutually exclusive and exactly one of them wins.
    ///
    /// <para>Rechecking "immediately before" the stop would NOT do: selection is a snapshot, and any
    /// recheck outside the section leaves a recheck-to-stop window in which a delivery lands and the
    /// reviewer is torn down under an in-flight round anyway. Inside the section the two orders are
    /// both terminal: a delivery that got here first has already advanced
    /// <see cref="AgentActivityClock.ActivitySeq"/> past the captured generation and this returns false;
    /// a claim that got here first sets <see cref="AgentInstance.ReapClaimed"/> and the delivery
    /// refuses. A delivery that arrives after a won claim loses NORMALLY — nothing is delivered, so the
    /// round's own dispatch fails and the server heals it on resubmit.</para>
    ///
    /// <para>Scope, stated honestly: this fences the IDLE and WEDGE rules only. The absolute
    /// <see cref="DaemonConfig.ReviewerMaxLifetime"/> cap carries
    /// <see cref="ReapCandidate.FencedOnActivity"/> false and reaps regardless of activity, unchanged —
    /// it takes the section when it can (so incarnation is revalidated the same way), but it is never
    /// DEFERRABLE by it; see the timeout arm below.</para>
    ///
    /// <para><b>The gate wait is bounded, and that is a correctness requirement, not hygiene.</b> A
    /// delivery holds the section across a raw <c>write(2)</c> to the pty master
    /// (<c>UnixPtyProcess.WriteAsync</c>, no timeout, not cancellable), which parks indefinitely if the
    /// child has stopped draining its stdin. An unbounded wait here is therefore a circular wait: the
    /// reap that would SIGTERM/SIGKILL the child — the only thing that unblocks that write — would be
    /// queued behind the write itself. Before this fence existed the reap was unconditional and broke
    /// the cycle by construction; the two arms below restore that property deliberately.</para></summary>
    async Task<bool> TryClaimReapAsync(ReapCandidate candidate) {
        var agent = candidate.Agent;
        bool entered;

        try {
            entered = await agent.BorrowedSnapshotGate.WaitAsync(ReapClaimGateWait, _shutdownCts.Token);
        } catch (OperationCanceledException) {
            return false; // daemon teardown supersedes the reap — it kills the registered children itself
        }

        if (!entered) {
            // The section is held by a delivery that is not finishing. The two rules answer this
            // OPPOSITELY, and the asymmetry is the whole point:
            //
            //   FENCED (idle/wedge) — an in-progress delivery IS activity, so "nothing has happened"
            //   is already false and there is nothing to reclaim. Give up; the next heartbeat re-selects
            //   from a fresh snapshot. (The wait is sized under the heartbeat period so at most one
            //   claim waiter per agent is alive at a time, rather than one accumulating per tick.)
            //
            //   UNFENCED (absolute lifetime) — must not be deferrable by ANY amount of traffic, least
            //   of all by a delivery that may never complete. It claims lock-free and proceeds to the
            //   terminate, which is what releases a parked write (SIGKILL closes the slave and the
            //   write returns EIO). That reachability is NOT free: StopAgentCoreAsync's graceful "/exit"
            //   send goes to the same fd and had to be bounded before terminate could be relied on —
            //   see the bound there, which this arm depends on.
            if (candidate.FencedOnActivity) {
                LogReapAborted(candidate.Id, candidate.Reason, "delivery_in_progress");
                return false;
            }

            return TryClaimUnfencedReapWithoutSection(candidate);
        }

        try {
            // Incarnation, not just membership: an id can be republished by a relaunch, and reaping the
            // fresh incarnation on its predecessor's evidence is the same class of mistake as reaping on
            // a stale generation.
            if (!_agents.TryGetValue(candidate.Id, out var current) || !ReferenceEquals(current, agent)) {
                LogReapAborted(candidate.Id, candidate.Reason, "agent_gone");
                return false;
            }

            // A stop from any other source (server, local socket) flips the status first; the reviewer
            // is already on its way down and must not be stopped twice.
            if (agent.Status != "Running") {
                LogReapAborted(candidate.Id, candidate.Reason, "not_running");
                return false;
            }

            // One read for both the fence and the log below, so the age/idle an operator sees is the
            // state the decision was actually made on rather than a re-sample taken after it.
            var clock = agent.ActivityClock.Snapshot();

            // THE fence. The clock is monotonic, so any difference is an advance: something happened
            // to this agent after it was selected, which falsifies the idle/wedge claim outright. The
            // comparison is against the generation captured at selection — never a freshly re-derived
            // idle/threshold check, which would just re-run the same snapshot decision one moment later
            // and answer the same way.
            if (candidate.FencedOnActivity && clock.ActivitySeq != candidate.ActivityGeneration) {
                LogReapAbortedOnActivity(candidate.Id, candidate.Reason, candidate.ActivityGeneration, clock.ActivitySeq);
                return false;
            }

            // Claimed LAST, after every validation: an aborted reap must leave the latch clear. The CAS
            // (not a plain assignment) is what makes this single claim shared with the un-sectioned path
            // above, which by definition cannot be holding this gate.
            if (!TryLatchClaim(candidate)) return false;

            // Logged off the SAME clock the decision was made from — CreatedAt/LastOutputAt would
            // misreport an ACP reviewer's idle time as frozen since launch.
            _logger.LogInformation(
                "Reaping review-flow reviewer {AgentId} ({Reason}); age {AgeHours:F1}h, idle {IdleHours:F1}h",
                candidate.Id, candidate.Reason,
                TimeSpan.FromMilliseconds(clock.AgeMs).TotalHours,
                TimeSpan.FromMilliseconds(clock.IdleForMs).TotalHours);

            return true;
        } finally {
            agent.BorrowedSnapshotGate.Release();
        }
    }

    /// <summary>The absolute-lifetime claim when <see cref="AgentInstance.BorrowedSnapshotGate"/> could
    /// not be taken in time. Deliberately does everything the sectioned path does EXCEPT hold the
    /// section and re-read the activity generation — this rule never aborts on activity, so the seq is
    /// not evidence it needs, and the section is exactly what is unavailable. Incarnation is still
    /// proven (a relaunch under the same id must not be killed on its predecessor's age) and the claim
    /// is still single-flight, because both paths CAS the same latch.
    ///
    /// <para>It races a delivery by construction, and the delivery it races is NOT necessarily a
    /// wedged one. The section is legitimately held past this wait by healthy work — the borrowed-
    /// snapshot refresh alone is budgeted 30s — so this claim can fire against an in-flight, entirely
    /// well-behaved delivery. That is why <see cref="HandleSendInput"/> re-reads the latch immediately
    /// before its write instead of only on entry: a healthy delivery must ABORT there rather than
    /// complete into a condemned agent. The genuinely parked case resolves differently — the write is
    /// released by the terminate this claim leads to (which is reachable only because
    /// <see cref="StopAgentCoreAsync"/>'s graceful send is bounded), and fails.</para>
    ///
    /// <para>Either way nothing advances the clock and nothing is reported, so the round fails and the
    /// server heals it on resubmit.</para></summary>
    bool TryClaimUnfencedReapWithoutSection(ReapCandidate candidate) {
        var agent = candidate.Agent;

        if (!_agents.TryGetValue(candidate.Id, out var current) || !ReferenceEquals(current, agent)) {
            LogReapAborted(candidate.Id, candidate.Reason, "agent_gone");
            return false;
        }

        if (agent.Status != "Running") {
            LogReapAborted(candidate.Id, candidate.Reason, "not_running");
            return false;
        }

        if (!TryLatchClaim(candidate)) return false;

        LogUnsectionedReapClaim(candidate.Id, candidate.Reason, ReapClaimGateWait.TotalSeconds);

        return true;
    }

    /// <summary>The single-flight claim both paths share. 0→1 or nothing.</summary>
    bool TryLatchClaim(ReapCandidate candidate) {
        if (Interlocked.CompareExchange(ref candidate.Agent.ReapClaimed, 1, 0) != 0) {
            // Debug, unlike its sibling aborts: losing this CAS is a benign internal collision (another
            // sweep or the other claim path got there first and the agent IS being reaped), not an
            // operational event — but it must not be silent either, or the one abort an operator cannot
            // see is the one that fires when two paths disagree.
            LogReapAlreadyClaimed(candidate.Id, candidate.Reason);
            return false;
        }

        // §2.7 B6 arm-A: a PARK candidate must NOT stamp its suppress reason
        // (ReviewerParkedResumableReason) here at claim time. The claim happens BEFORE ParkReviewerAsync
        // awaits the park ack, and FinalizeAgentRunAsync suppresses the hosted session-end whenever it
        // sees that exact reason — so if the reviewer's app-server child exits DURING the ack await, the
        // finalizer would suppress the session-end for a park that was never confirmed, orphaning the
        // ledger row (neither durably parked nor cleanly closed). Stamp the neutral default instead;
        // ParkReviewerAsync applies ReviewerParkedResumableReason itself, but only on a definite
        // ParkAck.Parked and immediately before the stop that drives finalization (an unconfirmed park is
        // not a park — a mid-await exit must end the session normally). Reap candidates are unchanged.
        candidate.Agent.PendingEndReason = candidate.Park ? "agent_exited" : candidate.Reason;

        return true;
    }

    /// <summary>The shared stop executor: graceful <c>/exit</c> then terminate (via
    /// <see cref="StopAgentCoreAsync"/>), or a PID-record fallback for an id this incarnation never
    /// registered. Reached from three kinds of caller: the un-sequenced <c>StopAgent</c> lane item, the
    /// sequenced <see cref="HandleStopAgentV2"/> lane item, and purely-internal daemon-side reaping
    /// (heartbeat reviewer-TTL/idle reaping, stuck-Starting reaping) that never touches the wire at all.
    /// The internal callers deliberately bypass the lane (§1.11): they already run off-pump and are
    /// concurrency-safe via the per-agent single-flight teardown latch, and routing them through the lane
    /// would let a parked consent prompt delay reviewer reaping — the exact inversion of its purpose.</summary>
    internal async Task HandleStopAgent(string agentId) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            // Phase B (D4 §6.4(3)): no in-memory agent — this may be a survivor of a PRIOR
            // daemon incarnation the server is still trying to stop (S2). Fall back to the PID record:
            // reap by exact identity if a matching live process is still there.
            await TryStopByPidRecordAsync(agentId);
            return;
        }

        // Defence-in-depth: a --private agent is invisible to the server (unregistered, not in
        // LiveAgentIds), so never act on a server-origin command for one even if its id leaks.
        // The local-socket path (HandleLocalStopAsync) deliberately bypasses this — that request
        // comes from the owner of the 0600 socket, not from the server.
        if (agent.IsPrivate) return;

        await StopAgentCoreAsync(agent);
    }

    /// <summary>Test-only: awaited inside <see cref="HandleSendInput"/>'s section in the window between
    /// its slow pre-write steps (the borrowed-snapshot refresh, attachment downloads) and its
    /// pre-write re-read of the reap-claim latch — i.e. exactly where a reaper's un-sectioned claim
    /// lands in production, since that refresh is budgeted LONGER than the claim's gate wait. A test
    /// occupies that window to prove the delivery aborts there; null in production (one null check).
    ///
    /// <para>The seam exists because the window cannot otherwise be entered deterministically: parking
    /// the refresh itself only proves the refresh's own failure path, which returns before this point.
    /// Same shape and rationale as <see cref="StopGateAfterSnapshotHookForTest"/> below.</para></summary>
    internal Func<Task>? SendInputBeforeWriteHookForTest;

    /// <summary>Test-only: invoked ONCE inside <see cref="StopAgentCoreAsync"/> immediately AFTER the
    /// suppress-Completed snapshot and BEFORE the Completed send, so a test can publish a verdict in
    /// exactly that TOCTOU window and prove the pre-send re-check suppresses it (finding 2). Null in
    /// production (a single null check, no behavioural effect).</summary>
    internal Action? StopGateAfterSnapshotHookForTest;

    /// <summary>
    /// The stop itself, with no caller-authorisation policy: graceful /exit, then terminate.
    /// Server-origin stops reach this through <see cref="HandleStopAgent"/> (which refuses
    /// private agents); local-socket stops call it directly. Returns true once
    /// <c>agent.Runtime.HasExited</c> confirms the process is actually gone after
    /// <see cref="IHostedAgentRuntime.TerminateAsync"/>; false if that confirmation never lands
    /// or any step above throws.
    /// </summary>
    async Task<bool> StopAgentCoreAsync(AgentInstance agent) {
        var agentId = agent.Id;

        try {
            LogStopping(agentId);

            // Design spec §3.3: StopAgentCoreAsync is the single funnel for every stop trigger —
            // server StopAgent/StopAgentV2, a local-socket stop, AND the heartbeat's own
            // TTL/idle/stuck-Starting reap sweep (HandleStopAgent's doc lists all three) — and any
            // of them can race the finalizer having ALREADY reported a launch-window verdict (a
            // reviewer that trips a containment tripwire is a plausible TTL/idle-reap candidate on
            // the very same heartbeat tick). A published verdict has already forced terminal
            // "Failed" and told the server via LaunchFailedAsync; a non-failure "Completed"
            // transition here would clear the FailureReason that call just set server-side.
            //
            // Two independent reads, ORed, because the verdict is PUBLISHED at reap-claim time
            // (TryStartReap) strictly before the finalizer's report runs — a same-tick sweep could
            // otherwise read LaunchFailureVerdictReported in the instant BEFORE that CAS flips it
            // and still emit the non-failure transition. Reading the runtime's own Verdict closes
            // that window: it is suppressed the moment the verdict exists, not only once it has
            // been reported. Post-window (ReapedInsideLaunchWindow == false) leaves both reads
            // false, preserving the byte-identical teardown for that case. Gated on these two
            // signals specifically, not `Status == "Failed"` — an unrelated Failed state must not
            // by itself block a legitimate Completed transition.
            var suppressCompleted = VerdictForbidsNonFailureStatus(agent);

            // Test seam: lets a test publish a verdict in the TOCTOU window between this snapshot and
            // the Completed send below. No-op in production.
            StopGateAfterSnapshotHookForTest?.Invoke();

            // Set status BEFORE cancelling ReadCts so the read loop's finally
            // block sees "Completed" and skips its own status change / event append.
            if (!suppressCompleted) SetAgentStatus(agent, "Completed");
            // Mark this as a user-initiated stop so the read-loop's finally-block
            // EndAgentSessionAsync call uses "agent_stopped" if it ends up being
            // the only successful call (e.g., transient SignalR failure here).
            // Phase B (D3): but PRESERVE a backstop reason the heartbeat already stamped
            // (reviewer_ttl_expired / reviewer_idle_expired) — only overwrite the "agent_exited"
            // default, so server-side attribution can tell a TTL/idle reap from a user stop.
            if (agent.PendingEndReason == "agent_exited") agent.PendingEndReason = "agent_stopped";

            // An unregistered agent has no server-side row to update.
            if (!agent.IsPrivate) {
                // Atomic check + send-INITIATION under _reapLock for an ACP runtime (finding 1
                // refinement): the round-1 second check narrowed but did not CLOSE the check-to-send
                // race — a verdict could publish between the check and this send. The gate holds the
                // publication lock across BOTH, so a Completed frame, if sent at all, is initiated
                // before publication and is therefore ordered-before any LaunchFailed on the single
                // hub connection. Non-ACP runtimes never carry a launch-window verdict, so the
                // snapshot is authoritative for them.
                if (agent.Runtime is AcpHostedAgentRuntime acpRuntime)
                    acpRuntime.TryInitiateNonFailureStatusSend(
                        () => _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId), out _);
                else if (!suppressCompleted)
                    _ = _server.AgentStatusChangedAsync(agentId, "Completed", agent.SessionId);
                _ = _server.AppendAgentRunEventAsync(agentId, new AgentRunStopped("user", null));
            }

            // Try a graceful shutdown first: send /exit so claude can fire its own
            // session-end hook (drains transcript, writes SessionEnded + summary,
            // optionally schedules what's-done). Falls through to SIGTERM/SIGKILL
            // below if claude doesn't exit in time.
            //
            // Claude CLI requires the slash-command text and the Enter key to arrive
            // as separate PTY writes (with a small delay between them) — sending them
            // in a single write makes Claude treat the carriage return as part of the
            // command buffer instead of a submit. HandleSendInput uses the same split
            // pattern; matching it here makes the graceful path actually fire.
            // BOTH steps are bounded, and bounding the SEND is the load-bearing half. Asking for the
            // graceful stop is not free work: for a PTY runtime it writes "/exit" to the same master fd
            // a delivery writes to (an uncancellable write(2)), so against a child that has stopped
            // draining its stdin it parks forever — and an unbounded await here would mean TerminateAsync
            // below is never reached, i.e. the one action that ends the wedge (SIGKILL closes the slave,
            // every parked write returns EIO) is unreachable precisely when it is needed. This is
            // pre-existing — every stop, including a user's, could hang here — but the reviewer reaper
            // now DEPENDS on reaching terminate, so the bound is no longer optional.
            //
            // No legitimate graceful path is truncated: PTY writes + a submit, ACP sends one
            // session/cancel notify, Antigravity returns Task.CompletedTask, and Pi's own grace is 3s.
            // A timeout here just falls through to the same terminate a graceful-exit timeout already
            // falls through to.
            //
            // Inside the try, not before it: RequestGracefulStopAsync() itself (not just the await) can
            // throw synchronously, and that throw must land in the catch below and fall through to
            // terminate — not escape via the OUTER catch and skip terminate, which on the reap path
            // leaves a permanent zombie (ReapClaimed latched, agent still Running, every future reap
            // CAS-refused).
            Task graceful = Task.CompletedTask;

            try {
                graceful = agent.Runtime.RequestGracefulStopAsync();
                await graceful.WaitAsync(GracefulExitWait);
                await agent.Runtime.WaitForExitAsync(GracefulExitWait);
            } catch (TimeoutException ex) {
                // WaitAsync abandons the send rather than cancelling it (nothing CAN cancel it), so the
                // abandoned task is observed separately — it completes on its own once the terminate
                // below releases the write.
                ObserveAbandonedGracefulStop(graceful, agentId);
                LogGracefulExitFailed(ex, agentId);
            } catch (Exception ex) {
                LogGracefulExitFailed(ex, agentId);
            }

            // PTY WaitForExitAsync(timeout) returns silently when the timeout elapses,
            // so a graceful-exit *timeout* doesn't throw. Check HasExited explicitly
            // so we can tell from logs whether the graceful path is actually working
            // in production or if this vendor's CLI is consistently being SIGTERMed instead.
            if (!agent.Runtime.HasExited) {
                LogGracefulExitTimedOut(agentId, GracefulExitWait.TotalSeconds, agent.Vendor);
            }

            // Cancel the read loop and terminate the process. We deliberately do NOT end
            // the AgentSession here: EndAgentSessionAsync now retries across SignalR
            // reconnects (so it can block while a dropped connection recovers), and a
            // user-initiated stop must not wait on that. Cancelling ReadCts unblocks the
            // read loop, whose finally block runs FinalizeAgentRunAsync once the process
            // exits — that ends the session (with retry) using agent.PendingEndReason
            // ("agent_stopped") and spawns the what's-done generator if the server asks.
            // So session-end is reliable as the post-exit backstop without delaying
            // teardown, and is idempotent: if claude already fired its own session-end
            // during the graceful window above, the backstop call is a server-side no-op.
            await agent.ReadCts.CancelAsync();
            await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10));

            // TerminateAsync's SIGKILL is followed by a single non-blocking waitpid, so the
            // child is usually not reaped yet; poll briefly before calling the stop a failure.
            if (!agent.Runtime.HasExited) await agent.Runtime.WaitForExitAsync(TimeSpan.FromSeconds(2));

            return agent.Runtime.HasExited;
        } catch (Exception ex) {
            LogStopError(ex, agentId);

            return false;
        }
    }

    /// <summary>Observes an abandoned graceful-stop send (see <see cref="StopAgentCoreAsync"/>) so its
    /// eventual completion — or fault, once the terminate releases whatever it was parked on — is not
    /// an unobserved task exception.</summary>
    void ObserveAbandonedGracefulStop(Task graceful, string agentId) =>
        _ = graceful.ContinueWith(
            t => LogGracefulExitFailed(t.Exception!.GetBaseException(), agentId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Observes a background EndAgentSession retry that outlived the finalize budget so a
    /// later fault isn't an unobserved task exception. Success and shutdown-cancellation
    /// are intentionally ignored; only a genuine fault is logged.
    /// </summary>
    void ObserveEndSessionInBackground(Task<EndAgentSessionResult> endTask, string agentId) =>
        _ = endTask.ContinueWith(
            t => LogEndSessionFailed(t.Exception!.GetBaseException(), agentId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>
    /// Spawns <c>kcap generate-whats-done {sessionId}</c> as a detached process.
    /// Used when the daemon-driven session-end path supplants claude's own session-end
    /// hook — claude normally spawns this generator from its CLI session-end handler,
    /// but when claude crashed or was killed before firing session-end the daemon has
    /// to do it instead. Best-effort: failure is logged but doesn't block other
    /// cleanup, and a missing kcap binary just means no what's-done summary.
    /// </summary>
    void SpawnWhatsDoneGenerator(string sessionId) {
        try {
            var psi = new ProcessStartInfo(_config.CapacitorPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"] = _config.ServerUrl,
                    [ConfigRoot.ConfigDirEnvVar] = _config.ConfigRoot.Directory
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            using var proc = Process.Start(psi);

            if (proc is null) {
                LogWhatsDoneSpawnFailed(null, sessionId);

                return;
            }

            // Detach: close redirected streams so we don't hold pipes for the child's
            // lifetime. The child runs to completion on its own and posts its result
            // to the server.
            proc.StandardInput.Close();
            proc.StandardOutput.Close();
            proc.StandardError.Close();

            LogWhatsDoneSpawned(sessionId, proc.Id);
        } catch (Exception ex) {
            LogWhatsDoneSpawnFailed(ex, sessionId);
        }
    }

    /// <summary>True only for input that IS the command — exactly <c>/quit</c> or <c>/exit</c>
    /// after trimming — never for prose that merely mentions one.</summary>
    internal static bool IsQuitCommand(string text) {
        var trimmed = text.AsSpan().Trim();

        return trimmed.Equals("/quit", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The reasons this daemon drops a dispatched input, as the server records them. Stable
    /// tokens, not prose: the surface shows which one applied, and <c>unknown_agent</c> additionally
    /// stands as per-id absence proof from the only daemon that could have hosted the agent.</summary>
    internal static class SendInputDropReason {
        public const string UnknownAgent      = "unknown_agent";
        public const string PrivateAgent      = "private_agent";
        public const string ReaperClaimed     = "reaper_claimed";
        public const string ReaperClaimedLate = "reaper_claimed_late";
    }

    /// <summary>Reports a drop to the server, when the dispatch carried an id to name. Never throws:
    /// the drop has already happened, and a report that fails must leave the daemon exactly where
    /// reporting nothing would. Callers inside a held section report after releasing it — the invoke
    /// has no timeout of its own.</summary>
    async Task ReportInputDroppedAsync(SendInputCommand cmd, string reason) {
        if (cmd.DispatchId is not { } dispatchId) return;

        try {
            await _server.SendInputRejectedAsync(dispatchId, cmd.AgentId, reason);
        } catch (Exception ex) {
            LogSendInputRejectReportFailed(ex, cmd.AgentId, reason);
        }
    }

    async Task HandleSendInput(SendInputCommand cmd) {
        var (agentId, text, attachmentIds, _) = cmd;

        if (!_agents.TryGetValue(agentId, out var agent)) {
            LogSendInputUnknownAgent(agentId, _agents.Count);
            await ReportInputDroppedAsync(cmd, SendInputDropReason.UnknownAgent);

            return;
        }

        if (agent.IsPrivate) {
            LogSendInputPrivateAgent(agentId);
            await ReportInputDroppedAsync(cmd, SendInputDropReason.PrivateAgent);

            return; // server-origin input ignored for private agents
        }

        LogSendInputReceived(agentId, agent.Runtime.Vendor, text.Length, attachmentIds?.Length ?? 0);

        // A quit command typed into chat: a runtime with no TUI has nothing that interprets it, so
        // forwarding would hand the text to the model as an ordinary prompt — at best role-played
        // ("Quitting"), never a stop. Translate it onto the same serial lane a server stop rides.
        // PTY runtimes keep receiving the text verbatim: their TUI owns the command's meaning.
        if (!agent.Runtime.EmitsTerminalOutput && IsQuitCommand(text)) {
            LogSendInputQuitCommand(agentId, agent.Runtime.Vendor);
            await HandleUnsequencedStopAgent(agentId);
            return;
        }

        // Codex turn diagnostic: whether to run the post-send rollout probe, plus this round's
        // generation and the rollout length sampled just BEFORE delivery. Declared out here so both
        // survive the try/finally to reach ArmCodexTurnProbe below.
        var   isCodex       = string.Equals(agent.Runtime.Vendor, "codex", StringComparison.OrdinalIgnoreCase);
        long? codexBaseline = null;
        long  codexGen      = 0;

        // Set inside the section, reported outside it: the report is an unbounded server round trip,
        // and awaiting one while holding this gate would hold every later input for that agent behind
        // a stalled connection — and stretch the section the reaper's own claim races against.
        string? dropReason = null;

        await agent.BorrowedSnapshotGate.WaitAsync(_shutdownCts.Token);
        try {
            // Losing side of the reap claim (round-dispatch grace §3): a reap-claimed agent gets
            // nothing — no write, no clock advance, no report. Failing the dispatch here (not writing
            // into a dying runtime) is deliberate; the server heals it on resubmit.
            if (agent.IsReapClaimed) {
                LogSendInputReapClaimed(agentId);
                dropReason = SendInputDropReason.ReaperClaimed;

                return;
            }

            if (!await TryRefreshBorrowedSnapshotAsync(agent)) return;

            var message = text;

            if (attachmentIds is { Length: > 0 }) {
                var paths = await DownloadAttachmentsAsync(agent.Worktree.Path, attachmentIds);

                if (paths.Count > 0) {
                    message = $"{text}\n\n[Attached files: {string.Join(", ", paths)}]";
                }
            }

            // Re-checked HERE, not only at the top of the section: the borrowed-snapshot refresh
            // budget (30s, BorrowedSnapshotRefreshTimeout) plus any downloads routinely exceeds the
            // reap claim's own gate wait (20s, ReapClaimGateWait), so an unfenced claim
            // (TryClaimUnfencedReapWithoutSection) can land mid-section. The latch is monotonic 0→1
            // via Volatile.Read, so this re-read can never false-positive.
            if (SendInputBeforeWriteHookForTest is { } beforeWrite) await beforeWrite();

            if (agent.IsReapClaimed) {
                LogSendInputReapClaimedLate(agentId);
                dropReason = SendInputDropReason.ReaperClaimedLate;

                return;
            }

            // Codex turn diagnostic: BEFORE delivering this round's input — and while the send gate
            // is held, so it is ordered per agent — bump the round generation and sample the rollout
            // length from the cached path. Bumping first instantly invalidates any prior round's
            // still-running probe (it emits a verdict only while its generation is the latest), so a
            // fast Codex append caused by THIS input can never be credited to the previous round.
            // Sampling the length after the bump but before the send keeps the baseline honest (the
            // append lands strictly after it). A null here (path not cached yet, or the stat failed)
            // is handled in ArmCodexTurnProbe.
            if (isCodex) {
                codexGen = Interlocked.Increment(ref agent.CodexTurnProbeGen);
                if (agent.TranscriptPath is { } rolloutPath) codexBaseline = TryFileLength(rolloutPath);
            }

            // PTY runtimes use bracketed paste; ACP runtimes send a structured prompt.
            if (agent.BorrowedSnapshotSource is not null)
                await agent.Runtime.SendUserInputAndWaitForWriteAsync(message);
            else
                await agent.Runtime.SendUserInputAsync(message);

            // Input delivery counts as activity (AgentActivityClock.Advance(), shared with PTY
            // output/ACP envelopes/turn transitions); a throw from either await above skips it. Known
            // residual: a full ACP _pendingTurns queue drops input silently without throwing, so this
            // can advance on a delivery that was actually dropped — kill-delaying only, accepted.
            agent.ActivityClock.Advance();

            // One report per successfully handled invocation (SendInputCommand carries no round
            // identity, so a duplicate is tolerated and content-honest); fire-and-forget, contained,
            // one-way. Captured strictly after Advance() above, so it can never carry a pre-delivery
            // seq — monotonicity comes from _statusReportOrderingGate's acquisition order, never from
            // the order these Task.Run calls happen to be scheduled in.
            //
            // MUST offload rather than await here (lock order): _statusReportOrderingGate must never
            // be acquired while this agent's BorrowedSnapshotGate is held (see the gate's own doc).
            // Task.Run, not a bare discard — WaitAsync can complete synchronously on an uncontended
            // gate, which would otherwise run BuildStatusReport() (disk I/O) inline on this receive
            // loop while still holding BorrowedSnapshotGate.
            _ = Task.Run(() => SendDaemonStatusReportOnceAsync());

            LogSendInputDelivered(agentId, agent.Runtime.Vendor, message.Length);
        } finally {
            agent.BorrowedSnapshotGate.Release();

            // After the release, never before it — see dropReason's own note.
            if (dropReason is { } reason) await ReportInputDroppedAsync(cmd, reason);
        }

        // Codex turn diagnostic: arm the post-send rollout-growth probe. Only reached on the
        // successful-delivery path (the guards and the borrowed-snapshot failure above all return
        // before here).
        if (isCodex) ArmCodexTurnProbe(agent, codexBaseline, codexGen);
    }

    /// <summary>
    /// Codex turn diagnostic: schedule a bounded, off-handler probe that watches the reviewer's
    /// rollout for growth past <paramref name="baseline"/> (the length captured just before this
    /// round's input was delivered) and logs whether a turn began — the only clean turn-start signal
    /// a PTY runtime gives the daemon (see <see cref="CodexTurnObserver"/>).
    ///
    /// <para>Single-flight is by GENERATION, established in <see cref="HandleSendInput"/> before
    /// delivery: <paramref name="gen"/> is this round's generation, and the probe emits a verdict
    /// only while it is still the agent's latest — so a later round's growth is never credited to an
    /// earlier round even during that earlier probe's own poll. This method itself does no I/O and
    /// touches no disposable of the (already torn-down?) agent: it only schedules onto the pool, so
    /// it cannot throw a teardown fault back onto the just-completed send. A null
    /// <paramref name="baseline"/> (path not cached yet, or the pre-delivery stat failed) is logged
    /// and skipped — never a false "no turn"; the generation was still bumped, so any prior probe is
    /// already invalidated.</para>
    /// </summary>
    void ArmCodexTurnProbe(AgentInstance agent, long? baseline, long gen) {
        if (agent.TranscriptPath is not { } rolloutPath || baseline is not { } b) {
            LogCodexTurnRolloutUnresolved(agent.Id);
            return;
        }

        _ = Task.Run(() => ObserveCodexTurnAsync(agent, rolloutPath, b, gen));
    }

    async Task ObserveCodexTurnAsync(AgentInstance agent, string rolloutPath, long baseline, long gen) {
        try {
            // The linked token is created HERE (on the pool), not on the send handler, so a
            // concurrent teardown that has disposed ReadCts faults this best-effort probe rather
            // than the send. Bounded by the agent's stop, daemon shutdown, and the observe timeout.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);

            long CurrentLength() {
                try { return new FileInfo(rolloutPath).Length; } catch { return -1; } // <0 ⇒ momentarily unreadable
            }

            // Monotonic elapsed measurement (same clock domain the observer uses) — DateTime.UtcNow
            // could skew or go negative if the wall clock shifts mid-observation.
            var startTs = TimeProvider.System.GetTimestamp();

            // Single-flight: this round's generation was bumped (under the send gate) before its
            // input was delivered, so a newer round instantly makes this one stale. The predicate is
            // checked inside the poll loop, so a superseded probe stops within one interval instead
            // of polling to the timeout; the generation stays the sole verdict authority.
            var outcome = await CodexTurnObserver.ObserveGrowthAsync(
                CurrentLength, baseline, CodexTurnObserveTimeout, CodexTurnObserveInterval, TimeProvider.System, cts.Token,
                isCurrent: () => Volatile.Read(ref agent.CodexTurnProbeGen) == gen);

            // Verdict authority: the in-loop predicate stops a superseded probe promptly, but it is
            // checked BEFORE each length stat — so a newer round could bump the generation AND grow
            // the rollout between that check and the read, yielding a TurnObserved that belongs to
            // the newer round. Re-check the generation HERE, immediately before logging, so a stale
            // round never emits a verdict (leaving only the sub-microsecond check-to-log window).
            if (Volatile.Read(ref agent.CodexTurnProbeGen) != gen) return;

            switch (outcome) {
                case CodexTurnObserver.Outcome.TurnObserved:
                    LogCodexTurnStarted(agent.Id, (long)TimeProvider.System.GetElapsedTime(startTs).TotalMilliseconds);
                    break;
                case CodexTurnObserver.Outcome.NotObserved:
                    LogCodexTurnNotObserved(agent.Id, CodexTurnObserveTimeout.TotalSeconds);
                    break;
                case CodexTurnObserver.Outcome.Unavailable:
                    // The rollout became unreadable and never showed growth — a measurement gap, NOT
                    // evidence the reviewer ignored the input. Do not emit the strong "no turn" line.
                    LogCodexTurnRolloutUnavailable(agent.Id);
                    break;
                // Superseded: a newer round took over — it reports; no verdict here.
                // Cancelled: the agent stopped or the daemon is shutting down — no verdict to report.
            }
        } catch (Exception ex) {
            LogCodexTurnObserveFailed(ex, agent.Id);
        }
    }

    static long? TryFileLength(string path) {
        try { return new FileInfo(path).Length; } catch { return null; }
    }

    static readonly TimeSpan BorrowedSnapshotRefreshTimeout = TimeSpan.FromSeconds(30);

    async Task<bool> TryRefreshBorrowedSnapshotAsync(AgentInstance agent) {
        if (agent.BorrowedSnapshotSource is not { } source || agent.Work != WorkLocation.OwnedWorktree)
            return true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        timeout.CancelAfter(BorrowedSnapshotRefreshTimeout);
        try {
            await agent.Runtime.WaitForTurnIdleAsync(timeout.Token);
            var auth = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(source);
            if (!auth.Allowed ||
                !SameFileSystemPath(auth.CanonicalCwd, source) ||
                !SameFileSystemPath(auth.CanonicalGitRoot, agent.Worktree.SourceRepo))
                throw new InvalidOperationException($"borrow_auth_failed: {auth.Reason ?? "source_identity_changed"}");
            var generation = await _worktreeManager.SyncBorrowedSnapshotFromSourceAsync(
                agent.Worktree.SourceRepo, agent.Worktree.SnapshotRoot ?? agent.Worktree.Path,
                // The prefix computed at creation, carried — never re-derived. The only path available
                // here is the TARGET-side execution path, and deriving from that is what lets the launch
                // cwd and the exclusion classifier end up on two different spellings.
                agent.Worktree.GitRelativeCwd
                    ?? throw new InvalidOperationException(
                        "borrowed_snapshot_git_relative_cwd_missing"),
                [], agent.Worktree.ReviewContextRoot
                    ?? throw new InvalidOperationException(
                        "borrowed_snapshot_review_context_missing"), timeout.Token);
            var reviewerToken = agent.ReviewerBridgeToken
                ?? throw new InvalidOperationException(
                    "borrowed_snapshot_review_context_token_missing");
            var retired = _permissionBridge.PublishReviewerContext(reviewerToken, generation);
            if (retired is not null)
                WorktreeManager.RemoveReviewContextGeneration(retired);
            return true;
        } catch (Exception ex) when (ex is not OperationCanceledException || !_shutdownCts.IsCancellationRequested) {
            LogBorrowedSnapshotRefreshFailed(ex, agent.Id);
            // Fail closed: the disposable snapshot may be partial, so terminate this reviewer
            // and never retry or reuse it for another round.
            agent.PendingEndReason = "borrowed_snapshot_refresh_failed";
            try { await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(10)); } catch { /* cleanup owns final reap */ }
            return false;
        }
    }

    static bool SameFileSystemPath(string? left, string? right) =>
        left is not null && right is not null && string.Equals(
            Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    async Task HandleSendSpecialKey(string agentId, string key) {
        if (!_agents.TryGetValue(agentId, out var agent)) {
            LogSendSpecialKeyUnknownAgent(agentId, key, _agents.Count);
            return;
        }

        if (agent.IsPrivate) return; // server-origin key ignored for private agents

        await agent.Runtime.SendSpecialKeyAsync(key);
    }

    async Task<List<string>> DownloadAttachmentsAsync(string worktreePath, string[] attachmentIds) {
        var attachDir = Path.Combine(worktreePath, ".attached");
        Directory.CreateDirectory(attachDir);

        // Write .gitignore to prevent accidental commits
        var gitignorePath = Path.Combine(attachDir, ".gitignore");

        if (!File.Exists(gitignorePath)) {
            await File.WriteAllTextAsync(gitignorePath, "*\n");
        }

        var paths = new List<string>();

        foreach (var id in attachmentIds) {
            try {
                using var httpClient = _httpClientFactory.CreateClient("Attachments");

                var resolution = await _tokens.GetValidTokensForServerAsync(_config.Profiles.Name, _config.ServerUrl);

                if (resolution.Tokens is not null) {
                    httpClient.DefaultRequestHeaders.Authorization = new("Bearer", resolution.Tokens.AccessToken);
                }

                var response = await httpClient.GetAsync($"/api/attachments/{id}");

                if (!response.IsSuccessStatusCode) {
                    LogAttachmentNotFound(id, response.StatusCode);

                    continue;
                }

                var rawFileName = response.Content.Headers.ContentDisposition?.FileNameStar
                 ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                 ?? $"attachment-{id[..8]}";

                // Sanitize: strip path separators to prevent directory traversal
                var fileName = Path.GetFileName(rawFileName);

                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = $"attachment-{id[..8]}";

                var filePath = GetUniqueFilePath(attachDir, fileName);
                var fullPath = Path.GetFullPath(filePath);
                var safeDir  = Path.GetFullPath(attachDir) + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(safeDir)) {
                    LogAttachmentPathEscape(rawFileName);

                    continue;
                }

                await using var fs = File.Create(filePath);
                await response.Content.CopyToAsync(fs);

                paths.Add($".attached/{Path.GetFileName(filePath)}");
            } catch (Exception ex) {
                LogAttachmentError(ex, id);
            }
        }

        return paths;
    }

    static string GetUniqueFilePath(string directory, string fileName) {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path)) {
            return path;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext            = Path.GetExtension(fileName);
        var counter        = 2;

        do {
            path = Path.Combine(directory, $"{nameWithoutExt}-{counter}{ext}");
            counter++;
        } while (File.Exists(path));

        return path;
    }

    Task<string[]> HandleFindRepoForRemote(FindRepoForRemoteRequest req)
        => _repoMatcher.FindAsync(req.Owner, req.Repo, req.CandidatePaths ?? [], _shutdownCts.Token);

    /// <summary>
    /// Handles the server's <c>ProbeBorrowSource</c> client-result invocation (Phase A, task
    /// A3): "can you borrow this path?". Delegates the actual policy (allowlist, git-root resolution,
    /// symlink canonicalization) to <see cref="BorrowAuthorizer"/> — constructed fresh over the
    /// daemon's current <see cref="DaemonConfig"/> so a config reload is picked up on the next probe —
    /// and maps its <see cref="BorrowAuthResult"/> onto the wire-facing <see cref="BorrowProbeResult"/>.
    /// </summary>
    async Task<BorrowProbeResult> HandleProbeBorrowSource(string path) {
        var result = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(path);

        return new BorrowProbeResult(result.Allowed, result.CanonicalCwd, result.CanonicalGitRoot, result.Reason);
    }

    /// <summary>
    /// Task 8: handles the server's <c>ResolveReviewerModel</c> client-result invocation — the
    /// side-effect-free reviewer-model preflight. PURE resolution: it consults the SELECTED vendor's
    /// advertised resolver first (via the shared cross-vendor coordinator, which inspects the OTHER
    /// advertised unattended resolvers only after the selected one rejects, to name a mismatch vendor)
    /// and NEVER spawns a subprocess, creates a worktree, or writes config. Echoes the exact
    /// <c>RequestId</c>/<c>Vendor</c>; returns the daemon's own RPC protocol version so a protocol drift
    /// fails the preflight closed; and maps the daemon-internal disposition set onto the server's
    /// accepted/unavailable/invalid wire dispositions (a vendor mismatch becomes an <c>unavailable</c>
    /// carrying <c>RecognizedVendor</c>).
    /// </summary>
    ReviewerModelResolveResponseV1 HandleResolveReviewerModel(ReviewerModelResolveRequestV1 req) {
        // The ADVERTISED unattended resolvers — the same predicate DaemonRunner.ComputeUnattendedVendor-
        // Capabilities gates the capability on (installed + unattended-certified + resolver present).
        // Vendor-neutral: we read each factory's own resolver, never a central vendor→model table.
        var advertised = _runtimeFactories.Values
            .Where(f => f.IsAvailable() && f.SupportsUnattended && f.ReviewerModelResolver is not null)
            .Select(f => f.ReviewerModelResolver!)
            .ToList();

        // The coordinator resolves on the SELECTED vendor's resolver first and inspects the OTHER
        // advertised resolvers only after the selected one rejects (to name a mismatch vendor).
        var resolution = ReviewerModelResolvers.Resolve(req.Vendor, req.RequestedModel, advertised);

        // Echo the exact RequestId + Vendor (the server rejects a mismatch), and return THIS daemon's own
        // RPC protocol version — never an echo of the request's expectation — so a protocol drift on
        // either side is detected and fails the preflight CLOSED.
        var response = new ReviewerModelResolveResponseV1(
            req.RequestId, req.Vendor, ReviewerModelResolvers.RpcProtocolVersion, Disposition: "unavailable");

        return resolution.Disposition switch {
            ReviewerModelDisposition.Accept => response with {
                Disposition             = "accepted",
                CanonicalRequestedModel = resolution.CanonicalRequestedModel,
                LaunchModel             = resolution.LaunchModel,
                EquivalenceKey          = resolution.EquivalenceKey,
            },
            // A cross-vendor recognition maps to unavailable with the ordinal-first OTHER vendor named;
            // the server re-validates RecognizedVendor against what this daemon currently advertises.
            ReviewerModelDisposition.VendorMismatch => response with {
                Disposition      = "unavailable",
                RecognizedVendor = resolution.DiagnosticCode,
            },
            ReviewerModelDisposition.Invalid => response with {
                Disposition    = "invalid",
                DiagnosticCode = resolution.DiagnosticCode,
            },
            // Unavailable, or any unexpected disposition — fail closed as plain unavailable.
            _ => response,
        };
    }

    /// <summary>
    /// Task 8: builds the post-launch <see cref="ExplicitReviewerModelResolvedV1"/> report for an
    /// explicit-model reviewer launch, or <see langword="null"/> when it cannot be built (no resolver for
    /// the vendor, or the launch model no longer resolves — both fail the report closed rather than send
    /// an unvalidatable one). The reported <c>EquivalenceKey</c> is DERIVED from the concrete launch model
    /// via the SAME resolver (so the server's key-equality validation is meaningful, not a trivial echo),
    /// and the <c>PolicyVersion</c> is the resolver's own per-vendor policy version. Pure/static so it is
    /// unit-testable in isolation from the launch gauntlet.
    /// </summary>
    static ExplicitReviewerModelResolvedV1? BuildExplicitReviewerModelReport(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IReviewerModelResolver? resolver) {
        // The explicit path is gated by the server on the vendor advertising a resolver — defensively,
        // no resolver means we can neither derive nor validate a key: fail the report CLOSED.
        if (resolver is null) return null;

        // The concrete model the daemon launched with is the server-pinned LaunchModel VERBATIM. For
        // Codex this is load-bearing: its slug-level equivalence key is date-SENSITIVE, so reporting a
        // date-suffixed session-metadata model would drift the key and fail the server's echo
        // validation (see the Codex_DatedSlug regression test). For Claude the family-level key absorbs
        // any date, so the launch model's key still matches the pinned family anchor.
        var resolution = resolver.Resolve(block.LaunchModel);

        // Derive the equivalence key from that concrete model via the SAME resolver — so the server's
        // key-equality check is meaningful, not a trivial echo of the pinned block. A launch model that
        // no longer anchored-accepts (a policy drift) can't produce a valid report: fail closed.
        if (resolution.Disposition != ReviewerModelDisposition.Accept || resolution.EquivalenceKey is null)
            return null;

        return new ExplicitReviewerModelResolvedV1(
            AgentId:         agentId,
            LaunchAttemptId: block.LaunchAttemptId,
            Vendor:          vendor,
            ResolvedModel:   block.LaunchModel,          // verbatim — never recanonicalized/date-suffixed
            PolicyVersion:   resolver.PolicyVersion,     // the per-vendor resolver policy version
            EquivalenceKey:  resolution.EquivalenceKey); // DERIVED via the resolver; equals the pinned key
    }

    /// <summary>
    /// Registers an agent with the server exactly as a UI-launched agent: AgentRegistered +
    /// terminal dims + AgentRunStarted, then persists/announces the repo path. No-ops for a
    /// PrivateLocal agent. Shared by the hosted launch and the registered local launch so the
    /// two cannot drift. Dims come from <see cref="AgentInstance.CurrentCols"/>/<c>CurrentRows</c>
    /// (hosted = HostedPtyCols/Rows; local = the client's terminal size).
    /// </summary>
    async Task RegisterAgentAsync(AgentInstance agent) {
        if (agent.IsPrivate) return;

        await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.SandboxPolicy, agent.ApprovalPolicy, agent.PermissionPreset, agent.RuntimeTransport);

        // Report the PTY size so read-only viewers lock their xterm to it. Best-effort.
        try {
            await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
        } catch (Exception ex) {
            LogTerminalDimsSendFailed(ex, agent.Id);
        }

        _ = _server.AppendAgentRunEventAsync(
            agent.Id,
            new AgentRunStarted(agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.Worktree.Path, agent.Vendor)
        );

        // Persist repo path and notify server so the launch dialog updates.
        _ = Task.Run(async () => {
                try {
                    await new RepoPathStore(_configRoot).AddAsync(agent.RepoPath);
                    await _server.UpdateRepoPathsAsync();
                } catch (Exception ex) {
                    LogRepoPathPersistFailed(ex, agent.Id);
                }
            }
        );
    }

    /// <summary>
    /// Binds the ACP canonical session to <paramref name="agent"/> (<c>AcpSessionStarted</c>) —
    /// this call MUST run after <see cref="RegisterAgentAsync"/> has already registered the agent (the server rejects a
    /// bind for an unregistered agent) and strictly before any transcript event reaches the server;
    /// callers (<see cref="HandleLaunchAgent"/>) enforce the first half by only calling this after
    /// <c>await RegisterAgentAsync(agent)</c>, and this method enforces the second half itself by
    /// awaiting the bind before ever constructing the forwarder. Once bound, registers the binding
    /// for reconnect re-bind (<see cref="ServerConnection.RegisterAcpBinding"/>), builds the
    /// synthesized <c>SessionStarted@Seq0</c> envelope (<see cref="AcpEventTranslator.BuildSessionStarted"/>),
    /// and starts <see cref="ForwardAcpTranscriptAsync"/> as background work — the resulting task is
    /// kept on <see cref="AgentInstance.AcpForwarder"/> so <see cref="FinalizeAgentRunAsync"/> can
    /// coordinate the bounded final-drain at teardown.
    ///
    /// Best-effort: any failure in the bind/setup step is logged and swallowed, never propagated to
    /// the caller. By the time this runs, <paramref name="agent"/> is already registered with the
    /// server and its ACP process is already live — letting a transcript-plumbing failure escape
    /// into <see cref="HandleLaunchAgent"/>'s outer catch would incorrectly route it through the
    /// failed-launch cleanup path (worktree removal) against an agent that is actually running.
    /// Degrades to "no live transcript for this session" rather than failing the launch.
    ///
    /// <b>Bind-vs-finalize stale-binding race:</b>
    /// <paramref name="acpCts"/> is <paramref name="agent"/>'s <see cref="AgentInstance.AcpCts"/>,
    /// created by the caller BEFORE this task was fired. Its token — not the raw daemon shutdown
    /// token — gates the bind call below AND (once built) the forwarder's run task, so
    /// <see cref="FinalizeAgentRunAsync"/> can cancel just this setup/forwarder. The bind call can
    /// block for the length of a reconnect outage (<c>ConnectionRetry</c>); if the agent's whole
    /// lifecycle finalizes while it's still in flight, a LATE successful bind must not register a
    /// binding for what is by then a dead agent (it would leak into <c>_acpBindings</c> and be
    /// replayed on every future reconnect with nothing left to ever drain it) — the liveness check
    /// immediately below the bind await closes that race.
    /// </summary>
    async Task StartAcpForwardingAsync(AgentInstance agent, IAcpTranscriptSource transcript, string vendor, CancellationTokenSource acpCts) {
        try {
            await _server.AcpSessionStartedAsync(
                agent.Id,
                vendor,
                transcript.AcpSessionId,
                transcript.Cwd,
                transcript.ResolvedModel,
                null, // metadata: no wire-contract fields required for the prototype
                acpCts.Token
            );

            // Non-envelope AcpSessionStarted bind: no source-claim cursor, so always a fresh forwarder
            // (SessionStarted@0). §2.7 B4's rebind cursor rides ONLY the source-claim path below.
            StartForwarderAfterBind(agent, transcript, vendor, acpCts, acceptedSeq: -1);
        } catch (Exception ex) {
            LogAcpBindFailed(ex, agent.Id);
        }
    }

    /// <summary>
    /// The post-bind half of ACP forwarding (everything AFTER the canonical session is bound): the
    /// liveness / TOCTOU guards, the local reconnect-binding registration, and the transcript
    /// forwarder build + run. Factored out of <see cref="StartAcpForwardingAsync"/> so the §2.5
    /// deferred-first-turn source-claim path — which binds via <c>AcpSessionSourceClaim</c>, NOT
    /// <c>AcpSessionStarted</c> — can reuse it verbatim without re-binding. Synchronous (no awaits). The
    /// CALLER owns the try/catch.
    ///
    /// <para><paramref name="acceptedSeq"/> (§2.7 B4): a FRESH bind (<c>-1</c>, the default) builds the
    /// SessionStarted@0 initial envelope and the forwarder's local seq starts at 0. An envelope-sourced
    /// REBIND (a parked reviewer relaunch, <c>≥ 0</c>) initializes the forwarder from the source-claim's
    /// canonical <c>AcceptedSeq</c> instead — no SessionStarted, new events numbered from that seq + 1
    /// (see §2.5 item 6). Only the <see cref="StartEnvelopeSourcedSessionAsync"/> source-claim path can
    /// carry a cursor; the non-envelope <see cref="StartAcpForwardingAsync"/> path always passes -1.</para>
    /// </summary>
    void StartForwarderAfterBind(AgentInstance agent, IAcpTranscriptSource transcript, string vendor, CancellationTokenSource acpCts, long acceptedSeq = -1) {
        // Liveness check: the bind await (either path) can span a reconnect outage. If finalize already
        // ran (cancelling acpCts and/or removing the agent from _agents) while we waited, abort — do not
        // register a binding, build a forwarder, or start it for an agent that's finalizing or gone.
        if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
            LogAcpBindAbortedAgentGone(agent.Id);

            return;
        }

        _server.RegisterAcpBinding(
            agent.Id,
            new AcpBindInfo(vendor, transcript.AcpSessionId, transcript.Cwd, transcript.ResolvedModel)
        );

        // Post-register re-check (TOCTOU): finalize can run between the liveness check above and this
        // register, having already cancelled/unregistered+cleaned up the agent — leaving the binding we
        // just registered stale (replayed on reconnect for a dead agent). Undo it. The finalizer's own
        // unconditional UnregisterAcpBinding covers the mirror case (finalize after this point);
        // UnregisterAcpBinding is idempotent so a double-remove is harmless.
        if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
            _server.UnregisterAcpBinding(agent.Id);
            LogAcpBindAbortedAgentGone(agent.Id);

            return;
        }

        // §2.7 B4: a fresh bind (acceptedSeq < 0) synthesizes SessionStarted@0 and starts the local seq at
        // 0; an envelope-sourced rebind (acceptedSeq >= 0) suppresses SessionStarted and resumes numbering
        // from the canonical cursor, so round-2 events aren't deduped away against round-1's high-water.
        var isRebind = acceptedSeq >= 0;

        var sessionStarted = isRebind ? (AcpEventEnvelope?) null : AcpEventTranslator.BuildSessionStarted(
            seq: 0,
            DateTimeOffset.UtcNow.ToString("O"),
            cwd: transcript.Cwd,
            model: transcript.ResolvedModel,
            rawSessionId: transcript.AcpSessionId
        );

        var forwarder = new AcpTranscriptForwarder(
            send: (batch, ct) => _server.SendAcpEventsAsync(agent.Id, transcript.AcpSessionId, batch, ct),
            initialEnvelope: sessionStarted,
            envelopes: transcript.Envelopes,
            logger: _logger,
            resumeFromSeq: isRebind ? acceptedSeq : null
        );

        var runTask = ForwardAcpTranscriptAsync(agent, forwarder, acpCts.Token);
        agent.AcpForwarder = new AcpForwarderHandle(forwarder, runTask);
    }

    /// <summary>
    /// §2.5 deferred-first-turn SOURCE-CLAIM sequence, fired fire-and-forget after
    /// <c>RegisterAgentAsync</c> for a runtime that holds its first turn
    /// (<see cref="IHostedAgentRuntime.RequiresSourceClaimBeforeFirstTurn"/>). Ordering:
    /// <list type="number">
    /// <item>durably source-claim the session (server binds + writes the ownership ledger row);</item>
    /// <item>start the transcript forwarder WITHOUT re-binding (the claim already bound);</item>
    /// <item>dispatch the held first turn — the earliest instant any hook can fire, strictly after the
    ///       durable claim committed;</item>
    /// <item>clear the ledger's provisional flag in a background confirm loop.</item>
    /// </list>
    /// A <c>Rejected</c> claim (or a pre-source-claim server's method-not-found) is a coded launch
    /// failure that tears the agent down; a confirm failure NEVER does (the row stays provisional, and
    /// the server's live-owner expiry deferral + recovery settle it). Contains all its own faults — it
    /// is fire-and-forget with no outer catch. Every step is gated on <paramref name="acpCts"/>, so a
    /// finalize during any await aborts cleanly.
    /// </summary>
    async Task StartEnvelopeSourcedSessionAsync(
            AgentInstance agent, IHostedAgentRuntime runtime, IAcpTranscriptSource transcript,
            string vendor, CancellationTokenSource acpCts) {
        AcpSourceClaimOutcome claim;
        try {
            claim = await _server.AcpSessionSourceClaimAsync(agent.Id, transcript.AcpSessionId, acpCts.Token);
        } catch (OperationCanceledException) when (acpCts.IsCancellationRequested) {
            // The agent finalized while the claim was in flight — nothing bound, nothing to undo.
            LogAcpBindAbortedAgentGone(agent.Id);

            return;
        } catch (Exception ex) {
            // A pre-source-claim server (method-not-found) or any claim fault is unrecoverable — the
            // session can never be envelope-stamped. Tear down (coded launch failure).
            LogAcpSourceClaimFailed(ex, agent.Id);
            await FailEnvelopeSourcedLaunchAsync(agent.Id, AcpHostedAgentRuntimeFactory.DescribeLaunchFailure(ex));

            return;
        }

        if (claim.Outcome == AcpBindOutcome.Rejected) {
            // The server declined ownership (a stale/foreign/terminal binding). Unrecoverable for this
            // launch — the daemon must not proceed to a first turn on an unstamped session.
            LogAcpSourceClaimRejected(agent.Id);
            await FailEnvelopeSourcedLaunchAsync(agent.Id, "Hosted session source claim rejected by the server");

            return;
        }

        try {
            // The claim already bound server-side, so build the forwarder WITHOUT AcpSessionStarted. It
            // is running BEFORE the first turn dispatches, so it captures that turn's envelopes. §2.7 B4:
            // pass the claim's canonical AcceptedSeq — -1 for a brand-new session (fresh SessionStarted@0),
            // or the resume high-water for a parked-reviewer rebind (suppress SessionStarted, resume seq).
            StartForwarderAfterBind(agent, transcript, vendor, acpCts, acceptedSeq: claim.AcceptedSeq);

            // StartForwarderAfterBind aborts silently if finalize ran (cancelled acpCts and/or removed the
            // agent) during the bind — re-check the SAME liveness before releasing the held first turn, so a
            // launch that finalized while binding never dispatches turn/start into a teardown. (The runtime
            // also observes acpCts inside BeginFirstTurnAsync; this closes the agent-removed-but-token-live
            // arm StartForwarderAfterBind guards on.)
            if (acpCts.IsCancellationRequested || !_agents.ContainsKey(agent.Id)) {
                LogAcpBindAbortedAgentGone(agent.Id);

                return;
            }

            // Dispatch the held first turn and await its response — the signal we confirm on.
            await runtime.BeginFirstTurnAsync(acpCts.Token);

            // Clear the ledger's provisional flag in the background: retries transient failures for as
            // long as the agent lives and NEVER tears it down. Reached only on a SUCCESSFUL first turn;
            // a first-turn failure throws into the catch below (which tears the agent down) and never
            // fires this.
            _ = ConfirmSessionLaunchLoopAsync(agent, transcript.AcpSessionId, claim.OwnershipToken, acpCts.Token);
        } catch (OperationCanceledException) when (acpCts.IsCancellationRequested) {
            LogAcpBindAbortedAgentGone(agent.Id);
        } catch (Exception ex) {
            // Forwarder-setup or first-turn DISPATCH failure — the launch cannot run, so tear it down,
            // symmetric with a claim failure (the alternative would leave a zombie holding a slot). This
            // fires ONLY for a real error; a finalize during BeginFirstTurnAsync is the OCE arm above. The
            // confirm never fired, so the still-provisional ledger row is closed by the server's Rule-2
            // expiry. FailEnvelopeSourcedLaunchAsync is no-throw, so awaiting it here is safe.
            LogAcpBindFailed(ex, agent.Id);
            await FailEnvelopeSourcedLaunchAsync(agent.Id, AcpHostedAgentRuntimeFactory.DescribeLaunchFailure(ex));
        }
    }

    /// <summary>Tears down a registered agent whose source claim failed — the proven
    /// <see cref="CleanupAgentAsync"/> (single-flight, disposes the runtime + releases the slot +
    /// unregisters) then <c>LaunchFailedAsync</c> composition, mirroring the launch outer-catch. Never
    /// <c>FinalizeAgentRunAsync</c>, which would classify a still-live process by exit code.
    /// <para>Guaranteed NO-THROW: it is awaited from the fire-and-forget source-claim task OUTSIDE any
    /// try, so a teardown fault (a cleanup or a LaunchFailed hub error) is contained here and logged
    /// rather than escaping as an unobserved task exception.</para></summary>
    async Task FailEnvelopeSourcedLaunchAsync(string agentId, string reason) {
        if (!_agents.TryGetValue(agentId, out var agent))
            return; // already finalizing/gone — its own teardown owns it

        // Mark the agent terminally Failed BEFORE CleanupAgentAsync disposes the runtime: disposal ends
        // the read loop, whose FinalizeAgentRunAsync classifies by exit code and would otherwise emit a
        // Completed/Failed AgentStatusChanged that races and could MASK this coded launch failure (a
        // later "Completed" clearing the server-side FailureReason). A pre-set "Failed" makes that
        // exit-code classification a no-op — the same guard the launch-verdict path uses (see the
        // "Force terminal Failed BEFORE the report" block in FinalizeAgentRunAsync).
        SetAgentStatus(agent, "Failed");

        try {
            await CleanupAgentAsync(agentId);
            await _server.LaunchFailedAsync(agentId, reason);
        } catch (Exception ex) {
            LogAcpBindFailed(ex, agentId); // contain the teardown fault (fire-and-forget caller has no catch)
        }
    }

    /// <summary>
    /// The background confirm loop: calls <c>ConfirmSessionLaunch</c> until it settles, retrying only
    /// transient (non-terminal) failures on a paced cadence for as long as the agent lives (gated on
    /// <paramref name="ct"/> == the agent's AcpCts). <c>Confirmed</c>/<c>AlreadyConfirmed</c> is done;
    /// <c>Superseded</c>/<c>NotFound</c> is permanent (stop); a thrown error is transient (retry after a
    /// delay). It NEVER tears the agent down — an unconfirmed row is benign (the server's live-owner
    /// expiry deferral protects it and its recovery settles it when connectivity returns).
    /// </summary>
    async Task ConfirmSessionLaunchLoopAsync(AgentInstance agent, string acpSessionId, long ownershipToken, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var outcome = await _server.ConfirmSessionLaunchAsync(acpSessionId, ownershipToken, ct);
                if (outcome is AcpLaunchConfirmOutcome.Superseded or AcpLaunchConfirmOutcome.NotFound)
                    LogConfirmSessionLaunchStopped(agent.Id, outcome.ToString());

                return; // Confirmed / AlreadyConfirmed / Superseded / NotFound are all terminal for this loop
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return; // the agent finalized — stop quietly, never tear down
            } catch (Exception ex) {
                LogConfirmSessionLaunchRetrying(ex, agent.Id);
                try { await Task.Delay(ConfirmRetryDelay, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Fire-and-forget wrapper around <see cref="AcpTranscriptForwarder.RunAsync"/> — a forwarder fault must NEVER
    /// crash the agent or the daemon. <see cref="AcpTranscriptForwarder.RunAsync"/> already swallows
    /// its own cancellation and retries indefinitely on a send failure, but this wrapper is the outer
    /// safety net for anything else that could still escape it (e.g. the transcript channel itself
    /// faulting from a translator bug upstream) — logged, never rethrown, so the returned task always
    /// completes successfully and <see cref="FinalizeAgentRunAsync"/> can safely await it.
    /// </summary>
    async Task ForwardAcpTranscriptAsync(AgentInstance agent, AcpTranscriptForwarder forwarder, CancellationToken ct) {
        try {
            await forwarder.RunAsync(ct);
        } catch (Exception ex) {
            LogAcpForwarderFaulted(ex, agent.Id);
        }
    }

    /// <summary>
    /// Time budget for the ACP bounded final-drain — how long
    /// <see cref="FinalDrainAcpTranscriptAsync"/> waits for the forwarder's run task to finish
    /// draining (after the runtime is disposed) before giving up and letting
    /// <see cref="FinalizeAgentRunAsync"/> proceed to <see cref="ServerConnection.EndAgentSessionAsync"/>
    /// regardless. Deliberately small and independent of <see cref="EndAgentSessionBudget"/> — a
    /// slow/stuck drain degrades to "no trailing transcript", never a stacked delay on top of the
    /// session-end budget, and never pins shutdown (the primary invariant this exists to protect).
    /// Settable so tests don't wait for the real value.
    /// </summary>
    internal TimeSpan AcpFinalDrainBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>§2.5: paced retry cadence for the background <c>ConfirmSessionLaunch</c> loop
    /// (transient failures only; it runs for the agent's lifetime and never tears it down). Settable so
    /// tests don't wait the real value.</summary>
    internal TimeSpan ConfirmRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Disposes the ACP runtime FIRST so its <c>DisposeAsync</c> completes the
    /// transcript channel (courtesy-flushing any still-open aggregation run) — <paramref name="acpForwarder"/>'s
    /// run task can only ever return once that channel completes — then gives the forwarder a FINITE
    /// budget (<see cref="AcpFinalDrainBudget"/>) to drain whatever's left to the server before
    /// returning UNCONDITIONALLY. Never throws and never blocks past the budget: a disposal fault or
    /// a drain that exceeds it is logged, not propagated, so <see cref="FinalizeAgentRunAsync"/>'s
    /// own outage-cleanup guarantee (<see cref="EndAgentSessionBudget"/>) is never compounded by this
    /// call. This is also what keeps the forwarder stopped BEFORE the binding goes terminal (the
    /// caller ends the session immediately after this returns) — the ordering the hot-loop
    /// guard's edge case relies on in the normal (non-outage) flow.
    ///
    /// When the drain misses its budget, the forwarder is
    /// presumably still blocked/retrying a send against an unresponsive connection —
    /// <see cref="AcpTranscriptForwarder.RunAsync"/> otherwise retries indefinitely. Cancel
    /// <paramref name="agent"/>'s per-agent <see cref="AgentInstance.AcpCts"/> so it unwinds
    /// promptly (it is <c>ct</c>-aware at every await point) instead of leaking an orphaned task
    /// that keeps sending against an agent that's finalizing right now.
    /// </summary>
    async Task FinalDrainAcpTranscriptAsync(AgentInstance agent, AcpForwarderHandle acpForwarder) {
        try {
            await agent.Runtime.DisposeAsync();
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "disposing ACP runtime for final transcript drain", agent.Id);
        }

        var completed = await Task.WhenAny(acpForwarder.RunTask, Task.Delay(AcpFinalDrainBudget));

        if (completed != acpForwarder.RunTask) {
            LogAcpFinalDrainTimedOut(agent.Id, AcpFinalDrainBudget.TotalSeconds);

            if (agent.AcpCts is { } acpCts) {
                try { await acpCts.CancelAsync(); } catch { /* best-effort — never let this pin teardown */ }
            }
        }
    }

    Task HandleResizeTerminal(ResizeTerminalCommand cmd) {
        // Ignore server-origin resize for private agents (defence-in-depth; see HandleStopAgent).
        if (_agents.TryGetValue(cmd.AgentId, out var agent) && !agent.IsPrivate) {
            // The server sends the min aggregate across all web viewers, or (0,0) when the
            // last web viewer left. Fold it into the same min-clamp as the local clients rather than
            // resizing the PTY directly — a small web viewer must not corrupt a large local terminal
            // (or vice-versa), and a departing web viewer must let the PTY grow back to the local size.
            //
            // (0,0) clears WebDims. Accept other dims only when they're positive AND fit the PTY's
            // ushort winsize — ignore anything else (negative, or > ushort.MaxValue) so a bad value
            // can't wrap on the cast and poison the shared clamp (e.g. a wrapped 0 would block all
            // resizing). The server already bounds-checks; this is defence-in-depth — the daemon must
            // not trust the wire.
            var clear = cmd is { Cols: 0, Rows: 0 };
            var valid = cmd is { Cols: > 0 and <= ushort.MaxValue, Rows: > 0 and <= ushort.MaxValue };

            if (clear || valid) {
                lock (agent.SinksLock) {
                    agent.WebDims = clear ? null : new AgentInstance.Dim((ushort)cmd.Cols, (ushort)cmd.Rows);
                    ClampPtyLocked(agent);
                }

                // Announce the clamped size so every web viewer re-locks (and reconnect resends the
                // real size, not stale ones). Outside the lock, best-effort, fire-and-forget — same
                // as the local resize path in ApplyResizeClamp.
                _ = SafeSendDimsAsync(agent);
            }
        }

        return Task.CompletedTask;
    }

    static readonly TimeSpan ReRegisterRetryDelay = TimeSpan.FromMilliseconds(250);
    const           int      ReRegisterMaxAttempts = 3;

    /// <summary>
    /// Re-registers this daemon's live agents with the server (AgentRegistered +
    /// AgentStatusChanged) so per-session ownership is restored after a (re-)connect. Wired into
    /// <see cref="ServerConnection.ReRegisterAgentsHook"/> and awaited inside
    /// <see cref="ServerConnection.RegisterDaemonAsync"/> BEFORE readiness is restored — so a
    /// permission invoke gated on <c>IsReady</c> can't fire before ownership recovery.
    ///
    /// Each agent's re-registration is retried a bounded number of times before giving up, so a
    /// transient blip doesn't leave that agent's ownership unrestored while the daemon still
    /// flips ready (the qodo "ready despite reregister failures" gap). On final failure we log and
    /// move on rather than throw: one agent's persistent failure must NOT withhold readiness for
    /// the whole daemon (that would block every other agent's permissions and loop reconnects).
    /// The bounded ownership-retry in <see cref="ServerConnection.RequestPermissionAsync"/> is the
    /// final safety net for the residual case.
    /// </summary>
    internal async Task ReRegisterAgentsAsync() {
        // PrivateLocal agents are never registered with the server, so never re-register them.
        foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
            // A published launch-window verdict (or its reported flag) means this agent is terminally
            // Failed and about to be unregistered by the finalizer — re-sending its (possibly stale
            // "Running") non-failure Status would clear the FailureReason the verdict's LaunchFailed
            // set server-side (finding 2). The outer Status filter can still admit it during the race
            // where the finalizer has flipped the flag but not yet the Status field, so this inner
            // re-check — on the properly-ordered flag/verdict, not the plain Status — is what closes
            // it. Skip re-registration entirely; the finalizer owns this agent's terminal transition.
            if (VerdictForbidsNonFailureStatus(agent)) continue;

            for (var attempt = 1; ; attempt++) {
                try {
                    await _server.AgentRegisteredAsync(agent.Id, agent.Prompt, agent.Model, agent.Effort, agent.RepoPath, agent.SandboxPolicy, agent.ApprovalPolicy, agent.PermissionPreset, agent.RuntimeTransport);

                    // Re-gate the status send atomically under _reapLock, per attempt (finding 1
                    // refinement): the outer pre-check cannot cover a verdict published DURING the
                    // AgentRegistered await above (or on a later retry). The gate suppresses the send
                    // if a verdict is now published, else initiates it before publication can proceed
                    // so it is ordered-before any LaunchFailed. Awaited OUTSIDE the lock (the gate only
                    // holds it across initiation), preserving the retry-on-failure semantics.
                    if (agent.Runtime is AcpHostedAgentRuntime acpRuntime) {
                        if (!acpRuntime.TryInitiateNonFailureStatusSend(
                                () => _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId), out var statusSend))
                            break; // verdict published → terminal Failed; skip this agent's re-registration
                        await statusSend;
                    } else {
                        await _server.AgentStatusChangedAsync(agent.Id, agent.Status, agent.SessionId);
                    }

                    // Re-send the fixed PTY dims. The server stores them in memory, so a
                    // server restart (not just a daemon blip) wipes them — without this
                    // resend the read-only viewers never re-lock and the TUI garbles
                    // again exactly as before the fix. Best-effort: its own
                    // catch keeps a dims-send failure from escaping to the retry handler
                    // (which would re-register the agent) or withholding readiness.
                    try {
                        await _server.SendTerminalDimensionsAsync(agent.Id, agent.CurrentCols, agent.CurrentRows);
                    } catch (Exception ex) {
                        LogTerminalDimsSendFailed(ex, agent.Id);
                    }

                    // do NOT replay the full output buffer here. The old
                    // replay re-sent the entire 2 MB ring on every reconnect, which
                    // the server appended to its own buffer and live-broadcast on
                    // top of the current screen — duplicated, and interleaved with
                    // the read loop's concurrent live sends, producing the garbled
                    // Terminal tab. The server retains its own per-agent buffer
                    // across a daemon rebind (it only clears on reconcile-to-Failed),
                    // so late-joining web clients still get history via the server's
                    // SubscribeToTerminal replay. Continuity of in-flight output is
                    // handled by TerminalOutputSender, which holds unsent chunks
                    // while the transport is down and flushes them, in order, once
                    // the connection is back.
                    break;
                } catch (Exception) when (attempt < ReRegisterMaxAttempts && !_shutdownCts.IsCancellationRequested) {
                    try {
                        await Task.Delay(ReRegisterRetryDelay, _shutdownCts.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }
                } catch (Exception ex) {
                    LogReRegisterFailed(ex, agent.Id);

                    break;
                }
            }
        }
        // NOTE: the settlement lost-ack re-delivery is deliberately NOT here — this hook runs inside the
        // registration bracket BEFORE readiness is restored, so CommandAckAsync would drop every ack. It
        // is wired to OnRegisteredHook instead, which runs post-MarkRegistered (IsReady == true).
    }

    static readonly TimeSpan StartupTimeout     = TimeSpan.FromSeconds(90);
    static readonly TimeSpan MinSessionLifespan = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when the agent process exited before establishing a real interactive
    /// session. We require both that output was actually received (the read loop
    /// observed at least one chunk) AND that the gap between spawn and the last
    /// output is at least <see cref="MinSessionLifespan"/>. The
    /// <paramref name="hasReceivedOutput"/> guard prevents a no-output process
    /// from being misclassified when the <c>CreatedAt</c> and <c>LastOutputAt</c>
    /// field initializers happen to straddle a long pause.
    /// </summary>
    internal static bool IsStartupFailure(DateTime createdAt, DateTime lastOutputAt, bool hasReceivedOutput)
        => !hasReceivedOutput || lastOutputAt - createdAt < MinSessionLifespan;

    /// <summary>Design spec §3.5: a <c>LaunchFailed</c> reason must never be forwarded empty. A
    /// verdict's <c>Reason</c> should never be blank in practice — every writer
    /// (<c>HandleMcpSurfaceViolation</c>, the bridge's reason-bearing reap callback) passes a coded
    /// string — but this is the general, defensive cover, keyed off a caller-supplied identifier for
    /// WHAT produced the blank reason (a type name today) rather than an exception specifically, so
    /// the same mapping also covers a pre-<c>StartAsync</c> factory failure's plain exception message
    /// if a future call site adopts it.</summary>
    internal static string MapLaunchFailureReason(string? reason, string fallbackSource) =>
        !string.IsNullOrWhiteSpace(reason)
            ? reason
            : AcpHostedAgentRuntimeFactory.FormatFallbackLaunchReason(fallbackSource); // finding 5: single owner of the format

    static readonly HashSet<string> ValidEffortLevels = ["low", "medium", "high", "max"];

    /// <summary>When an unlinked launch stops being ordinary slowness and starts being worth saying
    /// out loud. A healthy codex links in about five seconds, so a minute is far past normal while
    /// still well short of the poll timeout that ends detection entirely.</summary>
    static readonly TimeSpan SessionIdSlowWarnAfter = TimeSpan.FromSeconds(60);

    static readonly TimeSpan SessionIdPollInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan SessionIdPollTimeout  = TimeSpan.FromMinutes(3);

    // Codex turn-start diagnostic: how long, and how often, to watch a hosted Codex reviewer's rollout for growth
    // after a round's input is delivered, so the daemon log can say whether Codex began a turn.
    // A working reviewer appends response items within seconds of starting; 2 minutes of silence
    // is the "received input, produced no turn" signature, not a slow-but-working reviewer.
    static readonly TimeSpan CodexTurnObserveInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan CodexTurnObserveTimeout  = TimeSpan.FromMinutes(2);

    /// Locates the transcript a freshly spawned PTY agent writes — Claude's per-worktree
    /// project dir (a symlink onto the source repo's, shared with the user's own sessions,
    /// so the locator disambiguates by cwd), Codex's rollout tree (disambiguated by cwd and
    /// spawn time). A vendor without a locator is a no-op. Best-effort background work,
    /// cancelled with the agent; it never blocks a launch.
    Task DetectSessionIdAsync(AgentInstance agent, string vendor, DateTime spawnedAtUtc) {
        Func<ISet<string>, (string SessionId, string Path)?>? locate = vendor.ToLowerInvariant() switch {
            "claude" => ruledOut => SessionTranscriptLocator.TryLocateWinner(
                _harnesses.Of<ClaudeHarness>().Paths.ProjectDir(agent.Worktree.Path), agent.Worktree.Path, spawnedAtUtc, ruledOut),
            "codex"  => ruledOut => CodexSessionRolloutLocator.TryLocateWinner(
                _harnesses.Of<CodexHarness>().Paths.Sessions, agent.Worktree.Path, spawnedAtUtc, ruledOut),
            _        => null,
        };
        if (locate is null) return Task.CompletedTask;

        Interlocked.Increment(ref _discoveryStarts);
        return RunDiscoveryAsync(agent, locate);
    }

    internal Task RunDiscoveryForTest(AgentInstance agent, Func<ISet<string>, (string SessionId, string Path)?> locate) =>
        RunDiscoveryAsync(agent, locate);

    async Task RunDiscoveryAsync(AgentInstance agent, Func<ISet<string>, (string SessionId, string Path)?> locate) {
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(agent.ReadCts.Token, _shutdownCts.Token);
            var discovery = new TranscriptDiscovery(TimeProvider.System, SessionIdPollInterval, SessionIdPollTimeout);

            // Cancelled and awaited in the finally below, never merely dropped: an agent that exits
            // inside the window is finalized and cleaned up without anyone touching ReadCts, and a
            // warning that lands after that describes an agent nobody has any more.
            using var warnCts  = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var       warnTask = WarnIfStillUnlinkedAsync(agent, warnCts.Token);

            try {

            var found = await discovery.RunAsync(locate, async winner => {
                // Mutation first, pulse second — and the pulse before any server call, which can
                // stall on a reconnect and must never hold the app's status push hostage.
                agent.SessionId ??= winner.SessionId;
                agent.TranscriptPath = winner.Path;
                _statusNotifier.Pulse();
                LogSessionIdDetected(agent.Id, winner.SessionId);

                if (agent.IsPrivate) return;
                await _server.AppendAgentRunEventAsync(agent.Id, new AgentRunHeartbeat(winner.SessionId));
                await _server.AgentStatusChangedAsync(agent.Id, agent.Status, winner.SessionId);
            }, cts.Token);

            if (!found && !cts.IsCancellationRequested) LogSessionIdNotDetected(agent.Id, SessionIdPollTimeout.TotalSeconds);
            } finally {
                warnCts.Cancel();
                await warnTask.ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogSessionIdDetectFailed(ex, agent.Id);
        }
    }

    /// <summary>Says, once and early, what the operator is otherwise left to infer from three minutes
    /// of nothing. Never cancels or reaps the launch — detection keeps running to its own timeout,
    /// and a slow link still resolves.</summary>
    async Task WarnIfStillUnlinkedAsync(AgentInstance agent, CancellationToken ct) {
        try {
            await Task.Delay(SessionIdSlowWarnAfter, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        if (agent.SessionId is null)
            LogSessionIdSlow(agent.Id, SessionIdSlowWarnAfter.TotalSeconds, SessionIdPollTimeout.TotalSeconds);
    }

    async Task RunHeartbeatLoopAsync(CancellationToken ct) {
        while (await _heartbeatTimer.WaitForNextTickAsync(ct)) {
            // Phase B (D3): reap review-flow reviewers past their lifetime/idle backstop. Done
            // before the per-agent loop so a reaped reviewer isn't also heartbeated this tick. Reason
            // stamped on PendingEndReason so the end attribution is correct even if HandleStopAgent's
            // own end call loses to the read-loop's.
            // Phase B (D4 §6.4(2a)): retry killing any unconfirmed-death quarantined process,
            // draining those confirmed gone (frees admission). Single-flight (skips if a prior retry runs).
            _ = RetryQuarantineOnceAsync(ct);

            // Phase B (D4 §6.4(3)): re-run the orphan reap — record pass is epoch-guarded (never
            // touches a current-incarnation live agent), env-marker scan reaps a prior incarnation's
            // recordless survivors. Fire-and-forget; single-flight; swallows its own faults.
            _ = ReapOrphansOnceAsync(ct);

            // Selection is a snapshot; the DECISION is the claim each of these makes under the agent's
            // own fence (TryClaimReapAsync), which re-validates incarnation and — for the idle/wedge
            // rules — the activity generation this candidate was selected against. The end-reason stamp
            // moved in there with it: an aborted reap must not leave "reviewer_idle_expired" on a live
            // agent for whatever ends it later.
            // §2.7 B6 arm-A: a Park candidate (a resumable reviewer past the short resumable idle bound)
            // routes to the PARK path — slot freed, Codex thread kept for resume; every reap rule keeps
            // flowing to the reap path unchanged. Both are fire-and-forget and contain their own faults.
            foreach (var candidate in FindReviewersToReap())
                _ = candidate.Park ? ParkReviewerAsync(candidate) : ReapReviewerAsync(candidate);

            // PrivateLocal agents get no heartbeats and no stuck-Starting auto-stop (deny-all;
            // the local user is present and drives them directly).
            foreach (var agent in _agents.Values.Where(a => (a.Status is "Starting" or "Running") && !a.IsPrivate)) {
                // Detect agents stuck in "Starting" with no output
                if (agent.Status                         == "Starting" &&
                    DateTime.UtcNow - agent.LastOutputAt > StartupTimeout) {
                    LogAgentStuck(agent.Id, (DateTime.UtcNow - agent.LastOutputAt).TotalSeconds, agent.Runtime.Pid, agent.Runtime.HasExited);
                    _ = HandleStopAgent(agent.Id);

                    continue;
                }

                _ = _server.AppendAgentRunEventAsync(
                    agent.Id,
                    new AgentRunHeartbeat(agent.SessionId)
                );
            }
        }
    }

    async Task RunDaemonHeartbeatLoopAsync(CancellationToken ct) {
        var loop = new DaemonHeartbeatLoop(_server, PingDeadline, _logger);

        while (await _daemonHeartbeat.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but we
            // run as an unobserved background Task — guarding here keeps
            // the loop alive even if a future change accidentally lets an
            // exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Heartbeat tick faulted — continuing loop");
            }
        }
    }

    async Task RunTokenRefreshLoopAsync(CancellationToken ct) {
        var loop = new TokenRefreshLoop(new TokenStoreRefreshPort(_tokens, _config.Profiles.Name, ProactiveRefreshWindow), _logger, ProactiveRefreshMinInterval);

        while (await _tokenRefresh.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Token refresh tick faulted — continuing loop");
            }
        }
    }

    async Task RunSpoolDrainLoopAsync(CancellationToken ct) {
        var loop = new SpoolDrainLoop(
            _configRoot,
            _http,
            _config.ServerUrl,
            new HookSpool(_configRoot),
            new TranscriptSpool(_configRoot),
            _logger,
            onWhatsDoneRequested: SpawnWhatsDoneGenerator);

        while (await _spoolDrain.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Spool-drain tick faulted — continuing loop");
            }
        }
    }

    async Task RunTitleResolveLoopAsync(CancellationToken ct) {
        var loop = new TitleResolveLoop(
            SnapshotAgentsForTitles,
            (agentId, title) => { if (_agents.TryGetValue(agentId, out var agent)) SetResolvedTitle(agent, title); },
            new TitleServerPort(_http, _config.ServerUrl),
            NativeTitleFor,
            GenerateTitleForAsync,
            TimeProvider.System,
            _logger);

        while (await _titleResolve.WaitForNextTickAsync(ct)) {
            // Defence in depth: TickAsync is intentionally total, but this runs as an
            // unobserved background Task — guard here so the loop survives even if a
            // future change lets an exception escape the tick.
            try {
                await loop.TickAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Title resolve tick faulted — continuing loop");
            }
        }
    }

    /// A private agent's contract is "no per-agent server calls", so its view carries no session
    /// id: the resolver then never polls or pushes for it and only the local lanes apply.
    IReadOnlyList<TitleAgentView> SnapshotAgentsForTitles() =>
        [.. _agents.Values.Select(a => new TitleAgentView(
            a.Id, a.Vendor, a.Prompt,
            a.IsPrivate ? null : a.SessionId ?? (a.Runtime as IAcpTranscriptSource)?.AcpSessionId,
            a.TranscriptPath, a.CreatedAt))];

    static string? NativeTitleFor(TitleAgentView agent) =>
        agent is { Vendor: "claude", TranscriptPath: { } path } ? ClaudeNativeTitle.TryExtract(path) : null;

    async Task<string?> GenerateTitleForAsync(TitleAgentView agent, CancellationToken ct) {
        var result = await TitleGeneration.GenerateAsync(
            agent.Prompt!, null, msg => _logger.LogDebug("Title generation ({AgentId}): {Message}", agent.Id, msg),
            _config.Profiles.Resolution.Profile, _home,
            vendor: agent.Vendor == "codex" ? "codex" : "claude", ct: ct);

        return result?.Result;
    }

    async Task CleanupAgentAsync(string agentId) {
        // Phase B (D1): claim the single-flight teardown BEFORE removing the agent from _agents.
        // TryGetValue (not TryRemove) keeps the agent COUNTED in ActiveCount for the whole teardown, so a
        // concurrent launch can't observe an under-counted EffectiveCount mid-teardown and over-admit
        // (the P2 admission race). The CompareExchange latch on the SAME instance guarantees exactly one
        // teardown even if the launch-catch and the read-loop's finally race here.
        if (!_agents.TryGetValue(agentId, out var agent)) return;
        if (Interlocked.CompareExchange(ref agent.CleanupStarted, 1, 0) != 0) return;

        // The reviewer process has exited by the time we get here (this runs off the read-loop's exit
        // path), so revoke its bridge token now — after any final submit_review_result was served.
        if (agent.ReviewerBridgeToken is { } reviewerToken) {
            try { _permissionBridge.RevokeReviewerToken(reviewerToken); } catch (Exception ex) { LogCleanupStepFailed(ex, "revoking reviewer bridge token", agentId); }
        }

        // Wake any attached local clients blocked on the user's stdin so they can flush the
        // last output and send Exited (the agent is going away). The exit code is already
        // captured on agent.Runtime, so disposing it below doesn't lose it.
        try { await agent.ExitedCts.CancelAsync(); } catch { /* best-effort */ }

        // Each cleanup step is best-effort so later steps still run
        try { await agent.Runtime.DisposeAsync(); } catch (Exception ex) { LogCleanupStepFailed(ex, "disposing process", agentId); }

        if (_launchers.TryGetValue(agent.Vendor, out var launcher)) {
            try { launcher.Cleanup(agent); } catch (Exception ex) { LogCleanupStepFailed(ex, "launcher.Cleanup", agentId); }
        }

        // Owned worktrees are daemon-created and safe to remove. A borrowed cwd is the
        // user's own checkout (local in-place launch) — NEVER delete it or its branch:
        // RemoveAsync would Directory.Delete / `git worktree remove --force` + `branch -D`.
        // This is the spec's top safety invariant.
        if (agent.Work == WorkLocation.OwnedWorktree) {
            try { await WorktreeManager.RemoveAsync(agent.Worktree); } catch (Exception ex) { LogCleanupStepFailed(ex, "removing worktree", agentId); }
        }

        // Phase B (D4 §6.4(2)/(2a)): confirm the process is actually gone before dropping its PID
        // record. Prove "still ours" with the STORED spawn identity — NEVER a freshly-recaptured token:
        // if the child exited and its pid was recycled, a re-capture would adopt the unrelated process's
        // identity and the heartbeat would later kill it. Quarantine ONLY when the pid is alive AND still
        // matches the stored identity (a stuck child of ours) — retain the record + count it against
        // admission (fail closed) so the heartbeat retries the kill to confirmed death. A dead pid, a
        // recycled pid (proven mismatch), or an agent with no captured identity → confirmed gone, delete
        // the record. Add to quarantine BEFORE removing from _agents so EffectiveCount never dips
        // (Activeized→quarantined is count-preserving).
        var pid = agent.Runtime.Pid;
        // Quarantine (retain + count) when the child may still be OURS-and-alive: a stored identity that
        // still matches (true) OR is uncomparable (null — unreadable/foreign token). Delete only on a
        // proven-gone pid: dead, a conclusive recycle (MatchesTri == false), or an agent that was never
        // identified. Collapsing the uncomparable case to "gone" would drop a live child's record.
        if (agent.StartIdentity is { } startIdentity
            && ProcessIdentity.IsAlive(pid)
            && ProcessIdentity.MatchesTri(pid, startIdentity) != false) {
            _quarantine?.Add(new AgentKillQuarantine.Entry(
                agent.Id, pid, startIdentity, agent.Kind.ToString(), agent.CreatedAt, agent.FlowRunId, agent.FlowRole));
            _logger.LogWarning(
                "Agent {AgentId} (pid {Pid}) still alive after teardown — quarantined for heartbeat kill-retry", agent.Id, pid);
        } else {
            DeletePidRecord(agentId); // confirmed dead / conclusively recycled pid / never-identified
        }

        // Now drop the agent from the live registry — after a surviving child is already in quarantine,
        // so a concurrent launch never sees EffectiveCount transiently under-count this agent.
        WithdrawAndUnpublish(agentId);

        // Skip server unregister during shutdown — _ct is cancelled and the call
        // would throw TaskCanceledException. The server detects the daemon
        // disconnection through SignalR's transport-level signals. Filtered
        // catch covers the residual race where shutdown fires mid-call.
        // PrivateLocal agents were never registered, so never unregister them (deny-all).
        if (!agent.IsPrivate && !_shutdownCts.IsCancellationRequested) {
            try { await _server.AgentUnregisteredAsync(agentId); } catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) { } catch (Exception ex) {
                LogCleanupStepFailed(ex, "unregistering", agentId);
            }
        }
    }

    /// <summary>How long the whole shutdown report may take, across every agent. A shutdown that waits
    /// on the server is a shutdown that can be killed mid-teardown, so this bounds the pass rather
    /// than each call: agents past the ceiling go unreported, and the server is left to infer their
    /// ending from the transport drop.</summary>
    internal static readonly TimeSpan ShutdownReportBudget = TimeSpan.FromSeconds(5);

    /// <summary>The reason stamped on a run this daemon ended by going away, distinct from the user
    /// asking it to stop — the two look identical downstream otherwise.</summary>
    internal const string DaemonShutdownStopReason = "daemon_shutdown";

    /// <summary>Reports every live agent as ended, on its own budget and its own token: by the time
    /// this runs <c>_shutdownCts</c> is cancelled, and passing that token would cancel every call
    /// before it left. Best-effort throughout — teardown continues whatever this manages to say.</summary>
    async Task ReportAgentsEndedForShutdownAsync() {
        // Only agents this teardown still owns. One whose own finalizer already ran has reported its
        // real ending and is waiting on cleanup; reporting it again would append a second stop event
        // over the top of a truer one and race that finalizer's unregister.
        var live = _agents.Values.Where(a => !a.IsPrivate && a.Status is "Starting" or "Running").ToList();

        if (live.Count == 0) return;

        using var budget = new CancellationTokenSource(ShutdownReportBudget);

        LogShutdownReportStarted(live.Count, ShutdownReportBudget.TotalSeconds);

        foreach (var agent in live) {
            if (budget.IsCancellationRequested) {
                LogShutdownReportBudgetExpired(agent.Id);

                return;
            }

            // Not Completed: this daemon killed the child, whatever it was in the middle of, and a
            // run reported as completed is a failure classified as success. The reason carries the
            // distinction from a run that failed on its own.
            SetAgentStatus(agent, "Failed");

            try {
                await _server.ReportAgentEndedForShutdownAsync(
                    agent.Id, agent.SessionId, "Failed", DaemonShutdownStopReason, agent.Runtime.ExitCode, budget.Token)
                    .WaitAsync(budget.Token);

                LogShutdownReported(agent.Id);
            } catch (OperationCanceledException) {
                LogShutdownReportBudgetExpired(agent.Id);

                return;
            } catch (Exception ex) {
                LogShutdownReportFailed(ex, agent.Id);
            }
        }
    }

    public virtual async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;

        Interlocked.Increment(ref _disposeBodyRuns);

        // Faultable awaits below are each contained + logged individually so one failure can't
        // skip its siblings or the mandatory resource release in the finally — a disposal path
        // must never throw into DI teardown (NativeAOT: unhandled → abort()).
        try {
            // §3.3: close the execution lane to new work FIRST — before anything else, and without waiting for
            // anything. Order matters: cancelling the shutdown token below releases whatever the in-flight item
            // was waiting on (a consent prompt, say), and if the lane were still open at that moment it would
            // simply move on to the queued per-agent stops and run them against children the teardown below is
            // already killing. Closing the door first is what makes the supersession deterministic rather than a
            // race. The drain and the terminal settlement of accepted sequenced items happen in the processor's
            // own DisposeAsync at the very end of this method.
            Processor?.StopAcceptingForShutdown();

            try {
                await _shutdownCts.CancelAsync();
            } catch (ObjectDisposedException) {
                // Already torn down elsewhere — nothing left to cancel.
            } catch (Exception ex) {
                // A registered cancellation callback that throws faults the returned task
                // (AggregateException per the CTS contract) even though the cancel itself
                // succeeded. Uncontained, that fault would skip the processor drain and ALL
                // child termination/cleanup below — and the run-once guard means the DI pass
                // can never retry, stranding live child processes. Log and continue.
                LogDisposeStepFailed(ex, "shutdown-cancel");
            }

            // Drain and settle the execution lane BEFORE the child-teardown snapshot below. The token
            // cancellation above aborts a consent-parked launch promptly, but a launch that already
            // passed consent keeps running to registration — and if the lane settled AFTER the
            // enumeration, that late-registered child would miss teardown and survive graceful
            // shutdown (the next-boot PID scan is a recovery backstop, not a substitute). The
            // supersession semantics are unaffected: _closed was set first, so every queued item
            // still settles (sequenced) or discards (un-sequenced) in the drain arm regardless of
            // when the drain runs.
            try {
                if (Processor is { } lane) await lane.DisposeAsync();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "processor-drain");
            }

            foreach (var agent in _agents.Values.Where(a => a.Status is "Starting" or "Running")) {
                try {
                    await agent.ReadCts.CancelAsync();
                    await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
                } catch {
                    /* best-effort */
                }
            }

            // Children are gone; tell the server so before the registry is torn down. Without this the
            // daemon's whole account of a shutdown is the transport dropping, which says a daemon went
            // away but nothing about the agents it was hosting — and a successor daemon reconnecting
            // under the same name re-binds those retained entries onto its own connection, leaving
            // sessions the surface still composes into and a Stop that has nothing to act on.
            await ReportAgentsEndedForShutdownAsync();

            foreach (var agentId in _agents.Keys.ToList()) {
                try {
                    await CleanupAgentAsync(agentId);
                } catch (Exception ex) {
                    LogCleanupStepFailed(ex, "final cleanup", agentId);
                }
            }
        } finally {
            // Mandatory release — runs even if a step above threw, each step individually
            // guarded so one failure can't skip the rest.
            try {
                _heartbeatTimer.Dispose();
                _daemonHeartbeat.Dispose();
                _tokenRefresh.Dispose();
                _spoolDrain.Dispose();
                _titleResolve.Dispose();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "timers");
            }

            // The execution lane was drained and settled above, before the child-teardown snapshot;
            // this second call is a no-op (DisposeAsync is idempotent) kept as belt-and-braces for
            // any future early-return path added between the two.
            try {
                if (Processor is { } proc) await proc.DisposeAsync();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "processor");
            }

            // LAST: the shutdown CTS is disposed only after every token-dependent step above
            // (the agent-teardown loop, the lane drain and the timer-driven loops all use its
            // token). It stays readonly — the run-once guard, not a null-swap, is what makes
            // this safe against the structural double dispose.
            try {
                _shutdownCts.Dispose();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "shutdown-cts");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AgentOrchestrator dispose step '{Step}' failed; continuing shutdown")]
    partial void LogDisposeStepFailed(Exception ex, string step);

    /// <summary>
    /// Extracts readable text from the terminal output buffer by decoding UTF-8
    /// and stripping ANSI escape sequences. Returns the last ~500 chars to keep
    /// the error message reasonable for the UI snackbar.
    /// </summary>
    static string ExtractTerminalText(TerminalOutputBuffer buffer) {
        var chunks = buffer.GetAll();

        if (chunks.Count == 0) {
            return "";
        }

        var combined = new byte[chunks.Sum(c => c.Length)];
        var offset   = 0;

        foreach (var chunk in chunks) {
            Buffer.BlockCopy(chunk, 0, combined, offset, chunk.Length);
            offset += chunk.Length;
        }

        var raw     = Encoding.UTF8.GetString(combined);
        var cleaned = StripAnsiRegex().Replace(raw, "").Trim();

        return cleaned.Length > 500 ? cleaned[^500..] : cleaned;
    }

    // ── LoggerMessage source-generated methods ────────────────────────────

    // Vendor belongs on the same line as the model: without it, a model that does not belong to the
    // launched vendor is only visible by correlating with a later runtime line, and PTY vendors emit
    // no such line at all.
    [LoggerMessage(Level = LogLevel.Information, Message = "Launching agent {AgentId} for {Repo} (vendor={Vendor}, effort={Effort}, model={Model})")]
    partial void LogLaunching(string agentId, string repo, string vendor, string effort, string? model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vendor '{Vendor}' cannot apply a requested model; launching with its default and reporting no model instead of '{RequestedModel}', so the dashboard and analytics are not told a model is live that isn't.")]
    partial void LogModelSelectionUnsupported(string vendor, string requestedModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} spawned (PID={Pid}, worktree={Worktree}, vendor={Vendor})")]
    partial void LogAgentSpawned(string agentId, int pid, string worktree, string vendor);

    [LoggerMessage(
        Level   = LogLevel.Warning,
        Message = "Interactive Codex agent {AgentId} launched with sandbox={Sandbox} approval={Approval}: "
                + "kcap approval prompts will never appear for this agent and/or it can reach outside its worktree")]
    partial void LogBridgeDefeatingPosture(string agentId, string sandbox, string approval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} exited with code {ExitCode}")]
    partial void LogAgentExited(string agentId, int? exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping agent {AgentId}")]
    partial void LogStopping(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Detached sequenced execution for agent {AgentId} faulted — the processor's own lane already settled this as a terminal answer; this is fault-observation logging only, not a missed settlement")]
    partial void LogDetachedCommandFault(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tell the server that the un-sequenced launch for agent {AgentId} was refused by a shutting-down execution lane — the daemon is exiting and the server's reconciliation lanes are the backstop")]
    partial void LogRefusedLaunchNotifyFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Un-sequenced stop for agent {AgentId} refused by a shutting-down execution lane — daemon teardown supersedes it (no reply surface exists for this command)")]
    partial void LogUnsequencedStopRefused(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the explicit reviewer-model resolved report for agent {AgentId} (vendor {Vendor}, launch model {LaunchModel}) — no resolver or model no longer resolves; skipping the report (the server will fail the attempt closed)")]
    partial void LogExplicitReviewerModelUnreportable(string agentId, string vendor, string launchModel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download launch attachments for agent {AgentId} (continuing)")]
    partial void LogAttachmentDownloadFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to build the approval-policy snapshot for agent {AgentId}; launching without one (permissions fall back to prompting)")]
    partial void LogPolicySnapshotBuildFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Approval-policy snapshot for agent {AgentId} is degraded: {FirstDegradation}")]
    partial void LogPolicySnapshotDegraded(string agentId, string firstDegradation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh borrowed-checkout snapshot for agent {AgentId}; rejecting the round and terminating the reviewer")]
    partial void LogBorrowedSnapshotRefreshFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendInput received for agent {AgentId} (vendor={Vendor}, chars={Chars}, attachments={Attachments})")]
    partial void LogSendInputReceived(string agentId, string vendor, int chars, int attachments);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendInput delivered to agent {AgentId}'s {Vendor} runtime ({Chars} chars)")]
    partial void LogSendInputDelivered(string agentId, string vendor, int chars);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not report the dropped input for agent {AgentId} ({Reason})")]
    partial void LogSendInputRejectReportFailed(Exception ex, string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} not found on this daemon ({KnownAgents} agents registered)")]
    partial void LogSendInputUnknownAgent(string agentId, int knownAgents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} is private — server-origin input is ignored")]
    partial void LogSendInputPrivateAgent(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendInput to agent {AgentId} is a quit command; {Vendor} has no TUI to interpret it, stopping the agent instead of forwarding")]
    partial void LogSendInputQuitCommand(string agentId, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} was already claimed by the reviewer reaper and is being stopped — the round's dispatch fails here and the server heals it on resubmit")]
    partial void LogSendInputReapClaimed(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendInput dropped: agent {AgentId} was claimed by the reviewer reaper's unfenced absolute-lifetime path AFTER this delivery had already entered the section — the late re-read caught a healthy in-flight delivery racing a claim that landed mid-flight; the round's dispatch fails here and the server heals it on resubmit")]
    partial void LogSendInputReapClaimedLate(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reap of review-flow reviewer {AgentId} ({Reason}) aborted at the claim: {Cause}")]
    partial void LogReapAborted(string agentId, string reason, string cause);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reap of review-flow reviewer {AgentId} ({Reason}) aborted at the claim: activity advanced from generation {SelectedGeneration} to {CurrentGeneration} since it was selected")]
    partial void LogReapAbortedOnActivity(string agentId, string reason, ulong selectedGeneration, ulong currentGeneration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reap of review-flow reviewer {AgentId} ({Reason}) aborted at the claim: another claim got there first")]
    partial void LogReapAlreadyClaimed(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaping review-flow reviewer {AgentId} ({Reason}) without the per-agent delivery section: a delivery held it for more than {WaitSeconds:F0}s, and the absolute lifetime cap is not deferrable by an in-flight (possibly permanently parked) write")]
    partial void LogUnsectionedReapClaim(string agentId, string reason, double waitSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaping review-flow reviewer {AgentId} ({Reason}) failed")]
    partial void LogReapFailed(Exception ex, string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Parked resumable review-flow reviewer {AgentId} (canonical session {CanonicalSessionId}): slot freed, Codex thread kept alive for resume; hosted session-end suppressed")]
    partial void LogReviewerParked(string agentId, string canonicalSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Park of review-flow reviewer {AgentId} was REJECTED by the server — ending it on the normal path instead of parking")]
    partial void LogReviewerParkRejected(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Park of review-flow reviewer {AgentId} got no definite reply (ambiguous) — leaving it intact for the next sweep to retry")]
    partial void LogReviewerParkAmbiguous(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Parking review-flow reviewer {AgentId} ({Reason}) failed")]
    partial void LogParkFailed(Exception ex, string agentId, string reason);

    // Codex (PTY) turn-start diagnostic — after SendInput is delivered, did Codex act?
    [LoggerMessage(Level = LogLevel.Information, Message = "Codex turn started for agent {AgentId} after input ({ElapsedMs}ms after delivery)")]
    partial void LogCodexTurnStarted(string agentId, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Codex produced NO turn for agent {AgentId} within {Seconds}s of input delivery — the reviewer received the prompt but did not act on it")]
    partial void LogCodexTurnNotObserved(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Codex turn observer: could not resolve a rollout for agent {AgentId} — skipping turn-start diagnostic for this round")]
    partial void LogCodexTurnRolloutUnresolved(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Codex turn observer: rollout for agent {AgentId} became unreadable during observation — no turn verdict for this round (measurement gap, not evidence of no turn)")]
    partial void LogCodexTurnRolloutUnavailable(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Codex turn observer faulted for agent {AgentId} (diagnostic only — delivery unaffected)")]
    partial void LogCodexTurnObserveFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendSpecialKey '{Key}' dropped: agent {AgentId} not found on this daemon ({KnownAgents} agents registered)")]
    partial void LogSendSpecialKeyUnknownAgent(string agentId, string key, int knownAgents);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error reading output for agent {AgentId}")]
    partial void LogOutputReadError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} failed during startup (exit code {ExitCode}): {Reason}")]
    partial void LogStartupFailed(string agentId, int? exitCode, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report launch-window reap verdict for agent {AgentId} (continuing teardown)")]
    partial void LogVerdictReportFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent {AgentId} wedged on an unattended consent/trust dialog — failing the launch fast: {Reason}")]
    partial void LogConsentDialogWedge(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured failed-launch terminal tail for agent {AgentId} at {Path}")]
    partial void LogFailedLaunchCaptured(string agentId, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download attachment {Id}: {Status}")]
    partial void LogAttachmentNotFound(string id, System.Net.HttpStatusCode status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Attachment filename would escape directory: {FileName}")]
    partial void LogAttachmentPathEscape(string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error downloading attachment {Id}")]
    partial void LogAttachmentError(Exception ex, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to re-register agent {AgentId}")]
    partial void LogReRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send terminal dimensions for agent {AgentId} (read-only viewers may render garbled until the next reconnect)")]
    partial void LogTerminalDimsSendFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} stuck in Starting for {Seconds:F1}s with no output (PID={Pid}, exited={Exited}), terminating")]
    partial void LogAgentStuck(string agentId, double seconds, int pid, bool exited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error {Step} for agent {AgentId}")]
    partial void LogCleanupStepFailed(Exception ex, string step, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to report resolved model for agent {AgentId} (continuing)")]
    partial void LogReportResolvedModelFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to launch agent {AgentId}")]
    partial void LogLaunchFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during cleanup of agent {AgentId}")]
    partial void LogCleanupError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error stopping agent {AgentId}")]
    partial void LogStopError(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit failed for agent {AgentId}; falling back to SIGTERM")]
    partial void LogGracefulExitFailed(Exception ex, string agentId);

    // The vendor is a parameter, not the literal "claude": this path stops EVERY hosted vendor, and
    // naming the wrong CLI in a stop diagnostic misdirects whoever is reading the daemon log to work
    // out which reviewer failed to exit.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Graceful /exit window of {Seconds}s elapsed for agent {AgentId} without {Vendor} exiting; falling back to SIGTERM")]
    partial void LogGracefulExitTimedOut(string agentId, double seconds, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end session for agent {AgentId} (server may not record SessionEnded)")]
    partial void LogEndSessionFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to register local agent {AgentId} with the server (continuing; terminal stays usable)")]
    partial void LogLocalRegisterFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EndAgentSession for agent {AgentId} did not complete within {Seconds}s; proceeding with cleanup while the retry continues in the background (server reconciles on daemon disconnect)")]
    partial void LogEndSessionTimedOut(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DaemonStatusReport send did not complete within {Seconds}s; releasing the ordering gate (invocation already issued, so wire order still holds)")]
    partial void LogStatusReportSendTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Abandoned DaemonStatusReport send faulted after the ordering gate was released — ignoring")]
    partial void LogStatusReportSendFailedInBackground(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Spawned what's-done generator for session {SessionId} (PID {Pid})")]
    partial void LogWhatsDoneSpawned(string sessionId, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to spawn what's-done generator for session {SessionId}")]
    partial void LogWhatsDoneSpawnFailed(Exception? ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to persist repo path for agent {AgentId}")]
    partial void LogRepoPathPersistFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP bind/forwarder setup failed for agent {AgentId} — proceeding with no live transcript for this session")]
    partial void LogAcpBindFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP bind for agent {AgentId} resolved after the agent had already finalized — aborting setup without registering a binding")]
    partial void LogAcpBindAbortedAgentGone(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP transcript forwarder faulted for agent {AgentId}")]
    partial void LogAcpForwarderFaulted(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source claim for agent {AgentId} failed — tearing the launch down (the session cannot be envelope-stamped)")]
    partial void LogAcpSourceClaimFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Source claim for agent {AgentId} was rejected by the server — tearing the launch down")]
    partial void LogAcpSourceClaimRejected(string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirm loop for agent {AgentId} stopped without confirming (outcome {Outcome}) — the server's recovery owns the provisional row")]
    partial void LogConfirmSessionLaunchStopped(string agentId, string outcome);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Confirm for agent {AgentId} did not land — retrying (a confirm failure never tears the agent down)")]
    partial void LogConfirmSessionLaunchRetrying(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP final transcript drain for agent {AgentId} exceeded its {Seconds}s budget — proceeding to end the session; any undrained transcript is lost")]
    partial void LogAcpFinalDrainTimedOut(string agentId, double seconds);

    // The transcript scan is the only place agent.SessionId is ever assigned — the vendor's own
    // session-start hook posts to the SERVER, not here — so none of these three may describe it as a
    // fallback behind some other link.
    [LoggerMessage(Level = LogLevel.Information, Message = "Terminating {Count} agent(s) for daemon shutdown; reporting them ended within {Seconds:F0}s")]
    partial void LogShutdownReportStarted(int count, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} reported ended for daemon shutdown")]
    partial void LogShutdownReported(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown report budget expired at agent {AgentId}; the rest of this daemon's agents end only as far as the server infers from the transport drop")]
    partial void LogShutdownReportBudgetExpired(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to report agent {AgentId} ended for daemon shutdown")]
    partial void LogShutdownReportFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} linked to session {SessionId} by matching its transcript file")]
    partial void LogSessionIdDetected(string agentId, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No transcript matched agent {AgentId}'s worktree within {Seconds:F0}s, so it has no session id: the surface cannot leave 'waiting for session to start'. The child may be parked on a startup prompt — a TUI sitting on one still renders, so it does not read as stuck.")]
    partial void LogSessionIdNotDetected(string agentId, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcript-based session-id detection failed for agent {AgentId}; nothing else assigns one, so this agent stays unlinked")]
    partial void LogSessionIdDetectFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent {AgentId} still has no session id {Seconds:F0}s after spawn; the surface is showing 'waiting for session to start'. Detection continues until {TimeoutSeconds:F0}s.")]
    partial void LogSessionIdSlow(string agentId, double seconds, double timeoutSeconds);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\x1B[()][AB012]|\x1B\[[\?]?[0-9;]*[hlm]")]
    private static partial Regex StripAnsiRegex();

    /// <summary>
    /// Method exposed for unit tests so they can drive <see cref="HandleLaunchAgent"/>
    /// without going through SignalR. Keeps the private handler private to everyone else.
    ///
    /// <para>§3.3: a launch no longer executes on the caller's stack (it is committed to the serial lane),
    /// so this seam ALSO waits for the lane to go quiescent — every caller asserts on the launch's side
    /// effects immediately after it returns, and that is what they mean. Use
    /// <see cref="SubmitLaunchAgentForTest"/> when the point of the test is that the handler returns
    /// WITHOUT execution (a consent-parked launch would never drain).</para>
    /// </summary>
    internal async Task HandleLaunchAgentForTest(LaunchAgentCommand cmd) {
        await HandleLaunchAgent(cmd);
        await DrainLaneForTest();
    }

    /// <summary>§3.3 test-only: drive the launch handler and return the moment IT returns — no waiting for
    /// the lane. This is the seam for the unparking pins.</summary>
    internal Task SubmitLaunchAgentForTest(LaunchAgentCommand cmd) => HandleLaunchAgent(cmd);

    /// <summary>§3.3 test-only: completes once the execution lane has nothing queued or in flight (or
    /// immediately when no processor is published).</summary>
    internal Task DrainLaneForTest() => Processor?.WhenIdleForTest() ?? Task.CompletedTask;

    /// <summary>§3.3 test-only: publish the sequenced processor after construction — the pair to the
    /// <c>deferProcessorPublication</c> constructor flag, so a test can observe the transition barrier that
    /// production never exercises (publication there precedes handler wiring).</summary>
    internal void PublishSequencedProcessorForTest() => PublishSequencedProcessor();

    /// <summary>§3.3 test-only: the published processor, so a test can assert on the lane's own tracking
    /// state (active launch instances, queued-stop depth, coalescing keys).</summary>
    internal SequencedCommandProcessor? ProcessorForTest => Processor;

    /// <summary>§3.3 test-only: the un-sequenced stop-admission probe, so a test can pin what admission
    /// actually considers a target.</summary>
    internal bool IsKnownStopTargetForTest(string agentId) => IsKnownStopTarget(agentId);

    /// <summary>Test-only: drive the per-agent PTY read loop directly (mirrors the fire-and-forget
    /// <c>_ = ReadAgentOutputAsync(agent)</c> in HandleLaunchAgent) so a seeded agent's consent-dialog
    /// fail-fast + tail-capture can be exercised without the full ReviewFlow launch gauntlet.</summary>
    internal Task ReadAgentOutputForTest(AgentInstance agent) => ReadAgentOutputAsync(agent);

    /// <summary>Test-only: drive the read loop's finally-block teardown directly (mirrors what
    /// <c>ReadAgentOutputAsync</c>'s own finally invokes) so the verdict-report arm, its ordering
    /// against the process-exit wait, and the rest of teardown can be exercised without needing a
    /// runtime whose <c>ReadOutputAsync</c> stream can be driven to completion.</summary>
    internal Task FinalizeAgentRunForTest(AgentInstance agent) => FinalizeAgentRunAsync(agent);

    /// <summary>Test-only: the retained failed-launch log root, for asserting a capture landed.</summary>
    internal FailedLaunchLog? FailedLaunchLogForTest => _failedLaunchLog;

    /// <summary>Test-only: register a pre-built agent so cleanup/lifecycle can be driven directly.</summary>
    internal void RegisterAgentForTest(AgentInstance agent) => PublishAgent(agent);

    /// <summary>Test-only: look up a tracked agent by id (null if absent), so a launch test can
    /// assert the resolved <see cref="AgentInstance.Work"/> / <see cref="AgentInstance.Worktree"/>.</summary>
    internal AgentInstance? GetAgentForTest(string agentId) => _agents.GetValueOrDefault(agentId);

    /// <summary>Test-only entry point to the private cleanup path.</summary>
    internal Task CleanupAgentForTest(string agentId) => CleanupAgentAsync(agentId);

    /// <summary>Phase B (D4 §6.4(2a)): drive one quarantine-retry sweep (drains confirmed-dead
    /// entries and deletes their durable records) so a test needn't wait for a heartbeat tick.</summary>
    internal Task RetryQuarantineForTest() => RetryQuarantineOnceAsync(_shutdownCts.Token);

    /// <summary>Test-only: number of agents currently tracked (for awaiting cleanup).</summary>
    internal int ActiveAgentCountForTest => _agents.Count;

    /// <summary>Test-only entry point to the shared stop EXECUTOR — the ungated path internal reaping and
    /// local-socket stops use, which deliberately bypasses the execution lane (§1.11).</summary>
    internal Task HandleStopAgentForTest(string agentId) => HandleStopAgent(agentId);

    /// <summary>§3.3 test-only entry point mirroring the <c>StopAgent</c> hub wiring — i.e. the UN-SEQUENCED
    /// stop COMMAND, which is committed to the execution lane, unlike <see cref="HandleStopAgentForTest"/>
    /// above. Waits for the lane to drain, so a caller can assert on the stop's side effects.</summary>
    internal async Task HandleServerStopAgentForTest(string agentId) {
        await HandleUnsequencedStopAgent(agentId);
        await DrainLaneForTest();
    }

    /// <summary>§3.3 test-only: commit an un-sequenced stop and return the moment the handler returns — no
    /// waiting for the lane.</summary>
    internal Task SubmitServerStopAgentForTest(string agentId) => HandleUnsequencedStopAgent(agentId);

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.6): test-only entry point to the sequenced
    /// stop handler so a heal-barrier test can drive a Seq'd <see cref="StopAgentV2"/> through the
    /// processor's serial lane (advances the watermark; the confirmed-dead id then falls out of both
    /// LiveAgents and Quarantined). §3.3: waits for the lane to drain, since the handler itself no longer
    /// awaits execution.</summary>
    internal async Task HandleStopAgentV2ForTest(StopAgentV2 cmd) {
        await HandleStopAgentV2(cmd);
        await DrainLaneForTest();
    }

    /// <summary>§3.3 test-only: submit a sequenced stop and return the moment the handler returns — no
    /// waiting for the lane, so a test can pin that ACCEPTANCE is answered while EXECUTION queues.</summary>
    internal Task SubmitStopAgentV2ForTest(StopAgentV2 cmd) => HandleStopAgentV2(cmd);

    /// <summary>Test-only entry point to the private send-input handler (bracketed-paste submit).</summary>
    internal Task HandleSendInputForTest(SendInputCommand cmd) => HandleSendInput(cmd);

    /// <summary>Test-only: run ONE selected reap exactly as the heartbeat does (claim, then stop only
    /// if the claim was won) — the seam for driving a candidate selected before some racing event.</summary>
    internal Task ReapReviewerForTest(ReapCandidate candidate) => ReapReviewerAsync(candidate);

    /// <summary>Test-only: run ONE selected PARK exactly as the heartbeat does (claim, report, then the
    /// ack-gated teardown) — the seam for driving the arm-A park state machine against a fake
    /// <see cref="ServerConnection"/> whose <see cref="ServerConnection.ReportParticipantParkedAsync"/>
    /// returns a chosen <see cref="ParkAck"/>.</summary>
    internal Task ParkReviewerForTest(ReapCandidate candidate) => ParkReviewerAsync(candidate);

    /// <summary>Test-only: the claim ALONE, without the teardown it gates. Lets a contention test pin
    /// which side won the section without also driving a stop whose (slow, side-effecting) teardown is
    /// not what is under test.</summary>
    internal Task<bool> TryClaimReapForTest(ReapCandidate candidate) => TryClaimReapAsync(candidate);

    /// <summary>Test-only: the post-claim stop ALONE, without a real claim ahead of it — lets a test
    /// swap <c>_agents</c>' entry for <c>candidate</c>'s id between "claim won" and this call to prove
    /// the incarnation re-check aborts rather than stopping a relaunch.</summary>
    internal Task StopClaimedReapForTest(ReapCandidate candidate) => StopClaimedReapAsync(candidate);

    /// <summary>Test-only entry point to the private probe-borrow-source handler.</summary>
    internal Task<BorrowProbeResult> HandleProbeBorrowSourceForTest(string path) => HandleProbeBorrowSource(path);

    /// <summary>Task 8: test-only entry point to the private reviewer-model preflight handler.</summary>
    internal ReviewerModelResolveResponseV1 HandleResolveReviewerModelForTest(ReviewerModelResolveRequestV1 req) =>
        HandleResolveReviewerModel(req);

    /// <summary>Task 8: test-only entry point to the pure explicit-model resolved-report builder.</summary>
    internal static ExplicitReviewerModelResolvedV1? BuildExplicitReviewerModelReportForTest(
            string agentId, string vendor, ExplicitReviewerModelLaunch block, IReviewerModelResolver? resolver) =>
        BuildExplicitReviewerModelReport(agentId, vendor, block, resolver);

    internal Task RegisterAgentForTestAsync(AgentInstance agent) => RegisterAgentAsync(agent);
    internal Task ReRegisterAgentsForTestAsync() => ReRegisterAgentsAsync();
    internal void HandleResizeTerminalForTest(ResizeTerminalCommand cmd) => _ = HandleResizeTerminal(cmd);
    internal LocalPermissionBridge PermissionBridgeForTest => _permissionBridge;
}

/// <summary>
/// Stand-in <see cref="IPtyProcess"/> for failed-launch cleanup paths where we
/// need an <see cref="AgentInstance"/> to satisfy <c>launcher.Cleanup</c> but no
/// live PTY ever existed. All members are no-ops; the launcher only reads
/// <see cref="AgentInstance.Worktree"/> and <see cref="AgentInstance.McpConfigPath"/>.
/// </summary>
internal sealed class NoopPtyProcess : IPtyProcess {
    public static readonly NoopPtyProcess Instance = new();
    NoopPtyProcess() { }

    public int  Pid       => 0;
    public bool HasExited => true;
    public int? ExitCode  => 0;

    public ValueTask DisposeAsync() => default;

    public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
    public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        yield break;
    }
#pragma warning restore CS1998

    public Task WriteAsync(string input) => Task.CompletedTask;
    public Task WriteAsync(byte[] data) => Task.CompletedTask;
    public void Resize(ushort     cols, ushort rows) { }
    public void SendInterrupt() { }
}
