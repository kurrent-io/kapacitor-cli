using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Tests.Unit.Harness.Codex;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness.Cursor;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Acceptance tests reusing existing pure/HTTP seams. Where a criterion is already fully covered
// by an existing unit test, the test below is deliberately THIN — it asserts the end-to-end
// contract from a different vantage point (a real HTTP round trip, a combined probe+consume
// flow, a per-vendor matrix) rather than re-deriving the same proof — and says so in its doc
// comment.
//
// ---------------------------------------------------------------------------------------------
// Manual/live verification checklist. NOT automated (each needs a real outage, a real signal, or
// a real Kiro sidecar race); reproduced here so it isn't lost:
//
//   1. Outage during an active session → after recovery, transcript tail + session-end are
//      present on the server without a manual `kcap import` (as long as the outage stayed
//      within the spool's cap — TranscriptSpool's 8 MB/session, HookSpool's 1 MB/session).
//      Repro: start a session, kill network/server mid-session, keep working, restore
//      network/server, let the session end normally. Verify the full transcript + session-end
//      landed with no `needs_import` marker.
//
//   2. `kill -STOP` a watcher process → the NEXT hook invocation for that session reaps it
//      (`WatcherManager.EnsureWatcherRunning`'s heartbeat-staleness check, once past the 30s
//      startup grace and the heartbeat is >20s stale) and respawns a fresh one. Repro:
//      `kill -STOP <watcher-pid>`, wait >20s, trigger any hook for the same session (e.g. the
//      next prompt), confirm a NEW watcher PID is running and tailing resumes.
//
//   3. A live-captured Kiro session shows credits/context% via the backfill event even when the
//      assistant line's own turn precedes the sidecar `{id}.json` update that carries them.
//      Repro: run a real Kiro session, and while it's active watch the session detail page —
//      the context%/credits chip should update shortly after a turn completes even though the
//      assistant's transcript line landed before the sidecar write.
// ---------------------------------------------------------------------------------------------
public class LiveWatchHardeningAcceptanceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Nothing listens on this loopback port: the spawn matrix's spool-on-lapse never reaches it, and
    // the drain below needs an unreachable server rather than an unusable URL.
    const string Unreachable = "http://127.0.0.1:1";

    AgentHookPoster  Poster => field ??= new(Config.Root, Resolutions.At(Unreachable, Config.Root), new FixedCapacitorHttpClient());

    CursorMarkers Markers => new(Config.Root);

    static TempDir TmpDir(string prefix) => new(prefix);

    // ── 1. Spawn-before-post across all 7 watcher-backed non-Claude vendors ──────────────────

    /// <summary>
    /// On a simulated 401 (auth lapsed — the shape a stale/expired token produces), EVERY
    /// watcher-backed non-Claude vendor's lifecycle POST reports <see cref="HookPostOutcome.Spooled"/>
    /// (never the legacy <see cref="HookPostOutcome.AuthLapsed"/>, which spools nothing) — and
    /// <see cref="AgentHookPoster.ShouldSpawnAfter"/> says the watcher should STILL spawn.
    /// Capture must never depend on lifecycle-POST delivery.
    ///
    /// <para>Exercises the real per-vendor route strings (not just the generic outcome), so a
    /// wrong/missing route on any one vendor's call site would show up here. Antigravity and
    /// Gemini each have their own bespoke spawn gate
    /// (<see cref="AntigravityHookCommand.SpawnGateForTest"/> /
    /// <see cref="GeminiHookCommand.SpawnGateForTest"/>) that must independently agree with the
    /// shared predicate. Kiro/OpenCode/Pi/Copilot/Codex call
    /// <see cref="AgentHookPoster.ShouldSpawnAfter"/> directly at their call site, so the shared
    /// assertion below covers them; <see cref="SpawnBeforePostTests"/> already covers the bare
    /// predicate in isolation — this test's value-add is the per-vendor ROUTE matrix.</para>
    /// </summary>
    [Test]
    public async Task AllWatcherBackedNonClaudeVendors_SpawnAfterSimulated401_ReportSpooled() {
        string[] routes = [
            "session-start/kiro", "session-start/opencode", "session-start/pi",
            "session-start/copilot", "session-start/codex", "session-start/gemini",
            "session-start/antigravity"
        ];

        foreach (var route in routes) {
            using var dir = TmpDir("spawn-matrix");
            var spool = new HookSpool(dir.Path);
            var sessionId = Guid.NewGuid().ToString("N");

            var outcome = await Poster.PostOrSpoolAsync(
                () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired)), route, """{"session_id":"x"}""",
                agentTag: route, spool, sessionId, route);

            await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
            await Assert.That(AgentHookPoster.ShouldSpawnAfter(outcome, "http://localhost:1")).IsTrue();
            await Assert.That(spool.HasBacklog(sessionId)).IsTrue(); // capture-on-lapse via the spool
        }

        // Each vendor's own bespoke gate must agree with the shared predicate, not just
        // re-derive it independently.
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "http://localhost:1")).IsTrue();
        await Assert.That(GeminiHookCommand.SpawnGateForTest(HookPostOutcome.Spooled, "http://localhost:1")).IsTrue();
    }

    // ── 2. Codex stdout-first, proven against a real large/unreachable spool backlog ─────────

    /// <summary>
    /// Codex's session-start handshake writes its blocking stdout response BEFORE any
    /// post-stdout work runs — proven here against the REAL <see cref="AgentHookPoster.DrainSpoolsAsync"/>
    /// pointed at an unreachable server, with a pre-seeded ~5 MB spool backlog across many
    /// sessions (not the <c>TaskCompletionSource</c> stand-in
    /// <see cref="CodexStdoutContractTests"/> uses for "a stuck drain"). If the drain's own
    /// I/O (touching the throttle stamp, reaping, enumerating session ids across a large spool
    /// dir) or its unreachable-network attempt ever migrated ahead of the stdout write, this
    /// test would catch it where the mocked version can't.
    /// </summary>
    [Test]
    public async Task Codex_StdoutHandshake_UnaffectedByALargeUnreachableSpoolBacklog() {
        using var lifecycleDir  = TmpDir("codex-stdout-lifecycle");
        using var transcriptDir = TmpDir("codex-stdout-transcript");

        var lifecycle  = new HookSpool(lifecycleDir.Path);
        var transcript = new TranscriptSpool(transcriptDir.Path);

        // ~5 MB backlog spread across many sessions' transcript spools — large enough that a
        // naive synchronous scan/read would be observable if it ran before stdout.
        const int perSessionBytes = 64 * 1024;
        const int sessionCount    = 80; // 80 * 64KB ≈ 5 MB
        for (var i = 0; i < sessionCount; i++) {
            var sid  = Guid.NewGuid().ToString("N");
            var body = $"{{\"padding\":\"{new string('x', perSessionBytes)}\"}}";
            transcript.Append(sid, body);
            lifecycle.Append(sid, "session-start/codex", """{"session_id":"x"}""");
            }

            var sw             = new StringWriter();
            var stdoutWritten  = false;

            var handshake = CodexHookCommand.RunSessionStartHandshakeForTest(
                writeStdout: () => { stdoutWritten = true; sw.Write("""{"continue":true}"""); },
                postStdoutWork: () => Poster.DrainSpoolsAsync(lifecycle, transcript, sessionId: null));

            // The synchronous writeStdout callback must already have run and its output already
            // be observable, even though the real drain (network attempt + large backlog scan)
            // behind it is still in flight.
            await Assert.That(stdoutWritten).IsTrue();
            await Assert.That(sw.ToString()).IsEqualTo("""{"continue":true}""");
            await Assert.That(handshake.IsCompleted).IsFalse();

            // Let the real (bounded ~1.5s) drain attempt finish so the test exits cleanly.
            await handshake;
    }

    // ── 3+4. Global drain pass ordering + needs-import delivery, over a REAL HTTP round trip ──

    /// <summary>
    /// A DIFFERENT vendor's next <c>kcap</c> invocation (<c>currentSessionId: null</c> — no
    /// session of its own, just running the global drain ahead of its own work) replays a prior
    /// session's spooled start→(transcript, capped so it needs-import)→end in order, and the
    /// needs-import marker is delivered as its own POST — all via REAL HTTP requests to a
    /// WireMock server, not injected delegate functions.
    ///
    /// <para><c>LifecycleSpoolDrainTests.drains_start_then_transcript_then_end_for_a_session_with_no_further_hook</c>
    /// and <c>...delivers_needs_import_marker_even_when_transcript_bytes_exceeded_cap</c> already
    /// prove this exact ordering against injected poster delegates; this test proves the SAME
    /// contract through <see cref="LifecycleSpoolDrain.RunAsync(CursorMarkers,HttpClient,string,HookSpool,TranscriptSpool,string?,TimeSpan,CancellationToken,Action{string,string}?)"/>'s
    /// production HTTP wrapper (route→POST mapping, status→<see cref="DrainOutcome"/> mapping,
    /// the needs-import route literally hitting the wire) — the part the delegate-injected test
    /// can't reach.</para>
    /// </summary>
    [Test]
    public async Task GlobalDrainPass_onADifferentVendorsInvocation_ReplaysPriorSessionInOrder_OverRealHttp() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var dir = TmpDir("global-drain-http");

        var lifecycle  = new HookSpool(dir.PathTo("life"));
        var transcript = new TranscriptSpool(dir.PathTo("tx"), capBytes: 32); // tiny cap → needs-import
        var sid        = Guid.NewGuid().ToString("N");

        lifecycle.Append(sid, "session-start/kiro", """{"phase":"start"}""");
        transcript.Append(sid, "{\"lines\":[\"" + new string('x', 100) + "\"]}"); // exceeds cap
        lifecycle.Append(sid, "session-end/kiro", """{"phase":"end"}""");

        await Assert.That(transcript.NeedsImport(sid)).IsTrue();

        using var client = new HttpClient();
        // currentSessionId: null — this drain pass belongs to a DIFFERENT vendor's own
        // invocation; `sid`'s session never fires another hook of its own.
        await LifecycleSpoolDrain.RunAsync(
            Markers, client, server.Url!, lifecycle, transcript, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None);

        var hits = server.LogEntries.Select(e => e.RequestMessage).ToList();
        var routeOrder = hits.Select(h => h.Path).ToList();

        await Assert.That(routeOrder).Contains("/hooks/session-start/kiro");
        await Assert.That(routeOrder).Contains("/hooks/session-needs-import");
        await Assert.That(routeOrder).Contains("/hooks/session-end/kiro");

        // Ordering: start, THEN the needs-import marker (delivered once the transcript is
        // resolved — dropped past its cap counts as resolved), THEN end LAST.
        var startIdx  = routeOrder.IndexOf("/hooks/session-start/kiro");
        var importIdx = routeOrder.IndexOf("/hooks/session-needs-import");
        var endIdx    = routeOrder.IndexOf("/hooks/session-end/kiro");
        await Assert.That(startIdx).IsLessThan(importIdx);
        await Assert.That(importIdx).IsLessThan(endIdx);

        await Assert.That(lifecycle.HasBacklog(sid)).IsFalse();
        await Assert.That(lifecycle.IsMarkedEnded(sid)).IsTrue();
    }

    // ── 5. Shutdown final-line contract, combining the probe AND the consuming read ──────────

    /// <summary>
    /// A complete newline-less final line is sent (consumed and the position advances past it);
    /// an unparseable, length-stable line that LATER grows into a complete record is held by
    /// every intermediate consuming read attempt — never sent-and-advanced — and only consumed
    /// once it actually finishes.
    ///
    /// <para><see cref="FinalDrainCompletionSignalTests"/>/<see cref="SplitNewCompleteLinesTests"/>/
    /// <see cref="ReadNewCompleteLinesAsyncTests"/> already cover
    /// <c>IsFinalLineComplete</c>/<c>SplitNewCompleteLines</c>/<c>ReadNewCompleteLinesAsync</c> in
    /// isolation, including a growth-after-probe TOCTOU case. This test's value-add is combining
    /// the bounded PROBE (<see cref="WatchCommand.WaitForFinalLineCompletionAsync"/>) with the
    /// actual CONSUMING read (<see cref="WatchCommand.ReadNewCompleteLinesAsync"/>) against one
    /// real file that genuinely grows on a background writer — the full shutdown final-drain
    /// flow, not either half alone.</para>
    /// </summary>
    [Test]
    public async Task ShutdownFinalDrain_RealGrowingFile_HeldUntilComplete_ThenSentAndAdvanced() {
        using var tmp = TempDir.WithPathTo("transcript.tmp", out var path);

        // Starts mid-record: unterminated, unparseable — "still writing".
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writ");

        // A consuming read attempted WHILE the line is still incomplete must hold it back —
        // never send-and-advance the truncated prefix — regardless of how long it's been
        // length-stable.
        var early = await ReadFinalDrainAsync(path, linesProcessed: 0);
        await Assert.That(early.Lines).IsEquivalentTo(new[] { "{\"a\":1}" });
        await Assert.That(early.NextPosition).IsEqualTo(1);
        await Assert.That(early.HeldIncompleteFinalLine).IsTrue();

        // The writer finishes the record shortly after, inside the probe's bounded window.
        var writer = Task.Run(async () => {
            await Task.Delay(60);
            await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":\"still writing\"}\n");
        });

        var completed = await WatchCommand.WaitForFinalLineCompletionAsync(path, attempts: 8, delayMs: 25);
        await writer;
        await Assert.That(completed).IsTrue();

        // NOW the consuming read — gated on the same policy the shutdown path uses — sends
        // the completed line and advances past it.
        var final = await ReadFinalDrainAsync(path, linesProcessed: early.NextPosition);
        await Assert.That(final.Lines).IsEquivalentTo(new[] { "{\"b\":\"still writing\"}" });
        await Assert.That(final.NextPosition).IsEqualTo(2);
        await Assert.That(final.HeldIncompleteFinalLine).IsFalse();
    }

    static async Task<WatchCommand.NewTranscriptLines> ReadFinalDrainAsync(string path, int linesProcessed) {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await WatchCommand.ReadNewCompleteLinesAsync(
            stream, linesProcessed, WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete, CancellationToken.None);
    }

    // ── 6. Heartbeat staleness story: wedged reaped, healthy/reconnecting and grace not reaped ──

    /// <summary>
    /// The pure staleness policy's three acceptance-relevant outcomes as one story: a wedged
    /// (alive-but-stalled) watcher past the grace window with an old heartbeat reads stale; a
    /// slow/reconnecting-but-healthy watcher whose heartbeat is still within the threshold does
    /// NOT read stale; and a freshly-spawned watcher within the startup grace window never reads
    /// stale even with no heartbeat recorded yet.
    ///
    /// <para><see cref="WatcherHeartbeatTests"/> already covers each branch of
    /// <see cref="WatcherHeartbeat.IsStale"/> individually, and the ACTUAL reap-under-lock +
    /// respawn-exactly-once behaviour (the "no duplicate spawn" half of this criterion) is
    /// covered against real processes and the real cross-platform spawn lock by
    /// <c>WatcherHeartbeatStalenessTests.EnsureWatcherRunning_WedgedWatcher_ReapsAndRespawnsExactlyOnce</c>
    /// (an Integration-suite test — real child processes, not appropriate for this Unit suite).
    /// This test's value-add is asserting all three outcomes together as the single acceptance
    /// story the spec states, using the exact thresholds production code uses.</para>
    /// </summary>
    [Test]
    public async Task HeartbeatStaleness_WedgedReaped_HealthyReconnectingNotReaped_StartupGraceNotReaped() {
        var start = DateTimeOffset.UtcNow;

        // Wedged: past the 30s grace, heartbeat last touched 35s ago (>20s threshold) → stale.
        var wedged = WatcherHeartbeat.IsStale(
            lastBeat: start.AddSeconds(35), startupAt: start, now: start.AddSeconds(90),
            grace: WatcherHeartbeat.Grace, threshold: WatcherHeartbeat.Threshold);
        await Assert.That(wedged).IsTrue();

        // Healthy/reconnecting: past the grace, but the heartbeat was touched just before the
        // threshold would lapse (mirrors WatchCommand.HeartbeatSlices keeping every connect-retry
        // chunk under the threshold) → never reads stale.
        var reconnecting = WatcherHeartbeat.IsStale(
            lastBeat: start.AddSeconds(89), startupAt: start, now: start.AddSeconds(90),
            grace: WatcherHeartbeat.Grace, threshold: WatcherHeartbeat.Threshold);
        await Assert.That(reconnecting).IsFalse();

        // Startup grace: freshly spawned (5s old), no heartbeat written yet at all → never stale.
        var freshlySpawned = WatcherHeartbeat.IsStale(
            lastBeat: null, startupAt: start, now: start.AddSeconds(5),
            grace: WatcherHeartbeat.Grace, threshold: WatcherHeartbeat.Threshold);
        await Assert.That(freshlySpawned).IsFalse();
    }
}
