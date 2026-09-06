using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Claude Code hooks. Claude invokes one
/// CLI entry per session event; this method consolidates the seven
/// historical per-event subcommands (<c>session-start</c>, <c>stop</c>, …)
/// behind one entry point and routes off the <c>hook_event_name</c>
/// in the JSON payload — mirroring <see cref="CodexHookCommand"/> and
/// <see cref="CursorHookCommand"/>.
/// </summary>
public sealed class ClaudeHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    // Hard ceiling on the best-effort pre-POST drain (watcher kill + inline transcript
    // drain) for session-end / subagent-stop. Session-end runs in the detached continuation
    // (ClaudeSessionEndHandoff) under HookBudget's 15s, subagent-stop inside the hook; either
    // way a slow or retrying remote call in the drain could consume the whole budget (the HTTP
    // retry helper alone allows up to 30s) and the lifecycle POST would never be sent, leaving
    // the session stuck "Active". 8s leaves ample headroom to send the POST; the server's
    // StopAndDrain + the "kcap import" hint recover the rest.
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    /// <summary>
    /// What <c>kcap/hooks/hooks.json</c> gives the event, except where the host's own number is not
    /// the useful bound: Claude caps a SessionEnd plugin hook at 1.5s, so its 15s belongs to the
    /// detached continuation (<see cref="Cli.Harness.Claude.ClaudeSessionEndHandoff"/>) rather than to
    /// the hook; and PermissionRequest is allowed 36s there, which is time for the human to answer,
    /// not for kcap to spend.
    /// </summary>
    static TimeSpan Ceiling(string? command) => command switch {
        "session-end" => TimeSpan.FromSeconds(15),
        _             => TimeSpan.FromSeconds(5),
    };

    public Task<int> Handle(TextReader stdin, TextWriter? stdout = null) {
        var spool = new HookSpool(config);
        spool.ReapOlderThan(TimeSpan.FromDays(30));

        return HandleWithDeps(
            spool,
            stdin,
            () => http.ForHookAsync(),
            stdout
        );
    }

    internal async Task<int> HandleWithDeps(
            HookSpool spool, TextReader stdin,
            Func<Task<AuthAttempt>> clientFactory,
            TextWriter? stdout = null) {
        string body;
        try { body = await stdin.ReadToEndAsync(); } catch { return 0; }

        // Minimal parse (no auth/git) so we can spool AND start the watcher even if client creation hangs.
        string? command = null, sessionId = null, transcriptPath = null, cwd = null, source = null, agentId = null;
        try {
            var node = JsonNode.Parse(body);
            var ev   = node?["hook_event_name"]?.GetValue<string>();
            command        = ev is null ? null : ToKebab(ev);
            sessionId      = node?["session_id"]?.GetValue<string>()?.Replace("-", "");
            transcriptPath = node?["transcript_path"]?.GetValue<string>();
            cwd            = node?["cwd"]?.GetValue<string>();
            source         = node?["source"]?.GetValue<string>();
            agentId        = node?["agent_id"]?.GetValue<string>();
        } catch { }

        var budget    = clock.Budget(Ceiling(command));
        var clientCap = budget.Remaining;

        // Approval policy is decided before any client exists. The seam only appends its events to
        // the spool, so it needs no server — and routing it through HandleCore would let an
        // unreachable server (or an auth probe over budget) silently disable every deny, since that
        // path returns before HandleCore is ever reached.
        if (command == "pre-tool-use") {
            // Every other decision lane already degrades an unforeseen throw to exit 0. Without the
            // same boundary here a crash exits non-zero, which Claude renders as its opaque
            // hook-error banner — and for a natively auto-allowed tool the deny that never got
            // written lets the call run.
            try {
                if (sessionId is null) return 0;
                // Same two gates the rest of this method honours: a disabled session, and a repo/path
                // the profile excludes — an excluded session is ungoverned because its decisions could
                // not be recorded, and the audit contract is that every engine decision is.
                if (await ShouldSuppressCaptureAsync(sessionId, body, command, profiles.Effective, budget)) return 0;

                var rendered = Environment.GetEnvironmentVariable("KCAP_RENDERED_AGENT") is "1";

                return await new Cli.Harness.Claude.ClaudePolicySeam(config)
                    .HandlePreToolUseAsync(body, sessionId, rendered, stdout ?? Console.Out);
            } catch { return 0; }
        }

        // Skip client construction entirely for an unusable URL, before ANY dispatch, so it folds
        // into the same degraded arm a client-creation timeout already uses — keeping capture and
        // the spool intact without inventing a second disposition for a not-usable AuthAttempt.
        var created = HookHttp.IsPostable(Url)
            ? await CreateClientWithinBudgetAsync(clientFactory, clientCap)
            : null;

        if (created is null) {
            // The degraded arm bypasses HandleCore, so its disabled/exclusion gates must run here.
            var activeProfile = profiles.Effective;
            if (await ShouldSuppressCaptureAsync(sessionId, body, command, activeProfile, budget)) return 0;

            // Auth/client creation exceeded the hook budget (hung /auth/config or refresh during an
            // outage). The watcher and the spool need no client — start capture and persist the
            // lifecycle event so neither the transcript nor the session record is lost.
            if (command == "session-start" && sessionId is not null && transcriptPath is not null) {
                var isResumeOrCompact = source is not null &&
                    (source.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                     source.Equals("compact", StringComparison.OrdinalIgnoreCase));
                try {
                    await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
                        agentId: null, cwd: cwd, skipTitle: isResumeOrCompact);
                } catch { }
            }

            // The freeze is a local file write that needs no client, so it belongs on this arm too:
            // without it the first PreToolUse builds the snapshot against files edited since the
            // session began, and the session is governed by a policy it never started under.
            string? degradedPolicyNotice = command == "session-start" && sessionId is not null
                ? FreezePolicySnapshot(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd))
                : null;

            // Report what the append ACTUALLY did, and only for events that are spoolable at all —
            // announcing "spooled" ahead of the attempt would claim a replay that may never happen.
            var unusableUrl = !HookHttp.IsPostable(Url);
            var reason      = unusableUrl ? "unusable server URL" : "auth/client creation exceeded hook budget";

            if (command is "session-start" or "session-end" && sessionId is not null) {
                await ReportSpoolAsync(spool.Append(sessionId, command, NormalizeForSpool(body, command)),
                                       command, sessionId, reason, unusableUrl);
            }
            else if (command == "subagent-stop" && sessionId is not null && agentId is not null) {
                await ReportSpoolAsync(spool.Append(sessionId, "subagent-stop", NormalizeForSpool(body, command)),
                                       "subagent-stop", $"{sessionId}/{agentId}", reason, unusableUrl);
            }
            else if (unusableUrl) {
                await Console.Error.WriteLineAsync(
                    UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"{command ?? "hook"} dropped (not a spoolable event)"));
            }

            // Nothing else on this arm writes stdout, so the notice is the arm's only object.
            WriteSessionStart(stdout ?? Console.Out, null, degradedPolicyNotice);

            return 0;
        }

        var (client, authStatus) = created.Value;
        try {
            return await HandleCore(client, authStatus, spool, new StringReader(body), stdout: stdout);
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[kcap] claude hook failed (fail-open): {ex.Message}");
            return 0;
        } finally {
            client.Dispose();
        }
    }

    // Returns (client,status) if created within `cap`; null if the cap elapsed first
    // (abandoned creation task reaped on process exit).
    internal static async Task<AuthAttempt?> CreateClientWithinBudgetAsync(
            Func<Task<AuthAttempt>> factory, TimeSpan cap) {
        if (cap <= TimeSpan.Zero) return null;
        var task = factory();
        var winner = await Task.WhenAny(task, Task.Delay(cap));
        if (winner != task) {
            // Abandoned: observe ALL terminal states so a late fault (likely during the very
            // outage this guards) doesn't surface as an UnobservedTaskException; dispose the
            // client only if creation actually completed after the cap elapsed.
            _ = task.ContinueWith(static t => {
                if (t.IsFaulted) _ = t.Exception;
                else if (t.Status == TaskStatus.RanToCompletion) t.Result.Client.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }
        try { return await task; } catch { return null; }
    }

    // Minimal normalization for an auth-timeout-spooled body: dashless ids (match the server's
    // expected form) and, for session-end, an ended_at stamp so a late replay keeps idempotency.
    string NormalizeForSpool(string body, string command) {
        try {
            var node = JsonNode.Parse(body);
            if (node is null) return body;
            NormalizeGuidField(node, "session_id");
            NormalizeGuidField(node, "agent_id");
            if (command == "session-end" && node["ended_at"] is null)
                node["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
            // Surface 3: the degraded arm spools via this path and bypasses HandleCore's stamp, so a
            // replayed session-start must still carry the harness inventory (the hook-ingest carrier).
            if (command == "session-start") SessionStartInventory.Stamp(node.AsObject(), config, home);
            return node.ToJsonString();
        } catch { return body; }
    }

    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        return winner == inner ? await inner : 0;
    }

    // Await repo enrichment but never past the remaining hook budget. If it can't finish in time,
    // proceed with the un-enriched body (repo info still reaches the session via the watcher's own
    // detection) so the bounded POST/spool path is always reached before Claude kills the hook.
    static async Task<string> AwaitEnrichmentWithinBudget(Task<string> enrichment, string fallbackBody, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) {
            _ = enrichment.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            return fallbackBody;
        }
        var winner = await Task.WhenAny(enrichment, Task.Delay(budget));
        if (winner != enrichment) {
            _ = enrichment.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default); // observe if it later faults
            return fallbackBody;
        }
        try { return await enrichment; } catch { return fallbackBody; }
    }

    // Repo/path exclusion gate shared by the main command path and the permission-request
    // watcher self-heal: true when the active profile excludes this session's repo or cwd
    // (caller should skip capture). The fallback repo detection is budgeted so a slow git/gh
    // probe can't blow the hook deadline; if it can't resolve in time we fail open to capturing
    // (the per-cwd cache makes subsequent sessions in an excluded repo resolve and exclude promptly).
    /// <summary>
    /// The disabled-session and repo/path exclusion gates, callable from the degraded path.
    ///
    /// <para>Both live inside <c>HandleCore</c>, which is reached only when a client was created. The
    /// degraded branch spawns a watcher and spools without them — so routing an unusable URL there
    /// unguarded would deterministically capture sessions the user ran `kcap disable` on, or repos
    /// they excluded. That is a privacy regression a fix must not introduce.</para>
    ///
    /// <para>Takes the ALREADY-CANONICAL (dashless) session id: DisabledSessions looks a marker up by
    /// filename with no normalization, while the raw payload id is still dashed. Preserves the
    /// session-end marker cleanup, which a plain boolean would have dropped.</para>
    /// </summary>

    /// <summary>
    /// One honest line for a spool attempt, on the degraded path or after a failed live POST:
    /// "spooled" promises a replay, so it is said only when the append persisted something. An
    /// unusable URL routes through the shared source-aware diagnostic (which names what to fix and
    /// never echoes the URL); a budget overrun keeps its existing wording.
    /// </summary>
    async Task ReportSpoolAsync(bool spooled, string route, string key, string reason, bool unusableUrl) {
        var disposition = spooled
            ? $"{route} spooled, not sent ({key}); will retry on the next kcap hook"
            : $"{route} dropped — the spool write failed ({key})";

        if (unusableUrl) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, disposition));

            return;
        }

        await Console.Error.WriteLineAsync($"[kcap] {disposition} ({reason})");
    }

    // Why a bounded live POST left its payload to the spool: the status it got, or no response at all.
    static string FailureReason(int statusCode) =>
        statusCode == 0 ? "no response within the hook budget" : $"HTTP {statusCode}";

    internal async Task<bool> ShouldSuppressCaptureAsync(
            string? canonicalSessionId, string body, string? command, Profile? activeProfile, HookBudget budget) {
        if (canonicalSessionId is not null && DisabledSessions.IsDisabled(canonicalSessionId, config)) {
            if (command == "session-end") DisabledSessions.RemoveMarker(canonicalSessionId, config);
            return true;
        }

        return command is not null
            && await IsSessionExcludedAsync(activeProfile, body, budget);
    }

    internal async Task<bool> IsSessionExcludedAsync(Profile? profile, string body, HookBudget budget) {
        if (profile?.ExcludedRepos is { Length: > 0 } repos
         && await RepoExclusion.IsExcludedAsync(config, body, repos, budget.Remaining)) {
            return true;
        }

        if (profile?.ExcludedPaths is { Length: > 0 } paths) {
            try {
                var cwd = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (PathExclusion.IsExcluded(cwd, paths, home)) return true;
            } catch {
                // Best effort
            }
        }

        return false;
    }

    internal async Task<int> HandleCore(HttpClient client, AuthStatus authStatus, HookSpool spool,
        TextReader stdin,
        TextWriter? stdout = null) {
        // Hook stdout (the SessionStart hookSpecificOutput envelope / systemMessage nudge) goes to the
        // injected writer when provided, else the process Console. Injecting a writer lets tests capture
        // it WITHOUT redirecting the process-global Console.Out — so a concurrently-running test writing
        // to Console can't leak into the capture (the flake this fixes).
        var writer = stdout ?? Console.Out;
        var body = await stdin.ReadToEndAsync();

        var eventName = ExtractEventName(body);
        string? nativeSessionId = null;
        try { nativeSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>(); } catch { }

        if (eventName is null) {
            Console.Error.WriteLine("kcap hook --claude: missing hook_event_name in payload");
            return 1;
        }

        // Normalize Claude's PascalCase event names to the kebab-case server
        // route convention (`SessionStart` → `session-start`).
        var command = ToKebab(eventName);

        // Taken out here, where the event is finally known. Its own rather than the caller's: this is
        // an entry point in its own right, and a budget is nothing but a ceiling read off the shared
        // clock, so both name the same deadline.
        var budget = clock.Budget(Ceiling(command));

        // Inject home_dir and agent_host_id into all hook payloads, and normalize IDs.
        try {
            var node = JsonNode.Parse(body);

            if (node is not null) {
                NormalizeGuidField(node, "session_id");
                NormalizeGuidField(node, "agent_id");

                node["home_dir"] = home.Path;

                var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");

                if (agentHostId is not null) {
                    node["agent_host_id"] = agentHostId;
                }

                // Surface 3: attach this machine's harness inventory, session-start only (the
                // injections above apply to every event; the inventory is a session-start signal).
                if (command == "session-start") SessionStartInventory.Stamp(node.AsObject(), config, home);

                body = node.ToJsonString();
            }
        } catch {
            // Best effort — don't fail the hook if JSON parsing fails.
        }

        // Check if session is disabled — skip all server communication.
        try {
            var disabledSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();

            if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId, config)) {
                if (command == "session-end") {
                    DisabledSessions.RemoveMarker(disabledSessionId, config);
                }

                return 0;
            }
        } catch {
            // Best effort — don't fail if JSON parsing fails.
        }

        // PermissionRequest has its own handler path (daemon bridge / fire-and-forget). The
        // repo/path exclusion gates run further below (AFTER this dispatch), so the watcher
        // self-heal — which would start uploading the transcript — must apply them HERE first;
        // otherwise a permission prompt in an excluded project spawns a watcher that
        // session-start intentionally skipped (data leak). Disabled sessions already returned
        // above. The permission record/long-poll itself is unaffected: hosted agents need the
        // decision regardless of exclusion.
        if (command == "permission-request") {
            var permProfile = profiles.Effective;
            var selfHeal    = !await IsSessionExcludedAsync(permProfile, body, budget);

            return await new PermissionRequestCommand(config, profiles, http)
                .Handle(body, selfHeal, stdout);
        }

        // On session-start, clear the last-emitted repo cache so this session always gets a
        // RepositoryDetected event (the dedup cache is per-cwd, but each session needs its own link).
        if (command == "session-start") {
            try {
                var cwdNode = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (cwdNode is not null) {
                    RepositoryDetection.ClearLastEmitted(config, cwdNode);
                }
            } catch {
                // Best effort
            }
        }

        // Enrich hook payloads with repository info.
        // For session-start, session-end and subagent-stop, defer enrichment so a slow git/gh
        // probe never delays transcript-capture start (watcher-FIRST): session-end/subagent-stop
        // run it in parallel with the watcher kill, and session-start awaits it INSIDE its block
        // after EnsureWatcherRunning. Other commands enrich inline.
        Task<string>? deferredRepoTask = null;

        // detectPullRequest:false everywhere: a live `gh pr view` / `glab` round-trip (~600ms to
        // GitHub) is the single biggest client cost on the hook path and would push the facts
        // envelope past Claude's 5s SessionStart timeout. PR info is not needed here — the watcher
        // runs its own DetectRepositoryAsync (with PR detection) and backfills it independently.
        if (command == "session-start") {
            // Awaited INSIDE the session-start block after EnsureWatcherRunning so it never delays
            // transcript-capture start.
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(config, body, budget.Remaining, detectPullRequest: false);
        } else if (command is "session-end" or "subagent-stop") {
            // Budgeted so a slow git probe can't push the bounded POST/spool path past the hook
            // deadline. The await below is also budget-bounded as a hard backstop.
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(config, body, budget.Remaining, detectPullRequest: false);
        } else {
            body = await RepositoryDetection.EnrichWithRepositoryInfo(config, body, detectPullRequest: false);
        }

        // Resolve the V2 profile once for repo/path exclusion and
        // default_visibility injection. Reading these off the legacy top-level
        // LegacyV1Config silently misses v2 settings (the fields live under
        // the active profile), so per-profile `excluded_repos` / `private`
        // visibility were being ignored.
        var activeProfile = profiles.Effective;

        // Silently exit for excluded repos/paths (see IsSessionExcludedAsync).
        if (await IsSessionExcludedAsync(activeProfile, body, budget)) {
            return 0;
        }

        // Auth lapsed: do not POST and do not drain — every request would 401, so the backlog just
        // waits for the login. Exit cleanly (0) so Claude shows no per-turn error banner; nudge once on
        // session-start via a systemMessage (shown to the user, not injected into the model context).
        if (authStatus is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
            if (command == "session-start") {
                var notice = new JsonObject {
                    ["systemMessage"] = AuthRejectionNotice.RecordingNotice(
                        AuthRejectionNotice.FromAuthStatus(authStatus))
                };
                writer.WriteLine(notice.ToJsonString());
            }
            return 0;
        }

        // Drain stranded lifecycle events before handling the fresh one. Current session
        // first so a stranded session-start replays before this session's session-end.
        try {
            var drainBudget = TimeSpan.FromMilliseconds(Math.Min(2000, budget.Remaining.TotalMilliseconds));
            var curSid      = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
            if (drainBudget > TimeSpan.Zero)
                await spool.DrainAllAsync(curSid, ClaudePoster(client, drainBudget), drainBudget, CancellationToken.None);
        } catch { /* fail-open */ }

        // default_visibility and plan_content injection for session-start happen INSIDE the
        // session-start block below, after EnsureWatcherRunning and the deferred repo enrichment
        // await, so the watcher (transcript capture) is never delayed by them or by a slow probe.

        // For session-end and subagent-stop: kill watcher BEFORE posting hook
        // so transcript is fully drained before server computes stats.
        switch (command) {
            case "session-end": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null) {
                        // Clamp the pre-drain cap so it cannot consume the entire remaining budget
                        // that the bounded POST needs. Use whichever is smaller.
                        var remaining     = budget.Remaining;
                        var effectiveCap  = TimeSpan.FromMilliseconds(
                            Math.Min(PreHookDrainCap.TotalMilliseconds, remaining.TotalMilliseconds));

                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await _watchers.KillWatcher(sessionId);

                                if (transcriptPath is not null) {
                                    await _watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null);
                                }
                            },
                            effectiveCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] session-end pre-drain cap ({effectiveCap.TotalSeconds:0.#}s) elapsed; proceeding to POST. "
                              + $"Transcript tail may be incomplete — recoverable via: kcap import --session {sessionId}"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] session-end pre-hook failed: {ex.Message}");
                }

                body = await AwaitEnrichmentWithinBudget(deferredRepoTask!, body, budget.Remaining);

                break;
            }
            case "subagent-stop": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var agentId        = node?["agent_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null && agentId is not null) {
                        // Clamp the pre-drain cap so it cannot consume the entire remaining budget
                        // that the bounded POST needs (mirrors the session-end fix). Reserve at
                        // least Safety (1.5s) for the bounded POST so a slow drain doesn't starve
                        // it entirely. Use whichever is smallest.
                        var remaining    = budget.Remaining;
                        var effectiveCap = TimeSpan.FromMilliseconds(
                            Math.Max(0, Math.Min(PreHookDrainCap.TotalMilliseconds,
                                remaining.TotalMilliseconds - HookBudget.Safety.TotalMilliseconds)));

                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await _watchers.KillWatcher($"{sessionId}-{agentId}");

                                if (transcriptPath is not null) {
                                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                                    await _watchers.InlineDrainAsync(sessionId, agentTranscriptPath, agentId);
                                }
                            },
                            effectiveCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] subagent-stop pre-drain cap ({effectiveCap.TotalSeconds:0.#}s) elapsed; proceeding to POST"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] subagent-stop pre-hook failed: {ex.Message}");
                }

                body = await AwaitEnrichmentWithinBudget(deferredRepoTask!, body, budget.Remaining);

                break;
            }
        }

        // Dedicated bounded path for session-start: spawn the watcher FIRST (transcript capture
        // must never be lost even if the POST fails), then a single bounded POST, spool on
        // transient failure, and emit the context envelope + plan-content POST on success.
        if (command == "session-start") {
            var startNode      = JsonNode.Parse(body);
            var sessionId      = startNode?["session_id"]?.GetValue<string>();
            var transcriptPath = startNode?["transcript_path"]?.GetValue<string>();
            var sessionCwd     = startNode?["cwd"]?.GetValue<string>();
            var source         = startNode?["source"]?.GetValue<string>();
            var isResumeOrCompact = source is not null &&
                (source.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                 source.Equals("compact", StringComparison.OrdinalIgnoreCase));

            // 1. Capture never lost: spawn the watcher before any slow git/gh/POST.
            //    Idempotent — safe to call even if the POST subsequently fails.
            if (sessionId is not null && transcriptPath is not null) {
                await _watchers.EnsureWatcherRunning(sessionId, transcriptPath,
                    agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
            }

            // Opt-in background skills refresh: detached and never awaited (the hook's latency
            // budget must not pay for a sync); the child throttles itself off the manifest.
            if (activeProfile?.Skills?.AutoSync == true && sessionCwd is not null)
                SkillsAutoSync.SpawnDetached(sessionCwd);

            // Now that the watcher is running, await the deferred repo enrichment (a slow git/gh
            // probe could not have delayed capture start) and then inject default_visibility +
            // plan_content onto the enriched body before the POST.
            body = await deferredRepoTask!;

            // best-effort git-root discovery for the session's cwd, fed to the server's
            // plan-artifact discovery so a repo-file plan/spec found at the workspace root can be
            // attributed even when cwd is a subdirectory. Fail-open: GitRepository.FindRoot swallows
            // I/O errors and returns null when no repo is found, in which case the field is simply
            // omitted (older servers ignore unknown fields regardless).
            var repoRoot = sessionCwd is null ? null : GitRepository.FindRoot(sessionCwd);

            if (repoRoot is not null) {
                try {
                    var node = JsonNode.Parse(body);

                    if (node is not null) {
                        node["workspace_root"] = repoRoot;
                        body                    = node.ToJsonString();
                    }
                } catch {
                    // Best effort
                }
            }

            // A degradation must reach the user, so it rides whatever this arm writes to stdout below.
            var policyNotice = sessionId is null ? null : FreezePolicySnapshot(sessionId, repoRoot);

            // Inject default_visibility from the active V2 profile. The legacy top-level
            // LegacyV1Config.DefaultVisibility shape is not populated by v2 configs (the field
            // lives under the profile), so reading it there silently fell back to "org_public"
            // and ignored per-profile `private` settings.
            if (activeProfile?.DefaultVisibility is { } vis) {
                try {
                    var node = JsonNode.Parse(body);

                    if (node is not null) {
                        node["default_visibility"] = vis;
                        body                       = node.ToJsonString();
                    }
                } catch {
                    // Best effort
                }
            }

            // Read plan file if slug is known and inject plan_content into payload.
            var planContentInjected = false;

            try {
                var node = JsonNode.Parse(body);
                var slug = node?["slug"]?.GetValue<string>();

                if (slug is not null) {
                    var planContent = ReadPlanFile(slug, ClaudeHarness.FromEnvironment(home).Paths);

                    if (planContent is not null) {
                        node!["plan_content"] = planContent;
                        body                  = node.ToJsonString();
                        planContentInjected   = true;
                    }
                }
            } catch {
                // Best effort
            }

            // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
            // session-start so a stranded session-start always reaches the server first.
            if (CurrentSessionHasBacklog(spool, sessionId)) {
                if (sessionId is not null) {
                    spool.Append(sessionId, "session-start", body);
                    await Console.Error.WriteLineAsync($"[kcap] session-start spooled (ordering guard); will retry on the next kcap hook ({sessionId})");
                }
                WriteSessionStart(writer, null, policyNotice);
                return 0;
            }

            // Advertise the coordination-notices capability so the server MAY return work-overlap
            // notices to render below (next to the memory index). Injected into a SEPARATE postBody,
            // never `body`: `body` is what the transient-failure and ordering-guard paths spool, and a
            // replay is a catch-up, not a live render — a spooled capability would let the server mark
            // notices delivered that the replay can never inject (they stay in the bell/Slack and reach
            // the next LIVE session-start instead). Live-only by construction: `kcap import` posts
            // /hooks/session-start/{vendor} with origin=historical and never reaches here. Suppressed by
            // the disable_coordination_notices opt-out, read from the EFFECTIVE profile (honoured for
            // KCAP_URL users too, unlike the memory read above). Fail-open.
            var coordinationNoticesDisabled = activeProfile?.DisableCoordinationNotices is true;
            var postBody = body;
            if (!coordinationNoticesDisabled) {
                try {
                    var node = JsonNode.Parse(body);
                    if (node is not null) {
                        node["coordination_notices"] = CoordinationNoticesEmitter.CapabilityVersion;
                        postBody                      = node.ToJsonString();
                    }
                } catch {
                    // Best effort — never fail the hook building the capability field.
                }
            }

            // kick off the team-memory index fetch in PARALLEL with the hook POST so
            // it adds no latency to the critical path. Fully best-effort / fail-open: any failure,
            // a 401, or a budget overrun yields a null fragment and nothing is injected. Started
            // after the ordering-guard / backlog returns above so a spooled session-start doesn't
            // pay for a fetch it won't use.
            var memoryDisabled = profiles.Resolution.Profile?.DisableMemoryIndex is true;
            var lifecycleReason = source?.ToLowerInvariant() switch {
                "resume" => SessionLifecycleReason.Resume,
                "reopen" => SessionLifecycleReason.Reopen,
                "fork" => SessionLifecycleReason.Fork,
                "compact" => SessionLifecycleReason.Compact,
                _ => SessionLifecycleReason.New
            };
            var memoryIndexTask = StartMemoryIndexTask(nativeSessionId, sessionCwd, memoryDisabled, lifecycleReason, budget.Remaining);

            // 2. Single bounded POST — keep resp alive to read the response body for the
            //    context-envelope emission and plan-content POST on success.
            var remaining = budget.Remaining;
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    // postBody carries the coordination-notices capability; the spool below uses the
                    // capability-free `body` so a replay never claims notices it cannot render.
                    using var content = new StringContent(postBody, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{Url}/hooks/session-start", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var code      = resp is null ? 0 : (int)resp.StatusCode;
                var retryable = resp is null || HookSpool.IsRetryable(code);
                resp?.Dispose();
                if (retryable && sessionId is not null) {
                    await ReportSpoolAsync(spool.Append(sessionId, "session-start", body),
                                           "session-start", sessionId, FailureReason(code), unusableUrl: false);
                }

                // The envelope below is built only from a 2xx body, so this is the arm's only
                // stdout write — without it the start event is dropped in silence.
                var rejection = code == 401
                    ? new JsonObject { ["systemMessage"] = AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid) }.ToJsonString()
                    : null;
                WriteSessionStart(writer, rejection, policyNotice);

                return 0;
            }

            // resp is 2xx — read the body ONCE for the envelope + plan-content emission.
            JsonNode? responseNode = null;
            try {
                var responseBody = await resp.Content.ReadAsStringAsync();
                responseNode = JsonNode.Parse(responseBody);
            } catch {
                // Best effort — envelope is optional; don't fail the hook.
            }

            // Plan-content POST from response-resolved slug (only if not already injected).
            if (responseNode is not null && !planContentInjected && sessionId is not null) {
                try {
                    var resolvedSlug = responseNode["slug"]?.GetValue<string>();

                    if (resolvedSlug is not null) {
                        var planContent = ReadPlanFile(resolvedSlug, ClaudeHarness.FromEnvironment(home).Paths);

                        if (planContent is not null) {
                            await PostPlanContentAsync(client, Url, sessionId, planContent);
                        }
                    }
                } catch {
                    // Best effort
                }
            }

            // Context-envelope emission (lessons/version-nudge).
            string? envelope = null;

            if (responseNode is not null) {
                try {
                    // The EFFECTIVE profile (the `activeProfile` resolved above), not
                    // profiles.Resolution.Profile, which is null whenever --server-url or KCAP_URL
                    // wins — so the resolution-only read silently ignored disable_session_guidelines
                    // for every KCAP_URL user (the same defect the memory adapters already fixed).
                    // Scoped to guidelines here; the memory read above keeps its existing behaviour.
                    var disabled        = activeProfile?.DisableSessionGuidelines is true;
                    var lessonsFragment = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled);
                    // update_check=false opts out of ALL kcap update nudging, including the
                    // in-agent one — skip emission entirely rather than let a server that still
                    // sends `version` sneak the fragment past a locally-disabled preference.
                    var updateCheckOff  = profiles.Resolution.Profile?.UpdateCheck is false;
                    var nudgeFragment   = updateCheckOff
                        ? null
                        : VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.CurrentDisplay());
                    // join the parallel memory-index fetch, bounded by the remaining
                    // hook budget so a slow fetch can't delay the hook (fail-open → null).
                    var memoryFragment = await AwaitMemoryFragmentAsync(memoryIndexTask, budget);

                    // Coordination notices ride the hook POST response (no extra fetch), same as the
                    // guidelines fragment. Gated on the same opt-out that suppressed the capability
                    // above: when disabled the server was never asked and returns nothing, but the
                    // gate is defense-in-depth so the opt-out holds even against an over-eager server.
                    var coordinationFragment = CoordinationNoticesEmitter.BuildFragment(
                        responseNode, coordinationNoticesDisabled);

                    // The static work-items nudge. Claude has always carried kcap-workitems, so
                    // the availability gate is always satisfied here; only the opt-out can suppress it.
                    var workItemsNudge = WorkItemsNudgeEmitter.Resolve(
                        HarnessId.Claude, sessionId, activeProfile?.DisableWorkItemsNudge is true, home);
                    var harnessNudge = HarnessNudgeEmitter.ResolveFragmentForHook(activeProfile?.DisableHarnessNudge is true, config, home);

                    envelope = SessionStartAdditionalContext.BuildEnvelope(
                        lessonsFragment, nudgeFragment, memoryFragment, coordinationFragment, workItemsNudge, harnessNudge);
                } catch {
                    // Best effort — never break session capture for hook output emission.
                }
            }

            resp.Dispose();

            WriteSessionStart(writer, envelope, policyNotice);

            return 0;
        }

        // Dedicated bounded POST for session-end: single attempt clamped to the remaining
        // hook budget, spools on transient failure, and checks generate_whats_done on success.
        // Other commands continue through the shared PostWithRetryAsync path below.
        if (command == "session-end") {
            // Parse once: stamp ended_at and extract sessionId in a single pass.
            string? sessionId = null;
            try {
                var node = JsonNode.Parse(body);
                sessionId = node?["session_id"]?.GetValue<string>();
                if (node is not null) {
                    // Stamped before the body is frozen so a spooled replay carries the count too.
                    if (sessionId is not null) StampPassThroughCount(node, sessionId);
                    node["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
                    body             = node.ToJsonString();
                }
            } catch { }

            // The session is over and the server holds the uploaded snapshot, so nothing here is
            // read again — and session-end is the only thing that evicts these directories.
            if (sessionId is not null) EvictPolicyState(sessionId);

            // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
            // session-end so a stranded session-start always reaches the server before it.
            if (CurrentSessionHasBacklog(spool, sessionId)) {
                if (sessionId is not null) {
                    spool.Append(sessionId, "session-end", body);
                    await Console.Error.WriteLineAsync($"[kcap] session-end spooled (ordering guard); will retry on the next kcap hook ({sessionId})");
                }
                return 0;
            }

            var remaining  = budget.Remaining;
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{Url}/hooks/session-end", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var code      = resp is null ? 0 : (int)resp.StatusCode;
                var retryable = resp is null || HookSpool.IsRetryable(code);
                resp?.Dispose();
                if (retryable) {
                    if (sessionId is not null) {
                        await ReportSpoolAsync(spool.Append(sessionId, "session-end", body),
                                               "session-end", sessionId, FailureReason(code), unusableUrl: false);
                    } else {
                        await Console.Error.WriteLineAsync("[kcap] session-end transient failure but session_id missing — cannot spool; event dropped");
                    }
                }
                return 0;
            }

            try {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node?["generate_whats_done"]?.GetValue<bool>() == true && sessionId is not null)
                    _watchers.SpawnWhatsDoneGenerator(sessionId);
            } catch { }
            resp.Dispose();
            return 0;
        }

        // Dedicated bounded POST for the per-agent subagent-stop: a single attempt clamped to the
        // remaining hook budget that spools on transient failure, so a dropped SubagentCompleted is
        // replayed on the next hook. Only the stop carrying agent_id maps to a completion;
        // without it, fall through to the shared best-effort path (behavior unchanged).
        if (command == "subagent-stop") {
            string? sessionId = null, agentId = null;
            try {
                var node  = JsonNode.Parse(body);
                sessionId = node?["session_id"]?.GetValue<string>();
                agentId   = node?["agent_id"]?.GetValue<string>();
            } catch { }

            if (sessionId is not null && agentId is not null) {
                // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
                // subagent-stop so a stranded session-start reaches the server before it.
                if (CurrentSessionHasBacklog(spool, sessionId)) {
                    spool.Append(sessionId, "subagent-stop", body);
                    await Console.Error.WriteLineAsync($"[kcap] subagent-stop spooled (ordering guard); will retry on the next kcap hook ({sessionId}/{agentId})");
                    return 0;
                }
                var remaining = budget.Remaining;
                HttpResponseMessage? resp = null;
                try {
                    if (remaining > TimeSpan.Zero) {
                        using var content = new StringContent(body, Encoding.UTF8, "application/json");
                        resp = await client.PostOnceAsync($"{Url}/hooks/subagent-stop", content, remaining, CancellationToken.None);
                    }
                } catch { resp = null; }

                if (resp is null || !resp.IsSuccessStatusCode) {
                    var code      = resp is null ? 0 : (int)resp.StatusCode;
                    var retryable = resp is null || HookSpool.IsRetryable(code);
                    resp?.Dispose();
                    if (retryable) {
                        await ReportSpoolAsync(spool.Append(sessionId, "subagent-stop", body),
                                               "subagent-stop", $"{sessionId}/{agentId}", FailureReason(code), unusableUrl: false);
                    }
                    return 0;
                }

                resp.Dispose();
                return 0;
            }
        }

        // The turn is over, so its pending asks expire with it: a decision journalled for one turn
        // must never answer an identical call in the next.
        if (command == "stop") {
            try {
                var stopSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
                if (stopSessionId is not null) new PolicyDecisionJournal(config).ClearTurn(stopSessionId);
            } catch { }
        }

        using var sharedContent = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try {
            response = await client.PostWithRetryAsync($"{Url}/hooks/{command}", sharedContent);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(Url, ex);

            return 1;
        }

        if (!response.IsSuccessStatusCode) {
            var code = (int)response.StatusCode;
            response.Dispose();

            // Exit 0 is deliberate: any non-zero exit renders as Claude's opaque hook-error banner
            // instead of the notice. `stop` only — `notification` fires per permission prompt and
            // would stack duplicates within one turn.
            if (code == 401) {
                if (command == "stop") {
                    writer.WriteLine(new JsonObject { ["systemMessage"] = AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid) }.ToJsonString());
                }

                return 0;
            }

            Console.Error.WriteLine($"HTTP {code}");

            return 1;
        }

        switch (command) {
            case "subagent-start": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var agentId        = node?["agent_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                if (sessionId is not null && agentId is not null && transcriptPath is not null) {
                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                    await _watchers.EnsureWatcherRunning($"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
                }

                break;
            }
            case "notification" or "stop": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();
                var sessionCwd     = node?["cwd"]?.GetValue<string>();

                if (sessionId is not null && transcriptPath is not null) {
                    await _watchers.EnsureWatcherRunning(sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// Freezes the session's approval policy at session start rather than at the first tool call, so
    /// an edit landing mid-session cannot change what governs a session already under way. Returns
    /// the degradation notice the user must see, or null when there is nothing to say.
    /// </summary>
    string? FreezePolicySnapshot(string sessionId, string? repoRoot) {
        try {
            var snapshot = new PolicySnapshotStore(config).LoadOrBuild(sessionId, repoRoot);

            return snapshot.Degraded && snapshot.Degradations.Count > 0
                ? $"[kcap] approval policy degraded: {snapshot.Degradations[0]}"
                : null;
        } catch {
            // Best effort — a policy hiccup never fails the hook.
            return null;
        }
    }

    /// <summary>
    /// Writes the session-start arm's single stdout object, folding a policy-degradation notice
    /// into whatever the flow produced. One object, never two: Claude parses the hook's stdout as
    /// one value, so a notice written beside an envelope would cost the reader both.
    /// </summary>
    static void WriteSessionStart(TextWriter writer, string? json, string? policyNotice) {
        if (policyNotice is null) {
            if (json is not null) writer.WriteLine(json);

            return;
        }

        JsonObject? merged;
        try { merged = json is null ? new JsonObject() : JsonNode.Parse(json) as JsonObject; }
        catch { merged = null; }

        if (merged is null) {
            writer.WriteLine(json);

            return;
        }

        merged["systemMessage"] = merged["systemMessage"] is JsonValue existing && existing.TryGetValue<string>(out var text)
            ? $"{text}\n{policyNotice}"
            : policyNotice;

        writer.WriteLine(merged.ToJsonString());
    }

    /// <summary>Records how many tool calls ran past the policy engine unmatched, and resets the
    /// counter so a resumed session never re-reports them.</summary>
    void StampPassThroughCount(JsonNode node, string sessionId) {
        try {
            var count = new PolicyDecisionJournal(config).TakePassThroughCount(sessionId);
            if (count > 0) node["policy_pass_through_count"] = count;
        } catch { }
    }

    /// <summary>Drops the session's snapshot, journal and snapshot-upload markers. Best effort
    /// throughout: a file that will not delete costs disk, never the hook.</summary>
    void EvictPolicyState(string sessionId) {
        var key = PolicySnapshotStore.Sanitize(sessionId);
        TryDelete(config.Path("policy", "sessions", $"{key}.json"));
        TryDelete(config.Path("policy", "journal", $"{key}.json"));

        try {
            var uploaded = config.Path("policy", "uploaded");

            if (Directory.Exists(uploaded)) {
                // The same sanitized key the emitter names the marker with — the raw id can carry a
                // separator that would put the file somewhere this prefix never sees. Matched by
                // prefix rather than a glob because the key is followed by a snapshot-id suffix.
                foreach (var marker in Directory.EnumerateFiles(uploaded)) {
                    if (Path.GetFileName(marker).StartsWith($"{key}-", StringComparison.Ordinal)) TryDelete(marker);
                }
            }
        } catch { }

        static void TryDelete(string path) {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Extracts <c>hook_event_name</c> from a hook payload. Returns null if
    /// the payload is malformed or the field is missing. Best-effort —
    /// errors are swallowed so a malformed payload doesn't crash the CLI.
    /// </summary>
    static string? ExtractEventName(string body) {
        try {
            return JsonNode.Parse(body)?["hook_event_name"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a hook event name to kebab-case: <c>SessionStart</c>,
    /// <c>session_start</c>, and <c>session-start</c> all return
    /// <c>session-start</c>.
    /// </summary>
    static string ToKebab(string s) {
        if (string.IsNullOrEmpty(s)) return s;

        var sb = new StringBuilder(s.Length + 4);

        for (var i = 0; i < s.Length; i++) {
            var c = s[i];

            if (c == '_') {
                sb.Append('-');
            } else if (char.IsUpper(c)) {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            } else {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = node[fieldName]?.GetValue<string>();

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    async Task<string?> StartMemoryIndexTask(
        string? nativeSessionId,
        string? cwd,
        bool disabled,
        SessionLifecycleReason reason,
        TimeSpan budget) {
        if (disabled || string.IsNullOrEmpty(nativeSessionId) || budget <= TimeSpan.Zero)
            return null;

        // The memory subsystem is optional, and the whole fetch stays inside the fail-open boundary.
        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = new SessionStartMemoryContextProvider(
                new SessionStartMemoryScopeResolver(config), http.ForBackgroundAsync);

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Claude, nativeSessionId, null,
                    IsTopLevel: true, ClassificationAuthoritative: true, reason,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, cwd, disabled, budget, CancellationToken.None));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    static async Task<string?> AwaitMemoryFragmentAsync(Task<string?> task, HookBudget budget) {
        try {
            var remaining = budget.Remaining;
            if (remaining <= TimeSpan.Zero) return task.IsCompletedSuccessfully ? task.Result : null;
            return await task.WaitAsync(remaining);
        } catch { return null; }
    }

    static string? ReadPlanFile(string slug, ClaudePaths paths) {
        var planPath = Path.Combine(paths.Plans, $"{slug}.md");

        try {
            return File.Exists(planPath) ? File.ReadAllText(planPath) : null;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] Failed to read plan file at {planPath}: {ex.Message}");

            return null;
        }
    }

    static async Task PostPlanContentAsync(HttpClient httpClient, string url, string sessionId, string planContent) {
        var       obj         = new JsonObject { ["plan_content"] = planContent };
        using var planPayload = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        await httpClient.PostWithRetryAsync($"{url}/api/sessions/{sessionId}/plan", planPayload);
    }

    /// <summary>
    /// Returns a poster closure that POSTs a spooled entry to the server and maps the response
    /// to a <see cref="DrainOutcome"/>. On a successful <c>session-end</c> replay, handles the
    /// <c>generate_whats_done</c> side effect so it is not lost.
    /// </summary>
    Func<string, string, Task<DrainOutcome>> ClaudePoster(HttpClient client, TimeSpan perAttempt) =>
        async (route, body) => {
            try {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp    = await client.PostOnceAsync($"{Url}/hooks/{route}", content, perAttempt, CancellationToken.None);
                if (!resp.IsSuccessStatusCode) return HookSpool.OutcomeOf((int)resp.StatusCode);
                if (route == "session-end") {
                    try {
                        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                        var sid  = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
                        if (node?["generate_whats_done"]?.GetValue<bool>() == true && sid is not null)
                            _watchers.SpawnWhatsDoneGenerator(sid);
                    } catch { }
                }
                return DrainOutcome.Delivered;
            } catch { return DrainOutcome.TransientStop; }
        };

    /// <summary>
    /// Returns true if the given session still has undelivered spool entries. Used as an ordering
    /// guard so a stranded session-start always reaches the server before its session-end.
    ///
    /// <para>Delegates to the public <see cref="HookSpool.HasBacklog"/> rather than
    /// re-implementing the file checks: the ordered drain (now running on every non-Codex hook,
    /// including <c>--claude</c>) can WITHHOLD a spooled session-end in the <c>.ordered-*</c> temp
    /// namespace pending the transcript tail. A stale private check that only looked at
    /// <c>{sid}.jsonl</c> / <c>{sid}.*.draining</c> would miss that withheld terminal and let a later
    /// Claude hook (e.g. subagent-stop) post directly, AHEAD of the still-withheld session-end —
    /// the exact cross-spool ordering violation Blockers 1/3 exist to prevent. <c>HasBacklog</c>
    /// covers all three namespaces; <see cref="CursorHookCommand"/> already routes through it.</para>
    /// </summary>
    static bool CurrentSessionHasBacklog(HookSpool spool, string? sid) =>
        sid is not null && spool.HasBacklog(sid);
}
