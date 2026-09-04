using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Dispatcher for Google Antigravity's control hooks. Antigravity is a GUI
/// IDE with no shell hooks; the kcap plugin (a block in Antigravity's <c>hooks.json</c>)
/// registers one command per lifecycle/tool event. Because the JSON payload carries NO
/// event-name field, the event is passed as a positional arg:
///   <c>kcap hook --antigravity &lt;Event&gt;</c>   with the payload on stdin.
///
/// Wire contract (mirrors <see cref="OpenCodeHookCommand"/> — Antigravity likewise has
/// the watcher own session content + session-end): only <c>PreInvocation</c> is
/// actionable — it POSTs /hooks/session-start/antigravity and ensures a watcher is
/// running (vendor=antigravity) tailing the conversation's <c>transcript_full.jsonl</c>.
/// PreInvocation re-fires cheaply on every turn; the server's deterministic lifecycle id
/// collapses the repeats and <see cref="WatcherManager.EnsureWatcherRunning"/> is a no-op
/// once live. Session-end is watcher-owned: Antigravity's IDE process outlives any one
/// conversation (like the Codex desktop), so the watcher self-terminates on idle and
/// POSTs /hooks/session-end/antigravity. <c>Stop</c>/<c>PostInvocation</c>/tool events
/// are no-ops here (the watcher already tails the transcript continuously).
///
/// Fail-open throughout — a kcap/server problem must never disrupt the Antigravity IDE. The
/// session-start POST goes through <see cref="AgentHookPoster.PostOrSpoolAsync(string, string, string, HookSpool, string, string)"/> (Task
/// 6): a lapsed/outage POST is durably spooled for a later drain, and the watcher still spawns
/// (<see cref="SpawnGateForTest"/>) — capture must not depend on lifecycle-POST delivery.
/// Antigravity conversation ids are dashed UUIDs; kcap canonicalizes them to the DASHLESS form
/// for BOTH the session-start payload and the watcher key so they resolve to one stream (the
/// dashed id lives on only in the transcript file path). Historical import canonicalizes the
/// same way, so a conversation captured live and later re-imported dedupes to one stream.
/// </summary>
sealed class AntigravityHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>Well inside the 15s <c>AntigravityHooks</c> writes into the vendor's hooks.json, which
    /// kills the hook outright: PreInvocation blocks the turn, and that firing never retries.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    public Task<int> Handle(string[] args) => Handle(args, Console.In, Console.Out);

    internal async Task<int> Handle(
            string[]       args,
            TextReader     stdin,
            TextWriter     stdout,
            Func<string?>? workspaceFallback = null
        ) {
        var eventName = EventArg(args);
        if (string.IsNullOrWhiteSpace(eventName)) {
            // Control hooks must always exit 0 (a non-zero exit makes Antigravity treat the
            // hook as failed) — surface the hint on stderr but don't fail the hook.
            Console.Error.WriteLine(
                "kcap hook --antigravity requires an event name, e.g. "
              + "`kcap hook --antigravity PreInvocation` (the kcap Antigravity plugin passes it; "
              + "re-run: kcap plugin install --antigravity)");
            return 0;
        }

        // PreInvocation is the only actionable event; the watcher owns everything else.
        if (eventName != "PreInvocation") return 0;

        JsonObject? payload;
        try {
            payload = JsonNode.Parse(await stdin.ReadToEndAsync()) as JsonObject;
        } catch {
            return 0; // malformed payload — fail open, next PreInvocation retries
        }
        if (payload is null) return 0;

        var conversationId = Str(payload, "conversationId");
        if (string.IsNullOrWhiteSpace(conversationId)) return 0;

        var transcriptPath = Str(payload, "transcriptPath");
        if (string.IsNullOrWhiteSpace(transcriptPath)) return 0; // nothing to tail

        // Canonical dashless id — matches how `kcap watch` and `kcap disable` normalize ids,
        // so session-start, the watcher's transcript batches, and disable all resolve to ONE
        // stream (the dashed conversationId is kept only for the transcript file path).
        var sessionId = conversationId!.Replace("-", "");

        var cwd = ResolveWorkspace(payload, workspaceFallback ?? AgentWorkspaceCwd);

        // Mirror the disabled-session fast path: `kcap disable` must stop every POST
        // and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) return 0;

        var activeProfile = profiles.Effective;

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths, home)) {
            return 0;
        }

        var hookBudget = clock.Budget(Ceiling);
        return await HandleSessionStart(sessionId, transcriptPath!, cwd, payload, activeProfile, stdout, hookBudget);
    }

    /// <summary>
    /// Writes the team-memory fragment in Antigravity's PreInvocation shape:
    /// <c>{"injectSteps":[{"userMessage":"…"}]}</c>. <c>userMessage</c> rather than
    /// <c>ephemeralMessage</c> because the vendor's own embedded hook contract documents the latter as
    /// transient, and the index is meant to persist for the conversation.
    ///
    /// <para><b>A null fragment writes ZERO BYTES.</b> This hook emitted nothing at all before the
    /// memory index existed, so rendering the adapter's <c>{}</c> on the no-fragment path would change
    /// the wire behaviour of EVERY invocation for EVERY user — including the IDE-only majority, whose
    /// product was never probed — to buy nothing. Mirrors Copilot and Kiro. Do not "simplify" this by
    /// rendering the null case: the shared adapter's own null rendering is <c>{}</c>, which is exactly
    /// what must not reach stdout here.</para>
    ///
    /// <para>Serialized before the first byte so a renderer fault degrades to silence rather than a
    /// partial document.</para>
    /// </summary>
    internal static void WritePreInvocationOutput(TextWriter writer, string? fragment, string? workItemsNudge = null) {
        if (fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)) return;

        string payload;

        try {
            payload = SessionStartMemoryOutputAdapters.Render(HarnessId.Antigravity, fragment, workItemsNudge);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return;
        }

        writer.Write(payload);
    }

    async Task<int> HandleSessionStart(
            string      sessionId,
            string      transcriptPath,
            string?     cwd,
            JsonObject  payload,
            Profile?    activeProfile,
            TextWriter  stdout,
            HookBudget  budget
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["home_dir"]        = home.Path,
            ["started_at"]      = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }
        if (Str(payload, "antigravityVersion") is { } version)
            forwarded["antigravity_version"] = version;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId)
            forwarded["agent_host_id"] = agentHostId;

        // Stamp default visibility BEFORE enrichment so it survives the JsonString
        // round-trip (same rationale as the OpenCode/Kiro dispatchers); null lets the
        // server fall back to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility)
            forwarded["default_visibility"] = visibility;

        SessionStartInventory.Stamp(forwarded, config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Scope root: the git root discovered from the payload cwd (stamped onto `forwarded` above,
        // if found) is preferred; the payload cwd is the fallback. Read back from `enriched` (not a
        // local captured above) so the fallback matches exactly what the server received. Never a
        // process-cwd fallback — see StartMemoryIndexTask's scope-safety note.
        var scopeRoot = AsString(JsonNode.Parse(enriched)?["workspace_root"]) ?? cwd;

        // Start the memory fetch so it OVERLAPS the lifecycle POST. Never before it, and never
        // awaited before it — the POST is what capture depends on.
        var memoryTask = StartMemoryIndexTask(sessionId, scopeRoot,
            activeProfile?.DisableMemoryIndex is true,
            activeProfile?.DisableSessionGuidelines is true,
            budget.Remaining);

        // Task 6: spawn-before-post. Route through the shared spool-aware poster (which
        // replaced this dispatcher's former bespoke poster) — a lapse/outage durably spools the
        // payload for a later drain AND still proceeds to spawn the watcher, so capture never
        // depends on lifecycle-POST delivery. Only a permanent Failed withholds the watcher.
        //
        // Started but NOT awaited yet, so the POST cannot stand between a fetched fragment and
        // stdout: PostOrSpoolAsync retries for ~30s, far beyond this hook's 5s ceiling. A slow or
        // unreachable server must never leave the once-per-conversation lease committed while the
        // fragment it paid for is still stuck behind the POST — the vendor kills the hook at its own
        // timeout, and that firing never retries.
        var spool    = new HookSpool(config);
        var postTask = _poster.PostOrSpoolAsync("session-start/antigravity", enriched, "antigravity-hook",
            spool, sessionId, route: "session-start/antigravity");

        // The fragment reaches stdout as soon as the bounded fetch resolves — before the POST is
        // awaited and before the watcher branch — so neither a slow POST nor a later
        // EnsureWatcherRunning stall can strand an already-committed injection. AwaitBounded already
        // subtracts HookBudget.Safety — do NOT subtract it again. Written even when the watcher-spawn
        // gate below returns early — a withheld watcher must not suppress injection.
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        // Nudges are unleased pure functions of the session id, so on this repeating callback they
        // must be gated by the vendor's own per-conversation counter: without the gate every turn
        // re-injects them as another persistent userMessage step.
        var workItemsNudge = IsFirstInvocation(payload)
            ? HarnessNudgeEmitter.Combine(
                WorkItemsNudgeEmitter.Resolve(HarnessId.Antigravity, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
                HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home))
            : null;
        WritePreInvocationOutput(stdout, fragment, workItemsNudge);
        await stdout.FlushAsync();

        // BOUNDED by what remains of the ceiling — the POST retries for ~30s, far past this hook's 5s
        // budget. On a lapse we stop waiting and spool durably instead, so a later drain pass replays
        // it; double delivery is harmless (the server's deterministic lifecycle event id collapses
        // both onto one SessionStarted).
        HookPostOutcome outcome;

        try {
            outcome = await postTask.WaitAsync(budget.Remaining);
        } catch (TimeoutException) {
            outcome = spool.Append(sessionId, "session-start/antigravity", enriched)
                ? HookPostOutcome.Spooled
                : HookPostOutcome.Skipped;
        }

        // Fail-open: a non-zero exit would surface as a failed hook; skip the watcher
        // this firing and let the next PreInvocation retry.
        if (!SpawnGateForTest(outcome, Url)) return 0;

        // Watcher key = the dashless session id (kcap watch strips dashes too, so the pid
        // file + the spawned watcher's stream all agree). The dashed conversation id lives on
        // in transcriptPath, from which the watcher derives the sibling gen_metadata db.
        //
        // Bounded for the same reason as the POST, and this is the LAST step between the committed
        // injection and the zero exit — a stall here would discard an already-written fragment. The
        // stale-watcher path can wait up to 5s for a graceful kill.
        try {
            await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "antigravity"
            ).WaitAsync(budget.Remaining);
        } catch (TimeoutException) {
            // Budget exhausted. The next PreInvocation ensures the watcher.
        }

        return 0;
    }

    /// <summary>Test seam mirroring <see cref="AgentHookPoster.ShouldSpawnAfter"/> — capture must
    /// start on <c>Posted</c> OR <c>Spooled</c>, never gated behind lifecycle-POST delivery.</summary>
    internal static bool SpawnGateForTest(HookPostOutcome o, string? Url)
        => AgentHookPoster.ShouldSpawnAfter(o, Url);

    /// <summary>
    /// The lifecycle this adapter reports. PreInvocation fires ONCE PER INVOCATION within a
    /// conversation (its payload carries `invocationNum`), so this is a REPEATING callback and the
    /// fenced lease is what makes injection once-per-conversation. Kiro's agentSpawn is the only
    /// other harness with this shape; every other adapter is CallbackMayRepeat: false and copying
    /// one would re-inject the index on every turn.
    ///
    /// <para>The lease key is derived from the harness token and the normalized session id only.
    /// `invocationNum` must never reach it, directly or transitively — it is the one field that
    /// varies between callbacks, so keying on it would mint a fresh lease per invocation.</para>
    /// </summary>
    internal static SessionMemoryLifecycle LifecycleFor(string sessionId) =>
        new(HarnessId.Antigravity, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true);

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as zero bytes.
    ///
    /// <para><b>Scope safety:</b> git root preferred, payload cwd as fallback; with neither, injection
    /// is skipped rather than letting the shared resolver fall back to the hook PROCESS's cwd and
    /// inject an unrelated repository's memories.</para>
    ///
    /// <para>The URL is checked BEFORE any client is constructed, so an unusable base url spends no
    /// lease and starts no task.</para>
    /// </summary>
    internal async Task<string?> StartMemoryIndexTask(
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
                LifecycleFor(sessionId),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Whether this PreInvocation is the conversation's first, read from the vendor's own
    /// <c>invocationNum</c> counter — the one payload field that varies between callbacks. Any value
    /// at or below one reads as first, and so does an absent or non-numeric one: a payload without a
    /// usable counter cannot distinguish callbacks, and emitting once too often beats never emitting.
    /// </summary>
    internal static bool IsFirstInvocation(JsonObject payload) =>
        payload["invocationNum"] is not JsonValue value
     || !value.TryGetValue<long>(out var invocation)
     || invocation <= 1;

    /// <summary>The event name — the first positional token after <c>--antigravity</c>.</summary>
    internal static string? EventArg(string[] args) {
        var idx = Array.IndexOf(args, "--antigravity");
        if (idx < 0 || idx + 1 >= args.Length) return null;

        var next = args[idx + 1];
        return next.StartsWith('-') ? null : next;
    }

    static string? FirstWorkspacePath(JsonObject payload) {
        if (payload["workspacePaths"] is JsonArray { Count: > 0 } paths
         && AsString(paths[0]) is { Length: > 0 } first) {
            return first;
        }
        // Fall back to a singular form if present.
        return Str(payload, "cwd");
    }

    /// <summary>
    /// The payload's workspace, else <paramref name="agentCwdFallback"/> — invoked ONLY when the
    /// payload yields nothing, so a vendor release that starts populating <c>workspacePaths</c>
    /// silently wins over the fallback.
    ///
    /// <para><b>Why a fallback exists at all (measured, agy 1.1.11).</b> Print mode
    /// (<c>agy -p</c>) sends <c>"workspacePaths": []</c> in every hook payload, while an
    /// interactive session sends the launch directory — same hook file, same payload shape
    /// otherwise. Without a workspace the memory index has no scope and the captured session no
    /// repo, so print-mode runs silently lost both. This was long misdiagnosed as "print mode
    /// ignores <c>injectSteps</c>": a hook probe proved injected steps land in the print-mode
    /// transcript on every invocation — the payload's empty workspace was starving OUR emission,
    /// not the vendor dropping it.</para>
    /// </summary>
    internal static string? ResolveWorkspace(JsonObject payload, Func<string?> agentCwdFallback) =>
        FirstWorkspacePath(payload) ?? agentCwdFallback();

    /// <summary>
    /// The agent process's own working directory — the launch dir, which is exactly what
    /// interactive mode reports as <c>workspacePaths[0]</c>. Resolved by walking the ppid chain to
    /// the nearest <c>agy</c> ancestor and reading ITS cwd. Never this hook process's cwd: the
    /// vendor chdirs hook children to the plugin directory, which is the wrong-scope hazard the
    /// scope-safety note in <see cref="StartMemoryIndexTask"/> exists to prevent. A resolved
    /// directory that no longer exists is rejected rather than handed to git. Fail-open on every
    /// path — a missing fallback just means print mode behaves as it did before this existed.
    /// </summary>
    internal static string? AgentWorkspaceCwd() {
        try {
            if (ProcessHelpers.GetParentPid() is not { } parentPid || parentPid <= 1) return null;

            if (ProcessHelpers.ResolveCodingAgentPid(parentPid, "agy", ProcessHelpers.GetProcessInfo)
                    is not { } agentPid) {
                return null;
            }

            var cwd = ProcessHelpers.GetProcessCwd(agentPid);

            return cwd is { Length: > 0 } && Directory.Exists(cwd) ? cwd : null;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Safely read a string field: returns null when the key is absent OR the value is a
    /// non-string JSON shape (number/object/array). <c>JsonNode.GetValue&lt;string&gt;()</c>
    /// throws on a shape mismatch, which would break the hook's fail-open contract.
    /// </summary>
    static string? Str(JsonObject payload, string key) => AsString(payload[key]);

    static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
