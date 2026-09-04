using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Task 13 — end-to-end acceptance tests for the Cursor tailing-watcher promotion,
/// composing the pure/HTTP seams Tasks 7-12 already built (mirroring
/// <c>LiveWatchHardeningAcceptanceTests</c>'s style: thin tests proving the contract from
/// a fresh vantage point, not re-deriving coverage an earlier task's unit tests already own).
/// A real SignalR wire round trip (the watcher's own <c>HubConnection</c>) is out of scope here —
/// this project has no in-process SignalR host, so the drain/ack path itself is unit-tested
/// server-side (<c>SendTranscriptBatchAckedWireTests</c>) and CLI-side via the pure
/// <see cref="WatchCommand.ReadNewCompleteLinesAsync"/>/<see cref="CursorRewriteGuard"/> seams.
///
/// <para>Covers the four acceptance scenarios from the plan/spec:</para>
/// <list type="number">
/// <item><b>Force-quit mid-turn → tail drained.</b> A transcript whose final line has no
/// trailing newline yet (the shape a killed Cursor agent leaves behind mid-write) is HELD by
/// every live-drain read and only CONSUMED by the shutdown final drain — the exact two-phase
/// contract <c>WatchCommand.RunWatch</c>'s final-drain call exercises
/// (<see cref="ForceQuitMidTurn_HeldByLiveDrain_ThenConsumedByFinalDrain"/>).</item>
/// <item><b>Idle-ceiling exit, without Cursor synthesizing its own session-end.</b>
/// <see cref="IdleCeiling_EndsTheCursorSession_ButSuppressesSessionEndSynthesis"/> — Cursor has
/// exactly one owner for end synthesis (the <c>sessionEnd</c> hook, or the server-side
/// lease-gated sweep backstop); the watcher must exit on the ceiling WITHOUT itself posting
/// session-end (that "sweep closes" half of the criterion is proven server-side by
/// <c>StaleActiveSessionReaperTests.CursorSweep_LeasePresent_AbsentAndIdle_Reaps</c>, Task 5 —
/// this CLI repo has no read-model/sweep visibility to re-prove it).</item>
/// <item><b>Open-but-idle past ceiling+threshold → closed, next hook reactivates</b> —
/// exercised via BOTH resume shapes, driving the REAL <see cref="CursorHookCommand.HandleCore"/>
/// dispatcher end to end (not just the pure <c>ShouldSpawnWatcher</c> predicate
/// <c>CursorWatcherSpawnTests</c> already covers): a <c>sessionStart</c> resume
/// (<see cref="Reactivation_ViaSessionStartHook_SpawnsAFreshWatcher"/>) and a NON-<c>sessionStart</c>
/// resume hook — the every-hook spawn path
/// (<see cref="Reactivation_ViaNonSessionStartHook_SpawnsAFreshWatcher"/>).</item>
/// </list>
/// </summary>
[NotInParallel] // shares the WatcherManager.SpawnOverrideForTesting / KCAP_WATCHER_DIR statics
                // with WatcherLifecycleTests / WatcherHeartbeatStalenessTests (bare NotInParallel
                // — no explicit key — puts all of them in the same implicit mutual-exclusion bucket).
public class CursorTailingWatcherTests {
    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    static readonly TempDir Tmp = new();

    static string? _previousWatcherDir;

