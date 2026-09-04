using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

// ReSharper disable ShortLivedHttpClient

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → POST /hooks/stop (best-effort, 2s cap, swallow-all).
///                       Codex fires Stop at every turn end, not session end;
///                       session-end stays owned by the watcher's
///                       parent-PID monitor. The POST lets the server
///                       emit the idle-wait marker that clears the chat
///                       "working" indicator. HandleStop also refreshes watcher
///                       liveness and emits {"continue":true} for Codex's parser.
///   PermissionRequest → in a daemon-launched hosted agent (KCAP_DAEMON_URL set), bounce
///                       through the daemon's LocalPermissionBridge and wait for the dashboard's
///                       decision (fail-closed on bridge errors: deny + exit nonzero). Otherwise:
///                       POST /hooks/permission-record (fire-and-forget; CLI emits no decision so
///                       Codex's normal in-CLI approval prompt takes over).
///   UserPromptSubmit  → swallowed (v1 — neither vendor consumes them)
///   PreToolUse        → swallowed
///   PostToolUse       → swallowed
/// </remarks>
sealed class CodexHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>Codex blocks on this hook's stdout and sets no timeout of its own, so this ceiling is
    /// the only thing standing between a stalled fetch and a stalled agent.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    // Codex's Stop and SessionStart hooks parse stdout as the
    // `stop.command.output` / `session-start.command.output` JSON schema and
    // reject empty bodies with "hook returned invalid stop hook JSON output".
    // `continue: true` is the schema default; emitting it explicitly satisfies
    // the parser without altering behavior.
    const string SessionScopedOutputJson = """{"continue":true}""";

    /// <summary>
    /// Writes the session-scoped handshake JSON Codex's SessionStart/Stop stdout parser requires.
    /// Extracted to a single seam so every call site — the happy path, the disabled/excluded
    /// early-outs, and the fail-open fallback — goes through the same write, and so
    /// <see cref="RunSessionStartHandshakeForTest"/> can order background work strictly after it.
    /// </summary>
    internal static void WriteSessionScopedOutput(TextWriter writer) => writer.Write(SessionScopedOutputJson);

    /// <summary>
    /// The SessionStart write, which — unlike Stop's — may carry a team-memory fragment.
    /// Delegates the envelope to the shared <see cref="SessionStartMemoryOutputAdapters"/> so the
    /// combined shape (<c>continue</c> + <c>hookSpecificOutput.additionalContext</c>) is rendered by
    /// the same vendor-neutral code every harness uses; this adapter owns NO rendering of its own.
    ///
    /// <para><b>Absent-fragment invariant:</b> with <paramref name="fragment"/> null the adapter
    /// returns the byte-for-byte <see cref="SessionScopedOutputJson"/> handshake, so every no-memory
    /// path (opt-out, exclusion, provider failure, budget exhaustion) is indistinguishable from
    /// pre-memory behaviour. <c>CodexSessionStartMemoryTests</c> pins that equality.</para>
    ///
    /// <para>The payload is serialized into a string BEFORE the first byte reaches
    /// <paramref name="writer"/>, so a renderer/serializer fault cannot emit a partial rich object
    /// followed by a second (minimal) one — that would break Codex's single-JSON-value contract.
    /// Any such fault degrades to the minimal handshake instead.</para>
    /// </summary>
    internal static void WriteSessionStartOutput(TextWriter writer, string? fragment, string? workItemsNudge = null) {
        string payload;

        try {
            // No fragment → the pre-existing constant, deliberately NOT the shared adapter's
            // rendering of the same envelope. Both encode `{"continue":true}`, but the adapter
            // appends a trailing newline to every envelope it renders (see its `json + "\n"`),
            // which Claude and Cursor already ship. Adopting it here would change the bytes Codex
            // receives on EVERY no-memory SessionStart — opt-out, exclusion, provider failure,
            // budget exhaustion — for no gain. Byte-identity on that path is an acceptance
            // criterion, so the constant wins; the adapter still owns the only shape that is
            // actually new (the fragment-bearing one), where the trailing newline matches the
            // sibling harnesses.
            // Byte-identity on the NO-CONTENT path (no memory fragment AND no nudge) is an acceptance
            // criterion, so the constant still wins there. A nudge alone routes through the adapter
            // (with the trailing newline the fragment-bearing shape already ships).
            payload = fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)
                ? SessionScopedOutputJson
                : SessionStartMemoryOutputAdapters.Render(HarnessId.Codex, fragment, workItemsNudge);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            payload = SessionScopedOutputJson;
        }

        writer.Write(payload);
    }

    /// <summary>
    /// Task 5: enforces the Codex stdout-first contract — <paramref name="writeStdout"/>
    /// (the synchronous handshake write) always completes before <paramref name="postStdoutWork"/>
    /// (the best-effort watcher-ensure + background spool drain) even starts. The parent Codex
    /// process blocks on the hook's stdout, so a large/unreachable spool backlog sitting behind
    /// <paramref name="postStdoutWork"/> must never delay or gate the write. Production's
    /// <see cref="HandleSessionStart"/> is the only non-test caller, always with real delegates;
    /// <c>CodexStdoutContractTests</c> supplies recording/blocking ones to assert the ordering
    /// without a live server.
    /// </summary>
    internal static async Task RunSessionStartHandshakeForTest(Action writeStdout, Func<Task> postStdoutWork) {
        writeStdout();
        await postStdoutWork();
    }

    /// <summary>
    /// Starts the shared SessionStart memory fetch so it overlaps the lifecycle POST
    /// instead of serializing ahead of it — the same start-early/await-bounded shape
    /// <c>ClaudeHookCommand.StartMemoryIndexTask</c> uses. Returns a task that NEVER faults:
    /// every failure mode resolves to null, which the writer renders as the minimal handshake.
    ///
    /// <para><b>Scope safety:</b> a blank scope root is NOT passed through. The shared scope
    /// resolver would otherwise fall back to the hook PROCESS's cwd and could inject an unrelated
    /// repository's memories (the guard <c>CursorHookCommand.RunMemoryOrchestrationAsync</c>
    /// documents). Codex's payload cwd — and the git root derived from it — are the only
    /// authoritative roots; with neither, injection is skipped entirely.</para>
    ///
    /// <para><b>Once per session:</b> no lifecycle <c>source</c> exists on Codex's SessionStart
    /// payload, so the reason is reported as <see cref="SessionLifecycleReason.New"/>. Re-injection
    /// on a resume of the SAME session id is prevented by the shared lease keyed on
    /// (harness, session id) rather than by a reason we cannot observe.</para>
    /// </summary>
    async Task<string?> StartMemoryIndexTask(
            string?    sessionId,
            string?    scopeRoot,
            bool       disabled,
            bool       guidelinesDisabled,
            TimeSpan   budget) {
        if ((disabled && guidelinesDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero)
            return null;

        // A blank or unacceptable URL must skip injection here, before any client is built,
        // rather than fail later mid-fetch. Mirrors PostBestEffortAsync's guard.
        if (!HookHttp.IsPostable(Url)) return null;

        // Construction itself stays inside the fail-open boundary: store-root validation and
        // the injected client factory can throw synchronously.
        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Codex, sessionId!, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true, SessionLifecycleReason.New,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Awaits the memory fetch under the budget REMAINING at this instant — never the budget it
    /// was started with. Codex blocks on this hook's stdout, so the wait is capped so the handshake
    /// still lands inside the hook ceiling even when the fetch never returns: the cap is
    /// <see cref="HookBudget.Remaining"/>, which ALREADY reserves <see cref="HookBudget.Safety"/> as
    /// headroom for serialization + the write itself — subtracting it again cut the usable window
    /// from 3.5s to 2s at a fresh hook start. On expiry the already-running fetch is
    /// abandoned (not cancelled mid-flight — its own lease bookkeeping owns that) and null is
    /// returned, so the write degrades to the minimal handshake rather than being delayed.
    /// </summary>
    static async Task<string?> AwaitMemoryFragmentAsync(Task<string?> task, HookBudget budget) {
        try {
            var remaining = budget.Remaining;

            if (remaining <= TimeSpan.Zero)
                return task.IsCompletedSuccessfully ? task.Result : null;

            return await task.WaitAsync(remaining);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    public async Task<int> Handle(TextReader stdin) {
        var body    = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = TryGetString(node, "hook_event_name");

        if (string.IsNullOrWhiteSpace(eventName)) return 0;

        // KCAP_SKIP=1 marks a kcap-launched headless Codex invocation
        // (CodexCliRunner sets it). Suppress all server / watcher / git
        // enrichment work so we don't forward the nested session's hooks
        // back into kcap, but still honour Codex's stdout contract — the
        // Stop / SessionStart parsers reject empty output, and a missing
        // PermissionRequest response leaves Codex hung.
        if (Environment.GetEnvironmentVariable("KCAP_SKIP") is "1") {
            switch (eventName) {
                case "SessionStart" or "Stop":
                    WriteSessionScopedOutput(Console.Out);
                    break;
                case "PermissionRequest":
                    // Empty hookSpecificOutput → Codex falls back to its
                    // own approval prompt. See HandlePermissionRequestStub.
                    Console.Write("{}");
                    break;
            }
            return 0;
        }

        // Normalize session_id to dashless GUID, inject home_dir, and tag the
        // agent host id when running inside a daemon-spawned agent. Mirrors
        // the Claude hook path in Program.cs (including the disabled-session
        // check below); the plan_content branch remains Claude-specific.
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = home.Path;

        var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        // Mirror the Claude path: if the user ran `kcap disable`, skip every
        // server POST and the watcher restart. Without this check the next Codex
        // Stop hook would re-enliven the watcher and re-send transcript data for
        // a session whose data was just deleted server-side.
        var disabledSessionId = TryGetString(node, "session_id");
        if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId, config)) {
            // Emit the session-scoped JSON Codex's Stop/SessionStart parsers expect,
            // then skip dispatch. (Claude's disabled branch also returns immediately
            // — see Program.cs around line 593.)
            if (eventName is "Stop" or "SessionStart") {
                WriteSessionScopedOutput(Console.Out);
            }
            return 0;
        }

        // Path exclusion is a string-prefix compare against the payload's cwd
        // — cheap, safe to run on every event (including Stop, which fires
        // per turn). Repo exclusion is handled inside HandleSessionStart
        // instead: running it here would call RepoExclusion.IsExcludedAsync,
        // which falls back to DetectRepositoryAsync (multiple git commands +
        // gh pr view) when the payload lacks a repository block — too
        // expensive for the per-turn Stop hook. Doing the repo check once at
        // SessionStart (after enrichment, when the repository block is
        // populated) and marking the session via DisabledSessions lets
        // subsequent events take the existing disabled-session fast path
        // above without paying any git cost.
        var activeProfile = profiles.Effective;

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(TryGetString(node, "cwd"), excludedPaths, home)) {
            EmitFallbackOutput(eventName);
            return 0;
        }

        try {
            return eventName switch {
                "SessionStart"      => await HandleSessionStart(node, activeProfile, clock.Budget(Ceiling)),
                "Stop"              => await HandleStop(node),
                "PermissionRequest" => await HandlePermissionRequest(node),
                "UserPromptSubmit"
                  or "PreToolUse"
                  or "PostToolUse"  => 0,  // v1: swallow informational events
                _                   => 0   // unknown — silently ignore
            };
        } catch (Exception ex) {
            // Fail open, but NOT by leaving stdout empty: unlike Claude, Codex's
            // SessionStart/Stop parser rejects empty output and a missing
            // PermissionRequest response hangs it. If a handler throws (e.g. an
            // IO/permission fault while building the authenticated client), the
            // CLI's top-level guard would exit 0 with empty stdout and Codex would
            // report "invalid hook output". Emit the
            // event-appropriate fallback here first, and record for diagnosis.
            CrashReporter.Record(config, "hook", ex);
            EmitFallbackOutput(eventName);
            return 0;
        }
    }

    // Emit the minimal output Codex's parser requires for the given event — the
    // session-scoped {"continue":true} for SessionStart/Stop, an empty object for
    // PermissionRequest (so Codex's own approval prompt takes over), nothing for
    // the swallowed informational events. Used both when we intentionally skip
    // work (exclusion) and as the fail-open fallback when a handler throws.
    static void EmitFallbackOutput(string eventName) {
        switch (eventName) {
            case "SessionStart" or "Stop":
                // Codex's SessionStart/Stop parser rejects empty stdout.
                WriteSessionScopedOutput(Console.Out);
                break;
            case "PermissionRequest":
                // Empty hookSpecificOutput → Codex's local approval prompt
                // takes over (matches the KCAP_SKIP=1 branch above).
                Console.Write("{}");
                break;
        }
    }

    async Task<int> HandleSessionStart(JsonNode node, Profile? activeProfile, HookBudget budget) {
        // Stamp the user's configured default visibility onto the payload
        // BEFORE git enrichment so it survives the JsonString round-trip.
        // /hooks/session-start/codex shares SessionStartHook with the Claude
        // route and the server-side SessionHookHandlers.HandleSessionStart
        // reads hook.DefaultVisibility for both vendors; without this, codex
        // sessions in org repos silently default to org-visible because
        // VisibilityService treats null as "fall back to org visibility".
        if (activeProfile?.DefaultVisibility is { } visibility) {
            node["default_visibility"] = visibility;
        }

        // best-effort git-root discovery, fail-open (omitted when cwd is absent or no
        // repo is found) — see the mirrored ClaudeHookCommand comment for rationale.
        if (TryGetString(node, "cwd") is { } startCwd && GitRepository.FindRoot(startCwd) is { } workspaceRoot) {
            node["workspace_root"] = workspaceRoot;
        }

        SessionStartInventory.Stamp(node.AsObject(), config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, node.ToJsonString());

        // Repo exclusion runs here (not above the event switch) so that the
        // repository block is already populated by enrichment — RepoExclusion
        // takes the fast in-payload path and skips the expensive
        // DetectRepositoryAsync fallback. Mark the session via
        // DisabledSessions so subsequent Stop / PermissionRequest events
        // take the existing disabled-session fast path at the top of Handle
        // without paying any git cost.
        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            var excludedSessionId = TryGetString(node, "session_id");

            if (excludedSessionId is not null) DisabledSessions.Mark(excludedSessionId, config);

            WriteSessionScopedOutput(Console.Out);
            return 0;
        }

        var enrichedNode = JsonNode.Parse(enriched);
        var sessionId    = TryGetString(enrichedNode, "session_id");

        // spawn-before-post. A lapsed-auth or transient/unreachable failure durably
        // spools the payload instead of dropping it (Spooled) — never AuthLapsed here, unlike the
        // legacy PostAsync path, so ShouldSpawnAfter below can safely spawn on Spooled too.
        // Start the team-memory fetch BEFORE the lifecycle POST so the two overlap; it is awaited
        // (budget-capped) immediately before the stdout write below, which is the only consumer.
        // Deliberately started after the exclusion/disabled early-outs above so an excluded repo
        // never reaches the memory subsystem at all.
        var memoryTask = StartMemoryIndexTask(sessionId,
            // The git root discovered above (stamped onto the node) is preferred; the payload cwd is
            // the fallback. Never a process-cwd fallback — see StartMemoryIndexTask's scope note.
            TryGetString(enrichedNode, "workspace_root") ?? TryGetString(enrichedNode, "cwd"),
            // The EFFECTIVE profile, not the resolution's own: ProfileResolver returns a
            // null Profile whenever --server-url or KCAP_URL wins, and GetActiveProfileAsync (which
            // produced activeProfile) is what falls back to the on-disk active profile. Reading the
            // resolved one silently ignored `disable_memory_index: true` for every KCAP_URL user.
            activeProfile?.DisableMemoryIndex is true,
            activeProfile?.DisableSessionGuidelines is true,
            // Remaining already reserves Safety — subtracting it again here halved the window.
            budget.Remaining);

        var spool = new HookSpool(config);
        var outcome = await _poster.PostOrSpoolAsync("session-start/codex", enriched, "codex-hook", spool,
            sessionId: sessionId ?? "", route: "session-start/codex");

        // Codex blocks on this hook's stdout — satisfy the handshake contract FIRST, and only
        // then run the best-effort watcher-ensure and the global spool drain. Routed through
        // RunSessionStartHandshakeForTest so the ordering is provable in isolation (see
        // CodexStdoutContractTests): a large/unreachable spool backlog behind postStdoutWork can
        // never delay or gate the write below.
        // Resolve the optional memory fragment BEFORE entering the handshake, so the ordering seam
        // still receives a synchronous write: the fragment is already a value by the time
        // writeStdout runs, and the post-stdout work remains strictly after it.
        //
        // The handshake now runs on EVERY outcome, including a permanent POST rejection. It used to be
        // skipped by an early `return 1` above, which left Codex — a host that BLOCKS on this hook's
        // stdout — with zero bytes: no `continue`, no context, just a wait for its hook timeout. The
        // recording outcome must not decide whether the host can proceed.
        var fragment = await AwaitMemoryFragmentAsync(memoryTask, budget);

        // The static work-items nudge, resolved (availability-gated + opt-out) independently
        // of the lease-driven memory/guidelines fragment and merged only at the output layer.
        var workItemsNudge = HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Codex, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home));

        await RunSessionStartHandshakeForTest(
            writeStdout: () => WriteSessionStartOutput(Console.Out, fragment, workItemsNudge),
            postStdoutWork: () => RunPostStdoutWork(spool, enrichedNode, sessionId, outcome));

        // Non-zero on a permanent rejection is preserved — it is the signal the session was not
        // recorded — but it is now reported AFTER the handshake rather than instead of it. The watcher
        // still does not spawn: RunPostStdoutWork gates that on ShouldSpawnAfter(outcome).
        return outcome == HookPostOutcome.Failed ? 1 : 0;
    }

    // Everything here runs AFTER Codex's stdout handshake and is best-effort: spawning the
    // watcher (when the lifecycle POST was delivered or durably spooled) and kicking off the
    // global lifecycle/transcript spool drain in the background. Neither may precede or block on
    // the stdout write in HandleSessionStart. The drain is fire-and-forget (never awaited) — its
    // own internal throttle/budget/try-catch make it safe to abandon if the process exits first;
    // reliable delivery for Codex sessions is carried by the daemon's periodic drain pass,
    // not by this opportunistic best-effort attempt.
    // guard-1: an envelope-sourced hosted session's transcript comes from the daemon, so spawning a
    // rollout watcher here would double-ingest it. Marker absent on every other session.
    static bool IsEnvelopeSourcedHostedSession() =>
        Environment.GetEnvironmentVariable("KCAP_HOSTED_APPSERVER") is "1";

    Task RunPostStdoutWork(
            HookSpool spool, JsonNode? enrichedNode, string? sessionId, HookPostOutcome outcome) {
        var transcriptSpool = new TranscriptSpool(config);

        _ = _poster.DrainSpoolsAsync(spool, transcriptSpool, sessionId);

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return Task.CompletedTask;

        var transcript = TryGetString(enrichedNode, "transcript_path");
        var cwd        = TryGetString(enrichedNode, "cwd");

        return sessionId is not null && transcript is not null && !IsEnvelopeSourcedHostedSession()
            ? _watchers.EnsureWatcherRunning(sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex")
            : Task.CompletedTask;
    }

    async Task<int> HandleStop(JsonNode node) {
        // Codex 'Stop' fires at every turn end, NOT session end. Session-end
        // is fired by the watcher's parent-PID monitor in WatchCommand.cs
        // when the codex process actually exits — that path POSTs
        // /hooks/session-end/codex with reason: "parent_exited" and handles
        // generate_whats_done. Treating Stop as session-end here would kill
        // the watcher after turn 1 and mismark multi-turn sessions as ended
        // before they actually finish.
        //
        // We keep the watcher alive (in case it crashed mid-session) AND
        // best-effort POST /hooks/stop so the server can emit the idle-wait
        // marker that clears the chat "working" indicator (symmetric with
        // Claude's stop hook).
        var sessionId  = TryGetString(node, "session_id");
        var transcript = TryGetString(node, "transcript_path");
        var cwd        = TryGetString(node, "cwd");

        if (sessionId is not null && transcript is not null) {
            // Guard-1: skip the watcher restart for an envelope-sourced hosted session (the daemon owns
            // its transcript); the idle-marker stop POST still fires so the "working" indicator clears.
            if (!IsEnvelopeSourcedHostedSession()) {
                await _watchers.EnsureWatcherRunning(sessionId, transcript,
                    agentId: null, sessionIdOverride: null, cwd: cwd,
                    skipTitle: false, vendor: "codex"
                );
            }

            await PostBestEffortAsync("stop", node, TimeSpan.FromSeconds(2));
        }

        // Codex's stop-hook output parser rejects empty stdout as
        // "invalid stop hook JSON output". Emit the schema default explicitly.
        WriteSessionScopedOutput(Console.Out);
        return 0;
    }

    async Task<int> HandlePermissionRequest(JsonNode node) {
        var daemonUrl = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");

        return daemonUrl is null
            ? await HandlePermissionRequestStub(node)
            : await HandlePermissionRequestViaBridge(daemonUrl, node);
    }

    async Task<int> HandlePermissionRequestStub(JsonNode node) {
        // Terminal Codex sessions can't answer a Capacitor UI prompt, so we
        // record the event server-side (best-effort) and emit no decision —
        // Codex falls back to its built-in in-CLI approval flow and the user
        // answers there.
        //
        // Do NOT post to /hooks/permission-request/{vendor} — that route runs
        // RunPermissionFlow which long-polls up to 10 hours waiting for a
        // hosted-agent UI decision. With Codex's 30 s hook timeout, the hook
        // process is killed long before the server returns.
        //
        // Single 2 s deadline covers BOTH the /auth/config discovery and the
        // /hooks/permission-record POST inside PostBestEffortAsync.
        // Without bounding discovery too, a server that accepts the TCP
        // connection but stalls on /auth/config can burn the full HttpClient
        // default (100 s) before we even start the POST, blowing past Codex's
        // 30 s hook timeout.
        // Recording must never block Codex's approval prompt — see
        // PostBestEffortAsync for the shared swallow-all/cap behavior.
        await PostBestEffortAsync("permission-record", node, TimeSpan.FromSeconds(2));

        // Empty hookSpecificOutput → Codex treats it as "no decision" and runs
        // its normal approval flow. See
        // codex-rs/hooks/src/events/permission_request.rs in openai/codex.
        Console.Write("{}");
        return 0;
    }

    async Task<int> HandlePermissionRequestViaBridge(string daemonUrl, JsonNode node) {
        if (!DaemonBridgeUrl.TryParseLoopback(daemonUrl, out var bridgeBase)) {
            Console.Error.WriteLine(
                $"[kcap] codex-hook permission-request: KCAP_DAEMON_URL must be http loopback, got: {daemonUrl}");
            return EmitDenyAndExitNonzero();
        }

        using var client = http.Loopback();
        // The daemon holds the request open until the human decides, and Codex sets no timeout of
        // its own on this hook, so the wait is theirs to end rather than the client's.
        client.Timeout = Timeout.InfiniteTimeSpan;

        try {
            var bridgePayload = BuildBridgePayload(node, HookAgentId.FromEnvironment());
            using var content = new StringContent(bridgePayload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostAsync($"{bridgeBase}/codex/permission-request", content);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine(
                    $"[kcap] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
                return EmitDenyAndExitNonzero();
            }

            var body = await resp.Content.ReadAsStringAsync();
            Console.Write(body);
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] codex-hook permission-request bridge error: {ex.Message}");
            return EmitDenyAndExitNonzero();
        }
    }

    internal static JsonObject BuildBridgePayload(JsonNode node, string? agentId) {
        var payload = (JsonObject)node.DeepClone();
        if (agentId is not null) payload["agent_id"] = agentId;
        return payload;
    }

    static int EmitDenyAndExitNonzero() {
        var response = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = new JsonObject { ["behavior"] = "deny" }
            }
        };

        Console.Write(response.ToJsonString());
        return 1;
    }

    /// <summary>
    /// Best-effort POST of <paramref name="node"/> to <c>/hooks/{endpoint}</c>, capped at
    /// <paramref name="cap"/> and swallowing every failure — it must never block, throw, or
    /// terminate the caller. The single deadline covers both /auth/config discovery and the POST.
    /// Callers that must satisfy Codex's stdout contract write their JSON output AFTER awaiting this.
    /// </summary>
    async Task PostBestEffortAsync(string endpoint, JsonNode node, TimeSpan cap) {
        // Silently, and before the deadline is armed: this method owes the caller no output, and a
        // URL nothing can be sent to is not worth spending the cap discovering.
        if (!HookHttp.IsPostable(Url)) {
            return;
        }

        using var cts = new CancellationTokenSource(cap);

        try {
            // The hook verb, so a lapse writes nothing to stderr: a per-turn Stop would spam it, and
            // Codex reads hook stderr as the hook's own result.
            var (client, status) = await http.ForHookAsync(cts.Token);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                    return;
                }

                using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{Url}/hooks/{endpoint}", content, cts.Token);
            }
        } catch {
            // Best-effort — must never block or fail the caller.
        }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = TryGetString(node, fieldName);

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    /// <summary>
    /// Safely extracts a string from <paramref name="node"/>[<paramref name="fieldName"/>].
    /// Returns null (instead of throwing) when the field is absent, null, or not a string.
    /// </summary>
    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
