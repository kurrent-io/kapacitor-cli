using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp review</c>. Spawns the freshly-built CLI
/// binary against a WireMock-stubbed Capacitor server (via <c>KCAP_URL</c>) with an isolated
/// config dir (<c>KCAP_CONFIG_DIR</c>). Mirrors the harness in
/// <see cref="McpFlowsServerTests"/> / <see cref="McpSessionsServerTests"/>.
/// </summary>
public class McpReviewServerTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [GitRepo]         public required GitRepo         Repo    { get; init; }

    readonly WireMockServer _server           = WireMockServer.Start();
    readonly List<Process>  _spawnedProcesses = [];

    [Before(Test)]
    public void InitWorkspaceRepo() => Repo.AddRemote("https://github.com/test-owner/test-repo.git");

    public void Dispose() {
        foreach (var p in _spawnedProcesses) {
            try {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.Dispose();
            } catch {
                // best-effort cleanup
            }
        }

        _server.Stop();
    }


    /// <summary>
    /// Spawns <c>kcap mcp review</c> (argless / auto PR-detection) against WireMock.
    /// <paramref name="urlOverride"/> replaces the WireMock URL (used to exercise the
    /// invalid-server_url path).
    /// </summary>
    Process SpawnMcpServer(string provider = "None", string? urlOverride = null) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "review");
        psi.WorkingDirectory = Repo.Path;
        psi.Environment["KCAP_URL"] = urlOverride ?? _server.Url!;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var line      = await proc.StandardOutput.ReadLineAsync(cts.Token);

        if (line is null) {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"MCP server closed stdout without responding. Stderr: {stderr}");
        }

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse response as JSON object: {line}");
    }

    static async Task ShutdownAsync(Process proc) {
        try { proc.StandardInput.Close(); } catch { /* already closed */ }
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        } catch {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    static JsonObject InitializeRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "initialize",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsListRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/list",
        ["params"]  = new JsonObject()
    };

    [Test]
    public async Task Initialize_returns_kcap_review_server_info() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-review");
            // server-level instructions preamble steers clients toward kcap for why/history.
            await Assert.That(result["instructions"]?.GetValue<string>()).IsNotNull();
            await Assert.That(result["instructions"]!.GetValue<string>()).IsNotEmpty();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_review_tools() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var names = tools!.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("get_pr_summary")).IsTrue();
            await Assert.That(names.Contains("get_transcript")).IsTrue();

            // Hard gate: the entrypoint tools carry the comparative routing cue so a future
            // edit can't silently drop it.
            string Desc(string tool) => tools!.First(t => t?["name"]?.GetValue<string>() == tool)!["description"]!.GetValue<string>();
            await Assert.That(Desc("get_pr_summary")).Contains("before git blame or git log");
            await Assert.That(Desc("get_file_context")).Contains("before git blame or git log");
            await Assert.That(Desc("search_context")).Contains("before git blame or git log");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Regression: kcap-review auto-registers via the plugin manifest, so Claude Code / Codex
    /// spawn `kcap mcp review` for every session. initialize / tools/list must stay local-only —
    /// the authenticated client (and its GET /auth/config round-trip + re-auth stderr hint) is
    /// created lazily on the first tools/call, so sessions that never use a review tool pay
    /// nothing. Mirrors McpSessionsServer / McpFlowsServer.
    /// </summary>
    [Test]
    public async Task Initialize_and_tools_list_do_not_consult_auth() {
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));

            var authHits = _server.FindLogEntries(Request.Create().WithPath("/auth/config").UsingGet());
            await Assert.That(authHits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A scheme-less server_url is refused before dispatch, so a tool call returns a graceful
    /// JSON-RPC tool error and the server keeps serving.
    /// </summary>
    [Test]
    public async Task Tool_call_with_invalid_server_url_returns_error_and_server_survives() {
        using var proc = SpawnMcpServer(urlOverride: "not-a-valid-url");
        try {
            await SendRequest(proc, InitializeRequest(1));

            var call = new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 2,
                ["method"]  = "tools/call",
                ["params"]  = new JsonObject {
                    ["name"]      = "get_pr_summary",
                    ["arguments"] = new JsonObject()
                }
            };
            var response = await SendRequest(proc, call);
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            // Server survived the bad request — a follow-up still gets a response.
            var again = await SendRequest(proc, ToolsListRequest(3));
            await Assert.That(again["result"]?["tools"]).IsNotNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
