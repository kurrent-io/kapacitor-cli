using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class PostWithRetryTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SucceedsOnFirstAttempt() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var client   = new HttpClient();
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostWithRetryAsync($"{_server.Url}/hooks/session-start", content);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task RetriesOnConnectionFailureThenSucceeds() {
        var tempServer = WireMockServer.Start();
        var port       = tempServer.Port;
        var url        = $"http://localhost:{port}/hooks/test";
        tempServer.Stop();

        WireMockServer? restartedServer = null;

        _ = Task.Run(async () => {
                await Task.Delay(600);
                restartedServer = WireMockServer.Start(port);

                restartedServer.Given(Request.Create().WithPath("/hooks/test").UsingPost())
                    .RespondWith(Response.Create().WithStatusCode(200).WithBody("recovered"));
            }
        );

        try {
            using var client   = new HttpClient();
            using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
            var       response = await client.PostWithRetryAsync(url, content, timeout: TimeSpan.FromSeconds(15));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        } finally {
            restartedServer?.Stop();
        }
    }
}

public class InlineDrainTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherManager? _watchers;
    WatcherManager  Watchers => _watchers ??= new(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient());

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task PostsCorrectBatch() {
        const string sessionId = "test-session-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp      = new TempDir();
        var transcriptPath = tmp.PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(
            transcriptPath,
            """{"type":"user","message":{"content":"hello"}}"""        + "\n" +
            """{"type":"assistant","message":{"content":"hi back"}}""" + "\n"
        );

        await Watchers.InlineDrainAsync(sessionId, transcriptPath, null);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(root.GetProperty("lines").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root.GetProperty("line_numbers")[0].GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("line_numbers")[1].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task PostsCorrectBatch_with_codex_vendor_when_specified() {
        const string sessionId = "test-session-codex-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp      = new TempDir();
        var transcriptPath = tmp.PathTo("rollout.jsonl");

        await File.WriteAllTextAsync(
            transcriptPath,
            """{"timestamp":"2026-05-07T15:50:21.989Z","type":"session_meta","payload":{}}""" + "\n"
        );

        await Watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "codex");

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("vendor").GetString()).IsEqualTo("codex");
    }
}

public class SessionStartAdditionalContextTests {
    [Test]
    public async Task BuildEnvelope_returns_null_when_no_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_null() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, null, null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_empty_or_whitespace() {
        var result = SessionStartAdditionalContext.BuildEnvelope("", "   ", "\n\t");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_wraps_single_fragment_in_envelope() {
        var result = SessionStartAdditionalContext.BuildEnvelope("hello world");

        await Assert.That(result).IsNotNull();
        var json = JsonNode.Parse(result!);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");
        await Assert.That(json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>()).IsEqualTo("hello world");
    }

    [Test]
    public async Task BuildEnvelope_joins_multiple_fragments_with_blank_line_in_order() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second");

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    [Test]
    public async Task BuildEnvelope_skips_null_and_blank_when_mixed_with_real_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, "first", "   ", "second", null);

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    [Test]
    public async Task BuildEnvelope_produces_single_top_level_json_object() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second")!;
        // Guard against a future bug that appends a second envelope: nothing
        // (not even whitespace) should follow the last closing brace.
        var lastClose  = result.LastIndexOf('}');
        var afterClose = result[(lastClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");
    }
}
