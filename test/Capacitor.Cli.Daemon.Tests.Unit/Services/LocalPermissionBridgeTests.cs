using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LocalPermissionBridgeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static (LocalPermissionBridge bridge, FakeServerConnection server) CreateBridge(
            Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? respond = null,
            ILogger<LocalPermissionBridge>? logger = null
        ) {
        var server = new FakeServerConnection(respond);
        var bridge = new LocalPermissionBridge(server, logger ?? NullLogger<LocalPermissionBridge>.Instance);

        return (bridge, server);
    }

    // Short HttpClient timeout so a misbehaving listener fails the test in seconds rather than
    // stalling the suite on the default ~100s. Bridge replies are loopback and immediate, so
    // anything past 5s indicates a regression worth surfacing fast.
    static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    // ---------------------------------------------------------------------------------
    // Shutdown must be idempotent AND non-throwing.
    //
    // The bridge is registered through two DI descriptors, so the container disposes the same
    // instance twice within one ServiceProviderEngineScope walk. Before the fix the second pass hit
    // StopAsync's _cts.CancelAsync() on an already-disposed CTS; the ObjectDisposedException
    // surfaced where nothing catches it, terminating the daemon.
    //
    // Daemon_host_registration_disposes_the_bridge_twice_without_terminating is the PRODUCTION
    // reproduction. The two tests immediately below are synthetic ordering/robustness checks that
    // pin the guard directly. All three fail without the fix.
    // ---------------------------------------------------------------------------------

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Stop_then_dispose_then_dispose_again_does_not_throw() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await bridge.StopAsync(CancellationToken.None);
        await bridge.DisposeAsync();

        // SYNTHETIC ordering test, not a production interleaving: in production both DisposeAsync
        // calls happen inside DI's single ServiceProviderEngineScope walk (see the host test
        // below) — DaemonRunner never disposes this service itself. Driving the sequence directly
        // pins the guard independently of the DI wiring, so a future registration change cannot
        // quietly remove the coverage.
        await bridge.DisposeAsync();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Stop_after_dispose_does_not_throw() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        await bridge.DisposeAsync();

        // SYNTHETIC robustness test. The corrected production cause involves no race, and this
        // exact order is not what the daemon does — but StopAsync is public and reachable from the
        // hosted-service lifecycle independently of disposal, so it must not throw on an
        // already-disposed CTS. This is the call that threw before the fix.
        await bridge.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The production reproduction, and the deterministic one. DaemonRunner registers this type
    /// through TWO singleton descriptors — <c>AddSingleton&lt;LocalPermissionBridge&gt;()</c> so the
    /// orchestrator can read its bound URL, and an <c>AddHostedService</c> factory resolving that
    /// same instance so the listener starts before any agent spawns. Microsoft DI tracks
    /// disposables per DESCRIPTOR without de-duplicating by reference, so
    /// <c>ServiceProviderEngineScope.DisposeAsync</c> walks this one instance twice, sequentially.
    /// No thread race is involved — the earlier assumption that a SIGTERM-driven shutdown was
    /// required to reach the second pass was wrong.
    /// </summary>
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Daemon_host_registration_disposes_the_bridge_twice_without_terminating() {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ServerConnection>(_ => new FakeServerConnection(null));

        // The exact two-descriptor registration from DaemonRunner.RunAsync.
        builder.Services.AddSingleton<LocalPermissionBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPermissionBridge>());

        var host = builder.Build();
        await host.StartAsync();

        // The production shutdown sequence: stop the hosted services, then dispose the host —
        // which disposes the ServiceProvider and, with it, this instance once per descriptor.
        await host.StopAsync();
        await DaemonRunner.DisposeHostAsync(host);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StartAsync_ExposesLoopbackBaseUrlWithToken() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            await Assert.That(bridge.BaseUrl).IsNotNull();
            var uri = new Uri(bridge.BaseUrl!);

            await Assert.That(uri.Host).IsEqualTo("127.0.0.1");
            await Assert.That(uri.Scheme).IsEqualTo("http");

            // Path is "/<32-char hex token>"
            var token = uri.AbsolutePath.Trim('/');
            await Assert.That(token.Length).IsEqualTo(32);
            await Assert.That(token.All(Uri.IsHexDigit)).IsTrue();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task PostingToWrongTokenReturns404() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            var uri      = new Uri(bridge.BaseUrl!);
            var bogusUrl = $"http://127.0.0.1:{uri.Port}/{new string('0', 32)}/claude/permission-request";

            using var client   = CreateClient();
            using var response = await client.PostAsync(bogusUrl, JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task PostingToWrongPathReturns404() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/something-else", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task GetReturns404() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.GetAsync($"{bridge.BaseUrl}/permission-request");

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task MalformedJsonReturns400() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var content  = new StringContent("{ this is not json", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", content);

            await Assert.That((int)response.StatusCode).IsEqualTo(400);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task MissingSessionIdReturns400() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { tool_name = "Bash" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(400);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    /// <summary>The permission-request body is capped like the flow-result submission body: an
    /// oversized POST from the local hook must 413 before JSON parsing, never buffer unbounded.</summary>
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task OversizedPermissionRequestBodyReturns413() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            var       oversized = new string('x', LocalPermissionBridge.MaxPermissionRequestBodyBytes + 1024);
            using var content   = new StringContent(oversized, Encoding.UTF8, "application/json");
            using var response  = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", content);

            await Assert.That((int)response.StatusCode).IsEqualTo(413);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ValidRequestStripsDashesAndForwardsArgsToServer() {
        var (bridge, server) = CreateBridge((sid, tool, input, suggestions, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();

            var payload = new {
                session_id             = "11111111-2222-3333-4444-555555555555",
                tool_name              = "Bash",
                tool_input             = new { command = "ls" },
                permission_suggestions = new { reason  = "ok" }
            };
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);

            var call = server.Calls[0];
            await Assert.That(call.SessionId).IsEqualTo("11111111222233334444555555555555");
            await Assert.That(call.ToolName).IsEqualTo("Bash");
            await Assert.That(call.ToolInput?.GetProperty("command").GetString()).IsEqualTo("ls");
            await Assert.That(call.Suggestions?.GetProperty("reason").GetString()).IsEqualTo("ok");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ResponseShapeMirrorsClaudeHookSchema() {
        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc" }));

            var       body = await response.Content.ReadAsStringAsync();
            using var doc  = JsonDocument.Parse(body);

            var hookOutput = doc.RootElement.GetProperty("hookSpecificOutput");
            await Assert.That(hookOutput.GetProperty("hookEventName").GetString()).IsEqualTo("PermissionRequest");
            await Assert.That(hookOutput.GetProperty("decision").GetProperty("behavior").GetString()).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ApplyPermissionsAndUpdatedInputAreCopiedIntoDecision() {
        using var apDoc = JsonDocument.Parse("""{"allow":["Bash(ls:*)"]}""");
        using var uiDoc = JsonDocument.Parse("""{"command":"ls -la"}""");

        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", apDoc.RootElement.Clone(), uiDoc.RootElement.Clone()))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc" }));

            var       body = await response.Content.ReadAsStringAsync();
            using var doc  = JsonDocument.Parse(body);

            var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("applyPermissions").GetProperty("allow")[0].GetString()).IsEqualTo("Bash(ls:*)");
            await Assert.That(decision.GetProperty("updatedInput").GetProperty("command").GetString()).IsEqualTo("ls -la");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task ServerFailureFallsBackToDeny() {
        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            throw new InvalidOperationException("hub call broke")
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            var       body = await response.Content.ReadAsStringAsync();
            using var doc  = JsonDocument.Parse(body);

            var decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("deny");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StopAsyncReleasesPort() {
        var (bridge, _) = CreateBridge();
        TcpListener? probe    = null;
        var          disposed = false;

        try {
            await bridge.StartAsync(CancellationToken.None);

            var port = new Uri(bridge.BaseUrl!).Port;
            await bridge.StopAsync(CancellationToken.None);

            // After stop, the port should accept a fresh bind. If StopAsync didn't release
            // it, this would either throw or hang.
            probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();

            // Keep the replacement listener bound while disposing the bridge. This reproduces
            // the suite-level race where StopAsync released the port, another fixture claimed it,
            // and the old listener's later Close() threw EADDRINUSE.
            await bridge.DisposeAsync();
            disposed = true;
        } finally {
            probe?.Stop();

            // Ensure cleanup still runs if setup or the assertion above fails. Dispose is
            // intentionally idempotent, so retrying after a partial shutdown is safe.
            if (!disposed) await bridge.DisposeAsync();
        }
    }

    // ── Per-vendor routing tests ──────────────────────────────────────────────

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Claude_path_returns_claude_response_shape() {
        using var apDoc = JsonDocument.Parse("""{"allow":["Bash(*)"]}""");
        using var uiDoc = JsonDocument.Parse("""{"command":"ls"}""");

        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", apDoc.RootElement.Clone(), uiDoc.RootElement.Clone()))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.TryGetProperty("applyPermissions", out _)).IsTrue();
            await Assert.That(decision.TryGetProperty("updatedInput", out _)).IsTrue();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Codex_path_returns_codex_response_shape() {
        using var apDoc = JsonDocument.Parse("""{"allow":["Bash(*)"]}""");
        using var uiDoc = JsonDocument.Parse("""{"command":"ls"}""");

        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", apDoc.RootElement.Clone(), uiDoc.RootElement.Clone()))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("allow");
            await Assert.That(decision.TryGetProperty("applyPermissions", out _)).IsFalse();
            await Assert.That(decision.TryGetProperty("updatedInput", out _)).IsFalse();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Legacy_path_without_vendor_returns_404() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Unknown_vendor_returns_404() {
        var (bridge, _) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/bogus/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(404);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Codex_path_invokes_server_and_shapes_codex_response() {
        // The bridge derives the vendor from the URL path segment and uses it
        // LOCALLY to pick the hook response shape. The vendor is intentionally
        // NOT forwarded over the SignalR wire — JsonHubProtocol's strict arg-count
        // binder would reject any extra argument the server hub method doesn't
        // declare. The proof of correct vendor routing is the response shape.
        var (bridge, server) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);

            // Codex hook schema: hookSpecificOutput.decision.behavior, no applyPermissions / updatedInput.
            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("allow");
            await Assert.That(decision.TryGetProperty("applyPermissions", out _)).IsFalse();
            await Assert.That(decision.TryGetProperty("updatedInput", out _)).IsFalse();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Claude_path_invokes_server_and_shapes_claude_response() {
        // Mirror of the Codex test: vendor is local-only state in the bridge, used
        // to pick the Claude-flavoured hookSpecificOutput envelope. Not on the wire.
        var (bridge, server) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);

            var       body       = await response.Content.ReadAsStringAsync();
            using var doc        = JsonDocument.Parse(body);
            var       hookOutput = doc.RootElement.GetProperty("hookSpecificOutput");
            await Assert.That(hookOutput.GetProperty("hookEventName").GetString()).IsEqualTo("PermissionRequest");
            await Assert.That(hookOutput.GetProperty("decision").GetProperty("behavior").GetString()).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Codex_path_strips_apply_permissions_from_server_decision() {
        using var apDoc = JsonDocument.Parse("""{"allow":["Bash(*)"]}""");

        var (bridge, _) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", apDoc.RootElement.Clone(), null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client   = CreateClient();
            using var response = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.TryGetProperty("applyPermissions", out _)).IsFalse();
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // A review-flow reviewer's own result-submission tool must be auto-approved by the bridge
    // WITHOUT surfacing a user prompt. Codex fires a PermissionRequest for the MCP tool call even
    // under `--ask-for-approval never`, and its hook bridges here; without this the unattended
    // reviewer blocks on a decision it can never get. The auto-approve is reviewer-token-gated:
    // only the participant's dedicated token reaches it (see the shared-token tests below).
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Codex_flow_result_submission_is_auto_approved_without_a_server_round_trip() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            // If the bridge ever consults the server for this tool the test should fail loudly:
            // deny so an accidental round-trip can't masquerade as an allow.
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new {
                session_id = "abc",
                tool_name  = "mcp__kcap_flow_result__submit_review_result",
                tool_input = new { kind = "clean" }
            };
            using var response = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);

            // Short-circuited entirely — the server hub was never asked.
            await Assert.That(server.Calls.Count).IsEqualTo(0);

            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // Regression guard: an ordinary tool still goes through the server (no over-broad auto-approve).
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Non_flow_result_tool_still_consults_the_server() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "Bash", tool_input = new { command = "ls" } };
            using var response = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // Qodo #255: the auto-approve must be precise. A tool from a DIFFERENT server whose id merely
    // ends in "submit_review_result" must NOT be short-circuited — it goes to the server like any
    // other tool, so the auto-approve can't be used to slip an unrelated tool past the prompt.
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Same_named_tool_from_a_different_server_is_not_auto_approved() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();

            // Ends with the tool name but names some other server — must be treated as untrusted.
            var payload = new { session_id = "abc", tool_name = "mcp__evil_server__submit_review_result" };
            using var response = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // The bare tool name (a vendor that passes the raw MCP tool name with no server prefix) is the
    // flow-result tool and is auto-approved without a server round-trip — on a reviewer token, even
    // for a non-config-locked vendor (claude): the bare names are unique to the reserved channel,
    // which only flow participants receive. This pins the bare-name arm as intentional.
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Bare_flow_result_tool_name_is_auto_approved() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "submit_review_result" };
            using var response = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);

            var       body     = await response.Content.ReadAsStringAsync();
            using var doc      = JsonDocument.Parse(body);
            var       decision = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
            await Assert.That(decision.GetProperty("behavior").GetString()).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // ── Reserved-channel auto-approve: send_flow_message + adversarial shapes ────────────
    //
    // The production defect: an unattended Codex participant calling send_flow_message got
    // "Denied out-of-allowlist tool mcp__kcap_flow_result__send_flow_message" — only
    // submit_review_result was special-cased. The fix generalizes the special case to the
    // catalog's unattended-safe set, parsed (never substring-matched) and reviewer-gated.

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_auto_approves_server_qualified_send_flow_message() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "mcp__kcap_flow_result__send_flow_message" };
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Bare_send_flow_message_is_auto_approved() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "send_flow_message" };
            using var r = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_auto_approves_hyphenated_reserved_server_qualified_tool() {
        // Claude normalizes hyphens to underscores, but the parse accepts the raw hyphenated
        // server id too — both normalize to the same exact server segment.
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "mcp__kcap-flow-result__send_flow_message" };
            using var r = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_denies_prefixed_spoof_of_reserved_server() {
        // "mcp__evil_kcap_flow_result__send_flow_message": a Contains-based matcher would see
        // "kcap_flow_result" inside the server segment and auto-approve. The ENTIRE server
        // segment must equal the reserved channel id → deny (never a prompt on a reviewer token).
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "mcp__evil_kcap_flow_result__send_flow_message" };
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("deny");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_denies_suffixed_spoof_of_reserved_tool() {
        // "mcp__kcap_flow_result__evil_send_flow_message": an EndsWith-based matcher would see the
        // "send_flow_message" suffix and auto-approve. The ENTIRE tool segment must be an exact
        // safe-set member → deny.
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "mcp__kcap_flow_result__evil_send_flow_message" };
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("deny");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Shared_token_reserved_channel_tool_names_consult_the_server() {
        // The auto-approve is reviewer-gated: the reserved channel is only injected for flow
        // participants, so on the SHARED (interactive) token an identically-named tool is
        // untrusted — it takes the normal server prompt path, qualified or bare. (Previously the
        // shared token also auto-approved mcp__kcap_flow_result__submit_review_result.)
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("allow", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();

            string[] toolNames = [
                "mcp__kcap_flow_result__submit_review_result",
                "submit_review_result",
                "mcp__kcap_flow_result__send_flow_message",
                "send_flow_message"
            ];

            foreach (var toolName in toolNames) {
                var payload = new { session_id = "abc", tool_name = toolName };
                using var r = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(payload));
                await Assert.That((int)r.StatusCode).IsEqualTo(200);
            }

            await Assert.That(server.Calls.Count).IsEqualTo(toolNames.Length);
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_with_empty_allowlist_still_auto_approves_reserved_channel_tools() {
        // A generic flow participant (arbitrary role, no read allowlist) gets a token bound to an
        // EMPTY server list — the reserved channel is injected independently of the allowlist, so
        // its tools must still auto-approve.
        var (bridge, server) = CreateBridge((_, _, _, _, _) =>
            Task.FromResult(new PermissionDecision("deny", null, null))
        );

        try {
            await bridge.StartAsync(CancellationToken.None);
            var participantUrl = bridge.RegisterReviewerToken([]);

            using var client = CreateClient();

            var payload = new { session_id = "abc", tool_name = "mcp__kcap_flow_result__send_flow_message" };
            using var r = await client.PostAsync($"{participantUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Claude_cli_hook_post_target_lands_at_new_url() {
        // Verify that the bridge accepts POSTs at the /{token}/claude/permission-request URL
        // that the CLI's PermissionRequestCommand now targets (Task 10 migration).
        var (bridge, server) = CreateBridge();

        try {
            await bridge.StartAsync(CancellationToken.None);

            // Simulate the URL that PermissionRequestCommand builds:
            // {KCAP_DAEMON_URL}/claude/permission-request
            var       targetUrl = $"{bridge.BaseUrl}/claude/permission-request";
            using var client    = CreateClient();
            using var response  = await client.PostAsync(targetUrl, JsonContent.Create(new { session_id = "abc", tool_name = "Bash" }));

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);
            await Assert.That(server.Calls[0].ToolName).IsEqualTo("Bash");
        } finally {
            await bridge.DisposeAsync();
        }
    }

    // ── Unattended reviewer-token auto-approval ──────────────────────────────────────

    static async Task<string?> Behavior(HttpResponseMessage r) {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_auto_approves_bound_read_tool_without_server_round_trip() {
        // deny if the server is ever consulted, so an accidental round-trip can't masquerade as allow.
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("deny", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            var payload = new { session_id = "abc", tool_name = "get_pr_summary" };   // bare Codex name
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_auto_approves_server_qualified_tool_in_bound_allowlist() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("deny", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            var payload = new { session_id = "abc", tool_name = "mcp__kcap_review__get_pr_summary" };  // Claude form
            using var r = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_auto_approves_submit_review_result() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("deny", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            var payload = new { session_id = "abc", tool_name = "submit_review_result" };
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(await Behavior(r)).IsEqualTo("allow");
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_denies_server_qualified_tool_outside_bound_allowlist() {
        // Bound to kcap-review only; a kcap-memory (write) call is out of allowlist → DENY, never a prompt.
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("allow", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            var payload = new { session_id = "abc", tool_name = "mcp__kcap_memory__save_memory" };
            using var r = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);      // NOT deferred to the interactive path
            await Assert.That(await Behavior(r)).IsEqualTo("deny");
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_missing_tool_name_returns_400() {
        var (bridge, server) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc" }));

            await Assert.That((int)r.StatusCode).IsEqualTo(400);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_missing_session_id_returns_400() {
        var (bridge, server) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { tool_name = "get_pr_summary" }));

            await Assert.That((int)r.StatusCode).IsEqualTo(400);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Shared_token_read_tool_still_prompts_no_escalation() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("allow", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);

            using var client = CreateClient();
            // Same tool, but on the SHARED (interactive) token → must go to the server, not auto-approve.
            var payload = new { session_id = "abc", tool_name = "get_pr_summary" };
            using var r = await client.PostAsync($"{bridge.BaseUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(1);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Revoked_reviewer_token_returns_404() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);
            bridge.RevokeReviewerToken(reviewerUrl);

            using var client = CreateClient();
            using var r1 = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "get_pr_summary" }));
            using var r2 = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "submit_review_result" }));

            await Assert.That((int)r1.StatusCode).IsEqualTo(404);
            await Assert.That((int)r2.StatusCode).IsEqualTo(404);
        } finally { await bridge.DisposeAsync(); }
    }

    // Regression: revocation must remove the grant from the dictionary ONLY, never the HttpListener
    // prefix. On the managed (Linux/macOS) HttpListener, a request on a KEEP-ALIVE connection to a
    // just-removed prefix no longer routes to HandleAsync and yields a transport artifact — a
    // spurious empty-body 200 or a connection reset — instead of the intended 404. Reusing one
    // client (so every request after the first rides a keep-alive connection) across many
    // register→revoke→request cycles reproduces that ~4%-per-request artifact deterministically:
    // with the prefix kept, every revoked request cleanly 404s from the dict miss.
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Revoked_token_requests_404_on_reused_keepalive_connection() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            using var client = CreateClient();

            for (var i = 0; i < 200; i++) {
                var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);
                bridge.RevokeReviewerToken(reviewerUrl);
                using var r = await client.PostAsync(
                    $"{reviewerUrl}/codex/permission-request",
                    JsonContent.Create(new { session_id = "abc", tool_name = "submit_review_result" }));
                await Assert.That((int)r.StatusCode).IsEqualTo(404);
            }
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Concurrent_reviewer_tokens_are_independent() {
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("deny", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var urlA = bridge.RegisterReviewerToken(["kcap-review"]);
            var urlB = bridge.RegisterReviewerToken(["kcap-review"]);
            bridge.RevokeReviewerToken(urlA);

            using var client = CreateClient();
            using var rB = await client.PostAsync($"{urlB}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "get_pr_summary" }));
            using var rA = await client.PostAsync($"{urlA}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "get_pr_summary" }));

            await Assert.That((int)rB.StatusCode).IsEqualTo(200);   // B unaffected by revoking A
            await Assert.That(await Behavior(rB)).IsEqualTo("allow");
            await Assert.That((int)rA.StatusCode).IsEqualTo(404);   // A revoked
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_is_never_logged() {
        var log = new CapturingLogger();
        var (bridge, _) = CreateBridge(logger: log);
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);
            var token       = new Uri(reviewerUrl).AbsolutePath.Trim('/');

            using var client = CreateClient();
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "get_pr_summary" }));
            await Assert.That((int)r.StatusCode).IsEqualTo(200);

            foreach (var msg in log.Messages)
                await Assert.That(msg.Contains(token, StringComparison.Ordinal)).IsFalse();
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_denies_bare_tool_name_from_non_codex_vendor() {
        // A bare tool name is only provably a kcap tool for a config-locked vendor (codex). On a
        // claude-path reviewer token, a bare built-in like "Bash" must be DENIED, not auto-approved.
        var (bridge, server) = CreateBridge((_, _, _, _, _) => Task.FromResult(new PermissionDecision("allow", null, null)));
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            using var r = await client.PostAsync($"{reviewerUrl}/claude/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "Bash" }));

            await Assert.That((int)r.StatusCode).IsEqualTo(200);
            await Assert.That(server.Calls.Count).IsEqualTo(0);        // not deferred to a prompt
            await Assert.That(await Behavior(r)).IsEqualTo("deny");
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_whitespace_session_id_returns_400() {
        var (bridge, server) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            // "----" → "" after dash-strip → not a usable session id → 400 before any auto-approval.
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "----", tool_name = "get_pr_summary" }));

            await Assert.That((int)r.StatusCode).IsEqualTo(400);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_token_whitespace_tool_name_returns_400() {
        var (bridge, server) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            using var r = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(new { session_id = "abc", tool_name = "   " }));

            await Assert.That((int)r.StatusCode).IsEqualTo(400);
            await Assert.That(server.Calls.Count).IsEqualTo(0);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Reviewer_context_get_returns_bound_immutable_generation_only() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var first = ReviewGeneration("first");
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"], first);

            using var client = CreateClient();
            using var response = await client.GetAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs");

            await Assert.That((int)response.StatusCode).IsEqualTo(200);
            await Assert.That(await response.Content.ReadAsByteArrayAsync())
                .IsEquivalentTo(first.JsonUtf8);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Shared_revoked_and_non_exact_routes_cannot_read_reviewer_context() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"], ReviewGeneration("one"));
            using var client = CreateClient();

            using var shared = await client.GetAsync(
                $"{bridge.BaseUrl}/review-context/workspace-mcp-configs");
            using var query = await client.GetAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs?path=.mcp.json");
            using var extra = await client.GetAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs/extra");
            using var post = await client.PostAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs", JsonContent.Create(new { }));
            bridge.RevokeReviewerToken(reviewerUrl);
            using var revoked = await client.GetAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs");

            await Assert.That((int)shared.StatusCode).IsEqualTo(404);
            await Assert.That((int)query.StatusCode).IsEqualTo(404);
            await Assert.That((int)extra.StatusCode).IsEqualTo(404);
            await Assert.That((int)post.StatusCode).IsEqualTo(404);
            await Assert.That((int)revoked.StatusCode).IsEqualTo(404);
        } finally { await bridge.DisposeAsync(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task Publishing_reviewer_context_atomically_replaces_the_generation() {
        var (bridge, _) = CreateBridge();
        try {
            await bridge.StartAsync(CancellationToken.None);
            var first = ReviewGeneration("first");
            var second = ReviewGeneration("second");
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"], first);

            var retired = bridge.PublishReviewerContext(reviewerUrl, second);

            await Assert.That(retired).IsSameReferenceAs(first);
            using var client = CreateClient();
            using var response = await client.GetAsync(
                $"{reviewerUrl}/review-context/workspace-mcp-configs");
            await Assert.That(await response.Content.ReadAsByteArrayAsync())
                .IsEquivalentTo(second.JsonUtf8);
        } finally { await bridge.DisposeAsync(); }
    }

    // The path is handed to a bridge that may write it, so it lives in a directory that gets cleaned.
    BorrowedReviewContextGeneration ReviewGeneration(string value) =>
        new(value, Tmp.PathTo(value), Encoding.UTF8.GetBytes($"{{\"value\":\"{value}\"}}"));

    /// <summary>A second bridge retries when its first probed port is already claimed in-process.</summary>
    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StartAsync_FirstPortAlreadyClaimed_RetriesAndRecovers() {
        var (first, _)  = CreateBridge();
        var (second, _) = CreateBridge();

        try {
            await first.StartAsync(CancellationToken.None);
            var firstPort    = new Uri(first.BaseUrl!).Port;
            var reservations = 0;

            second.ReserveLoopbackPortOverrideForTest = () => {
                if (Interlocked.Increment(ref reservations) == 1) return firstPort;

                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                try { return ((IPEndPoint)probe.LocalEndpoint).Port; } finally { probe.Stop(); }
            };

            await second.StartAsync(CancellationToken.None);

            await Assert.That(reservations).IsGreaterThanOrEqualTo(2);
            await Assert.That(new Uri(second.BaseUrl!).Port).IsNotEqualTo(firstPort);
        } finally {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
    public async Task StartAsync_CancellationInterruptsClaimRetry() {
        var (first, _)  = CreateBridge();
        var (second, _) = CreateBridge();
        using var cts = new CancellationTokenSource();

        try {
            await first.StartAsync(CancellationToken.None);
            var firstPort = new Uri(first.BaseUrl!).Port;

            second.ReserveLoopbackPortOverrideForTest = () => {
                cts.Cancel();
                return firstPort;
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => second.StartAsync(cts.Token));
            await Assert.That(second.BaseUrl).IsNull();
        } finally {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    [Test]
    [Arguments(10048, true)]
    [Arguments(32, true)]
    [Arguments(48, true)]
    [Arguments(98, true)]
    [Arguments(5, false)]
    public async Task IsAddressInUse_ClassifiesPlatformErrors(int errorCode, bool expected) {
        await Assert.That(LocalPermissionBridge.IsAddressInUse(new HttpListenerException(errorCode)))
            .IsEqualTo(expected);
    }

    /// <summary>
    /// The borrowed reviewer's delivery path. Its sandbox redirects HOME to a per-launch state dir,
    /// so the result channel cannot load a token store — it POSTs here instead and the daemon, which
    /// runs unsandboxed and holds the real credential, forwards. The forwarder lives on the GRANT so
    /// revoking the reviewer closes the submit path in the same operation that closes the read path.
    /// </summary>
    [Test]
    public async Task Reviewer_flow_result_capability_forwards_the_body_then_dies_with_the_token() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        try {
            string? forwardedPath = null;
            string? forwarded     = null;
            var reviewerUrl = bridge.RegisterReviewerToken([],
                submitForwarder: (apiPath, body, _) => {
                    forwardedPath = apiPath;
                    forwarded     = body;
                    return Task.FromResult((200, "{\"status\":\"accepted\"}"));
                });

            using var client = CreateClient();
            var response = await client.PostAsync($"{reviewerUrl}/flow-result",
                new StringContent("{\"kind\":\"clean\"}", Encoding.UTF8, "application/json"));

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(forwarded).IsEqualTo("{\"kind\":\"clean\"}");
            // The upstream path is the bridge's own constant, not anything the caller supplied.
            await Assert.That(forwardedPath).IsEqualTo("/api/flows/reviewer/result");
            await Assert.That(await response.Content.ReadAsStringAsync())
                .IsEqualTo("{\"status\":\"accepted\"}");

            // Fail-safe on revoke: a live submit path outliving the reviewer would let a lingering
            // child process report into a flow whose participant the server already reaped.
            bridge.RevokeReviewerToken(reviewerUrl);
            var afterRevoke = await client.PostAsync($"{reviewerUrl}/flow-result",
                new StringContent("{\"kind\":\"clean\"}", Encoding.UTF8, "application/json"));
            await Assert.That(afterRevoke.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        } finally {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The submit body is attacker-controlled: the POSTing process is a sandboxed vendor child. An
    /// unbounded ReadToEndAsync would let it exhaust the daemon's memory, so the bridge caps the read
    /// itself rather than trusting Content-Length — which a chunked or lying client need not honour.
    /// The forwarder must never run for a rejected body.
    /// </summary>
    [Test]
    public async Task Oversized_submit_body_is_rejected_without_reaching_the_forwarder() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        try {
            var forwarded = 0;
            var reviewerUrl = bridge.RegisterReviewerToken([],
                submitForwarder: (_, _, _) => {
                    forwarded++;
                    return Task.FromResult((200, "{}"));
                });

            using var client = CreateClient();
            var oversized = new string('x', LocalPermissionBridge.MaxSubmitBodyBytes + 1024);
            var response = await client.PostAsync($"{reviewerUrl}/flow-result",
                new StringContent(oversized, Encoding.UTF8, "application/json"));

            await Assert.That((int)response.StatusCode).IsEqualTo(413);
            await Assert.That(forwarded).IsEqualTo(0);
        } finally {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A body just under the cap still goes through, so the guard cannot pass by rejecting
    /// everything — the failure mode a bare size check invites.</summary>
    [Test]
    public async Task Submit_body_just_under_the_cap_still_reaches_the_forwarder() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        try {
            string? forwarded = null;
            var reviewerUrl = bridge.RegisterReviewerToken([],
                submitForwarder: (_, body, _) => {
                    forwarded = body;
                    return Task.FromResult((200, "{}"));
                });

            using var client = CreateClient();
            var payload = new string('y', LocalPermissionBridge.MaxSubmitBodyBytes - 1024);
            var response = await client.PostAsync($"{reviewerUrl}/flow-result",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(forwarded).IsEqualTo(payload);
        } finally {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>A grant minted without a forwarder (every non-borrowed reviewer) must not expose a
    /// submit path at all — not a 500, not an empty 200. Pins that the capability is scoped to the
    /// launches that actually need it rather than opening on every reviewer token.</summary>
    [Test]
    public async Task Reviewer_without_a_submit_forwarder_has_no_flow_result_capability() {
        var (bridge, _) = CreateBridge();
        await bridge.StartAsync(CancellationToken.None);

        try {
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"]);

            using var client = CreateClient();
            var response = await client.PostAsync($"{reviewerUrl}/flow-result",
                new StringContent("{\"kind\":\"clean\"}", Encoding.UTF8, "application/json"));

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        } finally {
            await bridge.StopAsync(CancellationToken.None);
        }
    }

    sealed class CapturingLogger : ILogger<LocalPermissionBridge> {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel                logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}

/// <summary>
/// Bypasses ServerConnection's HubConnection plumbing so the bridge can be exercised
/// without a real server. RequestPermissionAsync is virtual on the base class.
/// </summary>
sealed class FakeServerConnection(Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? respond)
    : ServerConnection(new() { Name = "test", ServerUrl = "http://127.0.0.1:1" }, UnusedTokenStore.Create(),
        NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance) {
    public List<Call> Calls { get; } = [];
    public List<(string SessionId, string RequestId, PermissionDecision Decision)> Responds { get; } = [];

    /// Scripted legs. Null = compose through RequestPermissionAsync via `respond`.
    public Func<CancellationToken, Func<bool>, Task<string>>? BeginScript { get; set; }
    public Func<string, CancellationToken, Task<PermissionDecision>>? AwaitScript { get; set; }
    public Func<RespondOutcome> RespondScript = () => new RespondOutcome(RespondOutcomeKind.Applied, null);

    public override Task<PermissionDecision> RequestPermissionAsync(string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default) {
        Calls.Add(new Call(sessionId, toolName, toolInput, suggestions));
        return respond is null ? Task.FromResult(new PermissionDecision("allow", null, null)) : respond(sessionId, toolName, toolInput, suggestions, ct);
    }

    public override Task<string> BeginPermissionRequestAsync(string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct, Func<bool> abandoned) {
        Calls.Add(new Call(sessionId, toolName, toolInput, suggestions));
        if (BeginScript is not null) return BeginScript(ct, abandoned);
        if (abandoned()) throw new PermissionRequestAbandonedException();
        return Task.FromResult("srv-1");
    }

    public override Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct) =>
        AwaitScript is not null ? AwaitScript(serverRequestId, ct)
            : respond is not null ? respond("", null, null, null, ct)
            : Task.FromResult(new PermissionDecision("allow", null, null));

    public override Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) {
        Responds.Add((sessionId, serverRequestId, decision));
        return Task.FromResult(RespondScript());
    }

    public sealed record Call(string SessionId, string? ToolName, JsonElement? ToolInput, JsonElement? Suggestions);
}
