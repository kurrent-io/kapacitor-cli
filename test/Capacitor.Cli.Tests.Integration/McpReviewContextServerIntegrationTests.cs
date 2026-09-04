using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Integration;

[NotInParallel]
public class McpReviewContextServerIntegrationTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Daemon_context_mode_starts_without_backend_and_performs_one_exact_get() {
        var token = "0123456789abcdef0123456789abcdef";
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var capability = $"http://127.0.0.1:{port}/{token}/review-context/workspace-mcp-configs";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");
        listener.Start();
        var manifest = "{\"schemaVersion\":1,\"entries\":[]}";
        using var config = new TempConfigRoot();
        var requests = 0;
        var serve = Task.Run(async () => {
            var context = await listener.GetContextAsync();
            Interlocked.Increment(ref requests);
            await Assert.That(context.Request.HttpMethod).IsEqualTo("GET");
            await Assert.That(context.Request.RawUrl)
                .IsEqualTo($"/{token}/review-context/workspace-mcp-configs");
            await Assert.That(context.Request.Headers["Authorization"]).IsNull()
                .Because("the URL is the whole credential; a lane that attaches a bearer would send one it never read");
            var bytes = Encoding.UTF8.GetBytes(manifest);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var process = Spawn(capability, config.Root);
        try {
            var initialize = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject()
            });
            await Assert.That(initialize["result"]!["serverInfo"]!["name"]!.GetValue<string>())
                .IsEqualTo("kcap-review-context");
            await Assert.That(requests).IsEqualTo(0);

            var listed = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list",
                ["params"] = new JsonObject()
            });
            var tools = listed["result"]!["tools"]!.AsArray();
            await Assert.That(tools.Count).IsEqualTo(1);
            await Assert.That(tools[0]!["name"]!.GetValue<string>())
                .IsEqualTo("get_branch_authored_mcp_configs");
            await Assert.That(requests).IsEqualTo(0);

            var called = await Send(process, new JsonObject {
                ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call",
                ["params"] = new JsonObject {
                    ["name"] = "get_branch_authored_mcp_configs",
                    ["arguments"] = new JsonObject()
                }
            });
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(called["result"]!["content"]![0]!["text"]!.GetValue<string>())
                .IsEqualTo(manifest);
            await Assert.That(requests).IsEqualTo(1);
            await Assert.That(Directory.GetFileSystemEntries(config.Directory)).IsEmpty()
                .Because("context mode must bypass auth/config and update-check state");
        } finally {
            try { process.StandardInput.Close(); } catch { }
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
    }

    Process Spawn(string capability, ConfigRoot config) {
        var info = KcapProcess.StartInfo(Daemons.Store, config, "mcp", "review");
        info.Environment["KCAP_REVIEW_CONTEXT_MODE"] = "1";
        info.Environment["KCAP_REVIEW_CONTEXT_URL"] = capability;
        info.Environment["KCAP_URL"] = "not-a-backend-url";

        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start kcap");
    }

    static async Task<JsonObject> Send(Process process, JsonObject request) {
        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        await process.StandardInput.FlushAsync();
        var line = await process.StandardOutput.ReadLineAsync(
            new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        if (line is null)
            throw new InvalidOperationException(
                "MCP process exited: " + await process.StandardError.ReadToEndAsync());
        return JsonNode.Parse(line)!.AsObject();
    }
}
