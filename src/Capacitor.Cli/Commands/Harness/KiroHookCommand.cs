using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for AWS Kiro CLI hooks. Kiro (the rebranded
/// Amazon Q Developer CLI) delivers each hook as JSON on STDIN; the kcap
/// installer writes one entry per event with the event name embedded in the
/// command: <c>kcap hook --kiro --event agentSpawn</c>.
/// </summary>
/// <remarks>
/// Wire contract (Kiro event → server route):
///   agentSpawn → POST /hooks/session-start/kiro, then ensure the watcher is
///                running (vendor=kiro). agentSpawn fires on EVERY prompt with
///                the SAME session id, so the server's deterministic lifecycle
///                event id collapses them to one SessionStarted and the
///                idempotent EnsureWatcherRunning is a no-op once live. The
///                watcher tails Kiro's append-only JSONL session log
///                (~/.kiro/sessions/cli/{id}.jsonl), streams it (vendor=kiro), and
///                — because Kiro has NO session-end hook — synthesizes
///                /hooks/session-end/kiro when it observes the kiro-cli process exit.
///   (any other) → no-op exit 0.
///
/// Kiro appends non-empty hook stdout straight into agent context. That is exactly the
/// team-memory injection channel for <c>agentSpawn</c> — but it is also why every OTHER event
/// here still emits NOTHING (a stdout-writing <c>stop</c> hook re-injects and loops the agent),
/// and why <c>agentSpawn</c>, which fires on EVERY prompt, must inject at most ONCE per session.
/// The raw fragment is written with no JSON envelope and no diagnostics: whatever lands on stdout
/// becomes conversation context verbatim.
/// </remarks>
sealed class KiroHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>kcap writes no <c>timeout_ms</c> into Kiro's hook entry, so Kiro's own default
    /// governs and this conservative ceiling is the safe floor. Kiro discards the stdout of a hook it
    /// killed, so an overrun costs the injection outright.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    /// <summary>Bound on the nudge claim's store work. The fragment write never waits on the claim —
    /// a still-pending claim defers the nudges to a post-flush append — so this caps only that
    /// deferred wait.</summary>
    static readonly TimeSpan NudgeClaimBudget = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Writes the team-memory fragment as Kiro consumes it: raw text, no envelope.
    ///
    /// <para>A null fragment writes ZERO bytes — the shared adapter's own Kiro rendering, so unlike
    /// Codex and Copilot there is no null-case asymmetry to encode. Serialized before the first byte
    /// so a renderer fault degrades to silence rather than injecting a partial document.</para>
    /// </summary>
    internal static void WriteAgentSpawnOutput(TextWriter writer, string? fragment, string? workItemsNudge = null) {
        string payload;

        try {
            payload = SessionStartMemoryOutputAdapters.Render(HarnessId.Kiro, fragment, workItemsNudge);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return;
        }

        writer.Write(payload);
    }

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as zero bytes.
    ///
    /// <para><b>The lease is load-bearing here, not incidental.</b> <c>agentSpawn</c> fires on every
    /// prompt with the SAME session id, so without the once-per-session lease the index would be
    /// re-injected and re-charged every turn. Hence
    /// <see cref="SessionLifecycleReason.RepeatedTurnCallback"/> + <c>CallbackMayRepeat: true</c>,
    /// which the shared policy resolves to a lease-guarded decision. A new session brings a new
    /// session id, hence a new lease key and a fresh injection — no Kiro-specific logic.</para>
    ///
    /// <para>No commit gate (unlike Copilot), because no POST outcome can make a fetched fragment
    /// undeliverable here. That covers the outcome only: the caller must ALSO keep anything slow from
    /// sitting between the lease commit and the write, since Kiro only consumes stdout from a hook
    /// that completed. See the call site, where both remaining awaits are budget-bounded.</para>
    ///
    /// <para><b>Scope safety:</b> git root preferred, payload cwd as fallback; with neither, injection
    /// is skipped rather than letting the shared resolver fall back to the hook PROCESS's cwd and
    /// inject an unrelated repository's memories.</para>
    /// </summary>
    async Task<string?> StartMemoryIndexTask(
            string     sessionId,
            string?    scopeRoot,
            bool       disabled,
            bool       guidelinesDisabled,
            TimeSpan   budget) {
        if ((disabled && guidelinesDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Kiro, sessionId, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true,
                    SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    public async Task<int> Handle(TextReader stdin, string[] args) {
        // The installer always passes --event; default to agentSpawn so a
        // hand-rolled hook entry without it still records.
        var eventName = GetArg(args, "--event") ?? "agentSpawn";

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        // agentSpawn is the only actionable event; anything else is a no-op that
        // MUST exit 0 with empty stdout.
        if (eventName != "agentSpawn") return 0;

        // Kiro's session id is the conversation UUID (dashed). Unlike every other
        // vendor it is NOT in the hook's STDIN payload — Kiro's agentSpawn payload
        // is only {hook_event_name, cwd, prompt}. Kiro instead exposes the id to
        // hook processes via the KIRO_SESSION_ID env var, so read it from there
        // (with a payload fallback in case a future schema adds the field). Keep the
        // dashed form for the server payload (matches the transcript's
        // conversation_id) and the dashless form for local keys (watcher pid file /
        // disable markers), mirroring every other vendor dispatcher.
        var dashedSessionId = Environment.GetEnvironmentVariable("KIRO_SESSION_ID");
        if (string.IsNullOrEmpty(dashedSessionId)) {
            dashedSessionId = TryGetString(node, "session_id");
        }
        if (string.IsNullOrEmpty(dashedSessionId)) return 0;
        if (!Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) return 0;

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(config);

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = profiles.Effective;

        // Cheap string-prefix path exclusion runs on every firing; repo exclusion
        // runs once after enrichment, then marks the session disabled so later
        // agentSpawn firings take the fast path above.
        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths, home)) {
            return 0;
        }

        var hookBudget = clock.Budget(Ceiling);
        return await HandleAgentSpawn(node, dashedSessionId, sessionId, cwd, activeProfile, spool, hookBudget);
    }

    async Task<int> HandleAgentSpawn(
            JsonNode   node,
            string     dashedSessionId,
            string     sessionId,
            string?    cwd,
            Profile?   activeProfile,
            HookSpool  spool,
            HookBudget budget
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "agentSpawn",
            ["session_id"]      = dashedSessionId,
            ["home_dir"]        = home.Path
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot dispatchers).
        // null lets the server fall back to org-repo visibility, which would
        // silently flip private-default users' Kiro sessions to org-visible.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        // Model lives in the sibling {id}.json (the JSONL turn lines carry none),
        // so the server gets it only from this hook. Best-effort: at agentSpawn the
        // file may not exist yet — the next agentSpawn (fires every prompt) backfills.
        if (ReadKiroModel(KiroHarness.FromEnvironment(home).Paths, dashedSessionId) is { } model) {
            forwarded["model"] = model;
        }

        SessionStartInventory.Stamp(forwarded, config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Started BEFORE the lifecycle POST so the two overlap, and only after the disabled/
        // excluded-path/excluded-repo early-outs above so an excluded repo never reaches the memory
        // subsystem. The git root stamped onto the forwarded payload is the preferred scope; the
        // payload cwd is the fallback (never a process-cwd fallback — see StartMemoryIndexTask).
        var memoryTask = StartMemoryIndexTask(sessionId,
            TryGetString(JsonNode.Parse(enriched), "workspace_root") ?? cwd,
            // The EFFECTIVE profile: ProfileResolver returns a null Profile whenever --server-url or
            // KCAP_URL wins, so reading the resolution's own profile here would silently ignore the
            // user's opt-out on those deployments (the defect found reviewing the Copilot adapter).
            activeProfile?.DisableMemoryIndex is true,
            activeProfile?.DisableSessionGuidelines is true,
            // Remaining already reserves Safety — do not subtract it again.
            budget.Remaining);

        // agentSpawn fires per prompt and Kiro persists appended stdout, so the nudges below are
        // gated by a durable once-per-session claim — the payload carries no counter to key on.
        // The claim overlaps the memory fetch; the write site consults it without waiting.
        var nudgeClaim = NudgeLease.TryClaimAsync(config, clock.Time, HarnessId.Kiro, sessionId, NudgeClaimBudget);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure skips the
        // watcher this firing — agentSpawn fires again next prompt and retries.
        //
        // Started but NOT awaited yet, so the POST cannot stand between a fetched fragment and stdout:
        // PostWithRetryAsync retries for 30s, far beyond this hook's 5s ceiling. Safe to run
        // concurrently with the write below because the poster only ever writes to stderr.
        var postTask = _poster.PostOrSpoolAsync("session-start/kiro", enriched, "kiro-hook",
            spool, sessionId, route: "session-start/kiro");

        // The fragment reaches stdout as soon as the bounded fetch resolves — before the POST is
        // awaited and before the watcher branch — so neither a slow POST nor a later
        // EnsureWatcherRunning stall can strand an already-committed injection. Flushed explicitly:
        // a fragment sitting in a buffer when Kiro's hook timeout kills the process is a fragment
        // whose lease was spent for nothing.
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        // Nothing may stand between the resolved fetch and the write below, so the claim is only
        // consulted here, never waited for: a still-pending claim defers the nudges to a second
        // append after the flush, where it IS awaited to completion — an abandoned-but-running
        // claim could commit its record with nothing emitted and silence the nudges for the
        // session. The emitters run at most once per firing: the harness nudge stamps a ledger.
        string? ResolveNudges() => HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Kiro, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home));
        var nudgeDecided = nudgeClaim.IsCompleted;
        var workItemsNudge = nudgeDecided && await nudgeClaim ? ResolveNudges() : null;
        WriteAgentSpawnOutput(Console.Out, fragment, workItemsNudge);
        await Console.Out.FlushAsync();

        // The deferred append: clipped to the remaining ceiling as well as the claim's own budget,
        // because overrunning the ceiling discards even flushed output — the fragment, not just the
        // nudge. A clipped-but-still-running claim can commit unemitted in that corner; one session
        // without its nudge beats a discarded fragment. The nudge-only render's marker line is read
        // only by the Pi/OpenCode capture scripts, so it is inert in Kiro context.
        if (!nudgeDecided && budget.Remaining is { Ticks: > 0 } claimWait) {
            var claimed = false;
            try { claimed = await nudgeClaim.WaitAsync(claimWait); } catch (TimeoutException) { }
            if (claimed) {
                WriteAgentSpawnOutput(Console.Out, null, ResolveNudges());
                await Console.Out.FlushAsync();
            }
        }

        // BOUNDED by what is left of the ceiling. Writing early is not sufficient on its own: Kiro
        // appends stdout only from a hook that COMPLETED, so an invocation killed at Kiro's timeout
        // discards the fragment even though the lease is already committed — and no later agentSpawn
        // re-fetches. Recording is the retryable half here (agentSpawn fires every prompt); the
        // injection is not. So on lapse we stop waiting, spool durably, and exit 0.
        //
        // Double delivery is harmless: an in-flight POST landing after this spools the same payload,
        // and the server's deterministic lifecycle event id collapses both onto one SessionStarted.
        HookPostOutcome outcome;

        try {
            outcome = await postTask.WaitAsync(budget.Remaining);
        } catch (TimeoutException) {
            // Spooled, not Failed: a drain pass will replay it, so capture must still start — but only
            // claim that when the write actually landed.
            outcome = spool.Append(sessionId, "session-start/kiro", enriched)
                ? HookPostOutcome.Spooled
                : HookPostOutcome.Skipped;
        }

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return 0;

        // The watcher tails Kiro's own append-only session log
        // ~/.kiro/sessions/cli/{id}.jsonl (the file is named with the dashed id).
        // The watcher also owns session-end: GetCodingAgentPid() inside
        // SpawnWatcher passes the kiro-cli pid as --parent-pid, so the watcher
        // POSTs session-end/kiro when kiro-cli exits.
        var transcriptPath = KiroHarness.FromEnvironment(home).Paths.SessionJsonl(dashedSessionId);

        // Bounded for the same reason as the POST, and this is the LAST step between the committed
        // injection and the zero exit. Not cheap in the worst case: the stale-watcher path kills and
        // respawns, waiting up to 5s for a graceful exit. Deferring costs one prompt of unwatched
        // transcript (startup is idempotent and agentSpawn fires again next prompt); a killed hook
        // costs the whole session's injection. An abandoned in-flight spawn reconciles on that firing.
        try {
            await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "kiro"
            ).WaitAsync(budget.Remaining);
        } catch (TimeoutException) {
            // Budget exhausted (possibly already zero, which skips the attempt outright). The next
            // agentSpawn ensures the watcher; exiting 0 now is what keeps the fragment deliverable.
        }

        return 0;
    }

    /// <summary>
    /// Reads the session model from the sibling <c>{id}.json</c>
    /// (<c>session_state.rts_model_state.model_info.model_id</c>, e.g. "auto").
    /// Returns null when the file is absent (agentSpawn can fire before Kiro
    /// writes it) or unparseable — model is best-effort enrichment.
    /// </summary>
    static string? ReadKiroModel(KiroPaths paths, string dashedSessionId) {
        try {
            var path = paths.SessionJson(dashedSessionId);
            if (!File.Exists(path)) return null;

            // Shared open: never lock Kiro out of its own sidecar.
            var model = JsonNode.Parse(File.ReadAllTextShared(path))
                ?["session_state"]?["rts_model_state"]?["model_info"]?["model_id"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(model) ? null : model;
        } catch {
            return null;
        }
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