    [Before(Class)]
    public static void SetUp() {
        _previousWatcherDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", Tmp.Path);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", _previousWatcherDir);
        Tmp.Dispose();
    }

    [After(Test)]
    public void ResetOverridesAndConfigDir() {
        Cli.WatcherManager.SpawnOverrideForTesting = null;
    }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ── 1. Force-quit mid-turn → tail drained ─────────────────────────────────────────────────

    /// <summary>
    /// Simulates a Cursor agent force-quit mid-write of its final transcript line: the process
    /// dies before flushing the trailing newline, but the bytes already on disk parse as a
    /// complete JSON record. Every LIVE drain (the watcher's normal per-poll read) must hold that
    /// line back — consuming a truncated write would risk dropping a still-growing line — but the
    /// SHUTDOWN final drain (what <c>RunWatch</c> runs right before exit, whether the exit is
    /// StopWatcher, idle-ceiling, or parent-exit) must consume it: a force-quit means nothing will
    /// ever write the newline, so holding it forever would silently lose the last turn.
    /// </summary>
    [Test]
    public async Task ForceQuitMidTurn_HeldByLiveDrain_ThenConsumedByFinalDrain() {
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("session.jsonl");
        // Two flushed lines, then a force-quit mid-write of the third: complete JSON, no
        // trailing newline (Cursor writes the record body first, the newline last).
        await File.WriteAllTextAsync(
            transcriptPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"first"}]}}""" + "\n" +
            """{"role":"assistant","message":{"content":[{"type":"text","text":"second"}]}}""" + "\n" +
            """{"role":"user","message":{"content":[{"type":"text","text":"force-quit line"}]}}"""
        );

        await using (var liveStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            var liveDrain = await WatchCommand.ReadNewCompleteLinesAsync(
                liveStream, linesProcessed: 0, WatchCommand.IncompleteFinalLinePolicy.Hold, CancellationToken.None);

            // Only the two flushed lines — the unterminated third is held, and NextPosition
            // stays before it so a later drain re-reads it once complete. HeldIncompleteFinalLine
            // is true here too (there IS a real held partial) — it just isn't acted on by a live
            // drain; only the final drain below treats it as a needs-import signal.
            await Assert.That(liveDrain.Lines.Count).IsEqualTo(2);
            await Assert.That(liveDrain.NextPosition).IsEqualTo(2);
            await Assert.That(liveDrain.HeldIncompleteFinalLine).IsTrue();
        }

        // The agent process is gone — nothing will ever append the trailing newline. The
        // shutdown final drain must still recover the line rather than losing the turn.
        await using (var finalStream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
            var finalDrain = await WatchCommand.ReadNewCompleteLinesAsync(
                finalStream, linesProcessed: 2, WatchCommand.IncompleteFinalLinePolicy.ConsumeIfComplete, CancellationToken.None);

            await Assert.That(finalDrain.Lines.Count).IsEqualTo(1);
            await Assert.That(finalDrain.Lines[0]).Contains("force-quit line");
            await Assert.That(finalDrain.NextPosition).IsEqualTo(3);
            await Assert.That(finalDrain.HeldIncompleteFinalLine).IsFalse(); // consumed, not held
        }
    }

    // ── 2. Idle-ceiling exit without Cursor synthesizing its own session-end ─────────────────

    /// <summary>
    /// Combines the two decisions <c>RunWatch</c> makes back to back at its idle-ceiling exit:
    /// <see cref="WatchCommand.ShouldEndOnIdle"/> (should the watcher exit?) and
    /// <see cref="WatchCommand.CursorSuppressesEndPost"/> (should it also POST session-end on the
    /// way out?). For Cursor these must diverge from every other idle-ceiling vendor: the watcher
    /// exits, but it must NOT be the one to synthesize session-end (that stays owned by the
    /// sessionEnd hook / the server-side lease sweep — see the class doc for where "sweep closes"
    /// is actually proven). Codex/Antigravity, by contrast, DO post it themselves.
    /// </summary>
    [Test]
    public async Task IdleCeiling_EndsTheCursorSession_ButSuppressesSessionEndSynthesis() {
        var now   = DateTimeOffset.UtcNow;
        var idle  = now.AddMinutes(-61);
        var idleTimeout = TimeSpan.FromMinutes(60);

        var cursorShouldExit = WatchCommand.ShouldEndOnIdle(
            vendor: "cursor", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: idleTimeout);
        await Assert.That(cursorShouldExit).IsTrue();

        var cursorSuppresses = WatchCommand.CursorSuppressesEndPost(vendor: "cursor", idleExit: cursorShouldExit);
        await Assert.That(cursorSuppresses).IsTrue();

        // Contrast: Codex hits the SAME idle-ceiling exit condition but must NOT suppress —
        // the suppression is Cursor-specific, not "every idle-ceiling vendor".
        var codexShouldExit = WatchCommand.ShouldEndOnIdle(
            vendor: "codex", isSessionWatcher: true, thresholdReached: true,
            lastActivityAt: idle, now: now, idleTimeout: idleTimeout);
        await Assert.That(codexShouldExit).IsTrue();
        await Assert.That(WatchCommand.CursorSuppressesEndPost(vendor: "codex", idleExit: codexShouldExit)).IsFalse();

        // And Cursor's OTHER exit paths (not idle-ceiling) are unaffected by the suppression —
        // e.g. an explicit StopWatcher-driven exit (idleExit=false) still posts normally.
        await Assert.That(WatchCommand.CursorSuppressesEndPost(vendor: "cursor", idleExit: false)).IsFalse();
    }

    // ── 3. Open-but-idle past ceiling → closed, next hook reactivates (both resume shapes) ──

    /// <summary>
    /// A prior watcher for this session is gone (no pid file — "closed", whether by the
    /// idle-ceiling exit above or the server-side sweep). The very next <c>sessionStart</c> hook
    /// — Cursor's own resume/reconnect event — must spawn a fresh watcher, driven through the
    /// REAL <see cref="CursorHookCommand.HandleCore"/> dispatcher (JSON parsing, event-map
    /// resolution, the sessionStart-specific spawn-before-POST ordering) rather than calling the
    /// spawn predicate directly.
    /// </summary>
    [Test]
    public async Task Reactivation_ViaSessionStartHook_SpawnsAFreshWatcher() {
        using var tmp = new TempDir();
        var sessionId      = NewSessionId();
        var transcriptPath = tmp.PathTo($"{sessionId}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, """{"role":"user","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        Cli.WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.PathTo("spool"));

        var body = $$"""{"hook_event_name":"sessionStart","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath.Replace(@"\", @"\\")}}"}""";
        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(client, new StringReader(body), spool);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spawned).IsEquivalentTo([sessionId]);
    }

    /// <summary>
    /// The "every-hook spawn path" half of the same criterion: a resume that arrives via ANY
    /// non-terminal, non-<c>sessionStart</c> hook (here <c>postToolUse</c>, Cursor's own
    /// telemetry-only event) must ALSO spawn a fresh watcher once its own lifecycle POST
    /// succeeds — the recovery-spawn precedence <c>CursorHookCommand.ShouldSpawnWatcher</c>
    /// encodes, driven here through the real dispatcher rather than the pure predicate alone.
    /// </summary>
    [Test]
    public async Task Reactivation_ViaNonSessionStartHook_SpawnsAFreshWatcher() {
        using var tmp = new TempDir();
        var sessionId      = NewSessionId();
        var transcriptPath = tmp.PathTo($"{sessionId}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, """{"role":"user","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        Cli.WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.PathTo("spool"));

        var body = $$"""{"hook_event_name":"postToolUse","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath.Replace(@"\", @"\\")}}","tool_name":"Bash"}""";
        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(client, new StringReader(body), spool);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spawned).IsEquivalentTo([sessionId]);
    }

    // ── 5. Resume at an unterminated final line, then its terminator arrives → no line drift (r6) ──

    /// <summary>
    /// end-to-end regression for the P1 finding: a watcher resumes at a
    /// server frontier that lands exactly on a COMPLETE-but-unterminated final record (the r5
    /// scenario — a prior process's shutdown final drain already sent and the server already
    /// acked that record), then Cursor appends the record's own trailing newline before writing
    /// its NEXT record (Cursor's normal write order: body, then '\n', then the next record).
    ///
    /// Before this fix, <c>SeedCursorByteOffsetAsync</c> seeded <c>CursorByteOffset</c> at EOF
    /// while leaving <c>LinesProcessed</c> at the final record's own (already-acked) line number.
    /// The next poll's bounded read then started exactly at that EOF, saw the freshly-appended
    /// leading <c>'\n'</c>, and — because <see cref="WatchCommand.ReadNewCompleteLinesAsync"/>
    /// seeds its line index from <c>LinesProcessed</c> — misread it as closing a NEW, phantom
    /// empty line, then labelled the real next record one line too high, permanently: the server,
    /// still waiting at the true frontier, would see a persistent apparent gap while the watcher
    /// kept resending from the stale offset.
    ///
    /// This composes the same two pure seams the rest of this class uses instead of a live
    /// SignalR round trip: <see cref="WatchCommand.SeedCursorByteOffsetAsync"/> (the resume) and
    /// <see cref="WatchCommand.ReadNewCompleteLinesAsync"/> (the next poll's bounded read), fed
    /// from the SAME <c>WatchState</c> fields <c>RunWatch</c>/<c>DrainNewLines</c> thread between
    /// them in production.
    /// </summary>
    [Test]
    public async Task ResumeAtUnterminatedFinalLine_ThenTerminatorArrives_NextRecordKeepsItsTrueLineNumber() {
        using var tmp = new TempDir();
        var sessionId = NewSessionId();
        try {
            var transcriptPath = tmp.PathTo("session.jsonl");
            // Line 0 (terminated) + line 1, complete but not yet terminated — exactly what a
            // prior watcher's shutdown final drain sent and the server already acknowledged.
            await File.WriteAllTextAsync(transcriptPath, "{\"a\":1}\n{\"b\":2}");

            var guard = new CursorRewriteGuard(Config.Root, sessionId);
            // The server resumes this fresh watcher process at line 2 (1-based: 2 lines already
            // acked — line 0 and the unterminated line 1).
            var state = new WatchState { LinesProcessed = 2 };

            var seeded = await Watch.SeedCursorByteOffsetAsync(
                state, lineNumber: 2, sessionId, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            await Assert.That(seeded).IsTrue();
            await Assert.That(Markers.IsQuarantined(sessionId)).IsFalse();
            // Rewound to the unterminated record's own start, NOT EOF — and LinesProcessed
            // rewound with it, so the two frontiers stay in lockstep.
            await Assert.That(state.CursorByteOffset).IsEqualTo(8L);  // "{\"a\":1}\n".Length
            await Assert.That(state.LinesProcessed).IsEqualTo(1);

            // Cursor now appends the terminator for the already-acked line 1, followed by the
            // NEXT record — its normal write order (body, then '\n', then the next record's body).
            await File.AppendAllTextAsync(transcriptPath, "\n{\"c\":3}\n");

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var drainRead = await WatchCommand.ReadNewCompleteLinesAsync(
                stream, state.LinesProcessed, WatchCommand.IncompleteFinalLinePolicy.Hold, CancellationToken.None,
                captureRawBytes: true, rawBytesReadFrom: state.CursorByteOffset, newRangeByteOffset: state.CursorByteOffset);

            // The rewound line 1 ("{"b":2}") is re-read/re-sent — harmless, the server's
            // source-ack frontier dedupes a resend at/behind it. No phantom empty line appears
            // between it and the next record.
            await Assert.That(drainRead.Lines).IsEquivalentTo(["{\"b\":2}", "{\"c\":3}"]);
            await Assert.That(drainRead.LineNumbers).IsEquivalentTo([1, 2]);

            // The critical assertion: {"c":3} is NOT shifted to line 3 — it keeps its true,
            // natural 0-indexed position (2), immediately after line 1.
            var newRecordIndex = drainRead.Lines.IndexOf("{\"c\":3}");
            await Assert.That(drainRead.LineNumbers[newRecordIndex]).IsEqualTo(2);

            // Byte/line frontiers stay aligned — NextPosition (the next LinesProcessed value) is
            // exactly 3, matching the file's true 3 complete lines; no permanent gap opens up.
            await Assert.That(drainRead.NextPosition).IsEqualTo(3);
        } finally {
            try { File.Delete(Markers.QuarantinePath(sessionId)); } catch { }
        }
    }
}
