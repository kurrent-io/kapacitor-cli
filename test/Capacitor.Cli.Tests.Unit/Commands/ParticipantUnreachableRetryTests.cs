using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Covers the round-submit auto-retry for the coded, eventually-retryable
/// <c>participant_unreachable</c> 409 (liveness supervision: an inactivity-stopped reviewer's
/// absence isn't durably proven yet at the moment the driver submits its next round).
///
/// <para>Scope is deliberately narrow: only <c>submit_review_round</c> / <c>send_to_participant</c>
/// pass <c>extraRetryableCode</c> — <see cref="McpFlowsServerSettlementRetryTests"/> and
/// <see cref="SettlementProgressWindowTests"/> already pin that <c>start_review_flow</c>/<c>start_flow</c>
/// and the poll-GET lanes are untouched (they never pass the new parameter, so its default of
/// <c>null</c> preserves their behavior exactly). This file only needs to cover the round-submit
/// dispatch and the shared low-level gate's handling of the new code.</para>
///
/// <para>None of the fixtures below carry <c>last_processed_seq</c> — participant_unreachable never
/// does (there is no daemon sequenced-lane watermark for "absence not yet proven"), so every
/// exhaustion here lands at the FLAT <see cref="McpFlowsServer.SettlementElapsedDeadline"/> (3
/// minutes), never a rolling-window extension.</para>
/// </summary>
public class ParticipantUnreachableRetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static VirtualFlowRetryClock Clock() => new();

    static JsonObject SubmitArguments(string flowRunId = "flow-liveness-1") => new() {
        ["flow_run_id"] = flowRunId,
        ["context"]     = "please address the findings above"
    };

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    const string ParticipantUnreachableBody =
        """{"error":"participant_unreachable","message":"participant 'reviewer' is not reachable and its absence is not yet proven — retry after the server has proven the prior agent absent."}""";

    // === Required test 1: 409 then success -> transparent retry, round proceeds ===

    [Test]
    public async Task Participant_unreachable_then_success_retries_and_the_round_proceeds() {
        const string flowRunId = "flow-heals-1";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
              .InScenario("heals")
              .WillSetStateTo("healed")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(ParticipantUnreachableBody));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
              .InScenario("heals")
              .WhenStateIs("healed")
              // Terminal-in-POST result (round_id/round_number null) — isolates the round-submit
              // retry from the separate poll lane, exactly like the settlement-busy start tests do.
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"flow-heals-1","status":"running","round_id":null,"round_number":null,"result_kind":null,"result_text":null}"""));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("submit_review_round", SubmitArguments(flowRunId)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(11));

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains(flowRunId);
        await Assert.That(text).DoesNotContain("participant_unreachable");

        // Exactly two attempts: the refused round-submit, then the retry the server healed against.
        var attempts = server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}/rounds");
        await Assert.That(attempts).IsEqualTo(2);
    }

    // === Required test 2: persistently-unreachable -> gives up within the bound, message intact ===

    [Test]
    public async Task Persistently_unreachable_gives_up_within_the_flat_3m_bound_and_keeps_the_message() {
        const string flowRunId = "flow-never-heals";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(ParticipantUnreachableBody));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("submit_review_round", SubmitArguments(flowRunId)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(11));

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();

        // The coded rejection surfaces verbatim — never silent — and the server's own message
        // ("...retry after the server has proven the prior agent absent.") rides along intact.
        await Assert.That(text).StartsWith("Error (participant_unreachable)");
        await Assert.That(text).Contains(
            "participant 'reviewer' is not reachable and its absence is not yet proven — retry after " +
            "the server has proven the prior agent absent.");
        await Assert.That(text).Contains("retryable");

        var attempts = server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}/rounds");
        await Assert.That(text).Contains($"{attempts} attempts");

        // Pin the SHAPE, not just "it eventually gave up": genuinely retried well past a small
        // attempt-count cap, and gave up at the flat elapsed deadline exactly — never the 8-minute
        // absolute cap (there is no seq evidence to ever extend the rolling window for this code).
        await Assert.That(attempts).IsGreaterThan(10);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
    }

    // === Required test 3: stays within the shared ToolCallBudget ===

    /// <summary>Mirrors <c>ToolCallBudgetTests</c>' composition, entered via <c>participant_unreachable</c>
    /// instead of <c>flow_settlement_busy</c>: real time genuinely spent retrying the round-submit POST
    /// must come OFF the round-poll lane's budget, not stack on top of it. First POST(s) hold real
    /// virtual time then heal; the round accepted is a real (non-terminal-in-POST) running round, so
    /// the poll lane starts; every subsequent GET stays busy forever so the poll runs to whatever cap
    /// it was left with. Total elapsed must land exactly on <see cref="McpFlowsServer.ToolCallBudget"/>,
    /// never past it.</summary>
    sealed class HealsAfterHoldThenPollsHandler(VirtualFlowRetryClock clock, TimeSpan unreachableHold) : HttpMessageHandler {
        public int Posts { get; private set; }
        public int Gets  { get; private set; }
        public TimeSpan SettlementSpent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.Method == HttpMethod.Post) {
                Posts++;
                if (Posts == 1) {
                    clock.Advance(unreachableHold);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) {
                        Content = new StringContent(ParticipantUnreachableBody)
                    });
                }

                SettlementSpent = clock.Elapsed;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(
                        """{"flow_run_id":"flow-budget-2","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}""")
                });
            }

            Gets++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) {
                Content = new StringContent("""{"error":"flow_settlement_busy","message":"still settling"}""")
            });
        }
    }

    [Test]
    public async Task Participant_unreachable_retry_time_is_deducted_from_the_poll_lane_not_added_to_it() {
        var clock   = new VirtualFlowRetryClock();
        var hold    = TimeSpan.FromSeconds(150); // 2m30s — real hold, still inside the 3m flat window
        var handler = new HealsAfterHoldThenPollsHandler(clock, hold);

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://budget.test") };

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("submit_review_round", SubmitArguments("flow-budget-2")),
            client, "http://budget.test", cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(7));

        // Both lanes genuinely ran.
        await Assert.That(handler.Posts).IsEqualTo(2);
        await Assert.That(handler.Gets).IsGreaterThan(3);

        // The headline bound: total elapsed can NEVER exceed the shared budget.
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(McpFlowsServer.ToolCallBudget);
        // ...and in this scenario (a real, sub-deadline hold followed by a poll lane that never
        // terminates) it lands EXACTLY on the budget — the poll lane got the remainder, not a fresh cap.
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.ToolCallBudget);

        var pollWindow = clock.Elapsed - handler.SettlementSpent;
        await Assert.That(handler.SettlementSpent).IsGreaterThanOrEqualTo(hold);
        await Assert.That(pollWindow).IsEqualTo(McpFlowsServer.ToolCallBudget - handler.SettlementSpent);
        await Assert.That(pollWindow).IsLessThan(TimeSpan.FromMinutes(8)); // strictly less than the standalone PollCap

        // Ended gracefully (still running), not as an error.
        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        await Assert.That(result["result"]!["content"]![0]!["text"]!.GetValue<string>()).Contains("Flow still running");
    }

    // === Required test 4: a non-retryable coded error is untouched (scoping proof) ===

    [Test]
    [Arguments("stale_round_token", "that round already completed — discard this submission.")]
    [Arguments("run_closed", "the flow run is closed — discard this message.")]
    [Arguments("server_catching_up", "projections are still replaying.")]
    public async Task Other_coded_errors_on_round_submit_are_still_not_retried(string code, string message) {
        const string flowRunId = "flow-other-code";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  $$"""{"error":"{{code}}","message":"{{message}}"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("submit_review_round", SubmitArguments(flowRunId)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains(code);

        // Exactly one attempt — proves this is a SCOPED retry (participant_unreachable only), not a
        // blanket "retry every coded 409 on the round-submit path" change.
        var attempts = server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}/rounds");
        await Assert.That(attempts).IsEqualTo(1);
    }

    // === Low-level gate coverage: SendWithSettlementRetryAsync's extraRetryableCode parameter ===

    [Test]
    public async Task Default_extraRetryableCode_leaves_participant_unreachable_unretried() {
        // Sanity: every OTHER call site (start_review_flow/start_flow, and any direct call that omits
        // extraRetryableCode) must see participant_unreachable exactly as it saw any other unrelated
        // coded 4xx before this change — a single attempt, no retry.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(ParticipantUnreachableBody));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = (await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct),
            clock, SettlementBackoff.Seeded(11)) as McpFlowsServer.SettlementSendResult.Response)!.Value;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        await Assert.That(clock.Delays).IsEmpty();
    }

    [Test]
    public async Task ExtraRetryableCode_only_matches_the_exact_code_named() {
        // A code that is neither a settlement code nor the caller's named extra code is still refused,
        // even when an extra code IS supplied — proves the gate is an exact-match addition, not a
        // widened "any coded 409" allowance.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = (await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/rounds", null, ct),
            clock, SettlementBackoff.Seeded(11), extraRetryableCode: McpFlowsServer.ParticipantUnreachableCode)
            as McpFlowsServer.SettlementSendResult.Response)!.Value;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExtraRetryableCode_requires_the_response_actually_be_409() {
        // The server only ever raises participant_unreachable as 409. A different status carrying
        // the same coded envelope (a misbehaving proxy, or any future non-409 reuse of the code)
        // must NOT be treated as the retryable case — it must surface on the first attempt, exactly
        // like an unrelated coded error would.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(ParticipantUnreachableBody));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = (await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/rounds", null, ct),
            clock, SettlementBackoff.Seeded(11), extraRetryableCode: McpFlowsServer.ParticipantUnreachableCode)
            as McpFlowsServer.SettlementSendResult.Response)!.Value;

        // The real status is surfaced untouched, and exactly one attempt was made — no retry budget
        // was burned on a status the server never actually uses for this code.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        await Assert.That(clock.Delays).IsEmpty();
    }

    [Test]
    public async Task Non_409_participant_unreachable_surfaces_immediately_end_to_end() {
        const string flowRunId = "flow-wrong-status";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody(ParticipantUnreachableBody));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("submit_review_round", SubmitArguments(flowRunId)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(11));

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("participant_unreachable");

        var attempts = server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}/rounds");
        await Assert.That(attempts).IsEqualTo(1);
    }

    // === FormatSettlementDeadlineError: code-aware cause text ===

    [Test]
    public async Task Deadline_error_states_the_absence_not_proven_cause_for_participant_unreachable() {
        var text = McpFlowsServer.FormatSettlementDeadlineError(
            new McpFlowsServer.SettlementSendResult.DeadlineExhausted(
                "participant_unreachable", "still not proven absent", 6, TimeSpan.FromMinutes(3)));

        await Assert.That(text).StartsWith("Error (participant_unreachable)");
        await Assert.That(text).Contains("has not yet proven the previous reviewer agent");
        await Assert.That(text).Contains("Last server message: still not proven absent");
        // Never the settlement-busy-specific wording, which would misattribute the cause.
        await Assert.That(text).DoesNotContain("still settling a prior launch");
        await Assert.That(text).DoesNotContain("another review flow already running");
    }
}
