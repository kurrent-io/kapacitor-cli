using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Vendor-bearing catalog start routing (B3 /v4 primary with /v2 fallback) plus the
/// requested/applied echo defense-in-depth check.
/// </summary>
public class McpFlowsServerVendorOverrideTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static JsonObject StartArguments(string? vendor = null) {
        var args = new JsonObject {
            ["kind"]         = "code-review",
            ["target_kind"]  = "pr",
            ["target_ref"]   = "123",
            ["target_title"] = "some PR",
            ["context"]      = "some context"
        };

        if (vendor is not null) args["vendor"] = vendor;

        return args;
    }

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    // === StartFlowAsync: vendor threading + route selection ===

    [Test]
    public async Task StartFlowAsync_with_vendor_posts_to_v4_with_protocol_4_and_carries_vendor() {
        // §2.7 B3: a park-capable client routes every vendor-bearing catalog start to /v4
        // (protocol 4). A current server has the route, so a single POST lands on /v4 — no v2 hit.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"claude"}"""));
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, StartArguments("claude"), cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);

        var hit  = server.LogEntries.Single();
        await Assert.That(hit.RequestMessage.Path).IsEqualTo("/api/flows/review/start/v4");

        var body = JsonNode.Parse(hit.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["vendor"]!.GetValue<string>()).IsEqualTo("claude");
        await Assert.That(body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(4);
    }

    [Test]
    public async Task StartFlowAsync_with_vendor_falls_back_to_v2_when_v4_is_absent() {
        // Cross-rollout safety: an older server lacks /v4 and 404s it. The client must fall back to
        // the pre-B3 /v2 route (protocol 2) — two POSTs, /v4 then /v2 — and still succeed. On that
        // path a resumable park later reports the legacy participant_stopped, which is still a
        // resubmit trigger, so a crossed round never stalls.
        using var server = WireMockServer.Start();
        // /v4 deliberately left unstubbed — WireMock's default 404 simulates the old server.
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"claude"}"""));
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, StartArguments("claude"), cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(2);
        await Assert.That(server.LogEntries.ElementAt(0).RequestMessage.Path).IsEqualTo("/api/flows/review/start/v4");
        await Assert.That(server.LogEntries.ElementAt(1).RequestMessage.Path).IsEqualTo("/api/flows/review/start/v2");

        // The fallback body carries protocol 2 (the v4 attempt's protocol-4 body is discarded on 404).
        var v2Body = JsonNode.Parse(server.LogEntries.ElementAt(1).RequestMessage.Body!)!.AsObject();
        await Assert.That(v2Body["vendor"]!.GetValue<string>()).IsEqualTo("claude");
        await Assert.That(v2Body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(2);
    }

    [Test]
    public async Task StartFlowAsync_without_vendor_posts_to_v2_and_omits_vendor() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, StartArguments(vendor: null), cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);

        var hit = server.LogEntries.Single();
        await Assert.That(hit.RequestMessage.Path).IsEqualTo("/api/flows/review/start/v2");

        // WhenWritingNull byte-compat discipline (matching RequesterMachineId/DefinitionYaml):
        // an omitted vendor must never appear on the wire at all, not even as "vendor":null.
        await Assert.That(hit.RequestMessage.Body).DoesNotContain("vendor");
    }

    [Test]
    public async Task StartFlowAsync_always_posts_catalog_starts_to_v2_when_vendor_is_absent() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        // The versioned route is deliberately left unstubbed: WireMock's default 404 for it
        // proves the no-override path never touches it.
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, StartArguments(vendor: null), cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // === CheckVendorOverrideResult: pure decision logic ===

    [Test]
    [Arguments("submit_review_round")]
    [Arguments("send_to_participant")]
    [Arguments("get_flow_status")]
    public async Task CheckVendorOverrideResult_is_a_noop_for_tools_that_never_carry_vendor(string toolName) {
        var result = McpFlowsServer.CheckVendorOverrideResult(
            toolName, requestedVendor: "claude", HttpStatusCode.NotFound, isSuccess: false, postBody: "", out var flowRunIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(flowRunIdToClose).IsNull();
    }

    [Test]
    [Arguments("start_review_flow")]
    [Arguments("start_flow")]
    public async Task CheckVendorOverrideResult_404_requires_protocol_v2_even_without_explicit_vendor(string toolName) {
        var result = McpFlowsServer.CheckVendorOverrideResult(
            toolName, requestedVendor: null, HttpStatusCode.NotFound, isSuccess: false, postBody: "", out var flowRunIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(result.Value.Message).Contains("protocol v2");
        await Assert.That(flowRunIdToClose).IsNull();
    }

    [Test]
    public async Task CheckVendorOverrideResult_404_fails_closed_with_an_upgrade_message() {
        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.NotFound, isSuccess: false, postBody: "", out var flowRunIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(result.Value.Message).Contains("upgrade the kcap server");
        // Nothing ever started on a 404 — there is no run id to close.
        await Assert.That(flowRunIdToClose).IsNull();
    }

    [Test]
    public async Task CheckVendorOverrideResult_success_with_matching_echo_is_a_noop() {
        var body = """{"flow_run_id":"f1","status":"running","applied_reviewer_vendor":"claude"}""";

        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.OK, isSuccess: true, body, out var flowRunIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(flowRunIdToClose).IsNull();
    }

    [Test]
    public async Task CheckVendorOverrideResult_success_with_mismatched_echo_fails_and_names_the_run_to_close() {
        var body = """{"flow_run_id":"f1","status":"running","applied_reviewer_vendor":"codex"}""";

        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.OK, isSuccess: true, body, out var flowRunIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(result.Value.Message).Contains("claude");
        await Assert.That(result.Value.Message).Contains("codex");
        await Assert.That(flowRunIdToClose).IsEqualTo("f1");
    }

    [Test]
    public async Task CheckVendorOverrideResult_wrongtyped_echo_is_a_hard_mismatch_but_still_salvages_the_run_id() {
        // A 2xx body whose applied_reviewer_vendor is the wrong TYPE (a number, not a string) must
        // NOT throw past this method (which would skip the defensive close): it reads as "no valid
        // echo" → hard mismatch, and the still-valid flow_run_id is salvaged for the close.
        var body = """{"flow_run_id":"f1","applied_reviewer_vendor":123}""";

        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.OK, isSuccess: true, body, out var flowRunIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(flowRunIdToClose).IsEqualTo("f1");
    }

    [Test]
    public async Task CheckVendorOverrideResult_malformed_body_is_a_hard_mismatch_with_no_run_id() {
        // A non-JSON / non-object body can't be parsed — still a hard mismatch (the applied vendor
        // was never confirmed), with no run id to close.
        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.OK, isSuccess: true, postBody: "not json", out var flowRunIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(flowRunIdToClose).IsNull();
    }

    [Test]
    public async Task CheckVendorOverrideResult_non_success_non_404_is_a_noop_generic_failure_handles_it() {
        // A 5xx (or any other non-2xx that isn't the versioned-route 404) is left to the existing
        // generic FormatFlowStartError handling — this check only ever intercepts the 404 and the
        // post-success echo cases.
        var result = McpFlowsServer.CheckVendorOverrideResult(
            "start_review_flow", requestedVendor: "claude", HttpStatusCode.InternalServerError, isSuccess: false, postBody: "boom", out var flowRunIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(flowRunIdToClose).IsNull();
    }

    // === HandleToolCallAsync: full dispatch, WireMock-backed ===

    [Test]
    public async Task New_CLI_old_server_404_fails_closed_before_any_close_call() {
        using var server = WireMockServer.Start();
        // Both /v4 and its /v2 fallback are deliberately left unstubbed — WireMock's default 404 for
        // each simulates a server that predates /v4 AND reviewer vendor override. The final /v2 404
        // is the one the vendor-override guard fails closed on.
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments("claude")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("upgrade the kcap server");

        // Nothing ever started — there is no flow_run_id to close, and close must never be called.
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/close"))).IsFalse();
    }

    [Test]
    public async Task Echo_match_returns_normal_result_with_no_close_call() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"claude"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments("claude")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();

        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/close"))).IsFalse();
    }

    [Test]
    public async Task Echo_mismatch_fails_and_closes_the_run_defensively() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"codex"}"""));
        server.Given(Request.Create().WithPath("/api/flows/f1/close").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments("claude")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("claude");
        await Assert.That(text).Contains("codex");

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/f1/close")).IsEqualTo(1);
    }

    [Test]
    public async Task Echo_mismatch_close_failure_is_swallowed_mismatch_error_still_returned() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_vendor":"codex"}"""));
        server.Given(Request.Create().WithPath("/api/flows/f1/close").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments("claude")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("claude");
        await Assert.That(text).Contains("codex");
    }
}
