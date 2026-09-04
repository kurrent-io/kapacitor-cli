using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowResultServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowResultServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static JsonObject Args(string? roundToken = "round-1", string? kind = "findings", string? findings = "1. issue") {
        var o = new JsonObject();
        if (roundToken is not null) o["round_token"] = (JsonNode?)roundToken;
        if (kind is not null) o["kind"] = (JsonNode?)kind;
        if (findings is not null) o["findings"] = (JsonNode?)findings;
        return o;
    }

    static JsonObject MessageArgs(string? text = "heads up: found something outside this round's scope") {
        var o = new JsonObject();
        if (text is not null) o["text"] = (JsonNode?)text;
        return o;
    }

    static Func<TimeSpan, Task> NoDelay(List<TimeSpan> recorded) => ts => { recorded.Add(ts); return Task.CompletedTask; };

    [Test]
    public async Task Happy_path_posts_snake_case_body_and_confirms_result() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":2}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Result recorded. You may end your reply now.");

        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"agent_id\"");
        await Assert.That(body).Contains("\"round_token\"");
        await Assert.That(body).Contains("\"kind\"");
        await Assert.That(body).Contains("\"text\"");
        await Assert.That(body).Contains("agent-1");
    }

    /// <summary>
    /// The borrowed-reviewer delivery path. That launch's HOME is a per-launch state dir, so the
    /// channel has no token store and must POST to the daemon-minted loopback capability verbatim
    /// rather than composing an API path under a server root. The deliberately unusable apiRoot is
    /// the load-bearing half: if the override were ignored, the request would go there and the
    /// capability endpoint would see nothing.
    /// </summary>
    [Test]
    public async Task Capability_url_overrides_the_api_root_for_a_borrowed_reviewer() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/abc123/flow-result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var (text, isError) = await Server().SubmitCoreAsync(
            client, "http://unreachable.invalid", "agent-1", Args(), NoDelay([]),
            submitUrlOverride: $"{server.Url}/abc123/flow-result");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Result recorded. You may end your reply now.");
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo("/abc123/flow-result");
    }

    [Test]
    public async Task Clean_kind_omits_null_text_from_the_wire() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var (_, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "clean", findings: null), NoDelay([]));

        await Assert.That(isError).IsFalse();
        // McpJsonContext ignores null when writing, so a clean submit must not carry a text key.
        await Assert.That(server.LogEntries.Single().RequestMessage.Body!).DoesNotContain("\"text\"");
    }

    [Test]
    public async Task Retryable_no_active_flow_retries_then_succeeds() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Result recorded. You may end your reply now.");
        await Assert.That(delays).Count().IsEqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Retryable_error_exhausts_after_five_attempts_with_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"no_open_round","message":"no round awaiting a result"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no round awaiting a result");
        await Assert.That(text).Contains("Retry this tool call");
        await Assert.That(text).Contains("does not read the transcript");
        await Assert.That(delays).Count().IsEqualTo(4); // 5 attempts = 4 delays
        await Assert.That(server.LogEntries.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Stale_round_token_fails_immediately_without_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"stale_round_token","message":"That round is already closed and a newer round is open. Discard this result entirely — do NOT emit a FINDINGS:/NO FINDINGS marker for it — and respond only to the newest prompt you received."}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: "round-0"), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Discard this result entirely");
        await Assert.That(text).DoesNotContain("Retry this tool call");
        await Assert.That(delays).Count().IsEqualTo(0);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Server_catching_up_fails_immediately_with_retry_later_guidance() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"server_catching_up","message":"catching up"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("catching up");
        await Assert.That(text).Contains("try again in a few minutes");
        await Assert.That(delays).Count().IsEqualTo(0);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Validation_failures_do_not_hit_the_network() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var missingToken = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: null), NoDelay([]));
        var badKind      = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "bogus"), NoDelay([]));
        var noFindings   = await Server().SubmitCoreAsync(client, server.Url!, "agent-1", Args(findings: null), NoDelay([]));

        await Assert.That(missingToken.IsError).IsTrue();
        await Assert.That(missingToken.Text).Contains("round_token");
        await Assert.That(badKind.IsError).IsTrue();
        await Assert.That(noFindings.IsError).IsTrue();
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Send_message_posts_snake_case_body_with_stable_message_id() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        using var client = new HttpClient();

        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(), NoDelay([]), messageId: "msg-1"
        );

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Message sent to the flow driver. It will be delivered with the driver's next flow call — you may continue.");

        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"agent_id\"");
        await Assert.That(body).Contains("\"message_id\"");
        await Assert.That(body).Contains("\"text\"");
        await Assert.That(body).Contains("agent-1");
        await Assert.That(body).Contains("msg-1");
    }

    [Test]
    public async Task Send_message_retries_no_active_flow_with_same_message_id() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .InScenario("launch-race")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .InScenario("launch-race")
              .WhenStateIs("second")
              .WillSetStateTo("third")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .InScenario("launch-race")
              .WhenStateIs("third")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(), NoDelay(delays), messageId: "msg-stable"
        );

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("Message sent to the flow driver");
        await Assert.That(delays).Count().IsEqualTo(2);

        var bodies = server.LogEntries.Select(e => e.RequestMessage.Body!).ToList();
        await Assert.That(bodies).Count().IsEqualTo(3);
        foreach (var b in bodies) {
            await Assert.That(b).Contains("msg-stable");
        }
    }

    [Test]
    public async Task Send_message_run_closed_is_terminal_no_retry() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"run_closed","message":"this flow run is already closed"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(), NoDelay(delays)
        );

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("this flow run is already closed");
        await Assert.That(delays).Count().IsEqualTo(0);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Send_message_requires_text() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(text: null), NoDelay([])
        );

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: text must be a non-empty string.");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Send_message_rejects_non_string_text_without_throwing() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var args = new JsonObject { ["text"] = 42 };

        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", args, NoDelay([])
        );

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: text must be a non-empty string.");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Send_message_exhausts_retries_without_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(), NoDelay(delays)
        );

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("not registered yet");
        await Assert.That(text).DoesNotContain("FINDINGS");
        await Assert.That(delays).Count().IsEqualTo(4); // 5 attempts = 4 delays
        await Assert.That(server.LogEntries.Count).IsEqualTo(5);
    }

    [Test]
    public async Task Send_message_server_catching_up_fails_immediately_with_retry_later_guidance() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/participant/message").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"server_catching_up","message":"catching up"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await Server().SendMessageCoreAsync(
            client, server.Url!, "agent-1", MessageArgs(), NoDelay(delays)
        );

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("catching up");
        await Assert.That(text).Contains("try again in a few minutes");
        await Assert.That(delays).Count().IsEqualTo(0);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_exits_2_when_agent_env_is_missing() {
        var prior = Environment.GetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar);
        Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, null);
        try {
            var exit = await new McpFlowResultServer(
                Config.Root, Resolutions.At("https://example.test", Config.Root),
                AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient()).RunAsync();
            await Assert.That(exit).IsEqualTo(2);
        } finally {
            Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, prior);
        }
    }
}
