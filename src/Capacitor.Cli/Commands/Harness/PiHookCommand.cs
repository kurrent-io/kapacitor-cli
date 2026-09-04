using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Dispatcher for the Pi (badlogic/pi-mono) live-ingest extension. Pi
/// has no shell hooks; the shipped <c>kcap.ts</c> extension invokes this on
/// Pi's in-process lifecycle events:
///   <c>kcap hook --pi --event session-start --file &lt;path&gt; --cwd &lt;cwd&gt; --reason &lt;reason&gt;</c>
///   <c>kcap hook --pi --event session-end   --file &lt;path&gt; --cwd &lt;cwd&gt; --reason &lt;reason&gt;</c>
///
/// There is no stdin payload — Pi extensions pass arguments. The canonical
/// session id, cwd, and start time come from the Pi session JSONL header
/// (first line: <c>{"type":"session","id":uuid,"cwd":...,"timestamp":...}</c>).
///
/// Wire contract (mirrors <see cref="CopilotHookCommand"/>):
///   session-start → POST /hooks/session-start/pi (enriched with repo/PR), then
///                   spawn the shared watcher tailing the session file with
///                   vendor=pi (the server's PiTranscriptNormalizer owns
///                   content; the hook owns lifecycle).
///   session-end   → kill watcher + capped inline drain, then POST
///                   /hooks/session-end/pi.
/// Fail-open throughout — a kcap/server problem must never disrupt the pi session.
///
/// <para><b>stdout is a DATA channel on session-start</b>, carrying the team-memory fragment (raw
/// text, no envelope) for the extension to append to each turn's chained system prompt. Diagnostics go
/// to stderr. Every other event keeps writing nothing at all.</para>
/// </summary>
sealed class PiHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager  _watchers = new(config, profiles, http);
    readonly AgentHookPoster _poster   = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    // Pi's extension shells out with a 10s pi.exec timeout (see kcap.ts), so the
    // session-end drain must finish well inside that or the session-end POST is
    // starved and the session sticks "Active" (same drain-cap pattern as ClaudeHookCommand's).
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(6);

    /// <summary>Inside the same 10s pi.exec timeout, with room to spare: kcap.ts documents the
    /// ~3.5s of work this leaves once <see cref="HookBudget.Safety"/> is reserved.</summary>
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    public Task<int> Handle(string[] args) => Handle(args, Console.Out);

    /// <param name="stdout">Injected so the fragment the extension consumes is assertable without
    /// capturing the process's real console — which is also the only way to test the no-fragment path,
    /// since "wrote nothing" is indistinguishable from "was never called" on a shared writer.</param>
    internal async Task<int> Handle(string[] args, TextWriter stdout) {
        var eventName = GetArg(args, "--event");

        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --pi requires --event <session-start|session-end> "
              + "(the kcap Pi extension passes it; re-run: kcap plugin install --pi)");
            return 1;
        }

        var file = GetArg(args, "--file");
        if (string.IsNullOrWhiteSpace(file)) return 0; // ephemeral / no session file — nothing to record

        var header = await TryReadHeaderAsync(file);

        // Session id: prefer the header uuid, but Pi can hand us the session file
        // before its header line is flushed (session_start fires first), so fall
        // back to the uuid embedded in the filename ("<timestamp>_<uuid>.jsonl").
        // Without the suffix parse, a not-yet-flushed session-start would derive a
        // non-uuid id and silently drop the session + watcher.
        if (ExtractSessionId(file, header?.SessionUuid) is not { } sessionId) return 0;
        var cwd       = GetArg(args, "--cwd") ?? header?.Cwd;
        var reason    = GetArg(args, "--reason");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId, config)) {
            if (eventName == "session-end") DisabledSessions.RemoveMarker(sessionId, config);
            return 0;
        }

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(config);

        var activeProfile = profiles.Effective;

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths, home)) {
            return 0;
        }

        return eventName switch {
            "session-start" => await HandleSessionStart(sessionId, file, cwd, reason, header?.Timestamp,
                                                         activeProfile, spool, args, stdout,
                                                         clock.Budget(Ceiling)),
            "session-end"   => await HandleSessionEnd(sessionId, file, cwd, reason),
            _               => 0   // unknown — fail-open like the other dispatchers
        };
    }

    async Task<int> HandleSessionStart(
            string          sessionId,
            string          file,
            string?         cwd,
            string?         reason,
            DateTimeOffset? startedAt,
            Profile?        activeProfile,
            HookSpool       spool,
            string[]        args,
            TextWriter      stdout,
            HookBudget      budget
        ) {
        var source = string.IsNullOrEmpty(reason) ? "startup" : reason;

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
        if (startedAt is { } ts) forwarded["started_at"] = ts.ToString("O");
        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) forwarded["agent_host_id"] = agentHostId;

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot dispatchers).
        if (activeProfile?.DefaultVisibility is { } visibility) forwarded["default_visibility"] = visibility;

        SessionStartInventory.Stamp(forwarded, config, home);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(config, forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(config, enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        // Scope root: the git root stamped onto `forwarded` when found, else the payload cwd — read
        // back from `enriched` so the fallback matches what the server received. Never process-cwd.
        var scopeRoot = ScopeRootFrom(enriched, cwd);

        // Start the memory fetch so it OVERLAPS the lifecycle POST; gated on the extension DECLARING
        // it captures stdout (--memory-contract >= 1), else an older kcap.ts would spend the
        // once-only lease on output it discards.
        var memoryTask = MemoryContractOf(args) >= 1
            ? StartMemoryIndexTask(file, scopeRoot,
                activeProfile?.DisableMemoryIndex is true,
                activeProfile?.DisableSessionGuidelines is true,
                budget.Remaining,
                reason)
            : Task.FromResult<string?>(null);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. Only a
        // permanent failure keeps the prior non-zero exit and skips the watcher.
        var outcome = await _poster.PostOrSpoolAsync("session-start/pi", enriched, "pi-hook",
            spool, sessionId, route: "session-start/pi");

        // BEFORE the watcher gate and before any early return: a withheld watcher must not suppress
        // an injection whose once-per-session lease is already spent. pi.exec hands the extension
        // stdout regardless of exit code, so no commit gate is needed (unlike Copilot).
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, budget);
        var workItemsNudge = HarnessNudgeEmitter.Combine(
            WorkItemsNudgeEmitter.Resolve(HarnessId.Pi, sessionId, activeProfile?.DisableWorkItemsNudge is true, home),
            HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home));
        await WriteMemoryFragment(stdout, fragment, workItemsNudge);

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return outcome == HookPostOutcome.Failed ? 1 : 0;

        await _watchers.EnsureWatcherRunning(sessionId, file,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "pi"
        );
        return 0;
    }

    async Task<int> HandleSessionEnd(string sessionId, string file, string? cwd, string? reason) {
        // Kill watcher + inline-drain BEFORE the POST so the server computes
        // stats over the full transcript — capped so a slow drain can't starve
        // the session-end POST (mirror of ClaudeHookCommand).
        try {
            var drained = await TimeBudget.RunCappedAsync(
                async () => {
                    await _watchers.KillWatcher(sessionId);
                    await _watchers.InlineDrainAsync(sessionId, file, agentId: null, vendor: "pi");
                },
                PreHookDrainCap
            );

            if (!drained) {
                await Console.Error.WriteLineAsync(
                    $"[kcap] pi session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                  + $"Transcript tail may be incomplete — recoverable via: kcap import --pi --session {sessionId}");
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] pi session-end pre-hook failed: {ex.Message}");
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = string.IsNullOrEmpty(reason) ? "quit" : reason,
            ["home_dir"]        = home.Path,
            ["ended_at"]        = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) forwarded["cwd"] = cwd;
        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) forwarded["agent_host_id"] = agentHostId;

        // AuthLapsed / Posted → clean exit (0); a real failure keeps the prior non-zero exit.
        return await PostHookAsync("session-end/pi", forwarded.ToJsonString()) == HookPostOutcome.Failed ? 1 : 0;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    sealed record PiHeader(string? SessionUuid, string? Cwd, DateTimeOffset? Timestamp);

    /// <summary>
    /// Dashless Pi session id: the header's uuid when readable, else the uuid
    /// suffix of the <c>&lt;timestamp&gt;_&lt;uuid&gt;.jsonl</c> filename (Pi can
    /// hand us the file before its header line is flushed). Returns null when
    /// neither yields a uuid (a stray, non-Pi <c>.jsonl</c>).
    /// </summary>
    internal static string? ExtractSessionId(string file, string? headerUuid) {
        if (headerUuid is { Length: > 0 } h && Guid.TryParse(h, out _))
            return h.Replace("-", "");

        var stem      = Path.GetFileNameWithoutExtension(file);
        var candidate = stem.Contains('_') ? stem[(stem.LastIndexOf('_') + 1)..] : stem;

        return Guid.TryParse(candidate, out _) ? candidate.Replace("-", "") : null;
    }

    static async Task<PiHeader?> TryReadHeaderAsync(string path) {
        try {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            while (await reader.ReadLineAsync() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;
                if (root.Str("type") != "session") return null;

                DateTimeOffset? ts = root.Str("timestamp") is { } raw
                 && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                        ? parsed
                        : null;

                return new PiHeader(root.Str("id"), root.Str("cwd"), ts);
            }
        } catch {
            // Header not yet written / unreadable — fall back to the filename.
        }
        return null;
    }

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    Task<HookPostOutcome> PostHookAsync(string endpoint, string body)
        => _poster.PostAsync(endpoint, body, "pi-hook");

    internal static async Task WriteMemoryFragment(TextWriter stdout, string? fragment, string? workItemsNudge = null) {
        var payload = RenderMemoryOutput(fragment, workItemsNudge);
        if (payload.Length == 0) return;
        await stdout.WriteAsync(payload);
        await stdout.FlushAsync();
    }

    /// <summary>The exact bytes stdout receives. Pure so the zero-bytes rule is assertable.</summary>
    internal static string RenderMemoryOutput(string? fragment, string? workItemsNudge = null) =>
        fragment is null && string.IsNullOrWhiteSpace(workItemsNudge)
            ? ""
            : SessionStartMemoryOutputAdapters.Render(HarnessId.Pi, fragment, workItemsNudge);

    /// <summary>
    /// The lifecycle this harness reports. SessionId is the session FILE PATH — the identity
    /// normalizer hashes it (PiSessionPathCanonicalizer), so resume (same file) is lease-deduped and
    /// fork (new file) is freshly eligible. IsTopLevel/ClassificationAuthoritative are true because
    /// kcap.ts only ever fires for the pi process's OWN session. CallbackMayRepeat because restarts
    /// and resumes re-fire session_start for the same file.
    /// </summary>
    internal static SessionMemoryLifecycle LifecycleFor(string file, string? reason) =>
        new(HarnessId.Pi, file, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            MapReason(reason), CallbackMayRepeat: true);

    // Pi reasons pinned upstream: startup|reload|new|resume|fork. Unrecognized degrades to
    // RepeatedTurnCallback — never Unknown, which the policy treats as retry-later.
    static SessionLifecycleReason MapReason(string? reason) => reason switch {
        "new" or "startup" => SessionLifecycleReason.New,
        "resume"           => SessionLifecycleReason.Resume,
        "fork"             => SessionLifecycleReason.Fork,
        "reload"           => SessionLifecycleReason.Reopen,
        _                  => SessionLifecycleReason.RepeatedTurnCallback
    };

    internal async Task<string?> StartMemoryIndexTask(
            string   file,
            string?  scopeRoot,
            bool     disabled,
            bool     guidelinesDisabled,
            TimeSpan budget,
            string?  reason = null) {
        if ((disabled && guidelinesDisabled) || string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !HookHttp.IsPostable(Url))
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                LifecycleFor(file, reason),
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    internal static int MemoryContractOf(string[] args) =>
        int.TryParse(GetArg(args, "--memory-contract"), out var version) ? version : 0;

    static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string? ScopeRootFrom(string enriched, string? cwd) {
        try {
            return AsString(JsonNode.Parse(enriched)?["workspace_root"]) ?? cwd;
        } catch {
            return cwd;
        }
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
