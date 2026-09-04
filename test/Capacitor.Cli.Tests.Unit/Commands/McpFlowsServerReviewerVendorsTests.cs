using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerReviewerVendorsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient());

    static JsonObject ToolCall() => new() {
        ["params"] = new JsonObject { ["name"] = "list_reviewer_vendors", ["arguments"] = new JsonObject() }
    };

    // The tool payload is carried as the single content item's text (an MCP tool result).
    static JsonNode ResultJson(string response)
        => JsonNode.Parse(JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

    [Test]
    public async Task Lists_repo_hosting_reviewers_for_this_machine() {
        // The tool reads the machine id read-only (ReadPersisted, never Get) and filters on it; mirror
        // that so the stub daemon matches whether or not machine.json exists in the test env (a null id
        // drops the filter, so the daemon is included either way).
        var machine = new MachineId(Config.Root).ReadPersisted() ?? "test-machine";
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""
            [{"name":"d1","repo_paths":["/repo/a"],"machine_id":"{{machine}}",
              "supported_vendors":["codex","claude"],"unattended_vendors":["codex","claude"]}]
            """));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/repo/a", repoRoot: "/repo/a", repoInfo: null, driverVendor: "claude");

        await Assert.That(JsonNode.Parse(response)!["result"]!["isError"]).IsNull();
        var parsed = ResultJson(response);
        await Assert.That(parsed["reviewers"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(parsed["driver_vendor"]!.GetValue<string>()).IsEqualTo("claude");
        // reason lives under diagnostics; it is omitted (null) when reviewers are present.
        await Assert.That(parsed["diagnostics"]!["reason"]).IsNull();
    }

    [Test]
    public async Task Server_error_maps_to_lookup_failed() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/r", repoRoot: "/r", repoInfo: null, driverVendor: null);

        var parsed = ResultJson(response);
        await Assert.That(parsed["diagnostics"]!["reason"]!.GetValue<string>()).IsEqualTo("lookup_failed");
        await Assert.That(parsed["reviewers"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repo_unresolved_short_circuits_before_any_request() {
        using var server = WireMockServer.Start();
        // Would be an auth error if the tool called it — it must not, because repo_unresolved is a
        // local precondition that outranks any lookup/auth failure.
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/x", repoRoot: null, repoInfo: null, driverVendor: null);

        await Assert.That(JsonNode.Parse(response)!["result"]!["isError"]).IsNull();
        await Assert.That(ResultJson(response)["diagnostics"]!["reason"]!.GetValue<string>()).IsEqualTo("repo_unresolved");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0); // never hit the server
    }

    [Test]
    public async Task Repo_unresolved_when_no_repo_root() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/x", repoRoot: null, repoInfo: null, driverVendor: null);

        var parsed = ResultJson(response);
        await Assert.That(parsed["diagnostics"]!["reason"]!.GetValue<string>()).IsEqualTo("repo_unresolved");
    }
}
