using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// One tool call, ONE budget. <c>SettlementAbsoluteDeadline</c> (8m) and <c>PollCap</c> (8m) run
/// SEQUENTIALLY inside a single <c>HandleToolCallAsync</c> — the settlement retry returns a response,
/// then <c>ResolveRoundResultAsync</c> starts polling — so bounding them independently bounded the
/// call at ~16m against the ~10-minute MCP tool timeout the kcap plugin pins. A call that burned real
/// settlement time could then be killed by the harness mid-poll with a reviewer already launched and
/// paid for. <see cref="McpFlowsServer.ToolCallBudget"/> is the shared budget both lanes now draw
/// from, anchored immediately before the first POST attempt.
///
/// <para>Both tests assert the TOTAL elapsed time, not merely that the call ended — "it terminated"
/// passes under every wrong composition of these two deadlines.</para>
/// </summary>
public class ToolCallBudgetTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static JsonObject StartArguments() => new() {
        ["kind"]         = "code-review",
        ["target_kind"]  = "pr",
        ["target_ref"]   = "123",
        ["target_title"] = "some PR",
        ["context"]      = "some context"
    };

    static JsonObject ToolCallRequest() => new() {
        ["params"] = new JsonObject {
            ["name"]      = "start_review_flow",
            ["arguments"] = StartArguments().DeepClone()
        }
    };

    const string Busy = """{"error":"flow_settlement_busy","message":"holding"}""";

    /// <summary>Mirrors <c>McpFlowsServer.PollCap</c> (private). Pinned locally rather than exposed:
    /// if the production value moves, these tests must be re-derived deliberately, not silently follow.</summary>
    static readonly TimeSpan PollCap = TimeSpan.FromMinutes(8);

    /// <summary>Simulates a settlement-aware server that ABSORBS the admission wait by holding the
    /// POST open (the real behaviour the elapsed — not delay-summed — settlement deadline exists for):
    /// the first POST advances virtual time by <paramref name="settlementHold"/> and then 409s, the
    /// next POST succeeds with a running round, and every subsequent GET 409s forever so the poll lane
    /// runs to whatever deadline it was given.</summary>
    sealed class HoldThenPollHandler(VirtualFlowRetryClock clock, TimeSpan settlementHold) : HttpMessageHandler {
        public int Posts { get; private set; }
        public int Gets  { get; private set; }

        /// <summary>Elapsed time at the instant the settlement lane finished — i.e. the moment the poll
        /// lane begins. Everything after this is the poll lane's own window.</summary>
        public TimeSpan SettlementSpent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Post) {
                Posts++;
                if (Posts == 1) {
                    clock.Advance(settlementHold);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(Busy) });
                }

                SettlementSpent = clock.Elapsed;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(
                        """{"flow_run_id":"flow-budget","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}""")
                });
            }

            Gets++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(Busy) });
        }
    }

    async Task<(VirtualFlowRetryClock Clock, HoldThenPollHandler Handler, string Response)> RunAsync(TimeSpan settlementHold) {
        var clock   = new VirtualFlowRetryClock();
        var handler = new HoldThenPollHandler(clock, settlementHold);

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://budget.test") };

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest(), client, "http://budget.test",
            cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(7));

        return (clock, handler, response);
    }

    /// <summary>The defect, pinned by arithmetic: a call whose settlement lane genuinely held for 2m50s
    /// must NOT then get a fresh 8-minute poll. Total elapsed lands exactly on
    /// <see cref="McpFlowsServer.ToolCallBudget"/> — so the poll got budget MINUS the settlement hold,
    /// not the full <c>PollCap</c>. Pre-fix this same run ends at 2m50s + 8m = 10m50s, past the harness
    /// timeout; the strict assertion below fails on that value rather than merely tolerating it.</summary>
    [Test]
    public async Task Settlement_time_is_deducted_from_the_poll_lane_not_added_to_it() {
        var hold = TimeSpan.FromSeconds(170); // 2m50s — real hold, still inside the 3m no-progress window

        var (clock, handler, response) = await RunAsync(hold);

        // Both lanes really ran: the settlement retry re-POSTed after the hold, and the poll lane
        // then issued GETs. Without this the elapsed assertion could be satisfied by a call that
        // never reached one of the lanes at all.
        await Assert.That(handler.Posts).IsEqualTo(2);
        await Assert.That(handler.Gets).IsGreaterThan(3);

        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.ToolCallBudget);

        // Stated the other way round, as the property that actually matters: the poll lane received
        // strictly less than its own cap, short by exactly what the settlement lane spent.
        var pollWindow = clock.Elapsed - handler.SettlementSpent;
        await Assert.That(handler.SettlementSpent).IsGreaterThanOrEqualTo(hold);
        await Assert.That(pollWindow).IsEqualTo(McpFlowsServer.ToolCallBudget - handler.SettlementSpent);
        await Assert.That(pollWindow).IsLessThan(PollCap);

        // Ended on the graceful cap, not as an error — exhausting the budget is not a failure.
        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        await Assert.That(result["result"]!["content"]![0]!["text"]!.GetValue<string>()).Contains("Flow still running");
    }

    /// <summary>Positive control / backwards-compatibility: with no settlement hold at all — the
    /// overwhelmingly common case, and the shape every pre-existing settlement fixture has — the poll
    /// lane still gets its full <c>PollCap</c>, unchanged. The clip only bites a call that genuinely
    /// spent settlement time, which is what makes the test above a statement about the CLIP rather than
    /// about a globally shortened poll.</summary>
    [Test]
    public async Task No_settlement_hold_leaves_the_poll_lane_at_its_full_cap() {
        var (clock, handler, _) = await RunAsync(TimeSpan.Zero);

        await Assert.That(handler.Posts).IsEqualTo(2);

        // The poll lane's own window is the FULL cap — the clip did not bite. (Total elapsed is a few
        // hundred ms more: the one settlement backoff before the successful re-POST, which is real
        // spend and correctly comes off the shared budget, just not off PollCap here.)
        await Assert.That(clock.Elapsed - handler.SettlementSpent).IsEqualTo(PollCap);
        await Assert.That(clock.Elapsed).IsLessThan(McpFlowsServer.ToolCallBudget);
    }
}
