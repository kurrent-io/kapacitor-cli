using System.Net;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The settlement-retry lane's rolling no-progress window: a retryable 409's optional
/// <c>last_processed_seq</c> field (the daemon's sequenced-lane watermark at rejection time) re-arms
/// <see cref="McpFlowsServer.SettlementElapsedDeadline"/>'s 3-minute budget when it is the FIRST seq
/// observed or STRICTLY increases over the previous 409, up to the
/// <see cref="McpFlowsServer.SettlementAbsoluteDeadline"/> 8-minute hard cap. Every test here pins
/// the exact elapsed time the retry gave up at — "it eventually gave up" alone would pass under
/// several different (wrong) deadline compositions.
///
/// <para>Requests never take real time on the wire — a <see cref="SeqScriptedHandler"/> advances
/// the shared <see cref="VirtualFlowRetryClock"/> to an exact target elapsed time before returning
/// each scripted response, regardless of how much the backoff schedule's jitter already moved the
/// clock since the previous attempt (it only ever advances FORWARD to the target, by whatever delta
/// remains) — so the scripted seq transitions land at deterministic instants without needing a
/// zero-jitter backoff.</para>
/// </summary>
public class SettlementProgressWindowTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static VirtualFlowRetryClock Clock() => new();

    static string Busy409(long? seq) => seq is null
        ? """{"error":"flow_settlement_busy","message":"holding"}"""
        : $$"""{"error":"flow_settlement_busy","message":"holding","last_processed_seq":{{seq}}}""";

    /// <summary>Advances the shared clock to exactly <c>targetElapsed</c> (a no-op if
    /// already past it — attempts beyond the scripted list share the last scripted response without
    /// forcing any further advance) before returning the scripted 409 body for each successive
    /// call. Never throws past the end of the script: the tail entry repeats, which is what "held
    /// frozen thereafter" needs.</summary>
    sealed class SeqScriptedHandler(VirtualFlowRetryClock clock, params (TimeSpan TargetElapsed, long? Seq)[] script) : HttpMessageHandler {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var (targetElapsed, seq) = script[Math.Min(Requests, script.Length - 1)];
            Requests++;

            var target = clock.StartedAt + targetElapsed;
            var delta  = target - clock.UtcNow;
            if (delta > TimeSpan.Zero) clock.Advance(delta);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) {
                Content = new StringContent(Busy409(seq))
            });
        }
    }

    static async Task<McpFlowsServer.SettlementSendResult.DeadlineExhausted> RunToExhaustion(
            VirtualFlowRetryClock clock, HttpMessageHandler handler) {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://settlement.test") };

        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync("/start", null, ct), clock, SettlementBackoff.Seeded(7));

        var exhausted = result as McpFlowsServer.SettlementSendResult.DeadlineExhausted;
        await Assert.That(exhausted).IsNotNull();
        return exhausted!;
    }

    /// <summary>A seq that never changes, first seen at elapsed zero, exhausts at exactly the flat
    /// 3-minute window — the re-arm on first observation coincides with the window it was born with,
    /// so the frozen lane's original guarantee is untouched.</summary>
    [Test]
    public async Task Frozen_seq_exhausts_at_the_flat_3m_window() {
        var clock   = Clock();
        var handler = new SeqScriptedHandler(clock, (TimeSpan.Zero, 42L));

        var exhausted = await RunToExhaustion(clock, handler);

        await Assert.That(exhausted.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
    }

    /// <summary>The FIRST observation of a seq is itself progress evidence and re-arms the window
    /// from the moment it arrived. The first 409 here comes back at 2m30s carrying a seq: the caller
    /// has just been told the daemon lane's position, so it gets a full 3m from THAT instant
    /// (exhausting at 5m30s) rather than the 30s left of the flat window it was born with.
    ///
    /// <para>Same test pins the other half of the guarantee: every later attempt repeats that seq
    /// unchanged, so a lane frozen from its first observation onward still exhausts exactly one
    /// window (3m) after that observation — the re-arm buys a stalled lane no extra time at all.
    /// Mutation anchor: restoring the `lastSeq.HasValue &amp;&amp;` gate makes this exhaust at 3m.</para></summary>
    [Test]
    public async Task First_seq_observed_late_re_arms_the_window_from_its_arrival() {
        var clock   = Clock();
        var handler = new SeqScriptedHandler(clock, (TimeSpan.FromMinutes(2.5), 42L));

        var exhausted = await RunToExhaustion(clock, handler);

        var firstObservedAt = TimeSpan.FromMinutes(2.5);
        var expected        = firstObservedAt + McpFlowsServer.SettlementElapsedDeadline;

        await Assert.That(exhausted.Elapsed).IsEqualTo(expected);
        await Assert.That(clock.Elapsed).IsEqualTo(expected);

        // Not the un-re-armed answer, and nowhere near the absolute cap.
        await Assert.That(exhausted.Elapsed).IsNotEqualTo(McpFlowsServer.SettlementElapsedDeadline);
        await Assert.That(exhausted.Elapsed).IsLessThan(McpFlowsServer.SettlementAbsoluteDeadline);
    }

    /// <summary>The absolute cap still clips a late first observation followed by continuous
    /// progress: the last advance at 7m30s would re-arm the rolling window to 10m30s, but the run
    /// stops at the 8m cap measured from the first attempt.</summary>
    [Test]
    public async Task Late_first_seq_with_continuous_progress_is_still_clipped_by_the_8m_cap() {
        var clock = Clock();

        // First evidence at 2m30s, then a strict increase every 60s — each gap well inside the 3m
        // window, so only the cap can stop it. Nothing is scripted at or past 8m, so the test never
        // depends on how a virtual timeout races a handler's own clock advance mid-request.
        var script = new (TimeSpan, long?)[] {
            (TimeSpan.FromMinutes(2.5), 1L), (TimeSpan.FromMinutes(3.5), 2L), (TimeSpan.FromMinutes(4.5), 3L),
            (TimeSpan.FromMinutes(5.5), 4L), (TimeSpan.FromMinutes(6.5), 5L), (TimeSpan.FromMinutes(7.5), 6L)
        };
        var handler = new SeqScriptedHandler(clock, script);

        var exhausted = await RunToExhaustion(clock, handler);

        await Assert.That(exhausted.Elapsed).IsEqualTo(McpFlowsServer.SettlementAbsoluteDeadline);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementAbsoluteDeadline);
    }

    /// <summary>The headline rolling-window scenario: the seq advances at 1m and again at 2m30s,
    /// then freezes. The window resets on EACH advance, so exhaustion lands 3m after the LAST
    /// advance (≈5m30s) — not 3m from the start, and nowhere near the 8m absolute cap.</summary>
    [Test]
    public async Task Progress_then_stall_exhausts_3m_after_the_last_advance() {
        var clock = Clock();
        var handler = new SeqScriptedHandler(
            clock,
            (TimeSpan.Zero, 10L),                    // baseline
            (TimeSpan.FromMinutes(1), 20L),           // advance #1 -> resets deadline to 1m + 3m = 4m
            (TimeSpan.FromMinutes(2.5), 30L),         // advance #2 -> resets deadline to 2m30s + 3m = 5m30s
            (TimeSpan.FromMinutes(2.5), 30L));        // frozen thereafter (same seq every later attempt)

        var exhausted = await RunToExhaustion(clock, handler);

        var expected = TimeSpan.FromMinutes(2.5) + McpFlowsServer.SettlementElapsedDeadline;
        await Assert.That(exhausted.Elapsed).IsEqualTo(expected);
        await Assert.That(clock.Elapsed).IsEqualTo(expected);

        // Sanity: neither the naive flat-3m answer nor the 8m absolute cap.
        await Assert.That(exhausted.Elapsed).IsNotEqualTo(McpFlowsServer.SettlementElapsedDeadline);
        await Assert.That(exhausted.Elapsed).IsLessThan(McpFlowsServer.SettlementAbsoluteDeadline);
    }

    /// <summary>Continuous progress keeps re-arming the rolling window forever, so only the
    /// ABSOLUTE cap — 8 minutes from the first attempt — can ever stop it.</summary>
    [Test]
    public async Task Continuous_progress_stops_at_the_8m_absolute_cap() {
        var clock = Clock();

        // A strictly increasing seq every 60s, comfortably inside the 3m rolling window each time
        // so the window itself never lapses on its own. By the last entry (6m, resetting the window
        // to 6m + 3m = 9m) the rolling window has already been pushed past the 8m absolute cap, so
        // the effective deadline latches to the cap from then on — deliberately never scripting a
        // send AT OR PAST 8m itself, so the test doesn't depend on how a virtual timeout races a
        // handler's own clock advance mid-request.
        var script = Enumerable.Range(0, 7)
            .Select(i => (TimeSpan.FromMinutes(i), (long?)(i + 1)))
            .ToArray();
        var handler = new SeqScriptedHandler(clock, script);

        var exhausted = await RunToExhaustion(clock, handler);

        await Assert.That(exhausted.Elapsed).IsEqualTo(McpFlowsServer.SettlementAbsoluteDeadline);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementAbsoluteDeadline);
    }

    /// <summary>A restart/regression — the watermark drops below where it was, e.g. a daemon
    /// reconnect resetting its sequenced lane — must never count as progress. Real progress at 1m
    /// arms the window to 4m; the regression at 2m does NOT push it further out, so exhaustion still
    /// lands at 4m, not 5m (2m + 3m) or anything later.</summary>
    [Test]
    public async Task Seq_regression_counts_as_no_progress_and_does_not_extend() {
        var clock = Clock();
        var handler = new SeqScriptedHandler(
            clock,
            (TimeSpan.Zero, 100L),                 // baseline
            (TimeSpan.FromMinutes(1), 150L),        // real advance -> deadline = 1m + 3m = 4m
            (TimeSpan.FromMinutes(2), 5L),          // regression (5 < 150) -> no reset
            (TimeSpan.FromMinutes(2), 5L));         // held at the regressed value thereafter

        var exhausted = await RunToExhaustion(clock, handler);

        var expected = TimeSpan.FromMinutes(1) + McpFlowsServer.SettlementElapsedDeadline;
        await Assert.That(exhausted.Elapsed).IsEqualTo(expected);
        await Assert.That(exhausted.Elapsed).IsNotEqualTo(TimeSpan.FromMinutes(2) + McpFlowsServer.SettlementElapsedDeadline);
    }

    /// <summary>An EQUAL seq across attempts must not reset the window either — proving the gate is
    /// a strict `>`, not `>=`. A real advance at 1m arms the window to 4m; every attempt after that
    /// repeats the exact same seq, so exhaustion still lands at 4m.</summary>
    [Test]
    public async Task Equal_seq_counts_as_no_progress_proving_strict_increase() {
        var clock = Clock();
        var handler = new SeqScriptedHandler(
            clock,
            (TimeSpan.Zero, 7L),                  // baseline
            (TimeSpan.FromMinutes(1), 9L),         // real advance -> deadline = 1m + 3m = 4m
            (TimeSpan.FromMinutes(1.5), 9L),       // equal -> no reset
            (TimeSpan.FromMinutes(1.5), 9L));      // held equal thereafter

        var exhausted = await RunToExhaustion(clock, handler);

        var expected = TimeSpan.FromMinutes(1) + McpFlowsServer.SettlementElapsedDeadline;
        await Assert.That(exhausted.Elapsed).IsEqualTo(expected);
    }

    /// <summary>Old-server / never-reported compat: a 409 body that never carries
    /// <c>last_processed_seq</c> at all is "no progress evidence" — the retry falls back to
    /// EXACTLY today's flat 3-minute budget, and the coded rejection surfaced is the plain
    /// flow_settlement_busy body.
    ///
    /// <para>The backwards-compatibility half is asserted against the RENDERED user-facing text
    /// (<see cref="McpFlowsServer.FormatSettlementDeadlineError"/>) — the string an agent or user
    /// actually reads — not against <c>LastCode</c>, which is a fixed literal this fixture plants and
    /// therefore could never have carried an upgrade demand no matter what the production code did.
    /// Hard requirement: no path may tell a user their CLI/daemon is out of date. Mutation-checked by
    /// appending an upgrade sentence to that formatter, which fails exactly these two lines.</para></summary>
    [Test]
    public async Task Missing_seq_field_falls_back_to_the_flat_3m_budget() {
        var clock   = Clock();
        var handler = new SeqScriptedHandler(clock, (TimeSpan.Zero, null));

        var exhausted = await RunToExhaustion(clock, handler);

        await Assert.That(exhausted.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
        await Assert.That(exhausted.LastCode).IsEqualTo("flow_settlement_busy");
        await Assert.That(exhausted.LastMessage).IsEqualTo("holding");

        var rendered = McpFlowsServer.FormatSettlementDeadlineError(exhausted);

        // Non-vacuity guard: the rendered text really is the exhaustion message (so the two
        // DoesNotContain assertions below are running against real production output, not "").
        await Assert.That(rendered).Contains("flow_settlement_busy");
        await Assert.That(rendered).Contains("This is retryable");
        await Assert.That(rendered).DoesNotContain("upgrade");
        await Assert.That(rendered).DoesNotContain("outdated");
    }

    /// <summary>Parser unit coverage for <see cref="McpFlowsServer.TryParseLastProcessedSeq"/>
    /// directly, isolating the "absent vs. present-null vs. present-value" cases the retry loop
    /// depends on without going through a full retry run.</summary>
    [Test]
    [Arguments("""{"error":"flow_settlement_busy","message":"m"}""", null)]
    [Arguments("""{"error":"flow_settlement_busy","message":"m","last_processed_seq":null}""", null)]
    [Arguments("""{"error":"flow_settlement_busy","message":"m","last_processed_seq":5}""", 5L)]
    [Arguments("not json at all", null)]
    public async Task TryParseLastProcessedSeq_distinguishes_absent_null_and_present(string body, long? expected) {
        await Assert.That(McpFlowsServer.TryParseLastProcessedSeq(body)).IsEqualTo(expected);
    }
}
