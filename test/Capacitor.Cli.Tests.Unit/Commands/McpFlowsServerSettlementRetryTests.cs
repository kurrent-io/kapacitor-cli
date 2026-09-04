using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Covers the auto-retry for the two settlement-layer coded 409s (flow_settlement_busy /
/// reviewer_launch_incarnation_superseded): the low-level SendWithSettlementRetryAsync gate, its
/// wiring into the start path (HandleToolCallAsync), and the poll path (PollUntilTerminalAsync,
/// reached indirectly through HandleToolCallAsync).
/// </summary>
/// <remarks>
/// None of the 409 bodies below carry <c>last_processed_seq</c> — that is deliberate, not an
/// oversight. <c>SendWithSettlementRetryAsync</c> treats that field as progress evidence and
/// RE-ARMS its rolling no-progress window on the first one observed and on every advance (see
/// <c>SettlementProgressWindowTests.cs</c>); every fixed-elapsed-deadline assertion in this file
/// (e.g. the flat <c>SettlementElapsedDeadline</c>/<c>PollCap</c> exhaustion timings) relies on
/// that window never resetting. Adding <c>last_processed_seq</c> to any 409 fixture here — even
/// "for realism" — would silently start resetting the window and change these tests' pinned
/// timing assumptions without any test failing to flag it.
/// </remarks>
public class McpFlowsServerSettlementRetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    // Every wait in both retry lanes runs on the injected clock, so these tests are instant and
    // the requested schedule is directly assertable (VirtualFlowRetryClock.Delays).
    static VirtualFlowRetryClock Clock() => new();

    /// <summary>Unwraps the settled-response arm; fails loudly (cast) if the helper exhausted its
    /// deadline, which is never the expectation at these call sites.</summary>
    static HttpResponseMessage ResponseOf(McpFlowsServer.SettlementSendResult result) =>
        ((McpFlowsServer.SettlementSendResult.Response)result).Value;

    static JsonObject StartArguments() => new() {
        ["kind"]         = "code-review",
        ["target_kind"]  = "pr",
        ["target_ref"]   = "123",
        ["target_title"] = "some PR",
        ["context"]      = "some context"
    };

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    // === SettlementBackoff: the delay schedule shared by the POST and poll lanes ===
    //
    // Pinned formula (settlement-admission design §3.2 G): for retry n (1-based),
    // raw(n) = min(10s, 500ms · 2^(n−1)) with the cap applied BEFORE jitter, then equal jitter
    // delay(n) = raw(n)/2 + U(0, raw(n)/2), then truncation to the caller's remaining budget.

    [Test]
    [Arguments(1, 500)]
    [Arguments(2, 1_000)]
    [Arguments(3, 2_000)]
    [Arguments(4, 4_000)]
    [Arguments(5, 8_000)]
    [Arguments(6, 10_000)]   // capped
    [Arguments(7, 10_000)]
    [Arguments(40, 10_000)]  // a far-out ordinal must not overflow past the cap
    public async Task Backoff_raw_is_exponential_and_capped_at_ten_seconds(int retry, int expectedMs) {
        await Assert.That(SettlementBackoff.Raw(retry)).IsEqualTo(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Test]
    public async Task Backoff_applies_the_cap_before_jitter() {
        // Cap-before-jitter: a saturated ordinal jitters around the 10s CAP (5–10s), never around
        // the uncapped exponential (which at retry 8 would be 64s → 32–64s if capped afterwards).
        var low  = new SettlementBackoff(() => 0.0);
        var high = new SettlementBackoff(() => 0.999);

        await Assert.That(low.Delay(8, TimeSpan.FromHours(1))).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(high.Delay(8, TimeSpan.FromHours(1))).IsLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        await Assert.That(high.Delay(8, TimeSpan.FromHours(1))).IsGreaterThan(TimeSpan.FromSeconds(9));
    }

    [Test]
    public async Task Backoff_equal_jitter_puts_the_first_retry_in_250_to_500ms_and_steady_state_in_5_to_10s() {
        var backoff = SettlementBackoff.Seeded(4242);
        var budget  = TimeSpan.FromHours(1);   // never the binding constraint here

        for (var i = 0; i < 50; i++) {
            var first = backoff.Delay(1, budget);
            await Assert.That(first).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
            await Assert.That(first).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));

            var steady = backoff.Delay(9, budget);
            await Assert.That(steady).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
            await Assert.That(steady).IsLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task Backoff_truncates_to_the_remaining_budget() {
        var backoff = new SettlementBackoff(() => 0.999);   // ~the top of the jitter band

        // Budget shorter than the jittered delay -> exactly the budget, never past it.
        await Assert.That(backoff.Delay(6, TimeSpan.FromSeconds(2))).IsEqualTo(TimeSpan.FromSeconds(2));
        // Budget longer -> untruncated.
        await Assert.That(backoff.Delay(1, TimeSpan.FromHours(1))).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
        // Exhausted / negative budget -> zero, never a negative delay.
        await Assert.That(backoff.Delay(3, TimeSpan.Zero)).IsEqualTo(TimeSpan.Zero);
        await Assert.That(backoff.Delay(3, TimeSpan.FromSeconds(-5))).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task Backoff_is_deterministic_for_a_seeded_rng() {
        // Two independently seeded instances produce the identical sequence — which is what lets the
        // lane tests below assert the exact schedule the code under test will request.
        var a = SettlementBackoff.Seeded(99);
        var b = SettlementBackoff.Seeded(99);
        var budget = TimeSpan.FromHours(1);

        var fromA = Enumerable.Range(1, 8).Select(n => a.Delay(n, budget)).ToArray();
        var fromB = Enumerable.Range(1, 8).Select(n => b.Delay(n, budget)).ToArray();

        await Assert.That(fromA).IsEquivalentTo(fromB);
        // ...and it is a real schedule, not a constant.
        await Assert.That(fromA.Distinct().Count()).IsGreaterThan(1);
    }

    // === TryParseCodedError: pure decode, shared by FormatFlowStartError and the retry gate ===

    [Test]
    public async Task TryParseCodedError_decodes_code_and_message() {
        var ok = McpFlowsServer.TryParseCodedError(
            """{"error":"flow_settlement_busy","message":"try again"}""", out var code, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(code).IsEqualTo("flow_settlement_busy");
        await Assert.That(message).IsEqualTo("try again");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"message":"no code here"}""")]
    [Arguments("""{"error":""}""")]
    [Arguments("""{"error":123,"message":"wrong type"}""")]
    public async Task TryParseCodedError_returns_false_for_uncoded_or_malformed_bodies(string body) {
        var ok = McpFlowsServer.TryParseCodedError(body, out var code, out var message);

        await Assert.That(ok).IsFalse();
        await Assert.That(code).IsNull();
        await Assert.That(message).IsNull();
    }

    // === SendWithSettlementRetryAsync: the low-level gate, driven directly (fast, injectable delay) ===

    [Test]
    public async Task Settlement_busy_then_success_retries_transparently() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("settlement-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("settlement-busy")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f-new","status":"running"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = ResponseOf(await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct), clock, SettlementBackoff.Seeded(11)));
        var delays = clock.Delays;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(2);
        await Assert.That(delays).Count().IsEqualTo(1);
        // Equal jitter over the 500ms base: the first retry always lands in [250ms, 500ms].
        await Assert.That(delays[0]).IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(delays[0]).IsLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task Incarnation_superseded_then_success_retries_transparently() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("incarnation-superseded")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"reviewer_launch_incarnation_superseded","message":"superseded — retry."}"""));
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .InScenario("incarnation-superseded")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f-new","status":"running"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = ResponseOf(await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct), clock, SettlementBackoff.Seeded(11)));
        var delays = clock.Delays;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(2);
    }

    /// <summary>A server that never settles exhausts the ELAPSED deadline, not an attempt count:
    /// the helper keeps retrying on the shared schedule for the full 3 minutes of virtual time and
    /// then reports the last coded rejection it saw, with no live response attached.</summary>
    [Test]
    public async Task Exhaustion_of_the_elapsed_deadline_returns_the_last_coded_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"still racing"}"""));
        using var client = new HttpClient();

        var clock  = Clock();
        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct), clock, SettlementBackoff.Seeded(11));

        var exhausted = result as McpFlowsServer.SettlementSendResult.DeadlineExhausted;
        await Assert.That(exhausted).IsNotNull();
        await Assert.That(exhausted!.LastCode).IsEqualTo("flow_settlement_busy");
        await Assert.That(exhausted.LastMessage).IsEqualTo("still racing");
        await Assert.That(exhausted.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
        await Assert.That(exhausted.Attempts).IsEqualTo(server.LogEntries.Count);

        // Far past the old 3-attempt bound, and it never overshot the deadline.
        await Assert.That(exhausted.Attempts).IsGreaterThan(10);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
    }

    [Test]
    public async Task Different_coded_4xx_is_not_retried() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = ResponseOf(await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct), clock, SettlementBackoff.Seeded(11)));
        var delays = clock.Delays;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1); // no retry at all
        await Assert.That(delays).IsEmpty();
    }

    [Test]
    public async Task Uncoded_4xx_is_not_retried() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/start").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(400).WithBody("plain text error, not JSON"));
        using var client = new HttpClient();

        var clock = Clock();
        using var response = ResponseOf(await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync($"{server.Url}/start", null, ct), clock, SettlementBackoff.Seeded(11)));
        var delays = clock.Delays;

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        await Assert.That(delays).IsEmpty();
    }

    // === Elapsed deadline: token plumbing, request duration, caller cancellation ===

    /// <summary>A fake handler that lets a test observe (and react to) the token the helper actually
    /// hands to <see cref="HttpClient"/>, and simulate a request that HOLDS server-side.</summary>
    sealed class TokenObservingHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
        public int Requests { get; private set; }
        public List<CancellationToken> SeenTokens { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests++;
            SeenTokens.Add(ct);
            return await respond(ct);
        }
    }

    static HttpResponseMessage Busy() => new(HttpStatusCode.Conflict) {
        Content = new StringContent("""{"error":"flow_settlement_busy","message":"holding"}""")
    };

    /// <summary>The deadline token must genuinely reach HttpClient — this is impossible to pass if the
    /// helper creates a CTS but never threads it into the send. The handler holds the "request" past
    /// the deadline on the virtual clock and then observes its own token already cancelled.</summary>
    [Test]
    public async Task Deadline_token_reaches_the_in_flight_post_and_cancels_it() {
        var clock = Clock();

        var handler = new TokenObservingHandler(ct => {
            // The attempt holds server-side for longer than the whole elapsed budget.
            clock.Advance(McpFlowsServer.SettlementElapsedDeadline + TimeSpan.FromSeconds(30));
            ct.ThrowIfCancellationRequested();          // only possible if the token got through
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://settlement.test") };

        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync("/start", null, ct), clock, SettlementBackoff.Seeded(5));

        var exhausted = result as McpFlowsServer.SettlementSendResult.DeadlineExhausted;
        await Assert.That(exhausted).IsNotNull();
        await Assert.That(exhausted!.Attempts).IsEqualTo(1);
        await Assert.That(handler.Requests).IsEqualTo(1);           // abandoned, not retried past the deadline
        await Assert.That(handler.SeenTokens[0].CanBeCanceled).IsTrue();
        await Assert.That(handler.SeenTokens[0].IsCancellationRequested).IsTrue();
        // No coded body was ever read on this attempt, so there is nothing to report but the shape.
        await Assert.That(exhausted.LastCode).IsNull();
    }

    /// <summary>The budget counts REQUEST DURATION, not just the sum of the backoff delays — each
    /// attempt may itself hold on a server-side admission wait. Simulated 60s-holding busy responses
    /// therefore exhaust in a handful of attempts, well below the 10-minute MCP tool timeout.</summary>
    [Test]
    public async Task Elapsed_deadline_counts_request_duration_not_just_delay_sum() {
        var clock   = Clock();
        var handler = new TokenObservingHandler(_ => {
            clock.Advance(TimeSpan.FromSeconds(60));    // the server held this POST for a full admission wait
            return Task.FromResult(Busy());
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://settlement.test") };

        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync("/start", null, ct), clock, SettlementBackoff.Seeded(5));

        var exhausted = result as McpFlowsServer.SettlementSendResult.DeadlineExhausted;
        await Assert.That(exhausted).IsNotNull();
        await Assert.That(exhausted!.LastCode).IsEqualTo("flow_settlement_busy");
        // ~3 x 60s of held requests plus backoff — a delay-only budget would have allowed dozens.
        await Assert.That(handler.Requests).IsLessThanOrEqualTo(4);
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(TimeSpan.FromMinutes(10));   // under MCP_TOOL_TIMEOUT
        await Assert.That(clock.Elapsed).IsGreaterThanOrEqualTo(McpFlowsServer.SettlementElapsedDeadline);
    }

    /// <summary>Caller-token cancellation is NOT the helper's deadline: it rethrows untouched rather
    /// than being laundered into a deadline-exhausted result.</summary>
    [Test]
    public async Task Caller_token_cancellation_rethrows_untouched() {
        var clock = Clock();
        using var caller = new CancellationTokenSource();

        var handler = new TokenObservingHandler(ct => {
            caller.Cancel();                            // the CALLER gives up mid-flight
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://settlement.test") };

        await Assert.That(async () => await McpFlowsServer.SendWithSettlementRetryAsync(
                client, "https://flows.example.test", (c, ct) => c.PostAsync("/start", null, ct), clock, SettlementBackoff.Seeded(5), caller.Token))
            .Throws<OperationCanceledException>();
    }

    /// <summary>New-CLI burst degradation: enough slow predecessors ahead of this launch to cross the
    /// deadline yields the documented coded timeout tool result, not a fault and not a success.</summary>
    [Test]
    public async Task Burst_deeper_than_the_deadline_degrades_to_the_documented_coded_timeout() {
        var clock = Clock();

        // Every attempt lands behind a predecessor still settling, each holding ~50s server-side.
        var handler = new TokenObservingHandler(_ => {
            clock.Advance(TimeSpan.FromSeconds(50));
            return Task.FromResult(Busy());
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://settlement.test") };

        var result = await McpFlowsServer.SendWithSettlementRetryAsync(
            client, "https://flows.example.test", (c, ct) => c.PostAsync("/start", null, ct), clock, SettlementBackoff.Seeded(5));

        var exhausted = result as McpFlowsServer.SettlementSendResult.DeadlineExhausted;
        await Assert.That(exhausted).IsNotNull();

        var text = McpFlowsServer.FormatSettlementDeadlineError(exhausted!);
        await Assert.That(text).StartsWith("Error (flow_settlement_busy)");
        await Assert.That(text).Contains("retryable");
        await Assert.That(text).Contains($"{exhausted!.Attempts} attempts");
    }

    [Test]
    [Arguments(0, "0s")]
    [Arguments(45, "45s")]
    [Arguments(150, "2m 30s")]
    [Arguments(180, "3m")]
    public async Task Deadline_error_renders_elapsed_compactly(int seconds, string expected) {
        var text = McpFlowsServer.FormatSettlementDeadlineError(
            new McpFlowsServer.SettlementSendResult.DeadlineExhausted("flow_settlement_busy", "busy", 4, TimeSpan.FromSeconds(seconds)));

        await Assert.That(text).Contains($"over {expected}");
    }

    [Test]
    public async Task Deadline_error_singularizes_a_single_attempt_and_omits_an_absent_server_message() {
        var text = McpFlowsServer.FormatSettlementDeadlineError(
            new McpFlowsServer.SettlementSendResult.DeadlineExhausted(null, null, 1, TimeSpan.FromMinutes(3)));

        await Assert.That(text).Contains("1 attempt over 3m");
        await Assert.That(text).DoesNotContain("Last server message");
        // An attempt that never read a coded body still names the code this lane exists to absorb --
        // agents match on the token, so it stays stable.
        await Assert.That(text).StartsWith("Error (flow_settlement_busy)");

        // ...but it must NOT claim the daemon was settling. Nothing observed that: the deadline
        // cancelled the request before any coded response was parsed, and presenting the client's own
        // default as a server verdict is what the README used to describe.
        await Assert.That(text).Contains("this client's default");
        await Assert.That(text).DoesNotContain("still settling a prior launch");
    }

    // The other side of the discriminator: once a coded response HAS been seen, the cause is a real
    // observation and must still be stated.
    [Test]
    public async Task Deadline_error_states_the_settling_cause_when_a_coded_response_was_actually_seen() {
        var text = McpFlowsServer.FormatSettlementDeadlineError(
            new McpFlowsServer.SettlementSendResult.DeadlineExhausted(
                "flow_settlement_busy", "daemon busy", 6, TimeSpan.FromMinutes(3)));

        await Assert.That(text).Contains("still settling a prior launch");
        await Assert.That(text).DoesNotContain("this client's default");
        await Assert.That(text).Contains("Last server message: daemon busy");
    }

    // === Wired into the start path via HandleToolCallAsync (full dispatch) ===

    [Test]
    public async Task Start_review_flow_transparently_retries_settlement_busy_and_surfaces_no_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .InScenario("start-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .InScenario("start-busy")
              .WhenStateIs("second")
              // round_id/round_number null: a terminal-in-POST result, same shape the vendor-echo
              // tests use, so this test exercises ONLY the start-path retry — polling is covered
              // separately below.
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"f-fresh","status":"running","round_id":null,"round_number":null,"result_kind":null,"result_text":null}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("f-fresh");
        await Assert.That(text).DoesNotContain("flow_settlement_busy");

        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v2")).IsEqualTo(2);
    }

    /// <summary>The tool-boundary mapping: an exhausted elapsed deadline becomes a normal MCP tool
    /// error result carrying the last coded rejection plus attempt count and elapsed time — never an
    /// unhandled stdio fault, and never phrased as fatal (the busy IS retryable).</summary>
    [Test]
    public async Task Start_review_flow_maps_an_exhausted_deadline_to_a_coded_tool_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(3));

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();

        var text     = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        var attempts = server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/review/start/v2");

        await Assert.That(text).Contains("flow_settlement_busy");
        await Assert.That(text).Contains($"{attempts} attempts");
        await Assert.That(text).Contains("over 3m");
        await Assert.That(text).Contains("retryable");
        // The server's own last message rides along, so the caller sees what it kept hitting.
        await Assert.That(text).Contains("A concurrent settlement operation is racing this flow run.");

        // It genuinely used the elapsed budget rather than a small attempt cap.
        await Assert.That(attempts).IsGreaterThan(10);
        await Assert.That(clock.Elapsed).IsEqualTo(McpFlowsServer.SettlementElapsedDeadline);
    }

    [Test]
    public async Task Start_review_flow_does_not_retry_a_different_coded_4xx() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend for this run"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("budget_unverifiable");

        // Exactly one attempt — a different coded 4xx must not be retried.
        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v2")).IsEqualTo(1);
    }

    // === Wired into the poll path (PollUntilTerminalAsync), reached through HandleToolCallAsync ===

    [Test]
    public async Task Poll_path_transparently_retries_settlement_busy_and_returns_the_terminal_result() {
        const string flowRunId = "flow-poll-busy";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));

        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("poll-busy")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("poll-busy")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  $$"""{"flow_run_id":"{{flowRunId}}","round_number":1,"status":"closed","round_status":"clean","round_result_text":"all clean"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("all clean");
        await Assert.That(text).DoesNotContain("flow_settlement_busy");

        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(2);
    }

    /// <summary>The poll lane shares the POST lane's backoff SCHEDULE but keeps its own budget: it
    /// retries a settlement-busy GET on the exact same jittered ladder, bounded by the 8-minute
    /// PollCap rather than by an attempt count, and never overshoots that cap.</summary>
    [Test]
    public async Task Poll_lane_settlement_retries_follow_the_shared_schedule_and_stop_at_poll_cap() {
        const string flowRunId = "flow-poll-schedule";
        const int    seed      = 7;
        var          pollCap   = TimeSpan.FromMinutes(8);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));
        // Never settles — the lane must keep retrying until its own cap, not until an attempt count.
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"still settling"}"""));
        using var client = new HttpClient();

        var clock = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            clock: clock, backoff: SettlementBackoff.Seeded(seed));

        // The independently-seeded oracle: the same ladder, each rung truncated to what is left of
        // the cap. This is the schedule the lane must have requested, rung for rung.
        var expected  = new List<TimeSpan>();
        var oracle    = SettlementBackoff.Seeded(seed);
        var remaining = pollCap;
        for (var n = 1; remaining > TimeSpan.Zero; n++) {
            var next = oracle.Delay(n, remaining);
            if (next <= TimeSpan.Zero) break;
            expected.Add(next);
            remaining -= next;
        }

        await Assert.That(clock.Delays).IsEquivalentTo(expected);
        await Assert.That(clock.Delays.Count).IsGreaterThan(3);            // genuinely past the old 3-attempt bound
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(pollCap);     // never overshoots PollCap
        // ...and it is NOT subject to the POST lane's 3-minute elapsed deadline: this lane must be
        // able to keep polling well past it, all the way to its own cap.
        await Assert.That(clock.Elapsed).IsGreaterThan(McpFlowsServer.SettlementElapsedDeadline);

        // It stopped by exhausting the cap, not by turning the retryable busy into a hard error.
        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("Flow still running");
    }

    [Test]
    public async Task Poll_path_does_not_retry_a_different_coded_4xx() {
        const string flowRunId = "flow-poll-other";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null}"""));

        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"budget_unverifiable","message":"cannot verify spend for this run"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("budget_unverifiable");

        // Exactly one GET — a different coded 4xx must fail immediately, no retry.
        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(1);
    }
}
