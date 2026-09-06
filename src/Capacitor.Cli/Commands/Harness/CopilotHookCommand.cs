using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for GitHub Copilot CLI hooks. Copilot
/// command-hook payloads carry no uniform event-name field (sessionStart's
/// payload is just <c>{sessionId, timestamp, cwd, source, initialPrompt}</c>),
/// so the kcap hooks installer writes one entry per event with the event name
/// embedded in the command: <c>kcap hook --copilot --event sessionStart</c>.
/// </summary>
/// <remarks>
/// Wire contract (Copilot event → server route):
///   sessionStart → POST /hooks/session-start/copilot, then spawn the watcher
///                  tailing $COPILOT_HOME/session-state/{sid}/events.jsonl
///                  with vendor=copilot (the server's CopilotTranscriptNormalizer
///                  owns content; the hook owns lifecycle). Fires with
///                  source:"resume" on the same session id for --continue /
///                  --resume — the server's deterministic lifecycle event ids
///                  make the re-POST idempotent and the watcher resumes from
///                  the server watermark.
///   sessionEnd   → spawn the detached copilot-finalize drainer FIRST (it
///                  must be created before — and outlive — the rest of the
///                  hook to capture the session.shutdown tail Copilot writes
///                  after the hook returns), then kill watcher + capped inline
///                  drain (mirrors Claude's pre-drain cap), then POST
///                  /hooks/session-end/copilot.
///   agentStop    → no server POST. Fires at every turn end; used only to
///                  re-enliven a crashed watcher (mirrors Codex's Stop).
///   notification → best-effort forward to the Claude-shaped /hooks/notification
///                  (Copilot's payload already carries message / title /
///                  notification_type in the compatible shape).
/// Copilot treats hook stdout as optional. This dispatcher emits nothing for every event except
/// sessionStart, which writes a single {"additionalContext":"…"} document when — and only when —
/// a team-memory fragment is available to inject.
/// </remarks>
sealed class CopilotHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>kcap's own cap: Copilot's hook config carries no timeout, so nothing but this stops a
    /// slow start from being the user's wait.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Writes the SessionStart memory envelope — and ONLY when there is something to inject.
    ///
    /// <para><b>Silence on no fragment is deliberate.</b> Copilot's <c>sessionStart</c> hook writes
    /// nothing to stdout today and that is a working contract, so every no-memory path (opt-out,
    /// exclusion, provider failure, budget exhaustion, ineligible source) stays byte-identical to
    /// current behaviour rather than newly emitting <c>{}</c>. Emitting an empty object on paths that
    /// never produced output would be an unforced change to the hook's wire behaviour for no gain —
    /// the same reasoning applied to Codex's minimal handshake, and it matches the shared adapter's
    /// own precedent of rendering empty output for Claude's no-fragment case.</para>
    ///
    /// <para>The payload is serialized before the first byte is written, so a renderer fault degrades
    /// to silence rather than emitting a partial document — Copilot parses stdout as exactly one JSON
    /// object.</para>
    /// </summary>
    internal static void WriteSessionStartOutput(TextWriter writer, string? fragment, string? workItemsNudge = null) {
        if (fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)) return;

        string payload;

        try {
            payload = SessionStartMemoryOutputAdapters.Render(HarnessId.Copilot, fragment, workItemsNudge);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return;
        }

        writer.Write(payload);
    }

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as silence.
    ///
    /// <para><b>Scope safety:</b> the git root discovered from Copilot's payload <c>cwd</c> is
    /// preferred and the cwd is the fallback; with neither, injection is skipped rather than letting
    /// the shared resolver fall back to the hook PROCESS's cwd and inject an unrelated repository's
    /// memories.</para>
    ///
    /// <para><b>Eligibility:</b> callers reach this only for a real UUID <c>sessionStart</c> (the
    /// dispatcher drops tool-call-id subagent firings) and only for a non-excluded, enabled session.
    /// Copilot DOES report a lifecycle <c>source</c> (<c>startup</c>/<c>new</c>/<c>resume</c>), so the
    /// reason is mapped from it rather than assumed; re-injection across a resume of the same session
    /// id is prevented by the shared lease keyed on (harness, session id).</para>
    /// </summary>
    async Task<string?> StartMemoryIndexTask(
            string     sessionId,
            string?    scopeRoot,
            string?    source,
            bool       disabled,
            bool       guidelinesDisabled,
            TimeSpan   budget,
            Func<CancellationToken, Task<bool>>? commitGate) {
        if ((disabled && guidelinesDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Copilot, sessionId, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true,
                    SessionStartMemoryHookSupport.ReasonFor(source), CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled),
                commitGate);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    // Mirror of ClaudeHookCommand.PreHookDrainCap: Copilot kills the
    // sessionEnd hook at its configured timeout (default 30s, but kcap.json
    // entries set 30 and users can lower it) — the drain must never starve the
    // session-end POST, or the session sticks "Active" forever.
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    // Notification forwarding is telemetry — a stalled server must not block
    // Copilot's turn loop. Single budget covers auth discovery + POST.
    static readonly TimeSpan NotificationPostBudget = TimeSpan.FromSeconds(2);

    public async Task<int> Handle(TextReader stdin, string[] args) {
        var eventName = GetArg(args, "--event");

        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --copilot requires --event <name> (the kcap hooks installer writes it; "
              + "re-run: kcap plugin install --copilot)");
            return 1;
        }

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        // Copilot payloads carry the dashed session uuid under camelCase
        // `sessionId`. Keep the dashed form for filesystem lookups (the
        // session-state dir name is dashed) and the dashless form for the
        // server (AgentSession-{dashless} stream convention shared by every
        // vendor dispatcher).
        var dashedSessionId = TryGetString(node, "sessionId");

        if (string.IsNullOrEmpty(dashedSessionId)) return 0;

        // Copilot session ids are UUIDs (the session-state dir name) — but
        // subagent-scoped hook firings reuse the spawning toolCallId as
        // sessionId (captured v1.0.61 agentStop: sessionId:"toolu_01…",
        // transcriptPath:""). Those are not sessions: routing them onward
        // would spawn idle watchers (pid files + SignalR registrations) keyed
        // on tool-call ids. Subagent activity is already inlined in the
        // parent session's transcript, so dropping these loses nothing.
        if (!Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex disabled-session fast path: `kcap disable`
        // must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) {
            if (eventName == "sessionEnd") DisabledSessions.RemoveMarker(sessionId, config);
            return 0;
        }

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(config);

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = profiles.Effective;

        // Path exclusion is a cheap string-prefix compare — safe on every
        // event (agentStop fires per turn). Repo exclusion runs once inside
        // sessionStart after enrichment, then marks the session disabled so
        // later events take the fast path above (same split as Codex).
        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths, home)) {
            return 0;
        }

        return eventName switch {
            "sessionStart" => await HandleSessionStart(node, dashedSessionId, sessionId, cwd, activeProfile, spool, clock.Budget(Ceiling)),
            "sessionEnd"   => await HandleSessionEnd(node, dashedSessionId, sessionId, cwd),
            "agentStop"    => await HandleAgentStop(node, dashedSessionId, sessionId, cwd),
            "notification" => await HandleNotification(node, sessionId, cwd),
            _              => 0   // unknown — silently ignore (fail-open like the other dispatchers)
        };
    }

    async Task<int> HandleSessionStart(
            JsonNode   node,
            string     dashedSessionId,
            string     sessionId,
            string?    cwd,
            Profile?   activeProfile,
            HookSpool  spool,
            HookBudget budget
        ) {
        var source = TryGetString(node, "source") is { Length: > 0 } s ? s : "startup";

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = source,
            ["home_dir"]        = home.Path
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        if (TryGetString(node, "initialPrompt") is { } prompt) {
            forwarded["initial_prompt"] = prompt;
        }

        // Copilot stamps hook payloads with a unix-ms timestamp; forward it as
        // started_at so canonical SessionStarted carries the real start time
        // (the server falls back to UtcNow when absent — precedent).
        if (TryGetUnixMillis(node, "timestamp") is { } startedAt) {
            forwarded["started_at"] = startedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Same rationale as the Codex dispatcher: stamp default visibility
        // BEFORE enrichment so it survives the JsonString round-trip.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        SessionStartInventory.Stamp(forwarded, config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, forwarded.ToJsonString());

        // Repo exclusion after enrichment (fast in-payload path) — mark the
        // session so per-turn agentStop events skip via DisabledSessions.
        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Resolved from the lifecycle POST outcome below and consulted by the memory orchestrator just
        // before it commits the once-per-session lease. MUST be set on every path that reaches the
        // await, or the fetch task can never complete — hence the finally.
        var deliverable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start the team-memory fetch BEFORE the lifecycle POST so the two overlap; awaited
        // (budget-capped) after it, immediately before this hook's only stdout write. Started after
        // the exclusion/disabled early-outs above so an excluded repo never reaches the memory
        // subsystem. The git root stamped onto the forwarded payload is the preferred scope; cwd is
        // the fallback (never a process-cwd fallback — see StartMemoryIndexTask).
        var memoryTask = StartMemoryIndexTask(sessionId,
            TryGetString(JsonNode.Parse(enriched), "workspace_root") ?? cwd,
            source,
            // The EFFECTIVE profile, not the resolution's: ProfileResolver returns a null Profile
            // whenever --server-url or KCAP_URL wins, and the effective one falls back to the on-disk
            // active profile. Reading the resolution silently ignored `disable_memory_index: true`
            // for every KCAP_URL user.
            activeProfile?.DisableMemoryIndex is true,
            activeProfile?.DisableSessionGuidelines is true,
            // Remaining already reserves Safety — subtracting it again here halved the window.
            budget.Remaining,
            // Deliverability gate: the lease is committed only once the lifecycle POST has proved the
            // output can actually be honoured. Resolved on EVERY path below, before the await.
            _ => deliverable.Task);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. Only a
        // permanent failure keeps the prior non-zero exit and skips the watcher.
        HookPostOutcome outcome;

        try {
            outcome = await _poster.PostOrSpoolAsync("session-start/copilot", enriched, "copilot-hook",
                spool, sessionId, route: "session-start/copilot");
        } catch {
            deliverable.TrySetResult(false);
            throw;
        }

        // A permanent POST failure exits non-zero, and Copilot consumes this hook's stdout only on a
        // ZERO exit — so the envelope could not be honoured there. Telling the orchestrator makes it
        // RELEASE the once-per-session lease instead of spending it, so the next start of this session
        // retries rather than being permanently denied its one injection.
        deliverable.TrySetResult(outcome != HookPostOutcome.Failed);

        // Always awaited, on every outcome, so the fetch is never left dangling.
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);

        if (outcome == HookPostOutcome.Failed) return 1;

        // Copilot parses this hook's stdout as its (optional) single JSON result document. Silent when
        // there is neither a fragment nor a nudge, which keeps all pre-existing paths byte-identical.
        var workItemsNudge = HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Copilot, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home));
        WriteSessionStartOutput(Console.Out, fragment, workItemsNudge);

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return 0;

        await EnsureWatcherAsync(dashedSessionId, sessionId, node, cwd);
        return 0;
    }

    async Task<int> HandleSessionEnd(
            JsonNode node,
            string   dashedSessionId,
            string   sessionId,
            string?  cwd
        ) {
        var transcriptPath = TranscriptPathFor(dashedSessionId);

        // Copilot appends `session.shutdown` (per-model input/cache token
        // aggregates) — and sometimes the final assistant turn — to events.jsonl
        // only AFTER this hook returns, by which point the live watcher is dead
        // (KillWatcher below) and the server's session-end StopAndDrain has run,
        // so nothing else is reading the file. Spawn the detached finalizer FIRST,
        // before the capped pre-drain and the retrying session-end POST: if a
        // slow/unreachable server makes the POST burn the whole hook timeout,
        // Copilot SIGKILLs the hook — and we must have already created the drainer
        // by then. It is detached (setsid + closed std streams), so it survives
        // the hook being killed and still delivers the post-hook tail via one
        // idempotent inline-drain once `session.shutdown` lands (or it times out).
        // Its poll budget outlasts the worst-case hook lifetime for this reason.
        _watchers.SpawnCopilotFinalizeDrain(sessionId, transcriptPath);

        // Kill watcher + inline-drain BEFORE the POST so the server computes
        // stats over the full transcript — capped so a slow drain can't starve
        // the session-end POST (mirror of ClaudeHookCommand's pre-drain cap).
        try {
            var drained = await TimeBudget.RunCappedAsync(
                async () => {
                    await _watchers.KillWatcher(sessionId);
                    await _watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "copilot");
                },
                PreHookDrainCap
            );

            if (!drained) {
                await Console.Error.WriteLineAsync(
                    $"[kcap] copilot session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                  + $"Transcript tail may be incomplete — recoverable via: kcap import --copilot --session {sessionId}"
                );
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] copilot session-end pre-hook failed: {ex.Message}");
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = TryGetString(node, "reason") ?? "complete",
            ["home_dir"]        = home.Path
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (TryGetUnixMillis(node, "timestamp") is { } endedAt) {
            forwarded["ended_at"] = endedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // AuthLapsed / Posted → clean exit (0); a real failure keeps the prior non-zero exit.
        return await PostHookAsync("session-end/copilot", forwarded.ToJsonString()) == HookPostOutcome.Failed ? 1 : 0;
    }

    async Task<int> HandleAgentStop(JsonNode node, string dashedSessionId, string sessionId, string? cwd) {
        // Fires at every turn end — no server POST (sessionEnd owns lifecycle;
        // turn content arrives via the transcript). Just keep the watcher
        // alive in case it crashed mid-session, mirroring Codex's Stop branch.
        _ = node;
        await EnsureWatcherAsync(dashedSessionId, sessionId, node, cwd);
        return 0;
    }

    async Task<int> HandleNotification(JsonNode node, string sessionId, string? cwd) {
        // The server's NotificationHook requires message + notification_type.
        // Copilot's command-hook stdin ships the Claude-compatible snake_case
        // key today (verified against captured v1.0.61 payloads), but its
        // internal event model uses camelCase `notificationType` (visible in
        // the transcript's hook.start input echo) — read both so a future
        // Copilot release dropping the compat transformation degrades to
        // "still recorded" instead of silently losing every notification.
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

        if (TryGetString(node, "title") is { } title) forwarded["title"] = title;
        if (cwd is not null) forwarded["cwd"] = cwd;

        // Best-effort telemetry — bounded like the Codex permission-record
        // path so a stalled server can't block Copilot's loop.
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

    async Task EnsureWatcherAsync(string dashedSessionId, string sessionId, JsonNode node, string? cwd) {
        // agentStop payloads carry transcriptPath; sessionStart's don't —
        // derive it from the session-state layout in that case. Copilot also
        // ships transcriptPath as an EMPTY STRING on some firings, so treat
        // empty as absent rather than spawning a watcher on "".
        var transcriptPath = TryGetString(node, "transcriptPath") is { Length: > 0 } tp
            ? tp
            : TranscriptPathFor(dashedSessionId);

        await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "copilot"
        );
    }

    /// <summary>
    /// events.jsonl path for a session. Prefers the current
    /// <c>session-state/</c> root; falls back to the pre-GA
    /// <c>history-session-state/</c> when only the legacy dir has the file.
    /// When neither exists yet (sessionStart can fire before Copilot's first
    /// event write) returns the current-layout path — the watcher tolerates a
    /// not-yet-created file and picks it up on its next poll.
    /// </summary>
    string TranscriptPathFor(string dashedSessionId) {
        var paths   = CopilotHarness.FromEnvironment(home).Paths;
        var current = paths.EventsJsonl(paths.SessionStateDir, dashedSessionId);

        if (File.Exists(current)) return current;

        var legacy = paths.EventsJsonl(paths.LegacySessionStateDir, dashedSessionId);

        return File.Exists(legacy) ? legacy : current;
    }

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    Task<HookPostOutcome> PostHookAsync(string endpoint, string body)
        => _poster.PostAsync(endpoint, body, "copilot-hook");

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static DateTimeOffset? TryGetUnixMillis(JsonNode? node, string fieldName) {
        if (node?[fieldName] is not JsonValue v) return null;

        if (v.TryGetValue<long>(out var ms) && ms > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (v.TryGetValue<double>(out var dms) && dms > 0) return DateTimeOffset.FromUnixTimeMilliseconds((long)dms);

        return null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
