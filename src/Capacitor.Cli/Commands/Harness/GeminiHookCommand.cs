using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Google Gemini CLI hooks. Unlike
/// Copilot, Gemini's command-hook stdin payload carries a uniform
/// <c>hook_event_name</c> (PascalCase: <c>SessionStart</c> / <c>SessionEnd</c> /
/// <c>Notification</c>), so this dispatcher self-routes on it like Claude — the
/// installer registers a single <c>kcap hook --gemini</c> command per event.
/// </summary>
/// <remarks>
/// Wire contract (Gemini event → server route):
///   SessionStart → POST /hooks/session-start/gemini, then spawn the watcher
///                  tailing the payload's <c>transcript_path</c>
///                  (<c>~/.gemini/tmp/&lt;project&gt;/chats/session-*.jsonl</c>)
///                  with vendor=gemini. Gemini re-fires with source:"resume" on
///                  the same session id and appends to the same transcript file,
///                  so the server's deterministic lifecycle ids make the re-POST
///                  idempotent and the watcher resumes from the server watermark.
///   SessionEnd   → kill watcher + capped inline drain (mirror of the Copilot /
///                  Claude pre-drain cap), then POST /hooks/session-end/gemini.
///   Notification → best-effort forward to the Claude-shaped /hooks/notification.
/// KCAP'S STDERR IS PART OF GEMINI'S HOOK CONTRACT. Gemini treats hook stdout as a
/// JSON decision channel and selects the text to parse as
/// <c>stdout.trim() || stderr.trim()</c> — in a <c>child.on("close")</c> handler
/// that never looks at the event name. So an EMPTY stdout on ANY event makes it
/// parse this process's STDERR as the hook's result, and kcap writes failed-POST,
/// unusable-URL and auth diagnostics there on every event.
///
/// Hence the invariant, enforced structurally by <see cref="Handle"/>'s
/// <c>finally</c> rather than by each path remembering: a RECOGNISED hook firing
/// makes exactly one write ATTEMPT — the memory envelope on SessionStart when there
/// is one, else an explicit <c>{"continue":true}</c>. Only input with no parseable
/// <c>hook_event_name</c> stays silent. Do not add a returning path that skips it,
/// and do not "harmonise" the empty-fragment write away to match the other memory
/// adapters: those harnesses have no such fallback.
///
/// ATTEMPT, not "exactly one object reaches stdout": a throwing writer still consumes
/// the claim. See <see cref="HookResultWriter"/> for the two residues that leaves and
/// why neither is worth retrying.
///
/// The second half of the contract is the EXIT CODE, which is easy to miss because
/// nothing on this path mentions it. Gemini's plain-text fallback (what a stderr
/// string degrades to) is not unconditionally benign: it maps exit 0 and 1 to
/// <c>decision: "allow"</c> but ANY other code to <c>decision: "deny"</c>, which
/// <c>isBlockingDecision()</c> honours — that is a BLOCKED session, not junk
/// context. kcap stays out of that band only because <c>hook</c> is one of
/// <c>CrashReporter.FailOpenCommands</c>, which is what makes every top-level
/// handler in Program.cs return 0 for it. That set is load-bearing here;
/// <c>GeminiHookOutputContractTests</c> pins it.
///
/// Behaviour above is measured on <c>gemini 0.53.0</c> — a version-specific
/// observation, not a guarantee. The mitigation does not depend on it: emitting a
/// valid non-blocking object is correct under any of these selection rules.
/// </remarks>
sealed class GeminiHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    // Mirror of CopilotHookCommand.PreHookDrainCap: the drain must
    // never starve the session-end POST, or the session sticks "Active".
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    /// <summary>Far inside the 30s <c>GeminiHooksParser</c> writes into settings.json: the cap that
    /// matters is what a user will wait at startup, not what the host tolerates.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    // Notification forwarding is telemetry — a stalled server must not block
    // Gemini's turn loop.
    static readonly TimeSpan NotificationPostBudget = TimeSpan.FromSeconds(2);

    /// <summary>Gemini's explicit allow-with-no-context result. A literal rather than a serializer call
    /// ON PURPOSE: this is the payload every failure path degrades to, so producing it must itself be
    /// incapable of failing. Serializing it would add a throw path to the one value that exists to
    /// remove one. That is a guarantee about RENDERING, not delivery: whether the bytes reach stdout is
    /// <see cref="HookResultWriter"/>'s business, and it deliberately cannot promise that either.
    ///
    /// <para>Carries no <c>hookSpecificOutput</c> key, so Gemini's <c>getAdditionalContext()</c>
    /// short-circuits on its own <c>"additionalContext" in …</c> guard and contributes nothing; and no
    /// <c>decision</c>/<c>stopReason</c>, so it cannot block.</para></summary>
    internal const string AllowPayload = """{"continue":true}""";

    /// <summary>
    /// Renders the SessionStart hook result. With a fragment this is the memory envelope; without one it
    /// is <see cref="AllowPayload"/> — NOT zero bytes.
    ///
    /// <para>Pure, and separate from the write, so the rendering cannot fail a caller mid-payload:
    /// rendering completes before any byte is written. A render throw OR an empty render degrades to the
    /// allow object rather than to silence — silence is what re-exposes stderr.</para>
    /// </summary>
    internal static string RenderSessionStartPayload(string? fragment, string? workItemsNudge = null) {
        // Start from the payload that cannot fail, and only upgrade to the memory envelope when
        // rendering genuinely succeeds.
        var payload = AllowPayload;

        if (fragment is not null || !string.IsNullOrWhiteSpace(workItemsNudge)) {
            try {
                var rendered = SessionStartMemoryOutputAdapters.Render(HarnessId.Gemini, fragment, workItemsNudge);
                if (!string.IsNullOrEmpty(rendered)) payload = rendered;
            } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
                // keep AllowPayload
            }
        }

        return payload;
    }

    /// <summary>
    /// Write-once stdout sink that makes the emit invariant STRUCTURAL rather than a discipline every
    /// early return has to remember — which is exactly the discipline that failed for every event but
    /// SessionStart. <see cref="Handle"/> guarantees the write from a <c>finally</c>; a path that has a
    /// real payload claims the single write first, and the backstop then no-ops.
    ///
    /// <para>Deliberately not thread-safe: a hook invocation writes its result from one flow, and a lock
    /// would add a failure mode to the one component whose whole job is that bytes get written.</para>
    /// </summary>
    internal sealed class HookResultWriter(TextWriter writer) {
        bool _written;

        /// <summary>Writes <paramref name="payload"/> unless something has already been written. The
        /// claim is recorded BEFORE the attempt, so a throwing write cannot let the backstop append a
        /// second object onto a partial one.
        ///
        /// <para>THE CANONICAL NOTE ON WHY THE INVARIANT IS AN "ATTEMPT" — referenced from the class
        /// remarks and the tests rather than repeated, since restating it is how the claim drifted twice.
        /// A throwing writer leaves one of two residues, and they are NOT the same failure:</para>
        ///
        /// <para>• <b>Throw before any byte → stdout empty.</b> `"" || stderr` selects stderr, so this
        /// is the original bug, unfixed for this one case.</para>
        ///
        /// <para>• <b>Throw mid-payload → stdout truncated.</b> A non-empty stdout is truthy, so it stays
        /// selected and stderr is NOT reached. The truncated JSON fails to parse and degrades to plain
        /// text — junk context from our own payload, and an allow at the exit codes this command
        /// returns.</para>
        ///
        /// <para>Retrying fixes neither: nothing here can tell the two apart, and appending a second
        /// object behind a partial one just guarantees the truncated case's invalid-but-selected stdout.
        /// A stdout we cannot write to has no recovery from inside this process.</para></summary>
        internal void Write(string payload) {
            if (_written) return;

            _written = true;

            try { writer.Write(payload); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
        }

        /// <summary>The backstop: an explicit allow, carrying no context and no decision.</summary>
        internal void EnsureWritten() => Write(AllowPayload);
    }

    /// <summary>The exact <see cref="SessionMemoryLifecycle"/> this adapter hands the orchestrator.
    ///
    /// <para>Extracted so it is testable as a unit: asserting the shared mapper in isolation would stay
    /// green if this call site reintroduced a local mapper — which is precisely the regression that
    /// occurred here. Tests feed this into <c>SessionStartMemoryLifecyclePolicy.Decide</c> to assert the
    /// resulting eligibility, which is the behaviour that actually matters.</para>
    ///
    /// <para><c>CallbackMayRepeat: false</c> — Gemini's SessionStart is a session-level event, not a
    /// per-turn callback like Kiro's agentSpawn. A `resume` re-fire on the same session id is made
    /// idempotent by the lease, not by this flag.</para></summary>
    internal static SessionMemoryLifecycle LifecycleFor(string sessionId, string? source) =>
        new(HarnessId.Gemini, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            // Shared mapper, NOT a local one: it maps an unrecognised source to Unknown, which the
            // policy suppresses BEFORE any lease is acquired. A local mapper defaulting to New would
            // inject on an unverified reason AND spend the once-per-session lease on it.
            SessionStartMemoryHookSupport.ReasonFor(source), CallbackMayRepeat: false);

    async Task<string?> StartMemoryIndexTask(
            string     sessionId,
            string?    scopeRoot,
            bool       disabled,
            bool       guidelinesDisabled,
            string?    source,
            TimeSpan   budget) {
        if ((disabled && guidelinesDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                LifecycleFor(sessionId, source),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    public async Task<int> Handle(TextReader stdin) {
        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = TryGetString(node, "hook_event_name");

        // The ONE path that stays silent, and the reason the invariant below says "recognised": with no
        // parseable hook_event_name we cannot know Gemini invoked us as a hook at all, so there is no
        // result to attribute and nothing that would read it.
        if (string.IsNullOrEmpty(eventName)) return 0;

        // Invariant: a RECOGNISED hook firing makes exactly one write ATTEMPT, on every returning path.
        // Held by the finally rather than by each path remembering — the per-path version held for
        // SessionStart alone and left every other event handing Gemini kcap's stderr as the hook result.
        // `eventName` is deliberately not filtered to the events we route: Gemini's close handler never
        // consults it either.
        var result = new HookResultWriter(Console.Out);

        try {
            return await DispatchAsync(node, eventName, result);
        } finally {
            result.EnsureWritten();
        }
    }

    async Task<int> DispatchAsync(
            JsonNode  node,
            string    eventName,
            HookResultWriter result) {
        // Gemini session ids are dashed UUIDs; keep the dashless form for the
        // server (AgentSession-{dashless} convention shared by every vendor).
        var dashedSessionId = TryGetString(node, "session_id");
        if (string.IsNullOrEmpty(dashedSessionId) || !Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session. Suppression is not
        // silence — these paths are not stderr-free (Program.cs drains the spool before dispatch).
        if (DisabledSessions.IsDisabled(sessionId, config)) {
            if (eventName == "SessionEnd") DisabledSessions.RemoveMarker(sessionId, config);
            return 0;
        }

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(config);

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = profiles.Effective;

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths, home)) return 0;

        return eventName switch {
            "SessionStart" => await HandleSessionStart(node, sessionId, cwd, activeProfile, spool,
                                                       result, clock.Budget(Ceiling)),
            "SessionEnd"   => await HandleSessionEnd(node, sessionId, cwd),
            "Notification" => await HandleNotification(node, sessionId, cwd),
            _              => 0   // unknown / unsubscribed — fail-open like the other dispatchers
        };
    }

    async Task<int> HandleSessionStart(
            JsonNode  node,
            string    sessionId,
            string?   cwd,
            Profile?  activeProfile,
            HookSpool spool,
            HookResultWriter result,
            HookBudget       budget
        ) {
        var source = TryGetString(node, "source") is { Length: > 0 } s ? s : "startup";

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = source,
            ["home_dir"]        = home.Path
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        // Gemini stamps hook payloads with an ISO-8601 `timestamp`; forward it
        // as started_at so canonical SessionStarted carries the real start time
        // (the server falls back to UtcNow when absent).
        if (TryGetIsoTimestamp(node, "timestamp") is { } startedAt) {
            forwarded["started_at"] = startedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot path).
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        SessionStartInventory.Stamp(forwarded, config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Started in PARALLEL with the POST below, not before it — the lifecycle POST is the
        // latency-critical path. Remaining() already subtracts its own safety margin; do not subtract
        // again here (double-subtraction was a real defect in the Copilot adapter).
        var memoryTask = StartMemoryIndexTask(sessionId,
            scopeRoot: cwd is not null ? GitRepository.FindRoot(cwd) ?? cwd : null,
            disabled: activeProfile?.DisableMemoryIndex is true,
            guidelinesDisabled: activeProfile?.DisableSessionGuidelines is true,
            source: source,
            budget: budget.Remaining);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure keeps the
        // prior non-zero exit and skips the watcher; the next resume/startup retries.
        var outcome = await _poster.PostOrSpoolAsync("session-start/gemini", enriched, "gemini-hook",
            spool, sessionId, route: "session-start/gemini");

        // Claim the single write HERE, before the failed-POST return below, so the memory envelope is
        // what lands rather than the backstop's bare allow. The memory index is independent of lifecycle
        // capture — a server rejecting the POST has not invalidated an index already fetched — and Gemini
        // parses hook stdout unconditionally, with the exit code only setting its own `success` flag.
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        var workItemsNudge = HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Gemini, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home));
        result.Write(RenderSessionStartPayload(fragment, workItemsNudge));

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return outcome == HookPostOutcome.Failed ? 1 : 0;

        // Task 6: await (was fire-and-forget) so a spawn failure is observed here rather
        // than silently swallowed, and the process isn't torn down before the spawn completes.
        await EnsureWatcher(sessionId, node, cwd, source);
        return 0;
    }

    /// <summary>Test seam mirroring <see cref="AgentHookPoster.ShouldSpawnAfter"/> — session-start
    /// capture must start on <c>Posted</c> OR <c>Spooled</c>, never gated behind lifecycle-POST
    /// delivery.</summary>
    internal static bool SpawnGateForTest(HookPostOutcome o, string? baseUrl)
        => AgentHookPoster.ShouldSpawnAfter(o, baseUrl);

    async Task<int> HandleSessionEnd(JsonNode node, string sessionId, string? cwd) {
        var transcriptPath = TryGetString(node, "transcript_path");

        // Kill watcher + inline-drain BEFORE the POST so the server computes
        // stats over the full transcript — capped so a slow drain can't starve
        // the session-end POST. Only drain when Gemini gave us a transcript path
        // (it always does today; defensive otherwise).
        if (!string.IsNullOrEmpty(transcriptPath)) {
            try {
                var drained = await TimeBudget.RunCappedAsync(
                    async () => {
                        await _watchers.KillWatcher(sessionId);
                        await _watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "gemini");
                        // Gemini fires no subagent-stop hook, so the parent owns subagent
                        // teardown: kill each live child watcher, drain its tail, and finalize
                        // it (subagent-stop). Restart-safe — driven off the on-disk files,
                        // not an in-memory set. Shared with the watcher's parent-exit fallback
                        // so a crash that bypasses this hook still finalizes subagents.
                        await new GeminiSubagentTeardown(config, profiles, http).DrainAsync(sessionId, transcriptPath);
                    },
                    PreHookDrainCap
                );

                if (!drained) {
                    await Console.Error.WriteLineAsync(
                        $"[kcap] gemini session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                      + $"Transcript tail may be incomplete — recoverable via: kcap import --gemini --session {sessionId}"
                    );
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"[kcap] gemini session-end pre-hook failed: {ex.Message}");
            }
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = TryGetString(node, "reason") ?? "exit",
            ["home_dir"]        = home.Path
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (TryGetIsoTimestamp(node, "timestamp") is { } endedAt) {
            forwarded["ended_at"] = endedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // AuthLapsed / Posted → clean exit (0); a real failure keeps the prior non-zero exit.
        return await PostHookAsync("session-end/gemini", forwarded.ToJsonString()) == HookPostOutcome.Failed ? 1 : 0;
    }

    async Task<int> HandleNotification(JsonNode node, string sessionId, string? cwd) {
        // The server's NotificationHook requires message + notification_type.
        var message          = TryGetString(node, "message");
        var notificationType = TryGetString(node, "notification_type")
                            ?? TryGetString(node, "notificationType");

        if (message is null || notificationType is null) return 0;

        var forwarded = new JsonObject {
            ["hook_event_name"]   = "Notification",
            ["session_id"]        = sessionId,
            ["message"]           = message,
            ["notification_type"] = notificationType,
            ["home_dir"]          = home.Path
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        using var cts = new CancellationTokenSource(NotificationPostBudget);
        try {
            // The hook verb, so a lapse writes nothing to stderr: stay quiet and skip the doomed
            // POST rather than spend a per-turn line on it.
            var (client, status) = await http.ForHookAsync(cts.Token);
            using (client) {
                if (AgentHookPoster.IsAuthLapsed(status)) return 0;
                using var content = new StringContent(forwarded.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{Url}/hooks/notification", content, cts.Token);
            }
        } catch {
            // Recording must never fail the hook.
        }

        return 0;
    }

    async Task EnsureWatcher(string sessionId, JsonNode node, string? cwd, string source) {
        // Gemini hands us the transcript path directly (no derivation needed,
        // unlike Copilot). Empty/absent → skip (can't tail nothing).
        var transcriptPath = TryGetString(node, "transcript_path");
        if (string.IsNullOrEmpty(transcriptPath)) return;

        // Skip title (re)generation on resume/clear — the session already has
        // one and resume appends to the same transcript.
        var skipTitle = source is "resume" or "clear";

        // Task 6: awaited (was fire-and-forget `_ =`) so a spawn failure surfaces to the
        // caller instead of being silently dropped, and the host process doesn't exit before the
        // spawn completes.
        await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: skipTitle, vendor: "gemini"
        );
    }

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    Task<HookPostOutcome> PostHookAsync(string endpoint, string body)
        => _poster.PostAsync(endpoint, body, "gemini-hook");

    static DateTimeOffset? TryGetIsoTimestamp(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v
         && v.TryGetValue<string>(out var s)
         && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) {
            return ts;
        }

        return null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
