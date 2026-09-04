using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Tests.Unit.SessionStartMemory;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using Microsoft.Extensions.Time.Testing;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

// The hook resolves the harness nudge through HarnessPaths, so it reads every vendor override
// variable a peer suite clears mid-test.
[NotInParallel("VendorEnvOverrides")]
public class CursorHookCommandTests {
    [TempHome] public required TempHome Home { get; init; }

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "8c3276c2c8f743ce98898c2becf5240a";

    // The harness nudge fires unless an on-disk stamp throttles it, and a private root starts with
    // none — these tests assert on the memory fragment alone. Claim the window explicitly instead of
    // relying on whichever sibling test happened to claim it in the shared config dir first.
    static void ThrottleHarnessNudge(ConfigRoot root) =>
        new HarnessOfferStore(root).TryClaimCheck(HarnessNudgeEmitter.CheckThrottle);

    [Before(Test)]
    public void ThrottleNudgeForThisRoot() => ThrottleHarnessNudge(Config.Root);

    [Test]
    public async Task malformed_stdin_returns_zero() {
        using var fx   = new Fixture(Config.Root);
        var       exit = await fx.HandleAsync("not a json payload");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task missing_hook_event_name_returns_zero() {
        using var fx   = new Fixture(Config.Root);
        var       exit = await fx.HandleAsync("""{"session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task session_id_is_normalised_dashless_in_outgoing_payload() {
        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"8c3276c2-c8f7-43ce-9889-8c2becf5240a"}""");
        var sent = fx.SentToHook("session-start/cursor");

        await Assert.That(JsonNode.Parse(sent)!["session_id"]!.GetValue<string>())
            .IsEqualTo("8c3276c2c8f743ce98898c2becf5240a");
    }

    [Test]
    [NotInParallel("CapacitorAgentIdEnvVar")]
    public async Task home_dir_and_agent_host_id_are_injected() {
        Environment.SetEnvironmentVariable("KCAP_AGENT_ID", "host-42");

        try {
            using var fx = new Fixture(Config.Root);
            await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
            var sent = fx.SentToHook("session-start/cursor");
            var node = JsonNode.Parse(sent)!;
            await Assert.That(node["home_dir"]?.GetValue<string>()).IsNotNull();
            await Assert.That(node["agent_host_id"]?.GetValue<string>()).IsEqualTo("host-42");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_AGENT_ID", null);
        }
    }

    [Test]
    public async Task disabled_session_suppresses_POST() {
        var sid = Guid.NewGuid().ToString("N");
        DisabledSessions.Mark(sid, Config.Root);

        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task telemetry_events_post_but_do_not_spool_on_failure() {
        using var fx = new Fixture(Config.Root, postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync("""{"hook_event_name":"preToolUse","session_id":"abc","tool_name":"Glob"}""");
        await Assert.That(fx.SpoolFiles).IsEmpty();
    }

    /// <summary>
    /// Cursor POSTs directly rather than through <c>AgentHookPoster</c>, so it needs its own
    /// rejected-credential nudge — without it Cursor is the one vendor whose users get no
    /// explanation and no <c>kcap login</c> hint. Redirects the process-global Console.Error, so
    /// it runs alone.
    /// </summary>
    [Test, NotInParallel]
    public async Task server_rejected_credential_names_kcap_login_on_stderr() {
        using var fx = new Fixture(Config.Root, postStatus: HttpStatusCode.Unauthorized);
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        await Assert.That(capture.GetCapturedError()).Contains("kcap login");
    }

    /// <summary>Non-vacuous control: a non-401 failure keeps the bare status line, so the test
    /// above is proving 401 recognition rather than that any failure mentions the command.</summary>
    [Test, NotInParallel]
    public async Task server_error_does_not_name_kcap_login_on_stderr() {
        using var fx = new Fixture(Config.Root, postStatus: HttpStatusCode.InternalServerError);
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        await Assert.That(capture.GetCapturedError()).DoesNotContain("kcap login");
    }

    /// <summary>Cursor's live post spools any non-2xx, so the drain is the only place a 401 can lose an
    /// entry: it must stay spooled and land once the server accepts the credential again.</summary>
    [Test]
    public async Task spooled_entry_that_hits_a_401_on_drain_is_replayed_once_the_server_accepts() {
        using var rejecting = new Fixture(Config.Root, postStatus: HttpStatusCode.Unauthorized);
        using var accepting = new Fixture(Config.Root);
        rejecting.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");

        await rejecting.HandleAsync($$"""{"hook_event_name":"postToolUse","session_id":"{{Sid}}","tool_name":"Glob"}""");
        await Assert.That(rejecting.Spool.HasBacklog(Sid)).IsTrue();

        await new CursorHookCommand(Config.Root, accepting.Profiles, new HookClock(accepting.Clock), Home, new FixedCapacitorHttpClient()).HandleCore(
            accepting.Client,
            stdin: new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{Sid}}","tool_name":"Glob"}"""),
            spool: rejecting.Spool);

        await Assert.That(accepting.RouteOrder).Contains("session-start/cursor");
        await Assert.That(rejecting.Spool.HasBacklog(Sid)).IsFalse();
    }

    [Test]
    public async Task canonical_events_spool_on_POST_failure() {
        using var fx = new Fixture(Config.Root, postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith(Sid + ".jsonl");
    }

    [Test]
    public async Task spool_drain_runs_before_current_event_under_budget() {
        using var fx = new Fixture(Config.Root);
        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        await Assert.That(fx.RouteOrder).IsEquivalentTo(["session-start/cursor", "session-end/cursor"]);
    }

    // A telemetry-only mapping (postToolUse, SpoolOnFailure=false) must not let the
    // recovery-spawn watcher start while an earlier queued canonical event (here: sessionStart)
    // is still stuck undelivered. Simulates: sessionStart is already spooled from a prior failed
    // invocation; this invocation's top-of-method drain retries it and hits a transient failure
    // (503) so it stays queued, while postToolUse's own POST succeeds.
    // Bare NotInParallel: the spawn override is process-wide, so any concurrent peer's spawn lands
    // in this list. The class key is not enough — its cohort excludes only the env-override readers.
    [Test, NotInParallel]
    public async Task telemetry_hook_does_not_recovery_spawn_while_an_earlier_canonical_event_is_still_stuck() {
        var sid = Guid.NewGuid().ToString("N");
        var spool = new HookSpool(Config.PathTo("spool"));
        spool.Append(sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

        try {
            using var handler = new StubHandler(req => {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/hooks/session-start/cursor") {
                    // Transient failure on retry — the entry stays queued (NOT delivered, NOT dropped).
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                if (req.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); // postToolUse's own POST succeeds
            });
            using var client = new HttpClient(handler);

            var exit = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","tool_name":"Bash","transcript_path":"/tmp/{{sid}}.jsonl"}"""),
                spool);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(spawned).IsEmpty(); // must NOT spawn while sessionStart is still stuck
            await Assert.That(spool.HasBacklog(sid)).IsTrue(); // confirms the premise: still queued, not delivered
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    [Test]
    public async Task afterAgentThought_canonical_id_is_stable_across_replays() {
        using var fx   = new Fixture(Config.Root);
        var       body = """{"hook_event_name":"afterAgentThought","session_id":"abc","generation_id":"gen1","text":"hello"}""";
        await fx.HandleAsync(body);
        await fx.HandleAsync(body);

        var ids = fx.AllSentTo("agent-thought/cursor")
            .Select(b => JsonNode.Parse(b)!["canonical_event_id"]!.GetValue<string>())
            .Distinct()
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task sessionEnd_drains_transcript_before_posting_terminal_hook() {
        // Server's HandleSessionEnd clears the CursorAttachmentsFifo as soon
        // as it accepts the /hooks/session-end/cursor POST. If the CLI posted
        // sessionEnd first and only then ran the transcript backfill, the
        // final user line in the transcript would be normalized AFTER the
        // FIFO was wiped and any queued beforeSubmitPrompt attachments would
        // be lost. Verify the order is: transcript batch → session-end.
        using var fx = new Fixture(Config.Root);

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{Sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);
    }

    [Test]
    public async Task non_sessionEnd_events_still_post_before_backfill() {
        // Regression guard: only sessionEnd swaps the order. Other events
        // (here: beforeSubmitPrompt) must keep the existing post-then-backfill
        // ordering so lifecycle metadata reaches the server before any new
        // transcript context.
        using var fx = new Fixture(Config.Root);

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"hello"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"beforeSubmitPrompt","session_id":"{{Sid}}","prompt":"hello","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
    }

    [Test]
    public async Task telemetry_only_hook_touches_the_heartbeat_file() {
        // Even a telemetry-only hook (never spooled, lossy on failure) must touch the
        // per-session heartbeat — it reflects "Cursor is still firing hooks", independent of
        // whatever the transcript/spool machinery is doing.
        using var fx = new Fixture(Config.Root);
        var       sid = Guid.NewGuid().ToString("N");
        var       before = DateTimeOffset.UtcNow;

        await fx.HandleAsync($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","tool_name":"Bash"}""");

        var heartbeat = WatcherHeartbeat.Read(Markers.HeartbeatPath(sid));
        await Assert.That(heartbeat).IsNotNull();
        await Assert.That(heartbeat!.Value).IsGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task beforeSubmitPrompt_clears_its_barrier_once_the_live_POST_succeeds() {
        using var fx  = new Fixture(Config.Root); // defaults to HttpStatusCode.OK
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(Markers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task beforeSubmitPrompt_barrier_stays_pending_when_the_live_POST_fails() {
        using var fx  = new Fixture(Config.Root, postStatus: HttpStatusCode.InternalServerError);
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(Markers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsTrue();
    }

    [Test]
    public async Task sessionEnd_drains_the_hook_spool_before_the_pre_end_transcript_drain_and_clears_the_barrier() {
        // A beforeSubmitPrompt whose live POST failed leaves a barrier plus a spooled
        // user-prompt/cursor entry behind. sessionEnd must deliver that spooled entry (clearing
        // the barrier) before running its pre-end transcript drain, so a transcript line
        // depending on the attachment is never normalized ahead of it.
        using var fx  = new Fixture(Config.Root);
        var       sid = Guid.NewGuid().ToString("N");

        Markers.CreateBarrier(sid, DateTimeOffset.UtcNow);
        fx.Spool.Append(sid, "user-prompt/cursor", $$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}"}""");

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");
        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);

        await Assert.That(Markers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task null_transcript_path_does_not_trigger_backfill() {
        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc","transcript_path":null}""");
        await Assert.That(fx.AllSentTo("transcript")).IsEmpty();
    }

    /// The ceiling runs from process entry, so the resolve and the global spool drain are paid out
    /// of it before the hook is even dispatched. A work budget already spent must still record the
    /// event — HandleCore's own gates spool it instead of posting — never discard it.
    [Test]
    public async Task a_work_budget_spent_before_dispatch_still_spools_the_event() {
        using var fx = new Fixture(Config.Root);

        // Remaining == 0 while UntilCeiling still has the reserve left: exactly what a slow
        // pre-dispatch leaves behind.
        var spent = new FakeTimeProvider();
        var clock = new HookClock(spent);          // anchors on construction — advance AFTER it
        spent.Advance(CursorHookCommand.Ceiling - HookBudget.Safety);

        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), clock, Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(
                new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
                _ => Task.FromResult(new AuthAttempt(fx.Client, AuthStatus.Ok)),
                () => fx.Spool);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Spool.HasBacklog("abc")).IsTrue();
        await Assert.That(fx.Sent).IsEmpty();   // spooled INSTEAD of posted, not as well as
    }

    [Test]
    public async Task expired_budget_returns_zero_not_throws() {
        using var fx = new Fixture(Config.Root);

        // A budget already spent before dispatch forces BudgetExpired() true on the first check,
        // which can also propagate as OperationCanceledException from stdin/HTTP. Either way the
        // dispatcher must fail-open with return 0, never bubble the exception.
        var spent = new FakeTimeProvider();
        var clock = new HookClock(spent);
        spent.Advance(CursorHookCommand.Ceiling);

        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), clock, Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client,
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            fx.Spool
        );
        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        // Simulates an uncancellable hang inside TokenStore.RefreshAsync's
        // HttpClient.PostAsync — no CT plumbed through, default 100s timeout.
        // The Task.WhenAny ceiling in CursorHookCommand.Handle must beat that.
        var inner = Task.Run(async () => {
                await Task.Delay(TimeSpan.FromSeconds(10));

                return 42;
            }
        );
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var inner = Task.FromResult(7);
        var exit  = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task fresh_canonical_event_is_spooled_when_drain_consumes_budget() {
        // Drain blocks past the budget by parking the POST handler. The
        // dispatcher must spool the fresh sessionEnd that hasn't been
        // POSTed yet instead of losing it.
        using var fx = new Fixture(Config.Root);
        fx.SpendOnPost = CursorHookCommand.Ceiling;

        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");

        // The first drained POST spends the work budget, so BudgetExpired flips before the fresh
        // event can post and the fresh sessionEnd must land back in the spool, replacing the
        // just-delivered sessionStart line. No wall-clock bet: the append is not racing a
        // cancellation, because the reserve the work budget held back is still on the cap.
        //
        // HandleCore's outer deadline can return to the caller without waiting for the
        // still-in-flight drain; the append still happens on the abandoned background
        // continuation, so poll briefly for it instead of asserting immediately.
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");

        await Assert.That(exit).IsEqualTo(0);

        // Poll for CONTENT, not existence: the spool file is seeded → deleted once drained → re-created by
        // the fresh sessionEnd, and an existence check cannot tell the seeded file from the re-created one.
        // The handle must block nothing the drain does — HookSpool both appends to and File.Move/Delete's
        // this exact path, and every restriction is mandatory on Windows but invisible on Unix.
        // SingleOrDefault: a second *.jsonl for one session id would be a real bug worth throwing on.
        const int budgetSeconds = 10;

        var deadline      = DateTime.UtcNow + TimeSpan.FromSeconds(budgetSeconds);
        var spoolContent  = (string?)null;
        var observedEnd   = false;
        var lastIoFailure = (IOException?)null;

        while (!observedEnd && DateTime.UtcNow < deadline) {
            if (fx.SpoolFiles.SingleOrDefault() is { } path) {
                try {
                    await using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    spoolContent  = await reader.ReadToEndAsync();
                    lastIoFailure = null; // a clean read means any earlier failure was the transient race
                    observedEnd   = spoolContent.Contains("sessionEnd", StringComparison.Ordinal);
                } catch (IOException ex) {
                    // The enumerate→open race, not a sharing conflict: the share flags above permit
                    // everything the drain does, but a path EnumerateFiles just returned can be deleted
                    // before we open it.
                    lastIoFailure = ex;
                }
            }

            if (!observedEnd) await Task.Delay(20);
        }

        // Gated on whether the TARGET was observed, not on content being null: a clean read of the stale
        // seeded line leaves content non-null, so a null check would skip this and let a persistent
        // filesystem fault masquerade as "the append never happened".
        if (!observedEnd && lastIoFailure is not null) {
            throw new IOException(
                $"spool never yielded sessionEnd within {budgetSeconds}s; last IO failure: {lastIoFailure.Message}",
                lastIoFailure);
        }

        await Assert.That(spoolContent).IsNotNull();
        await Assert.That(spoolContent!).Contains("sessionEnd");
    }

    // Single-writer, deadline-safe stdout emission for Cursor's sessionStart. Cursor writes
    // zero stdout for every other event; a resolved sessionStart emits "{}\n" or a memory
    // fragment, whether at the end of a normal invocation, an early fail-open return, or the
    // dispatcher deadline (kind published but inner work unfinished) — never anything when the
    // event kind was never resolved.

    // Tests below that mutate Console.Out run [NotInParallel] with no group — alone against the
    // whole suite, not just this class. Other files also mutate the same process-global
    // Console.Out under different or no groups, and a named group only serializes within itself,
    // so a cross-group race would still corrupt captures.

    [Test, NotInParallel]
    public async Task SessionStart_emits_empty_object() {
        using var fx = new Fixture(Config.Root);
        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    [Test, NotInParallel]
    public async Task NonSessionStart_emits_nothing() {
        using var fx = new Fixture(Config.Root);
        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"postToolUse","session_id":"abc","tool_name":"Bash"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task LinkedChild_sessionStart_emits_empty_object() {
        using var fx = new Fixture(Config.Root);
        var parentId = Guid.NewGuid().ToString("N");
        var childId  = Guid.NewGuid().ToString("N");
        // Force the already-linked-child path directly (as an earlier hook would have
        // persisted it) without needing a real sibling transcript to correlate against.
        CursorLiveSubagentLinker.SaveLink(Config.Root, childId, parentId, "task");

        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{childId}}"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        // A linked child must short-circuit to {} before any orchestrator work.
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    // These two guarantees must hold at the level where the single cap+emitter actually lives:
    // client/auth setup through the recording+memory dispatch, under exactly one hard-cap
    // deadline. Calling HandleCore directly only proves the dispatch body's own race is
    // internally consistent — it can't catch a second, independent cap racing above it.
    [Test, NotInParallel]
    public async Task HardCap_before_resolve_emits_nothing() {
        using var capture = ConsoleOutput.StartCapture();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var fx = new Fixture(Config.Root);
        // Never resolves within the cap regardless of cancellation — proves the single
        // deadline race genuinely abandons the inner work rather than relying on it
        // noticing. clientFactory/spoolFactory stand in for real auth/spool setup so the
        // test stays hermetic while still exercising the REAL entry point's cap+emit logic.
        var clock = new FakeTimeProvider();
        var call  = new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(clock), Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(new NeverCompletingReader(),
                _ => Task.FromResult(new AuthAttempt(fx.Client, AuthStatus.Ok)),
                () => fx.Spool);

        // Nothing inside can end this, so spend the ceiling from here: the cap must still return.
        clock.Advance(CursorHookCommand.Ceiling);

        var exit = await call;
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    // On the deadline branch, HandleCore must deterministically Cancel() its own `cts` rather
    // than merely disposing it and trusting the CTS's internal timer to have already fired —
    // that timer and the Task.Delay deadline task are two independent timers racing the same
    // wall-clock target, so a dispose-without-cancel could leave the abandoned inner's
    // cancellation-aware reads never actually observing cancellation. The reader here only
    // completes via cancellation, so an observed cancellation is the only way this test can pass.
    [Test, NotInParallel]
    public async Task HandleCore_deadline_win_cancels_the_abandoned_inners_token() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture(Config.Root);
        var reader = new CancelObservingReader();

        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(fx.Client, reader, fx.Spool);

        await Assert.That(exit).IsEqualTo(0);
        // The read never resolved (no hook_event_name was ever parsed), so there is
        // nothing to emit.
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");

        // The abandoned reader's ReadToEndAsync only ever completes by observing its
        // CancellationToken fire. Give it a generous window relative to the 30ms budget —
        // this asserts the cancellation was actually delivered promptly by HandleCore's
        // explicit Cancel(), not "eventually, whenever the internal timer happens to tick".
        var won = await Task.WhenAny(reader.Cancelled.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await Assert.That(won).IsEqualTo(reader.Cancelled.Task);
    }

    [Test, NotInParallel]
    public async Task HardCap_after_resolve_sessionStart_emits_empty_once() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture(Config.Root);
        // sessionStart resolves instantly (fast JSON parse) but the live POST hangs well
        // past the 50ms dispatcher deadline.
        fx.HoldOnPost = TimeSpan.FromMilliseconds(300);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleWithDeps(
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            _ => Task.FromResult(new AuthAttempt(fx.Client, AuthStatus.Ok)),
            () => fx.Spool);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        // Let the orphaned inner work actually finish in the background, then re-assert
        // stdout is unchanged — the abandoned inner must never get a second/late write.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    // The single cap must also cover client/auth setup. A client factory that never completes
    // simulates a TokenStore hang; the deadline must still fire, return 0, and never let the
    // abandoned auth attempt produce a late write once it "completes" in the background.
    [Test, NotInParallel]
    public async Task HardCap_during_client_setup_emits_nothing_and_no_late_write() {
        using var capture = ConsoleOutput.StartCapture();
        using var fx = new Fixture(Config.Root);
        var neverAuths = new TaskCompletionSource<AuthAttempt>();

        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var clock = new FakeTimeProvider();
        var call  = new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(clock), Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(
                new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
                _ => neverAuths.Task,
                () => fx.Spool);

        // The auth attempt never resolves, so spend the ceiling from here: the cap must return.
        clock.Advance(CursorHookCommand.Ceiling);

        var exit = await call;
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        // Even if the abandoned auth call eventually resolves in the background, HandleCore
        // is never invoked for this attempt — there must be no late write.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("");
    }

    // The shared memory orchestrator for a top-level (non-child) sessionStart — fragment,
    // lifecycle, budget, opt-out, and workspace-root behavior.

    [Test, NotInParallel]
    public async Task Ready_fragment_emitted() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");

        using var capture = ConsoleOutput.StartCapture();
        // No budget arithmetic here: the fixture's clock is fake and never advanced, so scheduling
        // delays cannot turn this into a legitimate no-budget skip and make it assert the wrong
        // thing. NoBudget_skips_provider owns the budget math.
        // A real non-repo workspace root is required because an absent root skips injection;
        // forward-slashed so it's valid JSON on Windows too.
        using var wsDir = new TempDir("ws");
        var ws = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""");
        await Assert.That(exit).IsEqualTo(0);

        var stdout = capture.GetCapturedOutput();
        await Assert.That(stdout).StartsWith("""{"additional_context":""");
        await Assert.That(stdout).EndsWith("\"}\n");
        var node = JsonNode.Parse(stdout)!;
        var fragment = node["additional_context"]!.GetValue<string>();
        await Assert.That(fragment).Contains("Team memory");
    }

    // [NotInParallel] for Console.Out, which the capture below redirects process-wide — matching
    // this file's precedent for every other capturing test above. The profile setting reaches the
    // hook through its own resolution, so nothing process-global carries it.
    [Test, NotInParallel]
    public async Task DisableMemoryIndex_emits_empty_and_skips_provider() {
        using var fx = new Fixture(Config.Root, profile: new Profile { DisableMemoryIndex = true });
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using var capture = ConsoleOutput.StartCapture();
        var sid  = Guid.NewGuid().ToString("N");
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    [Test, NotInParallel]
    public async Task NoBudget_skips_provider() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");

        using var capture = ConsoleOutput.StartCapture();
        // Spend the whole ceiling before dispatching: Remaining is then zero regardless of how fast
        // recording completes, so the skip is the guard's and not a scheduling accident.
        fx.Clock.Advance(CursorHookCommand.Ceiling);

        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    // Cursor's sessionStart payload carries a null transcript_path, so the transcript-derived
    // subagent-classification arm has no producer. These tests pin that contract so the arm
    // cannot be quietly assumed live again.

    [Test, NotInParallel]
    public async Task SessionStart_with_null_transcript_path_stays_top_level_and_writes_no_link_marker() {
        using var fx = new Fixture(Config.Root);
        var sid = Guid.NewGuid().ToString("N");
        using var wsDir = new TempDir("ws");
        var ws  = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        // The REAL shape: transcript_path is JSON null at sessionStart.
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","transcript_path":null,"workspace_roots":["{{ws}}"]}""";

        using (ConsoleOutput.StartCapture()) {
            var exit = await fx.HandleAsync(payload);
            await Assert.That(exit).IsEqualTo(0);
        }

        // No link was persisted, so nothing classified this session as a subagent child. This
        // does not prove ResolveParent/SaveLink went unexecuted — ResolveParent can return null,
        // and SaveLink can swallow a write failure. The assertion is about the persisted outcome
        // only.
        await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, sid)).IsNull();
        // ...and it took the ordinary top-level route rather than the subagent divert.
        await Assert.That(fx.RouteOrder).Contains("session-start/cursor");
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-start");

        // This is an outcome pin, not a mutation-sensitive guard for the
        // `!string.IsNullOrEmpty(transcriptPath)` conjunct: deleting that conjunct leaves this
        // test passing, since a null path also fails downstream (no sibling directory, no path to
        // correlate). It protects against classifying a sessionStart from some other source —
        // e.g. deriving the transcripts dir from workspace_roots — without keeping the session
        // top-level.
    }

    [Test, NotInParallel]
    public async Task MemoryIndex_is_fetched_only_for_sessionStart() {
        var sid = Guid.NewGuid().ToString("N");
        using var wsDir = new TempDir("ws");
        var ws  = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        const string body = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using (ConsoleOutput.StartCapture()) {
            // Positive control FIRST. Without it this test could pass vacuously in an
            // environment where memory injection is disabled outright.
            using var starting = new Fixture(Config.Root);
            starting.MemoryIndexBody = body;
            await starting.HandleAsync(
                $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""");
            await Assert.That(starting.MemoryIndexRequested).IsTrue();

            // A postToolUse carries workspace_roots too (measured), so the only thing keeping it
            // away from the orchestrator is the call-site guard.
            using var other = new Fixture(Config.Root);
            other.MemoryIndexBody = body;
            await other.HandleAsync(
                $$"""{"hook_event_name":"postToolUse","session_id":"{{Guid.NewGuid():N}}","workspace_roots":["{{ws}}"]}""");
            await Assert.That(other.MemoryIndexRequested).IsFalse();
        }

        // Pins the orchestrator call-site guard, an internal invariant. It does not prove
        // ClassificationAuthoritative: true is warranted — that additionally needs the external
        // fact that a subagent child never receives sessionStart, a vendor behaviour no unit test
        // can establish.
    }

    [Test, NotInParallel]
    public async Task OncePerConversation() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var sid = Guid.NewGuid().ToString("N");
        // A real non-repo workspace root, forward-slashed for cross-platform JSON — an absent
        // root skips injection.
        using var wsDir = new TempDir("ws");
        var ws = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";

        using (var first = ConsoleOutput.StartCapture()) {
            // Generous budget — see Ready_fragment_emitted's comment on the tight ~0.5s margin
            // at the production 2s default under heavy CI/full-suite CPU contention.
            var exit1 = await fx.HandleAsync(payload);
            await Assert.That(exit1).IsEqualTo(0);
            await Assert.That(first.GetCapturedOutput()).Contains("additional_context");
        }

        using (var second = ConsoleOutput.StartCapture()) {
            var exit2 = await fx.HandleAsync(payload);
            await Assert.That(exit2).IsEqualTo(0);
            await Assert.That(second.GetCapturedOutput()).IsEqualTo("{}\n");
        }
    }

    [Test, NotInParallel]
    public async Task AbsentWorkspaceRoot_skips_provider_even_when_process_cwd_is_a_repo() {
        var originalCwd = Environment.CurrentDirectory;
        // A real git repo WITH a remote as the process cwd: were the guard missing, the shared
        // scope resolver's Directory.GetCurrentDirectory() fallback would derive THIS repo's scope
        // and fetch its (unrelated) memories into the Cursor session. The guard must prevent any fetch.
        using var repoDir = MakeTempRepoWithRemote("https://github.com/example/leak-check.git");
        using var capture = ConsoleOutput.StartCapture();
        try {
            Environment.CurrentDirectory = repoDir;
            using var fx = new Fixture(Config.Root);
            fx.MemoryIndexBody = "[]"; // decoy — never fetched because the guard short-circuits first
            var sid = Guid.NewGuid().ToString("N");

            // No workspace_roots field at all. Generous budget (see Ready_fragment_emitted's note
            // on the tight ~0.5s margin at the 2s default under full-suite CPU contention).
            var exit = await fx.HandleAsync(
                $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");

            await Assert.That(exit).IsEqualTo(0);
            // The guard means the provider is NEVER called when no authoritative workspace root is
            // supplied — so the process cwd's repo memories can never leak — and the response is {}.
            await Assert.That(fx.MemoryIndexRequested).IsFalse();
            await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    // Creates a throwaway git repo with a controlled origin remote so a test can put the process
    // cwd inside a repository the scope resolver would otherwise detect.
    static GitRepo MakeTempRepoWithRemote(string originUrl) {
        var repo = GitRepo.Create();

        repo.AddRemote(originUrl);

        return repo;
    }

    [Test, NotInParallel]
    public async Task NonGuidSessionId_emits_empty() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using var capture = ConsoleOutput.StartCapture();
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"not-a-guid"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
    }

    [Test, NotInParallel]
    public async Task CancelledFetch_leaves_lease_uncommitted() {
        using var fx = new Fixture(Config.Root);
        var sid = Guid.NewGuid().ToString("N");
        // A real non-repo workspace root, forward-slashed for cross-platform JSON — an absent
        // root skips injection before the provider is ever reached.
        using var wsDir = new TempDir("ws");
        var ws = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";
        var clock = new FakeTimeProvider();

        // ONE client, hanging only on the memory-index GET — which is what production does: the
        // memory lane borrows the hook's own client, so a separate one for it would test a wiring
        // that does not exist. The GET never completes on its own; it only ever resolves via the
        // budget-bound linked token handed to the request itself.
        var handler = new HangOnMemoryIndexHandler();
        using var hangingClient = new HttpClient(handler);

        // Deterministic, not wall-clock: the memory stage spends what the ceiling has left once the
        // spool-and-exit reserve is held back, measured on the hook's own injected clock. So it expires
        // only when the test advances that clock, after the request has entered the handler
        // (EnteredSignal), and the cancellation travels the production budget token.
        // The real scope resolver runs: its git spawn is bounded by a Stopwatch, so its wall-clock
        // cost cannot eat a budget that only moves when this test says so.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var call = new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(clock), Home, new FixedCapacitorHttpClient()).HandleCore(
            hangingClient, new StringReader(payload), fx.Spool);

        // Wait (bounded, real-time) for the request to ENTER the handler, then fire the budget clock.
        await handler.EnteredSignal.Task.WaitAsync(TimeSpan.FromSeconds(30));

        clock.Advance(CursorHookCommand.Ceiling);

        var exit1 = await call;
        elapsed.Stop();
        await Assert.That(exit1).IsEqualTo(0);
        // Cancelled completes only from the handler's cancellation catch, i.e. only because advancing
        // the budget clock cancelled the request's OWN token — proving the memory-budget token governs
        // the fetch. Awaited (bounded), not read as a snapshot: the command's return does not order
        // against the handler observing the token, so a bool read here races that propagation. Entered
        // rules out a skipped attempt, and returning far inside the 15s outer deadline rules that out.
        await Assert.That(handler.Entered).IsTrue();
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(elapsed.Elapsed.TotalSeconds).IsLessThan(10);

        // Advance well past the 30s lease duration so the still-"leased" (never committed — the
        // cancellation raced RetryAsync's own fencing too) record from the first attempt is
        // superseded rather than fencing a second attempt for 30 real seconds.
        clock.Advance(TimeSpan.FromSeconds(31));
        fx.MemoryIndexBody = "[]";

        var exit2 = await new CursorHookCommand(Config.Root, Resolutions.At(Fixture.StubUrl, Config.Root), new HookClock(clock), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, new StringReader(payload), fx.Spool);
        await Assert.That(exit2).IsEqualTo(0);
        // The index GET fires again on fx.Client — proving the first, cancelled attempt's
        // lease was never spent as "completed".
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
    }

    // The other side of that guard, asserted rather than left to a runner: no budget left means no
    // fetch, and the sessionStart still emits its single {}.
    [Test, NotInParallel]
    public async Task ExhaustedMemoryBudget_skips_the_fetch_and_still_emits() {
        using var fx = new Fixture(Config.Root);
        var sid = Guid.NewGuid().ToString("N");
        using var wsDir = new TempDir("ws");
        var ws = wsDir.Path.Replace('\\', '/').TrimEnd('/');
        var payload = $$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","workspace_roots":["{{ws}}"]}""";
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        using var capture = ConsoleOutput.StartCapture();
        // The recording POST spends the whole budget, which is how production reaches this guard:
        // the memory stage runs last, on what is left, and here nothing is.
        fx.SpendOnPost = CursorHookCommand.Ceiling;

        var exit = await fx.HandleAsync(payload);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
        await Assert.That(capture.GetCapturedOutput()).IsEqualTo("{}\n");
        // Pins THIS guard specifically: the provider would also decline a zero budget, but only
        // the orchestration guard returns before the store is built — and the store creates its
        // root on construction, so an absent directory is that guard having returned.
        await Assert.That(MemoryStoreProbe.WasBuilt(Config.Root)).IsFalse();
    }

    // The fixture must serve GET /api/memories/index distinctly from the generic
    // transcript-watermark GET (which stays 404). This test only proves the double is capable
    // of it — it drives a sessionStart and calls the endpoint directly rather than exercising
    // HandleCore's own automatic fetch.
    [Test]
    public async Task memory_index_endpoint_is_routed_distinctly_from_the_watermark_GET() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        // Drive a sessionStart through the normal fixture path — no behavior change yet.
        var exit = await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);

        using var resp = await fx.Client.GetAsync("http://localhost/api/memories/index");
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo(fx.MemoryIndexBody);

        // The generic watermark GET path is untouched — still 404.
        using var watermarkResp = await fx.Client.GetAsync("http://localhost/api/sessions/abc/transcript-watermark");
        await Assert.That(watermarkResp.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task legacy_cursor_spool_is_transformed_and_merged() {
        using var tmp = new TempDir();
        var legacyDir = tmp.CreateDir("legacy");
        var spoolDir  = tmp.CreateDir("spool");
        // Old format: {hook_event_name, body}
        legacyDir.CreateFile($"{Sid}.jsonl",
            $"{{\"hook_event_name\":\"sessionEnd\",\"body\":\"{{\\\"session_id\\\":\\\"{Sid}\\\"}}\"}}\n");

        var spool = new HookSpool(spoolDir);
        CursorHookCommand.MigrateLegacyCursorSpool(spool, legacyDir);

        var migrated = await File.ReadAllTextAsync(spoolDir.PathTo($"{Sid}.jsonl"));
        await Assert.That(migrated).Contains("\"route\":\"session-end/cursor\"");
        await Assert.That(File.Exists(legacyDir.PathTo($"{Sid}.jsonl"))).IsFalse();
    }

    sealed class Fixture : IDisposable {
        readonly TempHome _home = new("cursor-hook");

        readonly string _spoolPath;
        readonly string _transcriptPath;

        public List<string> Sent       { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool    Spool      { get; }
        public ConfigRoot   Config     { get; }
        public TimeSpan     HoldOnPost { get; set; } = TimeSpan.Zero;

        /// <summary>How much of the hook's budget a POST consumes. The deterministic stand-in for
        /// slow recording work: advancing the fake clock is what makes BudgetExpired flip, where a
        /// real delay only hoped the runner was slow enough.</summary>
        public TimeSpan     SpendOnPost { get; set; } = TimeSpan.Zero;

        // Lets a test fake the shared SessionStart memory-index endpoint
        // distinctly from the generic transcript-watermark GET (which stays 404).
        public string         MemoryIndexBody      { get; set; } = "[]";
        public HttpStatusCode MemoryIndexStatus    { get; set; } = HttpStatusCode.OK;
        public bool           MemoryIndexRequested { get; private set; }
        public Uri?           MemoryIndexRequestUri { get; private set; }

        public HttpClient Client                { get; }
        public string     TranscriptPathEscaped => _transcriptPath.Replace(@"\", @"\\");

        // The backfill holds a non-newline-terminated final line on every mid-session
        // (Hold-policy) call. A real Cursor transcript line is newline-terminated once flushed,
        // so tests write content the same way rather than exercising the holdback edge case
        // incidentally.
        public Task WriteTranscript(string content) =>
            File.WriteAllTextAsync(_transcriptPath, content.EndsWith('\n') ? content : content + "\n");

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath, "*.jsonl") : [];

        /// <summary>The resolution the hook reads its per-profile settings through. A test that
        /// needs a setting honoured passes the profile here rather than steering process-global
        /// state.</summary>
        public const string StubUrl = "http://localhost";

        public ProfileContext Profiles { get; }

        public Fixture(ConfigRoot config, HttpStatusCode postStatus = HttpStatusCode.OK, Profile? profile = null) {
            _spoolPath      = _home.PathTo("spool");
            _transcriptPath = _home.PathTo("transcript.jsonl");
            Config          = config;
            // The stub handler answers regardless of host, so any absolute URL will do — it has to be
            // absolute because the real POST helpers refuse a scheme-less one.
            Profiles        = profile is null
                ? Resolutions.At(StubUrl, config)
                : Resolutions.Of(profile, serverUrl: StubUrl);
            Spool           = new HookSpool(_spoolPath);

            var handler = new StubHandler(async req => {
                    var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                    var path = req.RequestUri!.AbsolutePath;
                    Sent.Add($"{path}|{body}");

                    if (path.StartsWith("/hooks/", StringComparison.Ordinal)) {
                        RouteOrder.Add(path.Replace("/hooks/", ""));
                    }

                    // The shared SessionStart memory-index GET is routed distinctly
                    // from the generic transcript-watermark GET below (which stays 404).
                    if (path == "/api/memories/index") {
                        MemoryIndexRequested  = true;
                        MemoryIndexRequestUri = req.RequestUri;
                        return new HttpResponseMessage(MemoryIndexStatus) {
                            Content = new StringContent(MemoryIndexBody, Encoding.UTF8, "application/json")
                        };
                    }

                    // GET watermark — return 404 so transcript backfill is a no-op without
                    // tripping the fail-open path.
                    if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);

                    if (SpendOnPost > TimeSpan.Zero) Clock.Advance(SpendOnPost);

                    if (HoldOnPost > TimeSpan.Zero) {
                        await Task.Delay(HoldOnPost);
                    }

                    return new HttpResponseMessage(postStatus);
                }
            );
            Client = new HttpClient(handler);
        }

        /// <summary>The hook's one clock. Fake, so its budget expires only when a test advances
        /// this — no test here can lose its budget to a loaded runner's scheduling.</summary>
        public FakeTimeProvider Clock { get; } = new();

        public Task<int> HandleAsync(string stdin) =>
            new CursorHookCommand(Config, Profiles, new HookClock(Clock), _home, new FixedCapacitorHttpClient()).HandleCore(
                Client,
                stdin: new StringReader(stdin),
                spool: Spool);

        public string SentToHook(string segment) =>
            Sent.First(s => s.StartsWith($"/hooks/{segment}", StringComparison.Ordinal)).Split('|', 2)[1];

        public IEnumerable<string> AllSentTo(string segment) =>
            Sent.Where(s => s.StartsWith($"/hooks/{segment}", StringComparison.Ordinal)).Select(s => s.Split('|', 2)[1]);

        public void Dispose() {
            Client.Dispose();
            _home.Dispose();
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }

    // Ignores the passed CancellationToken entirely — simulates a stdin read that would
    // hang regardless of the dispatcher deadline, so only the OUTER Task.WhenAny race
    // (not the inner read noticing cancellation) can possibly account for a prompt return.
    sealed class NeverCompletingReader : TextReader {
        public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
            Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None).ContinueWith(_ => "", TaskScheduler.Default);
    }

    // Honours the passed CancellationToken properly (unlike NeverCompletingReader above) —
    // it only ever completes by observing the token fire, then signals `Cancelled` before
    // throwing. Proves HandleCore's deadline branch deterministically cancels the abandoned
    // inner rather than merely disposing its CTS.
    sealed class CancelObservingReader : TextReader {
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken) {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => tcs.TrySetResult())) {
                await tcs.Task;
            }
            Cancelled.TrySetResult();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // The memory-index GET never resolves on its own — it only ends via the caller's own
    // cancellation, honoured properly (unlike StubHandler, which ignores its ct). Used to prove a
    // memory fetch cancelled at the budget deadline leaves the lease uncommitted rather than
    // committing a spurious "completed" record. Every other route answers as the fixture's stub
    // does, so the one client the hook is handed serves the lifecycle POST too. Entry is signalled
    // so the test advances the budget clock strictly after the request has entered.
    sealed class HangOnMemoryIndexHandler : HttpMessageHandler {
        volatile bool _entered;
        // Separate "never fetched at all", "fetched and abandoned", and "fetched and cancelled":
        // Entered distinguishes the first; Cancelled, completed only from the cancellation catch below,
        // the last. A signal rather than a bool snapshot so a caller awaits the handler actually
        // observing the token — the command's return is not ordered against that field being set.
        public bool Entered => _entered;
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Fires the moment SendAsync is entered, so the test advances the budget clock only after entry.
        public TaskCompletionSource EnteredSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.RequestUri!.AbsolutePath != "/api/memories/index")
                return new HttpResponseMessage(
                    request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.OK);

            _entered = true;
            EnteredSignal.TrySetResult();
            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } catch (OperationCanceledException) {
                Cancelled.TrySetResult();
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
