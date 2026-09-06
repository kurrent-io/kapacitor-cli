using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpAnalyticsServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpAnalyticsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient());

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Query_body_defaults_to_cwd_repo_scope() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT vendor FROM v_an_sessions"}"""), "abc123");

        await Assert.That(body["sql"]!.GetValue<string>()).IsEqualTo("SELECT vendor FROM v_an_sessions");
        await Assert.That(body["repos"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(body["repos"]![0]!.GetValue<string>()).IsEqualTo("abc123");
        await Assert.That(body.ContainsKey("max_rows")).IsFalse();
    }

    [Test]
    public async Task Query_body_global_scope_sends_null_repos() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions","scope":"global"}"""), "abc123");

        await Assert.That(body.ContainsKey("repos")).IsTrue();
        await Assert.That(body["repos"]).IsNull();
    }

    [Test]
    public async Task Query_body_fails_closed_when_repo_scope_is_unresolvable() {
        // No cwd repo hash + default (repo) scope must never silently widen to global.
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions"}"""), null))
            .Throws<ArgumentException>();

        // Explicit global works without a cwd repo.
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions","scope":"global"}"""), null);
        await Assert.That(body["repos"]).IsNull();
    }

    [Test]
    public async Task Query_body_rejects_unknown_scope_and_missing_sql() {
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1","scope":"everything"}"""), "abc123"))
            .Throws<ArgumentException>();
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"scope":"global"}"""), "abc123"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Query_body_rejects_wrong_typed_sql_and_scope_with_ArgumentException() {
        // Wrong-typed values (common from LLM clients) must surface as ArgumentException — which the
        // dispatcher turns into a field-specific tool error — never a raw InvalidOperationException
        // that would be masked as a generic "internal error".
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":123}"""), "abc123"))
            .Throws<ArgumentException>();
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":["SELECT 1"]}"""), "abc123"))
            .Throws<ArgumentException>();
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1","scope":42}"""), "abc123"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Query_body_passes_max_rows_through() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT vendor FROM v_an_sessions","max_rows":50}"""), "abc123");

        await Assert.That(body["max_rows"]!.GetValue<int>()).IsEqualTo(50);
    }

    [Test]
    public async Task Tool_call_with_wrong_shaped_params_returns_protocol_error_not_internal_error() {
        // "params" as an array (wrong shape) must degrade to an actionable -32602, never the
        // generic internal-error the outer catch produces. Malformed params returns before any
        // HTTP call, so the client is never used.
        using var client = new HttpClient();
        var request = Args("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[]}""");

        var response = await Server().HandleToolCallAsync(
            JsonValue.Create(1), request, client, "https://example.test", "abc123");

        var error = JsonNode.Parse(response)!["error"];
        await Assert.That(error).IsNotNull();
        await Assert.That(error!["code"]!.GetValue<int>()).IsEqualTo(-32602);
        await Assert.That(response).DoesNotContain("internal error");
    }

    [Test]
    public async Task Tool_call_maps_client_side_timeout_to_actionable_hint() {
        // A client-side HttpClient timeout throws TaskCanceledException (: OperationCanceledException)
        // before any server 408 — it must yield the same "narrow the query" hint, not "internal error".
        using var client = new HttpClient(new TimingOutHandler());
        var request = Args("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query_analytics","arguments":{"sql":"SELECT vendor FROM v_an_sessions"}}}""");

        var response = await Server().HandleToolCallAsync(
            JsonValue.Create(1), request, client, "https://example.test", "abc123");

        // The tool result is JSON-serialized (the em-dash escapes to —), so match on the
        // ASCII head of the message rather than the literal constant.
        await Assert.That(response).Contains("Query timed out");
        await Assert.That(response).Contains("narrow the date range");
        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).DoesNotContain("internal error");
    }

    [Test]
    public async Task Schema_client_side_timeout_uses_generic_hint_not_query_hint() {
        // A get_analytics_schema timeout must NOT tell the agent to "narrow the date range" —
        // there is no query to narrow.
        using var client = new HttpClient(new TimingOutHandler());
        var request = Args("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_analytics_schema","arguments":{}}}""");

        var response = await Server().HandleToolCallAsync(
            JsonValue.Create(1), request, client, "https://example.test", "abc123");

        await Assert.That(response).Contains("Schema fetch timed out");
        await Assert.That(response).DoesNotContain("narrow the date range");
        await Assert.That(response).DoesNotContain("internal error");
    }

    [Test]
    public async Task Map_response_unwraps_schema_text_envelope() {
        var text = McpAnalyticsServer.MapResponse("get_analytics_schema", HttpStatusCode.OK,
            """{"text":"Views and columns:\n  v_an_sessions(...)","max_rows":300}""", out var isError);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).StartsWith("Views and columns:");
        await Assert.That(text).DoesNotContain("max_rows");
    }

    [Test]
    public async Task Map_response_appends_truncation_trailer_only_when_truncated() {
        var truncated = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.OK,
            """{"columns":["n"],"rows":[{"n":1}],"row_count":1,"truncated":true,"max_rows":300}""", out var isError1);
        await Assert.That(isError1).IsFalse();
        await Assert.That(truncated).Contains("WARNING: result truncated to 300 rows");
        await Assert.That(truncated).Contains("Aggregate in SQL");

        var full = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.OK,
            """{"columns":["n"],"rows":[{"n":1}],"row_count":1,"truncated":false,"max_rows":300}""", out var isError2);
        await Assert.That(isError2).IsFalse();
        await Assert.That(full).DoesNotContain("truncated to");
    }

    [Test]
    public async Task Map_response_surfaces_rejection_reason_for_self_repair() {
        var text = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.BadRequest,
            """{"title":"query_rejected","status":400,"detail":"disallowed table: sessions"}""", out var isError);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("REJECTED: disallowed table: sessions");
    }

    [Test]
    public async Task Map_response_maps_statuses_to_actionable_messages() {
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.Unauthorized, "", out _))
            .IsEqualTo(McpAnalyticsServer.NotLoggedInMessage);
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.NotFound, "", out _))
            .IsEqualTo(McpAnalyticsServer.NotSupportedMessage);
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.RequestTimeout, "", out _))
            .IsEqualTo(McpAnalyticsServer.TimedOutMessage);
        // The query-specific "narrow the date range" hint is wrong for a schema fetch.
        await Assert.That(McpAnalyticsServer.MapResponse("get_analytics_schema", HttpStatusCode.RequestTimeout, "", out _))
            .IsEqualTo(McpAnalyticsServer.SchemaTimedOutMessage);
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.InternalServerError, "boom", out _))
            .Contains("HTTP 500");
    }

    [Test]
    public async Task Map_response_extracts_problem_detail_for_unlisted_statuses() {
        // An off-contract status (e.g. 429 from a rate limiter) that still speaks RFC-7807 should
        // surface the clean `detail`, not the raw JSON envelope.
        var rateLimited = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.TooManyRequests,
            """{"title":"rate_limited","status":429,"detail":"too many analytics queries — retry in 60s"}""", out var isError);
        await Assert.That(isError).IsTrue();
        await Assert.That(rateLimited).Contains("HTTP 429");
        await Assert.That(rateLimited).Contains("retry in 60s");
        await Assert.That(rateLimited).DoesNotContain("\"title\"");

        // Non-problem-document bodies (e.g. an HTML 502 from a proxy) fall back to the raw body.
        var proxyHtml = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.BadGateway,
            "<html>502 Bad Gateway</html>", out _);
        await Assert.That(proxyHtml).Contains("502 Bad Gateway");
    }

    [Test]
    public async Task Tools_list_exposes_the_two_read_only_tools() {
        var tools = McpAnalyticsServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name).ToArray())
            .IsEquivalentTo(new[] { "get_analytics_schema", "query_analytics" });

        var query = tools.Single(t => t.Name == "query_analytics");
        await Assert.That(query.InputSchema.Required).IsEquivalentTo(new[] { "sql" });
        await Assert.That(query.InputSchema.Properties.Keys.ToArray())
            .IsEquivalentTo(new[] { "sql", "scope", "max_rows" });
    }

    sealed class TimingOutHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
    }
}
