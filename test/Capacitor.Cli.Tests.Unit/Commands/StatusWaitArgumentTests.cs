using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Covers the optional `wait: true` argument on get_review_flow_status/get_flow_status (§6 of the
/// review-flows liveness-supervision design): absent or false must be byte-for-byte today's
/// single-GET behavior — the whole backwards-compat contract for every existing caller that never
/// sends the argument — while `wait: true` polls the SAME endpoint until the round is terminal or
/// the shared 8-minute PollCap elapses, sharing the round-submission poll lane's per-attempt
/// timeout, poll cadence, and transient-failure budget (mirrored here as local constants —
/// PollInterval/PollCap/MaxTransientRetries are private to McpFlowsServer, exactly like the existing
/// poll-path tests in McpFlowsServerSettlementRetryTests.cs already do).
/// </summary>
public class StatusWaitArgumentTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static readonly TimeSpan PollInterval       = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollCap            = TimeSpan.FromMinutes(8);
    const           int      MaxTransientRetries = 5;

    static VirtualFlowRetryClock Clock() => new();

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    static (string Text, bool IsError) Unwrap(string response) {
        var result   = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        var text     = result["content"]![0]!["text"]!.GetValue<string>();
        var isError  = result["isError"]?.GetValue<bool>() ?? false;
        return (text, isError);
    }

    // === Compat: wait absent/false must never diverge from today's single GET ===

    [Test]
    public async Task Wait_absent_issues_exactly_one_GET() {
        const string flowRunId = "flow-nowait-absent";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1}"""));
        using var client = new HttpClient();

        // A VIRTUAL clock is injected here even though the compat (no-wait) path never touches it:
        // if a routing bug ever sent this call into the wait branch by mistake, a real clock would
        // make the mutant hang for up to 8 minutes of actual wall time instead of failing fast on
        // the assertion below — this is what let the earlier hand-verification of this exact guard
        // (mutating ParseWaitArg to always return true) surface as a real-time hang rather than a
        // clean failure. Keeping the virtual clock here means a future regression fails in
        // milliseconds instead.
        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("status: running");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_false_issues_exactly_one_GET() {
        const string flowRunId = "flow-nowait-false";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1}"""));
        using var client = new HttpClient();

        // See the sibling absent-wait test above for why a virtual clock is injected even on this
        // compat path.
        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = false }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("status: running");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(1);
    }

    [Test]
    public async Task Wait_non_boolean_throws_a_clean_tool_error() {
        const string flowRunId = "flow-wait-badtype";

        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = "yes" }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("wait must be a boolean");
        // Never even reached the network — a malformed argument fails before any GET.
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    // === wait: true — polls until the round is terminal, then stops ===

    [Test]
    public async Task Wait_true_polls_until_round_terminal_then_stops_polling() {
        const string flowRunId = "flow-wait-terminal";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("wait-terminal").WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("wait-terminal").WhenStateIs("second").WillSetStateTo("third")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("wait-terminal").WhenStateIs("third")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"clean","last_result_kind":"clean","last_result_text":"all clean"}"""));
        using var client = new HttpClient();

        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("all clean");
        await Assert.That(text).Contains("status: closed");

        // Shape, not just outcome: exactly 3 GETs (2 non-terminal + the terminal one) — proves it
        // STOPPED polling once terminal rather than merely happening to return that text.
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(3);
        // Exactly 2 poll-interval waits between the 3 GETs — no wait after the terminal response.
        await Assert.That(clock.Delays).IsEquivalentTo([PollInterval, PollInterval]);
    }

    [Test]
    public async Task Wait_true_on_get_flow_status_alias_also_polls_to_terminal() {
        const string flowRunId = "flow-wait-alias";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("alias").WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"pair-flow","target_title":"t","round_count":0}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("alias").WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"failed","definition_id":"pair-flow","target_title":"t","round_count":1}"""));
        using var client = new HttpClient();

        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        // Run-level "failed" is terminal even with no round_status at all — the run itself is done.
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("status: failed");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(2);
    }

    // === wait: true — caps at the 8-minute PollCap ===

    [Test]
    public async Task Wait_true_caps_at_eight_minutes_and_returns_the_benign_still_running_text() {
        const string flowRunId = "flow-wait-cap";

        using var server = WireMockServer.Start();
        // Never terminal — the lane must stop by exhausting its own cap, not an attempt count.
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        using var client = new HttpClient();

        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("Flow still running");
        await Assert.That(text).Contains(flowRunId);
        await Assert.That(text).Contains("get_review_flow_status");

        // The load-bearing shape assertion: elapsed virtual time is EXACTLY the 8-minute cap, not
        // merely "some delay happened" — proves the cap is what stopped it.
        await Assert.That(clock.Elapsed).IsEqualTo(PollCap);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}"))
            .IsEqualTo((int)(PollCap.TotalSeconds / PollInterval.TotalSeconds));
    }

    // === wait: true — transient-failure budget matches the round-submission poll lane's rule ===

    [Test]
    public async Task Wait_true_gives_up_after_five_consecutive_server_errors() {
        const string flowRunId = "flow-wait-5xx";

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));
        using var client = new HttpClient();

        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains($"poll failed after {MaxTransientRetries} consecutive server errors");

        // Exactly MaxTransientRetries + 1 attempts (the one that trips the budget), matching the
        // round-submission poll lane's own "give up on the (N+1)th consecutive failure" rule.
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}"))
            .IsEqualTo(MaxTransientRetries + 1);
        await Assert.That(clock.Delays).Count().IsEqualTo(MaxTransientRetries);
    }

    [Test]
    public async Task Wait_true_resets_the_transient_budget_on_a_successful_response() {
        const string flowRunId = "flow-wait-reset";

        using var server = WireMockServer.Start();
        // Two failures, then success, twice — a reset budget never trips even though 4 failures
        // occur across the whole call (more than MaxTransientRetries if it didn't reset).
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WillSetStateTo("s1")
              .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WhenStateIs("s1").WillSetStateTo("s2")
              .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WhenStateIs("s2").WillSetStateTo("s3")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WhenStateIs("s3").WillSetStateTo("s4")
              .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WhenStateIs("s4").WillSetStateTo("s5")
              .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .InScenario("reset").WhenStateIs("s5")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"clean","last_result_kind":"clean","last_result_text":"all clean"}"""));
        using var client = new HttpClient();

        var clock    = Clock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = flowRunId, ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("all clean");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{flowRunId}")).IsEqualTo(6);
    }
}
