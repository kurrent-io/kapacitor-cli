using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Single-binary dispatcher for Cursor hooks. Cursor invokes the same
/// command for every hook event with <c>hook_event_name</c> in the JSON
/// payload, so we collapse the 8 event handlers behind one CLI entry
/// point. Mirrors <see cref="CodexHookCommand"/>'s shape but adds a
/// shared 2-second wall-clock budget, a per-session canonical-event
/// spool, and a watermark-driven transcript-line backfill.
/// </summary>
public sealed class CursorHookCommand(ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);
    readonly CursorMarkers  _markers  = new(config);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>2s of work is what kcap promises Cursor per hook so it never blocks the agent loop
    /// — its own promise, not a Cursor timeout, which is why it is written as the
    /// work plus the reserve <see cref="HookBudget.Remaining"/> takes back off it. One ceiling for
    /// every Cursor event: the budget is armed before the payload is parsed.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(2) + HookBudget.Safety;

    static readonly TimeSpan HookPostTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Production entry point. Delegates straight to <see cref="HandleInternal"/> with
    /// production factories — see that method's doc for how the single hard-cap deadline
    /// (review finding 1) covers client/auth setup through dispatch.
    /// </summary>
    public Task<int> Handle(TextReader stdin) => HandleInternal(stdin);

    /// <summary>
    /// Test seam for a bare hard-cap race over an arbitrary <see cref="Task{TResult}"/>. Kept
    /// as a generic, independently-tested utility (mirrors <c>ClaudeHookCommand.WithHardCap</c>,
    /// itself unused in that class's production path) — NOT used by <see cref="Handle"/>/
    /// <see cref="HandleInternal"/>, which own their own single deadline race end-to-end (see
    /// review finding 1: wrapping <em>another</em>, independent cap around an
    /// already-self-capping inner phase created two competing timers aimed at the same
    /// deadline, and the outer one — started earlier, before client/auth setup — could win
    /// without knowing whether a {} was owed, while the abandoned inner still held the sole
    /// stdout handle and could write late).
    /// </summary>
    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        if (winner != inner) return 0;
        return await inner;
    }

    /// <summary>
    /// The single hard-cap deadline for the ENTIRE dispatch — client/auth setup through the
    /// bounded async stdin read, recording-critical work, and memory (review finding 1).
    /// There is exactly one race here: client/auth creation is bounded by its own
    /// <see cref="Task.WhenAny(Task, Task)"/> against what this hook's budget has left (some
    /// <c>TokenStore</c> paths don't honour a <see cref="CancellationToken"/> and would
    /// otherwise sit on the default 100 s <see cref="HttpClient"/> timeout — this is the ONE
    /// place that step can be abandoned), and — ONLY once that step has resolved within
    /// budget — <see cref="HandleCore"/> is invoked with whatever's left, where its OWN
    /// (unchanged, already-correct) internal race becomes the sole remaining decision-maker and
    /// sole stdout writer for the rest of the dispatch. The two races are sequential, never
    /// concurrent: HandleCore is never invoked until the auth race has already resolved, so
    /// there is never a second, independent timer still live once HandleCore's own begins —
    /// eliminating the pre-fix "two nested 2s caps that don't order" bug. On EITHER branch
    /// timing out, the abandoned task never gets a chance to write (this method never invokes
    /// HandleCore for a still-pending auth attempt, and disposes/observes it in the background).
    /// </summary>
    internal Task<int> HandleInternal(TextReader stdin) =>
        HandleWithDeps(stdin,
            http.ForHookAsync,
            () => {
                var s = new HookSpool(config);
                MigrateLegacyCursorSpool(s, CursorHarness.FromEnvironment(home).Paths.SpoolDir);
                s.ReapOlderThan(TimeSpan.FromDays(30));
                return s;
            });

    /// <summary>
    /// The dispatch itself, with its deadline and its two factories handed in. Separate from
    /// <see cref="HandleInternal"/> so the guard below can be proven to run BEFORE a client is ever
    /// built — a factory that throws is the only way to show non-entry, since every effect of the
    /// guard is indistinguishable from the fail-open catch that would otherwise swallow it.
    /// </summary>
    internal async Task<int> HandleWithDeps(
            TextReader stdin,
            Func<CancellationToken, Task<AuthAttempt>> clientFactory,
            Func<HookSpool> spoolFactory
        ) {
        // At this point the event kind and session id are not yet parsed, so there is nothing to spool
        // and no way to know which stdout contract to write. Match Cursor's own auth-abandon arm: no
        // spool, no stdout, exit 0 — but still write the diagnostic, which needs only the URL.
        if (!HookHttp.IsPostable(Url)) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, "cursor hook skipped"));
            return 0;
        }

        var budget = clock.Budget(Ceiling);

        using var cts = budget.CancelAtCeiling();
        HttpClient? client = null;
        try {
            // The hook verb, so a lapse writes nothing to stderr. On a lapse, skip HandleCore
            // entirely: every POST would 401, so the backlog waits intact for the user to
            // re-run `kcap login`, and exit cleanly. Mirrors the Claude hook; kcap status
            // surfaces the expired state. Cursor has no user-facing notice channel.
            //
            // Bounded by its OWN Task.WhenAny against the full budget (not merely the linked
            // CancellationToken passed into it) — the one place client creation can be
            // abandoned if some TokenStore path ignores cancellation. HandleCore is only ever
            // reached once this resolves, so its own internal race never has a stale competing
            // timer left over from this step.
            var authTask   = clientFactory(cts.Token);
            var authWinner = await Task.WhenAny(authTask, budget.CeilingReached());
            if (authWinner != authTask) {
                // Abandoned: observe the eventual terminal state so a late fault/dispose
                // doesn't leak a client or surface as an UnobservedTaskException. No output is
                // written — the event kind isn't even known yet at this point — and HandleCore
                // is never invoked for this attempt, so a late write is structurally impossible.
                _ = authTask.ContinueWith(static t => {
                    if (t.IsFaulted) _ = t.Exception;
                    else if (t.Status == TaskStatus.RanToCompletion) t.Result.Client.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return 0;
            }

            var (c, status) = await authTask;
            client = c;
            if (AgentHookPoster.IsAuthLapsed(status)) return 0;

            var spool = spoolFactory();
            // No entry guard on the budget: the ceiling is measured from process entry, so a slow
            // resolve or spool drain can spend it before dispatch, and returning here would drop
            // the event outright. HandleCore's own per-step gates spool it instead of posting.
            return await HandleCore(client, stdin, spool);
        } catch {
            // Fail-open contract: never crash Cursor. Covers auth timeout,
            // unreachable server, malformed config, etc.
            return 0;
        } finally {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Test-friendly core. Caller owns the <see cref="HttpClient"/> and
    /// <see cref="HookSpool"/>. Races the entire inner phase against this hook's one
    /// budget and is the SOLE writer of
    /// Cursor's stdout — the inner phase (<see cref="HandleCoreInner"/>) never touches
    /// <see cref="Console.Out"/>, it only computes and returns the response. Exactly one
    /// write happens per resolved <c>sessionStart</c>; every other resolved event, and any
    /// unresolved/malformed input, writes nothing — byte-for-byte unchanged from before
    /// this feature landed.
    /// </summary>
    internal async Task<int> HandleCore(
            HttpClient client,
            TextReader stdin,
            HookSpool  spool
        ) {
        // Its own budget rather than the caller's: this is an entry point in its own right, and a
        // budget is nothing but a ceiling read off the shared clock, so both name the same deadline.
        var budget = clock.Budget(Ceiling);

        using var cts = budget.CancelAtCeiling();
        var kindSignal = new ResolvedEventKindSignal();

        var inner    = HandleCoreInner(client, stdin, spool, cts.Token, kindSignal, budget);
        var deadline = budget.CeilingReached();
        var winner   = await Task.WhenAny(inner, deadline);

        // On the deadline branch the inner is ABANDONED — never cancelled/awaited BY this
        // method — so it holds no stdout handle and can never produce a late/second write,
        // however long it eventually takes to unwind in the background. It IS, however,
        // explicitly cancelled here: `cts` was constructed with its own `budgetTotal` timer
        // (line above), so its token would likely transition to cancelled around the same
        // wall-clock moment regardless — but that internal timer racing this method's own
        // `using`-disposal at the very next statement is exactly that, a race, not a
        // guarantee. Calling Cancel() deterministically (rather than trusting the timer to
        // have already fired) is what makes the inner's cancellation-aware stdin read /
        // HTTP calls (both bound to cts.Token) actually observe cancellation promptly. This
        // also cancels the linked memory CTS in RunMemoryOrchestrationAsync — a cancelled
        // fetch there leaves the lease uncommitted, which is already the intended, tested
        // behavior (see CancelledFetch_leaves_lease_uncommitted).
        if (winner != inner) cts.Cancel();

        var response = winner == inner
            ? await inner
            : (kindSignal.Kind == "sessionStart" ? SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, null) : null);

        if (response is not null) Console.Write(response);
        return 0;
    }

    /// <summary>
    /// A write-once, thread-safe signal for the resolved <c>hook_event_name</c>, published
    /// the instant <see cref="CursorHookEventMap.TryResolve"/> succeeds — before any
    /// recording/memory work runs. Lets the OUTER deadline branch in <see cref="HandleCore"/>
    /// (and <see cref="HandleCoreInner"/>'s own catch-all, where try-scoped locals are out of
    /// scope) decide whether an abandoned/faulted invocation still owes a <c>sessionStart</c>
    /// its single <c>{}</c> response.
    /// </summary>
    sealed class ResolvedEventKindSignal {
        volatile string? _kind;
        public void Publish(string kind) => _kind = kind;
        public string? Kind => _kind;
    }

    /// <summary>
    /// The actual dispatcher body (formerly the whole of <see cref="HandleCore"/>). NEVER
    /// writes to <see cref="Console.Out"/> — every return path yields the response
    /// <see cref="HandleCore"/> should emit for a resolved <c>sessionStart</c> (the rendered
    /// memory envelope, or <c>{}</c> for every other fail-open path) or <c>null</c> for a
    /// resolved non-<c>sessionStart</c> event / unresolved or malformed input.
    /// </summary>
    async Task<string?> HandleCoreInner(
            HttpClient client,
            TextReader stdin,
            HookSpool  spool,
            CancellationToken ct,
            ResolvedEventKindSignal kindSignal,
            HookBudget budget
        ) {
        bool BudgetExpired() => budget.Remaining <= TimeSpan.Zero;

        try {
            var body = await stdin.ReadToEndAsync(ct);
            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch { return null; }
            if (node is null) return null;

            var eventName = TryGetString(node, "hook_event_name");
            if (string.IsNullOrWhiteSpace(eventName)) return null;
            if (!CursorHookEventMap.TryResolve(eventName, out var mapping)) return null;
            kindSignal.Publish(eventName);

            // Every resolved sessionStart converges on this single response — {} unless
            // Task 3's orchestrator (wired at the very end of this method for the top-level,
            // non-child success path) supplies a Ready fragment instead.
            string? EmptyOrNull() =>
                eventName == "sessionStart" ? SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, null) : null;

            NormalizeGuidField(node, "session_id");
            node["home_dir"] = home.Path;
            var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
            if (agentHostId is not null) node["agent_host_id"] = agentHostId;

            // Surface 3: attach this machine's harness inventory, session-start only.
            if (eventName == "sessionStart") SessionStartInventory.Stamp(node.AsObject(), config, home);

            if (eventName == "afterAgentThought") {
                var sid = TryGetString(node, "session_id") ?? "";
                var gen = TryGetString(node, "generation_id") ?? "";
                var txt = TryGetString(node, "text") ?? "";
                node["canonical_event_id"] = StableThoughtId(sid, gen, txt);
            }

            var sessionId      = TryGetString(node, "session_id");
            var transcriptPath = TryGetString(node, "transcript_path");

            if (sessionId is not null) {
                // Touch the hook heartbeat on every invocation carrying a session id — including
                // telemetry-only hooks — so it reflects "Cursor is still firing hooks" independent
                // of the tailing watcher's own liveness.
                _markers.TouchHeartbeat(sessionId, DateTimeOffset.UtcNow);

                // beforeSubmitPrompt is ordering-sensitive: its server-side effect (queuing an
                // attachment onto the per-session FIFO) must land before transcript-line
                // normalization can consume it. Create the barrier before anything below posts or
                // spools it; cleared on a 2xx from the live POST or a later spool-drain delivery.
                if (eventName == "beforeSubmitPrompt") _markers.CreateBarrier(sessionId, DateTimeOffset.UtcNow);
            }

            if (sessionId is not null && DisabledSessions.IsDisabled(sessionId, config)) return EmptyOrNull();

            // Subagent classification. Built to bring CursorSubagentCorrelator into the live
            // hook path (Cursor is NOT watcher-backed, so it would have to run right here in the
            // per-hook dispatcher), persisting the decision to an on-disk marker because this
            // process exits after every hook call.
            //
            // In practice the two halves have very different lifetimes:
            //  - TryLoadLink below is LIVE. It runs on every event, and a marker persisted by
            //    another surface or an older build still activates the divert.
            //  - The transcript-derived arm that WRITES a marker has NO PRODUCER: it needs both
            //    a sessionStart and a non-empty transcript_path, and a Cursor sessionStart never
            //    carries one. Nor would a subagent child reach it — a child never fires
            //    sessionStart at all.
            // So the "decision made once at the child's own sessionStart" this was designed
            // around never happens. Subagent nesting is delivered by `kcap import --cursor` plus
            // the server-side adoption sweep, over complete transcripts. See
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            string? subagentParentId  = null;
            string? subagentAgentType = null;
            if (sessionId is not null) {
                var marker = CursorLiveSubagentLinker.TryLoadLink(config, sessionId);
                if (marker is { } m) {
                    (subagentParentId, subagentAgentType) = (m.ParentSessionId, m.SubagentType);
                // NO PRODUCER on the measured cursor-agent contract: a sessionStart payload
                // always carries a null transcript_path, so this arm never opens. (The
                // TryLoadLink gate above is NOT inert — it runs on every event and still
                // consumes a marker persisted by another surface or an older build.)
                // Kept as the landing site for a native subagentStart revival; see
                // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
                // for why the trigger must move to the parent's hooks.
                } else if (eventName == "sessionStart" && !string.IsNullOrEmpty(transcriptPath)) {
                    try {
                        var candidates = CursorLiveSubagentLinker.DiscoverSiblingTranscripts(transcriptPath);
                        var link       = CursorLiveSubagentLinker.ResolveParent(sessionId, transcriptPath, candidates);
                        if (link is { } l) {
                            subagentParentId  = l.ParentSessionId;
                            subagentAgentType = string.IsNullOrEmpty(l.SubagentType) ? "task" : l.SubagentType;
                            CursorLiveSubagentLinker.SaveLink(config, sessionId, subagentParentId, subagentAgentType);
                        }
                    } catch {
                        // Fail-open: a locked/unreadable sibling transcript must never abort the
                        // hook. (This arm has no producer anyway — see above. It was also written
                        // to absorb what was believed to be an eventual-consistency gap; per
                        // ResolveParent's doc that framing was wrong, and the parent's Task
                        // tool_use is in fact never available while the child is still running.)
                    }
                }
            }
            var isSubagentChild = subagentParentId is not null;

            // Hoisted (rather than block-local) so the memory orchestration wired in
            // at the end of this method — reached only for the same top-level, non-child
            // sessionStart this block guards — can reuse the RAW workspace_roots[0] value as
            // Cwd without re-deriving it.
            string? workspaceRoot = null;

            // attach a `repository` node on sessionStart so the session groups
            // under its repo in the sidebar. Cursor payloads carry `workspace_roots`
            // rather than `cwd`, so the generic EnrichWithRepositoryInfo (which reads
            // `cwd`) can't be used — detect from workspace_roots[0] instead. Bounded by
            // the remaining dispatcher budget; fail-open (git detection is cached).
            // Skipped for a linked subagent child — its top-level sessionStart is never
            // posted (see below), so there is no session to attach a repository to.
            if (eventName == "sessionStart" && !isSubagentChild) {
                // Stamp the active profile's default visibility BEFORE the enrichment round-trip
                // below re-serializes `node`. Server-side VisibilityService treats a null
                // default_visibility as "org-repo fallback", so without this a private-default
                // user's Cursor sessions silently go org-visible. Budget-bounded + fail-open: the
                // hook must never block the agent loop (the import path stamps this separately).
                try {
                    if (!BudgetExpired()
                     && (profiles.Effective)?.DefaultVisibility is { } defaultVisibility)
                        node["default_visibility"] = defaultVisibility;
                } catch {
                    // fail-open — visibility is best-effort, never fatal to the hook.
                }

                // Safe extract: workspace_roots[0] may be absent or a non-string; GetValue<string>
                // would throw and (via the outer catch) drop the whole sessionStart hook.
                if (node["workspace_roots"] is JsonArray roots && roots.Count > 0
                 && roots[0] is JsonValue wv && wv.TryGetValue<string>(out var wr))
                    workspaceRoot = wr;

                // Task 9: spawn (or heal) this session's tailing Cursor watcher FIRST —
                // live transcript capture must never be lost even if the repo-enrichment call
                // below (or the eventual POST) is slow or fails. Idempotent; a no-op once alive.
                if (sessionId is not null && !string.IsNullOrEmpty(transcriptPath)) {
                    await MaybeSpawnWatcherAsync(sessionId, transcriptPath, workspaceRoot, eventName, isSubagentChild);
                }

                if (!string.IsNullOrEmpty(workspaceRoot)) {
                    var remaining = budget.Remaining;
                    if (remaining > TimeSpan.Zero) {
                        node = JsonNode.Parse(
                            await RepositoryDetection.EnrichWithRepositoryInfoFromCwd(config, node.ToJsonString(), workspaceRoot, remaining)
                        ) ?? node;
                    }
                }
            }

            var normalized = node.ToJsonString();

            if (sessionId is not null) {
                await spool.DrainAllAsync(sessionId, async (route, entryBody) => {
                    if (BudgetExpired()) return DrainOutcome.TransientStop;
                    try {
                        using var content = new StringContent(entryBody, Encoding.UTF8, "application/json");
                        using var resp    = await client.PostOnceAsync($"{Url}/hooks/{route}", content, HookPostTimeout, ct);
                        if (resp.IsSuccessStatusCode) {
                            // Task 8: a spooled beforeSubmitPrompt (user-prompt/cursor)
                            // finally being delivered here means the side-effect barrier this
                            // session may be holding on can now be cleared.
                            if (route == "user-prompt/cursor") _markers.ClearBarrier(sessionId);
                            // Task 12: this IS "a later invocation whose spool drain
                            // delivers the start" — a subagent-start that failed its first live
                            // POST (see HandleSubagentChildEventAsync) just got acknowledged here
                            // instead. Perform the deferred child-watcher spawn now.
                            if (route == "subagent-start") await MaybeSpawnChildWatcherFromPayloadAsync(entryBody);
                            return DrainOutcome.Delivered;
                        }
                        return HookSpool.OutcomeOf((int)resp.StatusCode);
                    } catch { return DrainOutcome.TransientStop; }
                }, budget.Remaining, ct);
            }

            // capture whether this session STILL has spool backlog after
            // the drain attempt above. A TransientStop mid-drain (or budget expiry) can leave
            // entries undelivered — including an earlier canonical lifecycle event like
            // sessionStart — and the ordering guard below only early-returns for mappings whose
            // OWN SpoolOnFailure is true. A telemetry-only mapping (preToolUse/postToolUse/etc.,
            // SpoolOnFailure == false) would otherwise sail past that guard and let the recovery
            // watcher spawn near the bottom of this method start streaming transcript content
            // before the still-backlogged earlier event ever reaches the server.
            var hasRemainingSpoolBacklog = sessionId is not null && spool.HasBacklog(sessionId);

            // a linked subagent child takes over entirely from here — it never
            // gets the normal mapping.RouteSegment POST (mirroring CursorImportSource.
            // SendSubagentLifecycleAsync, which never gives a correlated child its own top-level
            // lifecycle either). See HandleSubagentChildEventAsync for the divert.
            if (isSubagentChild) {
                // §4/§5: a linked child short-circuits to {} (sessionStart) / nothing
                // (everything else) before any orchestrator work — never entered for a child.
                await HandleSubagentChildEventAsync(
                    client, spool, sessionId!, eventName, transcriptPath,
                    subagentParentId!, subagentAgentType!, BudgetExpired, ct);
                return EmptyOrNull();
            }

            // Ordering guard: if a transient drain failure left this session's backlog in place,
            // spool the fresh event so an earlier queued event (e.g. sessionStart) is not overtaken.
            if (sessionId is not null && mapping.SpoolOnFailure && spool.HasBacklog(sessionId)) {
                spool.Append(sessionId, mapping.RouteSegment, normalized);
                return EmptyOrNull();
            }

            if (BudgetExpired()) {
                // Drain consumed the budget; preserve the fresh event so the
                // next invocation can still deliver it. Without this the new
                // canonical event would be lost when the spool replay backlog
                // is large or the server is slow.
                if (mapping.SpoolOnFailure && sessionId is not null) {
                    spool.Append(sessionId, mapping.RouteSegment, normalized);
                }
                return EmptyOrNull();
            }

            // For sessionEnd the server's HandleSessionEnd clears the per-session
            // CursorAttachmentsFifo before transcript_line normalization could
            // consume any still-queued beforeSubmitPrompt attachments. Drain the
            // transcript BEFORE posting the terminal hook so the FIFO survives
            // long enough for the final user line to attach. For every other
            // event we keep post-then-backfill so lifecycle metadata reaches the
            // server before any new transcript context.
            var drainBeforePost = eventName == "sessionEnd"
                               && sessionId is not null
                               && !string.IsNullOrEmpty(transcriptPath);

            if (drainBeforePost && !BudgetExpired()) {
                // Task 10 (D2): this IS the sessionEnd pre-end drain — the hook is the
                // last component that will ever observe this transcript, so a valid
                // newline-less final record must be consumed rather than held forever.
                await CursorTranscriptBackfill.RunAsync(
                    _markers,
                    client, Url, sessionId!, transcriptPath,
                    budget: BudgetExpired, ct, finalDrain: true);
            }

            if (BudgetExpired()) {
                // Drain consumed the budget; preserve the unposted sessionEnd
                // so the next invocation (if any) can still deliver it.
                if (mapping.SpoolOnFailure && sessionId is not null) {
                    spool.Append(sessionId, mapping.RouteSegment, normalized);
                }
                return EmptyOrNull();
            }

            var posted = await TryPostHookAsync(client, mapping.RouteSegment, normalized, ct);
            if (!posted && mapping.SpoolOnFailure && sessionId is not null) {
                spool.Append(sessionId, mapping.RouteSegment, normalized);
            }
            // Task 8: the ordering-sensitive beforeSubmitPrompt's own live POST just
            // succeeded — clear the barrier it created above.
            if (posted && eventName == "beforeSubmitPrompt" && sessionId is not null) {
                _markers.ClearBarrier(sessionId);
            }

            // Task 9: recovery spawn from a non-start hook — ONLY once this
            // invocation's own lifecycle POST has actually landed (never race a fresh watcher
            // ahead of the metadata the server needs to attribute its lines to). sessionStart
            // already spawned before its POST above; ShouldSpawnWatcher is false for sessionEnd
            // regardless, so this is safe to reach unconditionally for every other event.
            // additionally require no remaining spool backlog: this
            // invocation's own POST landing is not enough if an EARLIER queued event for this
            // session is still stuck undelivered (hasRemainingSpoolBacklog, captured above).
            if (posted && eventName != "sessionStart" && sessionId is not null && !string.IsNullOrEmpty(transcriptPath)
             && !hasRemainingSpoolBacklog) {
                await MaybeSpawnWatcherAsync(sessionId, transcriptPath, cwd: null, eventName, isSubagentChild);
            }

            if (!drainBeforePost && !BudgetExpired() && sessionId is not null && !string.IsNullOrEmpty(transcriptPath)) {
                await CursorTranscriptBackfill.RunAsync(
                    _markers,
                    client, Url, sessionId, transcriptPath,
                    budget: BudgetExpired, ct);
            }

            // §3–§6: the memory index runs strictly AFTER all of the above
            // recording-critical work, on whatever budget is left over, and only for a
            // top-level (isSubagentChild already diverted above at line ~309) sessionStart.
            if (eventName != "sessionStart") return null;
            var fragment = await RunMemoryOrchestrationAsync(
                client, sessionId, workspaceRoot, ct, budget);
            // The static work-items nudge, isolated from the lease-driven fragment above and
            // merged only at render. The opt-out flag is not in scope here (it lives inside the
            // orchestration), so re-read it; sessionStart fires once per session and the read is
            // fail-open under the surrounding catch.
            var nudgeProfile   = profiles.Effective;
            var workItemsNudge = WorkItemsNudgeEmitter.Resolve(HarnessId.Cursor, sessionId, nudgeProfile?.DisableWorkItemsNudge is true, home);
            var harnessNudge   = HarnessNudgeEmitter.ResolveFragmentForHook(nudgeProfile?.DisableHarnessNudge is true, config, home);
            return SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, fragment,
                HarnessNudgeEmitter.Combine(workItemsNudge, harnessNudge));
        } catch {
            // Fail-open per design: any exception (budget cancellation,
            // transcript-file IO race, JSON quirk we missed) must never crash Cursor's agent
            // loop. eventName may be out of scope here (the exception could predate its
            // parse), so fall back to the published kind — the same signal the outer deadline
            // branch reads.
            return kindSignal.Kind == "sessionStart" ? SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, null) : null;
        }
    }

    /// <summary>
    /// §3–§6: fetches the shared SessionStart memory index for a top-level
    /// (non-child — this is reached only once <c>isSubagentChild</c> has already diverted) Cursor
    /// <c>sessionStart</c>, mirroring
    /// <c>ClaudeHookCommand.StartMemoryIndexTask</c>: same shared store/provider/orchestrator,
    /// no second auth/scope/HTTP path. Runs strictly AFTER recording-critical work — never
    /// concurrently, never before — and only on what <see cref="HookBudget.Remaining"/> has left;
    /// a cancelled fetch leaves the lease uncommitted
    /// (retryable on a later hook) because the request's own <see cref="CancellationToken"/> is
    /// bound to that SAME leftover budget via a linked <see cref="CancellationTokenSource"/> —
    /// not a <c>WaitAsync</c> wrapper around an unbounded call.
    /// </summary>
    async Task<string?> RunMemoryOrchestrationAsync(
            HttpClient client,
            string?    sessionId,
            string?    workspaceRoot,
            CancellationToken dispatcherCt,
            HookBudget budget
        ) {
        if (sessionId is null) return null;

        // An absent/blank Cursor workspace root must NOT fall through to the scope resolver's
        // Directory.GetCurrentDirectory() fallback: that would derive a repo scope from the hook
        // PROCESS's cwd and could inject an UNRELATED repository's memories into this session.
        // With no authoritative workspace root there is no safe scope, so skip injection entirely.
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;

        // Whatever the one budget has left — already net of the spool-and-exit reserve, so it is NOT
        // subtracted again here (the same rule the Codex and Antigravity adapters state). On the
        // hook's own clock, so a test advancing it picks its side of the guard below.
        var memBudget = budget.Remaining;
        if (memBudget <= TimeSpan.Zero) return null;

        // Effective profile (ResolvedProfile.Profile is null under --server-url/KCAP_URL). ct-bound to
        // the dispatcher deadline so the config read cancels rather than lingering when abandoned.
        var activeProfile      = profiles.Effective;
        var disabled           = activeProfile?.DisableMemoryIndex is true;
        var guidelinesDisabled = activeProfile?.DisableSessionGuidelines is true;
        if (disabled && guidelinesDisabled) return null;

        try {
            // The injected clock, the same one the lease store below runs on: a hook run has exactly
            // one clock, so a test advancing it fires this deadline and ages the lease together.
            using var budgetCts = new CancellationTokenSource(memBudget, clock.Time);
            using var memCts    = CancellationTokenSource.CreateLinkedTokenSource(dispatcherCt, budgetCts.Token);

            var store = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            // Both lanes send on the hook's own client, which stays this method's caller's to dispose.
            var provider = SessionStartMemoryHookSupport.CompositeProvider(config, _ => Task.FromResult(client));

            return await new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                // ClassificationAuthoritative is hardcoded true, and this is VALID UNDER THE
                // MEASURED EVENT CONTRACT rather than proven from this file alone:
                //
                //  - What the source constructs: this method has exactly one call site, behind
                //    `if (eventName != "sessionStart") return null`. That is an internal
                //    invariant a unit test can pin.
                //  - What makes the flag WARRANTED: on cursor-agent 2026.07.23-e383d2b a
                //    subagent child never fires sessionStart at all (measured over four probe
                //    runs and two subagent kinds), so this method is never reached for a child.
                //    That is an external vendor behaviour a Cursor update could change.
                //
                // Deliberately NOT described as "correct by construction" — the dependency on
                // the vendor contract must stay visible so a future reader knows what to
                // re-check. Evidence, the re-probe procedure, and the untested Cursor IDE gap
                // are in
                // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
                new SessionMemoryLifecycle(HarnessId.Cursor, sessionId, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true, SessionLifecycleReason.New,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, workspaceRoot, disabled, memBudget, memCts.Token,
                    GuidelinesDisabled: guidelinesDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// the divert path for a Cursor hook belonging to a subagent child
    /// already linked to a parent (<see cref="CursorLiveSubagentLinker"/>). Mirrors
    /// <c>CursorImportSource.SendSubagentLifecycleAsync</c> — as designed, three things happen
    /// for a linked child, regardless of which Cursor hook fired: <c>subagent-start</c>
    /// (once, from its own <c>sessionStart</c>), transcript backfill routed under the parent
    /// with <c>agent_id=child</c> (on every hook — Cursor gives us no line-granular signal,
    /// so the watermark is just re-checked each time), and <c>subagent-stop</c> (once, from
    /// its own <c>sessionEnd</c>).
    ///
    /// <para>
    /// TWO OF THOSE THREE CANNOT HAPPEN. A Cursor subagent child fires neither
    /// <c>sessionStart</c> nor <c>sessionEnd</c>, so the start and stop arms below are
    /// unreachable from a real child; only the backfill arm can run, and only when a marker
    /// already exists. The design above is therefore a description of intent, not of observed
    /// behaviour — read it that way.
    /// </para>
    ///
    /// Every other Cursor hook for a linked child
    /// (<c>beforeSubmitPrompt</c>/<c>afterAgentThought</c>/telemetry) carries no signal the
    /// import path can ever replay either (Cursor's on-disk transcript has no side channel
    /// for them), so — for live/import parity — they are simply not forwarded, rather than
    /// being appended to a phantom <c>AgentSession-{child}</c> stream that never got a
    /// <c>SessionStarted</c>.
    ///
    /// <para>
    /// UNREACHABLE in a fresh installation on the measured cursor-agent contract: entry requires
    /// <c>isSubagentChild</c>, which requires a marker that has no producer there. It remains
    /// reachable from a marker persisted by another surface or an older build. Note also that
    /// the sessionStart/sessionEnd arms below can never fire from a real child, which never
    /// emits either event — a native revival must be driven by the PARENT's subagentStart /
    /// subagentStop instead. See
    /// docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
    /// </para>
    /// </summary>
    internal async Task<int> HandleSubagentChildEventAsync(
            HttpClient        client,
            HookSpool         spool,
            string            childSessionId,
            string?           eventName,
            string?           transcriptPath,
            string            parentSessionId,
            string            subagentType,
            Func<bool>        budgetExpired,
            CancellationToken ct
        ) {
        var isStart     = eventName == "sessionStart";
        var isStop      = eventName == "sessionEnd";
        var isLifecycle = isStart || isStop;

        // Precompute the lifecycle route + body so the ordering guard can spool it verbatim.
        var route = isStart ? "subagent-start" : "subagent-stop";
        var body  = isLifecycle
            ? (isStart
                ? CursorLiveSubagentLinker.BuildSubagentStartPayload(parentSessionId, childSessionId, subagentType, transcriptPath ?? "")
                : CursorLiveSubagentLinker.BuildSubagentStopPayload(parentSessionId, childSessionId, subagentType, transcriptPath ?? "")
              ).ToJsonString()
            : null;

        // Ordering guard — mirrors the top-level path's spool.HasBacklog check (HandleCore
        // above): the HandleCore drain already tried to deliver this child's spool, so a
        // non-empty backlog HERE means it hit a transient failure and an earlier
        // subagent-start is still queued undelivered. Do NOT let a later subagent-stop — or
        // the agent-routed transcript backfill — overtake it (a stop landing before its
        // start would leave the AgentSubsession stream mis-ordered / never opened). Spool the
        // fresh lifecycle event behind the backlog and skip the backfill; the content-less
        // mid-lifecycle hooks simply no-op. The next hook re-drains start-first.
        if (spool.HasBacklog(childSessionId)) {
            if (isLifecycle) spool.Append(childSessionId, route, body!);
            return 0;
        }

        // a non-start hook for a child whose subagent-start was never
        // acknowledged (2xx) must not let ANY child transcript flow, nor its own subagent-stop
        // POST. The backlog check above only catches a start that's still PENDING retry; a start
        // that hit a non-transient 4xx is permanently DROPPED by the generic drain (HandleCore's
        // poster callback) — the entry vanishes from the spool, so HasBacklog goes false even
        // though no AgentSubsession stream was ever opened server-side. Gate on the durable
        // positive-ack marker instead of "no backlog" so a dropped start permanently blocks this
        // child.
        //
        // This is fail-closed and SILENT: the return below emits no log, metric or surfaced
        // marker, so the loss is NOT diagnosable from the running system. Accepted to preserve
        // start-before-content ordering; the child's live capture is recovered only by
        // `kcap import --cursor` plus the server-side adoption sweep. The full state table is in
        // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md (D2a).
        if (!isStart && !_markers.HasSubagentStartAck(childSessionId)) {
            return 0;
        }

        // Content-less mid-lifecycle hooks (beforeSubmitPrompt/afterAgentThought/telemetry):
        // only the agent-routed transcript backfill carries anything the import path can also
        // replay, so that's all we do (plus a self-heal spawn — see below).
        if (!isLifecycle) {
            if (!string.IsNullOrEmpty(transcriptPath) && !budgetExpired()) {
                // self-heal. HasSubagentStartAck (above) only proves
                // subagent-start was acknowledged (2xx) AT SOME POINT — the child watcher process
                // itself may since have exited (the newly-enabled idle ceiling), crashed, or never
                // actually spawned at all (e.g. the acking invocation carried no transcript path,
                // and THIS later hook is the first one that does). Before this fix the spawn was
                // attempted only on the start arm — which, per the measured contract, a real child
                // never reaches — so every later nonterminal hook did nothing but backfill and a
                // dead/never-started child watcher was never restarted. This nonterminal path is
                // the one a real child DOES reach, which is what makes the self-heal load-bearing
                // rather than a nicety. EnsureWatcherRunning is idempotent (PID+heartbeat check), so
                // calling it here on every nonterminal hook is a cheap no-op once the watcher is
                // alive and a real recovery when it's not; the terminal (sessionEnd) branch below
                // still never spawns.
                await MaybeSpawnChildWatcherAsync(parentSessionId, childSessionId, transcriptPath);

                await CursorTranscriptBackfill.RunAsync(
                    _markers,
                    client, Url, parentSessionId, transcriptPath,
                    budget: budgetExpired, ct, agentId: childSessionId);
            }
            return 0;
        }

        // sessionEnd: drain the transcript before the terminal hook — same ordering rationale
        // as the top-level path, and mirrors SendSubagentLifecycleAsync (its transcript batch
        // always precedes subagent-stop too). Task 10 (D2): a pre-end drain consumes a
        // complete-but-unterminated final line rather than holding it.
        //
        // UNREACHABLE from a real child, which never fires sessionEnd — this arm survives only
        // for a marker-driven invocation that supplies the event some other way.
        if (isStop && !string.IsNullOrEmpty(transcriptPath) && !budgetExpired()) {
            await CursorTranscriptBackfill.RunAsync(
                _markers,
                client, Url, parentSessionId, transcriptPath,
                budget: budgetExpired, ct, agentId: childSessionId, finalDrain: true);
        }

        if (budgetExpired()) {
            spool.Append(childSessionId, route, body!);
            return 0;
        }

        var posted = await TryPostHookAsync(client, route, body!, ct);
        if (!posted) {
            // Task 12 — subagent-start POST failed (spooled): the child watcher must
            // NOT spawn here. Spawning now would let the watcher's own poll deliver child
            // transcript lines before SubagentStarted is ever appended (the server has no
            // AgentSubsession stream open yet to receive them). Spool it and STOP; running the
            // sessionStart backfill now would hit the same problem. HandleCore's generic
            // top-of-method spool drain (keyed on this same childSessionId) is what redelivers
            // this entry on a later hook invocation — see its "subagent-start" branch, which
            // performs the deferred spawn itself once that redelivery succeeds.
            spool.Append(childSessionId, route, body!);
            return 0;
        }

        // persist the positive ack so HandleSubagentChildEventAsync's
        // no-ack gate (above) is satisfied for every subsequent hook invocation for this child
        // (a fresh process each time, so nothing survives in memory).
        if (isStart) _markers.MarkSubagentStartAcked(childSessionId);

        // Task 12 — subagent-start is ACKNOWLEDGED (2xx) via this live POST. Only now
        // may the child's own tailing watcher be spawned — the invariant this task exists for
        // is that no child transcript line ever reaches the server before SubagentStarted.
        if (isStart && !string.IsNullOrEmpty(transcriptPath)) {
            await MaybeSpawnChildWatcherAsync(parentSessionId, childSessionId, transcriptPath);
        }

        // sessionStart: post subagent-start THEN backfill, so the AgentSubsession stream is
        // opened before its first transcript batch (mirrors SendSubagentLifecycleAsync).
        if (isStart && !string.IsNullOrEmpty(transcriptPath) && !budgetExpired()) {
            await CursorTranscriptBackfill.RunAsync(
                _markers,
                client, Url, parentSessionId, transcriptPath,
                budget: budgetExpired, ct, agentId: childSessionId);
        }

        return 0;
    }

    /// <summary>
    /// Task 12 — spawns (or heals) the child (subagent) Cursor watcher, keyed
    /// <c>{parentSessionId}-{childSessionId}</c> so it never collides with the parent's own
    /// top-level watcher key. Tails the CHILD's own transcript file and routes every batch it
    /// sends under <paramref name="parentSessionId"/> with <c>agentId = childSessionId</c>
    /// (<see cref="WatcherManager.EnsureWatcherRunning"/>'s <c>sessionIdOverride</c>/<c>agentId</c>
    /// pair — the same shape <c>ClaudeHookCommand</c>'s <c>subagent-start</c> case uses). Callers
    /// MUST only invoke this after an acknowledged (2xx) <c>subagent-start</c> — see the call site
    /// in <see cref="HandleSubagentChildEventAsync"/>.
    ///
    /// Quarantine is keyed on <paramref name="parentSessionId"/>, not the child: the runtime
    /// rewrite guard (<see cref="CursorRewriteGuard"/>) and <c>WatchCommand.RunWatch</c> both
    /// construct their guard/quarantine identity from the watcher process's own
    /// <c>sessionId</c> argument, which — for a child watcher spawned with
    /// <c>sessionIdOverride: parentSessionId</c> — resolves to the PARENT id
    /// (<c>_watchers.BuildSpawnArgs</c>: <c>sessionId = sessionIdOverride ?? key</c>). A
    /// parent session already given up on by the guard must not keep spawning fresh child
    /// watchers either.
    /// </summary>
    internal Task MaybeSpawnChildWatcherAsync(
            string parentSessionId,
            string childSessionId,
            string transcriptPath
        ) {
        if (_markers.IsQuarantined(parentSessionId)) return Task.CompletedTask;
        if (string.IsNullOrEmpty(transcriptPath)) return Task.CompletedTask;

        return _watchers.EnsureWatcherRunning(key: $"{parentSessionId}-{childSessionId}", transcriptPath,
            agentId: childSessionId, sessionIdOverride: parentSessionId, vendor: "cursor");
    }

    /// <summary>
    /// Task 12 — the generic spool-drain callback (<see cref="HandleCore"/>'s top-of-method
    /// drain, which runs for every session BEFORE the <c>isSubagentChild</c> divert) has just
    /// delivered a previously-spooled <c>subagent-start</c> entry. Parses the parent/child/
    /// transcript-path triple back out of its own payload shape
    /// (<see cref="CursorLiveSubagentLinker.BuildSubagentStartPayload"/>: <c>session_id</c> =
    /// parent, <c>agent_id</c> = child, <c>transcript_path</c> = the child's own file) and performs
    /// the spawn this deferred delivery unblocks. Fail-open: a malformed payload (should never
    /// happen — this method only ever spools its own generated JSON) just skips the spawn rather
    /// than risking the drain loop itself.
    /// </summary>
    Task MaybeSpawnChildWatcherFromPayloadAsync(string subagentStartBody) {
        try {
            var payload         = JsonNode.Parse(subagentStartBody);
            var parentSessionId = TryGetString(payload, "session_id");
            var childSessionId  = TryGetString(payload, "agent_id");
            var transcriptPath  = TryGetString(payload, "transcript_path");
            if (parentSessionId is null || childSessionId is null || string.IsNullOrEmpty(transcriptPath)) {
                return Task.CompletedTask;
            }
            // this delivery IS the 2xx ack for a previously-spooled
            // subagent-start; persist it so the no-ack gate in HandleSubagentChildEventAsync is
            // satisfied for this child from here on.
            _markers.MarkSubagentStartAcked(childSessionId);
            return MaybeSpawnChildWatcherAsync(parentSessionId, childSessionId, transcriptPath);
        } catch { return Task.CompletedTask; }
    }

    /// <summary>
    /// Task 9 — pure precedence predicate for whether THIS hook invocation should
    /// spawn (or heal) the per-session top-level Cursor watcher. Precedence, from the D1
    /// design: ① a terminal hook (<c>sessionEnd</c>) never spawns — only the pre-end drain and
    /// the terminal POST matter there, and spawning a watcher moments before killing the
    /// session would be pure churn; ② a correlated subagent child never spawns a top-level
    /// <c>key=sessionId</c> watcher — it is routed through the gated
    /// <c>parentSessionId-childSessionId</c> watcher instead (wired in a later task); ③/④ every
    /// other hook — <c>sessionStart</c> or a later recovery hook — may spawn.
    /// </summary>
    internal static bool ShouldSpawnWatcher(string eventName, bool isSubagentChild) {
        if (eventName == "sessionEnd") return false;
        if (isSubagentChild) return false;
        return true;
    }

    /// <summary>
    /// Task 9 — spawns (or heals, via <see cref="WatcherManager.EnsureWatcherRunning"/>'s
    /// existing idempotent PID+heartbeat check) the per-session top-level Cursor watcher for
    /// <paramref name="sessionId"/>, unless: the session is quarantined (a runtime rewrite-guard
    /// trip — Task 7 — must not keep resurrecting a watcher for a session already given up on),
    /// <see cref="ShouldSpawnWatcher"/> says no for this <paramref name="eventName"/>/
    /// <paramref name="isSubagentChild"/> combination, or there is no transcript path to tail.
    /// Always vendor <c>"cursor"</c>, keyed on the bare session id (top-level only — a linked
    /// child's watcher is a distinct, gated key from a later task).
    /// </summary>
    internal Task MaybeSpawnWatcherAsync(
            string  sessionId,
            string  transcriptPath,
            string? cwd,
            string  eventName,
            bool    isSubagentChild
        ) {
        if (_markers.IsQuarantined(sessionId)) return Task.CompletedTask;
        if (!ShouldSpawnWatcher(eventName, isSubagentChild)) return Task.CompletedTask;
        if (string.IsNullOrEmpty(transcriptPath)) return Task.CompletedTask;

        return _watchers.EnsureWatcherRunning(key: sessionId, transcriptPath,
            agentId: null, cwd: cwd, vendor: "cursor");
    }

    /// <summary>
    /// Migrates legacy Cursor spool files (<c>{hook_event_name, body}</c> format)
    /// from <paramref name="legacyDir"/> into <paramref name="dest"/> using the
    /// new <c>{route, body}</c> format, then deletes the migrated files.
    /// Best-effort — IO/JSON errors are swallowed to preserve fail-open contract.
    /// </summary>
    internal static void MigrateLegacyCursorSpool(HookSpool dest, string legacyDir) {
        try {
            if (!Directory.Exists(legacyDir)) return;
            foreach (var file in Directory.EnumerateFiles(legacyDir, "*.jsonl")) {
                try {
                    var sid = Path.GetFileNameWithoutExtension(file);
                    foreach (var line in File.ReadAllLines(file)) {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        string? ev, body;
                        try {
                            var n = JsonNode.Parse(line);
                            ev   = n?["hook_event_name"]?.GetValue<string>();
                            body = n?["body"]?.GetValue<string>();
                        } catch { continue; }
                        if (ev is null || body is null) continue;
                        if (CursorHookEventMap.TryResolve(ev, out var m)) dest.Append(sid, m.RouteSegment, body);
                    }
                    File.Delete(file); // delete only after appending; a crash mid-file may re-append on retry (harmless: server dedupes replays)
                } catch { /* per-file best effort */ }
            }
        } catch { }
    }

    async Task<bool> TryPostHookAsync(
            HttpClient        client,
            string            routeSegment,
            string            bodyJson,
            CancellationToken ct
        ) {
        try {
            using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            using var resp = await client.PostOnceAsync(
                $"{Url}/hooks/{routeSegment}", content, HookPostTimeout, ct);

            // Cursor posts directly instead of through AgentHookPoster, so the rejected-credential
            // nudge has to be repeated here — otherwise Cursor is the one vendor left with no
            // explanation and no `kcap login` hint. Live path only: the spool drain replays many
            // entries per pass and would repeat this line for each one.
            if ((int)resp.StatusCode == 401) {
                await Console.Error.WriteLineAsync(
                    AuthRejectionNotice.VendorStderrLine("cursor-hook", routeSegment, 401));
            }

            return resp.IsSuccessStatusCode;
        } catch { return false; }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = TryGetString(node, fieldName);
        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    static string StableThoughtId(string sessionId, string generationId, string text) {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var hash16 = Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        return $"{sessionId}:reasoning:{generationId}:{hash16}";
    }

    /// <summary>
    /// Safely extracts a string from <paramref name="node"/>[<paramref name="field"/>].
    /// Returns null (instead of throwing) when the field is absent, null, or not a string.
    /// </summary>
    static string? TryGetString(JsonNode? node, string field) {
        if (node?[field] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }
}
