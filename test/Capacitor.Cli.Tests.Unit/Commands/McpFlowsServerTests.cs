using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    // The ack retry's 2s wait now runs on the injected clock, so these tests stay instant while
    // still asserting the real schedule (VirtualFlowRetryClock.Delays records every requested wait).
    static VirtualFlowRetryClock Clock() => new();

    [Test]
    public async Task start_review_flow_description_discloses_the_paid_hosted_reviewer() {
        // Contract: the paid-cost disclosure the Pi bridge (and every harness that shows the flows
        // tool descriptions) relies on must actually be present in the tools/list output.
        var startReviewFlow = McpFlowsServer.BuildToolsList().Single(t => t.Name == "start_review_flow");

        await Assert.That(startReviewFlow.Description).Contains("PAID");
        await Assert.That(startReviewFlow.Description.ToLowerInvariant()).Contains("hosted reviewer");
    }

    [Test]
    public async Task start_flow_description_requires_explicit_intent_and_discloses_paid_models() {
        var startFlow = McpFlowsServer.BuildToolsList().Single(t => t.Name == "start_flow");

        await Assert.That(startFlow.Description).Contains("EXPLICIT INTENT");
        await Assert.That(startFlow.Description.ToLowerInvariant()).Contains("paid hosted models");
    }

    [Test]
    public async Task Roundless_start_renders_started_envelope() {
        var body = """{"flow_run_id":"f1","round_id":null,"round_number":null,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":null,"reviewer_session_id":null}""";

        var text = McpFlowsServer.TryFormatRoundlessStart(body, out var pendingIds);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("flow_run_id: f1");
        await Assert.That(text).Contains("status: running");
        await Assert.That(text).Contains("send_to_participant");
        await Assert.That(pendingIds).IsEmpty();
    }

    [Test]
    public async Task Roundless_start_renders_and_exposes_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_id":null,"round_number":null,"status":"running",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"early note","received_at":"2026-07-06T00:00:00Z"}
             ]}
            """;

        var text = McpFlowsServer.TryFormatRoundlessStart(body, out var pendingIds);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("pending_messages (1):");
        await Assert.That(text).Contains("- from tester [msg-a1]: early note");
        await Assert.That(pendingIds).IsEquivalentTo(new[] { "msg-a1" });
    }

    [Test]
    public async Task Single_participant_start_with_round_is_not_roundless() {
        var body = """{"flow_run_id":"f1","round_id":"r1","round_number":1,"status":"running"}""";
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart(body, out _)).IsNull();
    }

    [Test]
    public async Task Unparseable_body_is_not_roundless() {
        await Assert.That(McpFlowsServer.TryFormatRoundlessStart("not json", out _)).IsNull();
    }

    [Test]
    public async Task Wrong_typed_pending_fields_render_empty_instead_of_throwing() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":123,"from_participant":{"x":1},"text":"still shown","received_at":"2026-07-06T00:00:00Z"},
                {"message_id":"msg-ok","from_participant":"tester","text":"fine","received_at":"2026-07-06T00:00:00Z"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        await Assert.That(text).Contains("- from  []: still shown");
        await Assert.That(text).Contains("- from tester [msg-ok]: fine");
        await Assert.That(pendingIds).IsEquivalentTo(new[] { "msg-ok" });
    }

    [Test]
    public async Task Status_response_renders_pending_messages_and_returns_ids() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/","received_at":"2026-07-06T00:00:00Z"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched","received_at":"2026-07-06T00:00:01Z"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var firstIndex  = text.IndexOf("from tester [msg-a1]: found a broken symlink in scripts/", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]: heads-up, migration file also touched", StringComparison.Ordinal);
        await Assert.That(firstIndex).IsGreaterThan(-1);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);

        // The existing (id-discarding) overload still works and renders the same text.
        var thinWrapperText = McpFlowsServer.FormatStatusResponse(body);
        await Assert.That(thinWrapperText).IsEqualTo(text);
    }

    [Test]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t"}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t","pending_messages":null}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t","pending_messages":[]}""")]
    public async Task Status_response_without_pending_renders_nothing(string body) {
        var text = McpFlowsServer.FormatStatusResponse(body, out var pendingIds);

        await Assert.That(text).DoesNotContain("pending_messages");
        await Assert.That(pendingIds).IsEmpty();
    }

    [Test]
    public async Task Status_response_renders_vendor_audit_and_participants() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "requested_reviewer_vendor":"claude","applied_reviewer_vendor":"claude",
             "reviewer_vendor_source":"explicit",
             "participants":[{"role":"reviewer","vendor":"claude","model":"sonnet","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).Contains("requested_reviewer_vendor: claude");
        await Assert.That(text).Contains("applied_reviewer_vendor: claude");
        await Assert.That(text).Contains("reviewer_vendor_source: explicit");
        await Assert.That(text).Contains("reviewer: vendor=claude model=sonnet status=running");
        // Agreement renders NO mismatch warning — the warning must exist only on disagreement.
        await Assert.That(text).DoesNotContain("reviewer vendor mismatch");
    }

    [Test]
    public async Task Status_response_warns_when_reviewer_vendor_disagrees_with_applied_echo() {
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "applied_reviewer_vendor":"codex",
             "participants":[{"role":"reviewer","vendor":"claude","model":"sonnet","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).Contains("⚠ reviewer vendor mismatch: " +
            "participant 'reviewer' is 'claude' but applied_reviewer_vendor is 'codex'.");
        await Assert.That(text).Contains("treat its results as suspect: close the flow and report this");
    }

    [Test]
    public async Task Status_response_warns_from_server_flag_even_without_participants() {
        // A read-model-lagged response can omit the participant list while the server's own
        // fold-side check still flagged the disagreement — the flag alone must warn.
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "applied_reviewer_vendor":"codex","reviewer_vendor_mismatch":true}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).Contains("⚠ reviewer vendor mismatch: " +
            "the server flagged that the active reviewer's vendor disagrees with applied_reviewer_vendor.");
    }

    [Test]
    public async Task Status_response_ignores_stopped_reviewer_vendor_differences() {
        // A rotated-out (stopped) reviewer entry is historical: only the ACTIVE reviewer's vendor
        // compares against the applied echo locally.
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "applied_reviewer_vendor":"codex",
             "participants":[{"role":"reviewer","vendor":"claude","model":"sonnet","stopped":true},
                             {"role":"reviewer","vendor":"codex","model":"default","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).DoesNotContain("reviewer vendor mismatch");
    }

    [Test]
    public async Task Status_response_ignores_non_reviewer_vendor_differences() {
        // The run-level echo describes only the "reviewer" role — a multi-participant flow's other
        // roles legitimately run different vendors and must never trip the warning.
        var body = """
            {"flow_run_id":"f1","status":"running","definition_id":"pair-flow","target_title":"t",
             "applied_reviewer_vendor":"codex",
             "participants":[{"role":"tester","vendor":"claude","model":"sonnet","stopped":false},
                             {"role":"reviewer","vendor":"codex","model":"default","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(body);

        await Assert.That(text).DoesNotContain("reviewer vendor mismatch");
    }

    [Test]
    public async Task Polled_round_result_warns_before_the_result_text_on_vendor_mismatch() {
        var body = """
            {"flow_run_id":"f1","round_number":2,"status":"findings","round_status":"findings",
             "round_result_text":"FINDINGS:\n- looks fine",
             "applied_reviewer_vendor":"codex",
             "participants":[{"role":"reviewer","vendor":"claude","model":"sonnet","stopped":false}]}
            """;

        var text = McpFlowsServer.FormatPolledRoundResult(JsonNode.Parse(body)!.AsObject(), "f1");

        var warningIndex = text.IndexOf("⚠ reviewer vendor mismatch", StringComparison.Ordinal);
        var resultIndex  = text.IndexOf("FINDINGS:", StringComparison.Ordinal);
        await Assert.That(warningIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(resultIndex).IsGreaterThan(warningIndex);
    }

    [Test]
    public async Task Round_response_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_id":"r1","status":"findings","result_kind":"findings","result_text":"some findings",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;

        var text = McpFlowsServer.FormatRoundResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var resultIndex  = text.IndexOf("some findings", StringComparison.Ordinal);
        var pendingIndex = text.IndexOf("pending_messages (2):", StringComparison.Ordinal);
        await Assert.That(pendingIndex).IsGreaterThan(resultIndex);
        var firstIndex  = text.IndexOf("from tester [msg-a1]:", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]:", StringComparison.Ordinal);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }

    [Test]
    public async Task Close_response_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","status":"closed",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"}
             ]}
            """;

        var text = McpFlowsServer.FormatCloseResponse(body, out var pendingIds);

        await Assert.That(text).Contains("pending_messages (1):");
        await Assert.That(text).Contains("- from tester [msg-a1]: found a broken symlink in scripts/");
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1"]);
    }

    [Test]
    public async Task Polled_round_result_renders_pending_messages() {
        var body = """
            {"flow_run_id":"f1","round_number":2,"status":"closed","round_status":"clean","round_result_text":"all clean",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;
        var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();

        var text = McpFlowsServer.FormatPolledRoundResult(node, "f1", out var pendingIds);

        await Assert.That(text).Contains("pending_messages (2):");
        var resultIndex  = text.IndexOf("all clean", StringComparison.Ordinal);
        var pendingIndex = text.IndexOf("pending_messages (2):", StringComparison.Ordinal);
        await Assert.That(pendingIndex).IsGreaterThan(resultIndex);
        var firstIndex  = text.IndexOf("from tester [msg-a1]:", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("from reviewer [msg-b2]:", StringComparison.Ordinal);
        await Assert.That(secondIndex).IsGreaterThan(firstIndex);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }

    [Test]
    public async Task Polled_round_result_renders_reviewer_vendor_audit() {
        var node = JsonNode.Parse("""
            {"round_number":1,"status":"waiting","round_status":"clean",
             "requested_reviewer_vendor":"claude","applied_reviewer_vendor":"claude",
             "reviewer_vendor_source":"explicit"}
            """)!.AsObject();

        var text = McpFlowsServer.FormatPolledRoundResult(node, "f1");

        await Assert.That(text).Contains("requested_reviewer_vendor: claude");
        await Assert.That(text).Contains("applied_reviewer_vendor: claude");
        await Assert.That(text).Contains("reviewer_vendor_source: explicit");
    }

    [Test]
    public async Task Malformed_pending_entry_is_skipped() {
        var body = """
            {"flow_run_id":"f1","round_number":2,"status":"closed","round_status":"clean","round_result_text":"all clean",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"first"},
                "junk-string",
                {"message_id":"msg-b2","from_participant":"reviewer","text":"second"}
             ]}
            """;
        var node = JsonNode.Parse(body)!.AsObject();

        // Pins the carried Minor from Task 2's review: a malformed (non-object) array entry must
        // be skipped, not throw — FormatPolledRoundResult has no try/catch, so a throw here would
        // turn a terminal result into a generic internal error.
        var text = McpFlowsServer.FormatPolledRoundResult(node, "f1", out var pendingIds);

        await Assert.That(text).Contains("from tester [msg-a1]: first");
        await Assert.That(text).Contains("from reviewer [msg-b2]: second");
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);

        // Pins the E-c final-review Minor: the header count must reflect the RENDERED entries (2 —
        // the two well-formed objects), not the raw array length (3, which also counts the
        // "junk-string" entry that gets skipped).
        await Assert.That(text).Contains("pending_messages (2):");
        await Assert.That(text).DoesNotContain("pending_messages (3):");
    }

    [Test]
    public async Task Ack_posts_rendered_ids_snake_case() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1", "m2"], Clock());

        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"message_ids\"");
        var parsed = JsonNode.Parse(body)!.AsObject();
        var ids    = parsed["message_ids"]!.AsArray().Select(n => n!.GetValue<string>());
        await Assert.That(ids).IsEquivalentTo(["m1", "m2"]);
    }

    [Test]
    public async Task Ack_retries_once_then_swallows() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var clock = Clock();
        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1"], clock);
        var delays = clock.Delays;

        await Assert.That(server.LogEntries.Count).IsEqualTo(2);
        await Assert.That(delays).Count().IsEqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Ack_attempt_times_out_and_is_swallowed() {
        // E-c final review, Important: the shared MCP client normally has
        // Timeout = InfiniteTimeSpan (the review-flow endpoints long-poll), so
        // AckRenderedMessagesAsync now bounds each POST attempt itself (PerAckPostTimeout, 15s).
        // Driving the real 15s bound here would make this test slow without adding coverage —
        // instead this pins the pre-existing swallow behavior the new bound feeds into: an
        // HttpClient with its OWN short Timeout produces the same OperationCanceledException
        // shape TryPostAsync's bare catch already swallows, deterministically and fast.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(300)));
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(50) };

        var clock = Clock();
        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", ["m1"], clock);
        var delays = clock.Delays;

        // No exception propagated, and the retry-after-delay path still ran once — i.e. both the
        // initial attempt and the retry timed out and were swallowed rather than thrown.
        await Assert.That(delays).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Ack_skips_empty_ids() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", [], Clock());

        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Ordering pin, composition-shaped: the real dispatch path (HandleToolCallAsync) isn't
    /// directly invocable from a unit test (it needs a live authenticated HttpClient and the full
    /// stdio JSON-RPC loop — that seam is covered by Capacitor.Cli.Tests.Integration instead). So
    /// this test pins the composition explicitly: format a status response through the
    /// id-exposing overload (the same call the get_flow_status wiring makes), then ack exactly
    /// the ids that call returned — and assert the ack body carries exactly those ids, matching
    /// what the wiring in McpFlowsServer's get_review_flow_status/get_flow_status arm does.
    /// </summary>
    [Test]
    public async Task Ordering_pin_format_then_ack_sends_exactly_the_rendered_ids() {
        var statusBody = """
            {"flow_run_id":"f1","status":"running","definition_id":"code-review","target_title":"t",
             "pending_messages":[
                {"message_id":"msg-a1","from_participant":"tester","text":"found a broken symlink in scripts/"},
                {"message_id":"msg-b2","from_participant":"reviewer","text":"heads-up, migration file also touched"}
             ]}
            """;

        var text = McpFlowsServer.FormatStatusResponse(statusBody, out var pendingIds);
        await Assert.That(text).Contains("pending_messages (2):");

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/f1/messages/ack").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        await McpFlowsServer.AckRenderedMessagesAsync(client, server.Url!, "f1", pendingIds, Clock());

        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        var ackBody = server.LogEntries.Single().RequestMessage.Body!;
        var ackIds  = JsonNode.Parse(ackBody)!.AsObject()["message_ids"]!.AsArray().Select(n => n!.GetValue<string>());
        await Assert.That(ackIds).IsEquivalentTo(pendingIds);
        await Assert.That(pendingIds).IsEquivalentTo(["msg-a1", "msg-b2"]);
    }

    // === Reviewer MODEL override (protocol-v3 transport) ===

    static JsonObject ModelStartArguments(string? vendor, string? model, string kind = "code-review") {
        var args = new JsonObject {
            ["kind"]         = kind,
            ["target_kind"]  = "pr",
            ["target_ref"]   = "123",
            ["target_title"] = "some PR",
            ["context"]      = "some context"
        };
        if (vendor is not null) args["vendor"] = vendor;
        if (model  is not null) args["model"]  = model;
        return args;
    }

    static JsonObject ModelToolCall(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments.DeepClone() }
    };

    const string V3RunningWithAck =
        """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_model":"claude/opus-4","reviewer_model_equivalence_key":"claude/opus","applied_reviewer_vendor":"claude"}""";

    // --- StartFlowAsync: route selection + local vendor requirement ---

    [Test]
    public async Task StartFlowAsync_with_model_posts_exactly_one_v4_request_with_protocol_4_body() {
        // §2.7 B3: a model override is orthogonal to park-capability, so a v4 client routes a
        // model start to /v4 too (the superset route applies the model AND records protocol 4).
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(V3RunningWithAck));
        // /v3 and /v2 are deliberately left unstubbed — WireMock's default 404 proves the model path
        // lands on /v4 with no fallback (zero v3/v2/legacy retry).
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, ModelStartArguments("claude", "opus"),
            cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);

        var hit = server.LogEntries.Single();
        await Assert.That(hit.RequestMessage.Path).IsEqualTo("/api/flows/review/start/v4");
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/v3", StringComparison.Ordinal) || e.RequestMessage.Path.EndsWith("/v2", StringComparison.Ordinal))).IsFalse();

        var body = JsonNode.Parse(hit.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(body["vendor"]!.GetValue<string>()).IsEqualTo("claude");
        await Assert.That(body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(4);
    }

    [Test]
    public async Task StartFlowAsync_with_model_falls_back_to_v3_when_v4_is_absent() {
        // Cross-rollout safety for a model override: an old server lacks /v4 → 404 → fall back to /v3
        // (protocol 3), still applying the model. Two POSTs, /v4 then /v3.
        using var server = WireMockServer.Start();
        // /v4 left unstubbed → default 404 simulates the old server.
        server.Given(Request.Create().WithPath("/api/flows/review/start/v3").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(V3RunningWithAck));
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, ModelStartArguments("claude", "opus"),
            cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.LogEntries.Count).IsEqualTo(2);
        await Assert.That(server.LogEntries.ElementAt(0).RequestMessage.Path).IsEqualTo("/api/flows/review/start/v4");
        await Assert.That(server.LogEntries.ElementAt(1).RequestMessage.Path).IsEqualTo("/api/flows/review/start/v3");

        var v3Body = JsonNode.Parse(server.LogEntries.ElementAt(1).RequestMessage.Body!)!.AsObject();
        await Assert.That(v3Body["model"]!.GetValue<string>()).IsEqualTo("opus");
        await Assert.That(v3Body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(3);
    }

    [Test]
    public async Task StartFlowAsync_with_model_but_no_vendor_rejects_locally_without_posting() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v3").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(V3RunningWithAck));
        using var client = new HttpClient();

        await Assert.That(async () => await Server().StartFlowAsync(
                client, server.Url!, ModelStartArguments(vendor: null, model: "opus"),
                cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null))
            .Throws<ArgumentException>();

        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StartFlowAsync_dynamic_flow_rejects_top_level_model_without_posting() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v3").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(V3RunningWithAck));
        using var client = new HttpClient();

        var args = new JsonObject {
            ["definition_yaml"] = "participants: {}",
            ["target_kind"]     = "pr",
            ["target_ref"]      = "123",
            ["target_title"]    = "some PR",
            ["context"]         = "some context",
            ["vendor"]          = "claude",
            ["model"]           = "opus"
        };

        await Assert.That(async () => await Server().StartFlowAsync(
                client, server.Url!, args,
                cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "definition_id", requestingSessionId: null))
            .Throws<ArgumentException>();

        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StartFlowAsync_without_model_preserves_v2_route_and_omits_model() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        using var client = new HttpClient();

        using var response = await Server().StartFlowAsync(
            client, server.Url!, ModelStartArguments(vendor: null, model: null),
            cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, kindArgName: "kind", requestingSessionId: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var hit = server.LogEntries.Single();
        await Assert.That(hit.RequestMessage.Path).IsEqualTo("/api/flows/review/start/v2");
        await Assert.That(hit.RequestMessage.Body).DoesNotContain("model");

        var body = JsonNode.Parse(hit.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["client_flow_protocol_version"]!.GetValue<int>()).IsEqualTo(2);
    }

    // --- Requester session id: threaded from the caller, never re-read from the environment ---

    [Test]
    // All FOUR start dispatch lambdas: catalog ("kind") and generic ("definition_id"), each in its
    // no-model (settlement-retry, v2 route) and model-bearing (refresh-only, v3 route) form. A model
    // start goes through a different send wrapper, so dropping the argument from just one of the four
    // has to be caught here.
    [Arguments("start_review_flow", null, "/api/flows/review/start/v2")]
    [Arguments("start_flow", null, "/api/flows/review/start/v2")]
    [Arguments("start_review_flow", "opus", "/api/flows/review/start/v4")]
    [Arguments("start_flow", "opus", "/api/flows/review/start/v4")]
    public async Task HandleToolCall_posts_the_requesting_session_id_it_was_given(
            string toolName, string? model, string expectedPath) {
        // Guards the wiring the live defect ran through: RunAsync resolves the requesting session
        // from the running harness and hands it down, so a stale KCAP_SESSION_ID in this process's
        // environment can never reach the wire. If the dispatch stopped threading the value (or went
        // back to reading the env inside StartFlowAsync), the posted body would carry null or
        // whatever this test process inherited — not the id supplied here.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        // The model path now lands on /v4 (B3 superset route); it needs the model acknowledgement
        // fields, or the response is rejected before the body assertion below is reached.
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(V3RunningWithAck));
        using var client = new HttpClient();

        var arguments = ModelStartArguments(vendor: model is null ? null : "claude", model: model);
        if (toolName == "start_flow") {
            arguments["definition_id"] = arguments["kind"]!.GetValue<string>();
            arguments.Remove("kind");
        }

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall(toolName, arguments),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null,
            requestingSessionId: "d15c0ffee0000000000000000000ba5e");

        await Assert.That(JsonNode.Parse(response)!["result"]!["isError"]).IsNull();

        // Exactly one POST, on the route this case is meant to exercise — so a case that silently
        // fell through to the other wrapper can't pass by asserting the wrong request's body.
        var hit = server.LogEntries.Single();
        await Assert.That(hit.RequestMessage.Path).IsEqualTo(expectedPath);

        var body = JsonNode.Parse(hit.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["requesting_session_id"]!.GetValue<string>())
            .IsEqualTo("d15c0ffee0000000000000000000ba5e");
    }

    // --- CheckReviewerModelResult: pure decision logic ---

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.MethodNotAllowed)]
    public async Task CheckReviewerModelResult_old_server_404_or_405_maps_to_protocol_required(HttpStatusCode status) {
        var result = McpFlowsServer.CheckReviewerModelResult(
            "start_review_flow", status, isSuccess: false, postBody: "", out var runIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(result.Value.Message).Contains("reviewer_model_protocol_required");
        await Assert.That(runIdToClose).IsNull();
    }

    [Test]
    public async Task CheckReviewerModelResult_uncoded_non_success_body_is_surfaced_not_masked() {
        // Qodo #2: a non-404/405 uncoded body (e.g. a 5xx / proxy HTML-or-text error) is NOT an
        // old-server signal — it returns null so the caller surfaces the real status/body, rather
        // than masking a genuine failure as reviewer_model_protocol_required.
        var result = McpFlowsServer.CheckReviewerModelResult(
            "start_flow", HttpStatusCode.InternalServerError, isSuccess: false,
            postBody: "<html>502 Bad Gateway</html>", out var runIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(runIdToClose).IsNull();
    }

    [Test]
    public async Task CheckReviewerModelResult_genuine_coded_rejection_passes_through_to_generic_handler() {
        // A real v3 rejection (e.g. the model isn't launchable on the daemon) must NOT be masked as
        // a protocol issue — it surfaces via FormatFlowStartError instead (this helper returns null).
        var body = """{"error":"reviewer_model_unavailable","message":"the requested model is not available"}""";

        var result = McpFlowsServer.CheckReviewerModelResult(
            "start_review_flow", HttpStatusCode.BadRequest, isSuccess: false, body, out var runIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(runIdToClose).IsNull();
    }

    [Test]
    public async Task CheckReviewerModelResult_success_with_full_ack_is_a_noop() {
        var body = """{"flow_run_id":"f1","status":"running","applied_reviewer_model":"claude/opus-4","reviewer_model_equivalence_key":"claude/opus"}""";

        var result = McpFlowsServer.CheckReviewerModelResult(
            "start_review_flow", HttpStatusCode.OK, isSuccess: true, body, out var runIdToClose);

        await Assert.That(result).IsNull();
        await Assert.That(runIdToClose).IsNull();
    }

    [Test]
    [Arguments("""{"flow_run_id":"f1","status":"running","reviewer_model_equivalence_key":"claude/opus"}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running","applied_reviewer_model":"claude/opus-4"}""")]
    [Arguments("""{"flow_run_id":"f1","status":"running"}""")]
    public async Task CheckReviewerModelResult_success_missing_ack_fails_and_salvages_run_id(string body) {
        var result = McpFlowsServer.CheckReviewerModelResult(
            "start_review_flow", HttpStatusCode.OK, isSuccess: true, body, out var runIdToClose);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.IsError).IsTrue();
        await Assert.That(runIdToClose).IsEqualTo("f1");
    }

    // --- HandleToolCallAsync: full dispatch, WireMock-backed ---

    [Test]
    public async Task HandleToolCall_with_model_old_server_404_maps_to_protocol_required_no_v2_retry() {
        using var server = WireMockServer.Start();
        // Both /v4 and its /v3 fallback are left unstubbed — WireMock's default 404 for each
        // simulates a server that predates /v4 AND the reviewer-model override protocol. The model
        // path never falls to /v2, so the final /v3 404 maps to reviewer_model_protocol_required.
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("reviewer_model_protocol_required");

        // Never downgraded to v2, never closed anything.
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/v2"))).IsFalse();
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/close"))).IsFalse();
    }

    [Test]
    public async Task HandleToolCall_with_model_genuine_v3_rejection_surfaces_verbatim_not_reformatted() {
        // Task-9 review Minor: a genuine coded v3 rejection (the model IS understood by the v3
        // protocol, but the daemon can't launch it) must reach the caller byte-for-byte, via the
        // SAME generic FormatFlowStartError path a no-model rejection uses — never reformatted or
        // intercepted by the model-specific protocol-skew gate (which only fires for 404/405/uncoded/
        // protocol-skew-coded bodies).
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody(
                """{"error":"reviewer_model_unavailable","message":"the requested model is not available"}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();

        // Verbatim: the exact code + message, not the local protocol-required substitute.
        await Assert.That(text).Contains("reviewer_model_unavailable");
        await Assert.That(text).Contains("the requested model is not available");
        await Assert.That(text).DoesNotContain("reviewer_model_protocol_required");

        // A coded 400 is not a 404, so /v4 answers it directly — exactly one POST, no v3/v2
        // fallback, and nothing to close (no run ever started).
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo("/api/flows/review/start/v4");
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/close"))).IsFalse();
    }

    [Test]
    public async Task HandleToolCall_with_model_success_missing_ack_fails_and_closes_defensively() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null}"""));
        server.Given(Request.Create().WithPath("/api/flows/f1/close").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/f1/close")).IsEqualTo(1);
    }

    [Test]
    public async Task HandleToolCall_with_model_valid_ack_renders_model_audit_and_posts_v4_only() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """
                {"flow_run_id":"f1","round_id":"r1","status":"findings","result_kind":"findings",
                 "result_text":"looks good",
                 "requested_reviewer_model":"opus","applied_reviewer_model":"claude/opus-4",
                 "resolved_reviewer_model":"claude-opus-4-20260101","reviewer_model_source":"explicit",
                 "reviewer_model_equivalence_key":"claude/opus","applied_reviewer_vendor":"claude"}
                """));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]).IsNull();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();

        await Assert.That(text).Contains("requested_reviewer_model: opus");
        await Assert.That(text).Contains("applied_reviewer_model: claude/opus-4");
        await Assert.That(text).Contains("resolved_reviewer_model: claude-opus-4-20260101");
        await Assert.That(text).Contains("reviewer_model_source: explicit");

        // Exactly one POST, to v4 — no polling GET, no v3/v2 fallback.
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/review/start/v4")).IsEqualTo(1);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/v3", StringComparison.Ordinal) || e.RequestMessage.Path.EndsWith("/v2", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task HandleToolCall_with_model_settlement_busy_posts_v4_exactly_once_and_surfaces_the_coded_error() {
        // A model-bearing start mints AND launches a run on every POST, so a retryable settlement
        // 409 must NOT be auto-retried (that would violate exactly-one-POST and churn reviewer
        // launches). The coded error surfaces so the caller retries the whole start. A 409 is not a
        // 404, so /v4 answers it directly (no fallback).
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("flow_settlement_busy");

        // EXACTLY ONE POST to v4 — a model-bearing start is never settlement-retried.
        await Assert.That(server.LogEntries.Count(
            e => e.RequestMessage.Path == "/api/flows/review/start/v4")).IsEqualTo(1);
    }

    [Test]
    public async Task HandleToolCall_with_model_valid_ack_but_mismatched_vendor_closes_and_errors() {
        // The model/key ack is opaque and cannot prove which VENDOR was applied, so a v3 success
        // must ALSO pass the ordinal vendor-echo check. A mismatch salvages + closes the run and
        // returns an error.
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v4").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"flow_run_id":"f1","status":"running","round_id":null,"round_number":null,"applied_reviewer_model":"claude/opus-4","reviewer_model_equivalence_key":"claude/opus","applied_reviewer_vendor":"codex"}"""));
        server.Given(Request.Create().WithPath("/api/flows/f1/close").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ModelToolCall("start_review_flow", ModelStartArguments("claude", "opus")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null);

        var result = JsonNode.Parse(response)!.AsObject();
        await Assert.That(result["result"]!["isError"]!.GetValue<bool>()).IsTrue();
        var text = result["result"]!["content"]![0]!["text"]!.GetValue<string>();
        await Assert.That(text).Contains("claude");
        await Assert.That(text).Contains("codex");

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/f1/close")).IsEqualTo(1);
    }

    // --- Tool schema exposure ---

    [Test]
    [Arguments("start_review_flow")]
    [Arguments("start_flow")]
    public async Task Start_tool_exposes_optional_model_documenting_that_vendor_is_required(string toolName) {
        var tool  = McpFlowsServer.BuildToolsList().Single(t => t.Name == toolName);
        var props = tool.InputSchema.Properties;

        await Assert.That(props.ContainsKey("model")).IsTrue();
        // Required list must NOT contain model — it's optional.
        await Assert.That(tool.InputSchema.Required).DoesNotContain("model");
        // Documents the vendor coupling without enumerating any vendor/model values.
        await Assert.That(props["model"].Description.ToLowerInvariant()).Contains("vendor");
    }
}
