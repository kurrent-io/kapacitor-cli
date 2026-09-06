using System.Net;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare because KCAP_DAEMON_URL is read by more than one production command (also
// CodexHookCommand) and inherited by spawned children, so no cohort of key-holders
// can exclude its readers.
[NotInParallel]
public class PermissionRequestCommandTests {
    const string EnvVar = "KCAP_DAEMON_URL";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsUnset() {
        using var _ = EnvScope.Exclusive(EnvVar, null);

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsEmpty() {
        using var _ = EnvScope.Exclusive(EnvVar, "");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task AcceptsLoopbackHttpAndTrimsTrailingSlash() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://127.0.0.1:51234/abc/");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://127.0.0.1:51234/abc");
    }

    [Test]
    public async Task RejectsLocalhostDnsName() {
        // We require literal 127.0.0.1 — "localhost" could resolve to non-loopback in a misconfigured env.
        using var _ = EnvScope.Exclusive(EnvVar, "http://localhost:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsNonLoopbackHost() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://example.com:8080/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsHttpsLoopback() {
        // The daemon bridge is plain http on loopback — https implies a different
        // endpoint and shouldn't be accepted via this env var.
        using var _ = EnvScope.Exclusive(EnvVar, "https://127.0.0.1:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsMalformedUrl() {
        using var _ = EnvScope.Exclusive(EnvVar, "not-a-url");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task Bridge_payload_adds_agent_id_and_cwd_and_leaves_the_server_shape_alone() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":null,"cwd":"/repo","transcript_path":"/t"}""")!;
        var bridge = PermissionRequestCommand.BuildBridgePayload(node, "abc", "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(bridge["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(bridge["transcript_path"]).IsNull();

        var withoutAgent = PermissionRequestCommand.BuildBridgePayload(node, "abc", null);
        await Assert.That(withoutAgent["agent_id"]).IsNull();
        await Assert.That(withoutAgent["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
    }

    sealed class Accepting : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"behavior":"allow"}""", Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// The bridge post carries the raw tool input to an address the daemon minted, so it must draw the
    /// lane that ignores an ambient proxy — the agent's own environment supplies one often enough, and
    /// a proxied hop would take the payload off the machine.
    /// </summary>
    [Test]
    public async Task The_bridge_post_draws_the_loopback_lane() {
        using var bridgeUrl = EnvScope.Exclusive(EnvVar, "http://127.0.0.1:51234/bridge");
        // The bridge is the rendered agent's route; a terminal one records the event and never posts.
        using var rendered  = EnvScope.Exclusive("KCAP_RENDERED_AGENT", "1");
        using var handler   = new Accepting();
        var       http      = new RecordingCapacitorHttpClient(handler);

        var command = new PermissionRequestCommand(
            Config.Root, Resolutions.None(Config.Root), http);

        await using var stdout = new StringWriter();

        var exit = await command.Handle(
            """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"ls"}}""",
            selfHealWatcher: false, stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "Loopback" });
    }

    sealed class Counting : HttpMessageHandler {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref Requests);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// Recording is best-effort and the credential is gone, so the POST could only earn a 401 — and
    /// this hook is holding up the approval prompt while it waits for one.
    /// </summary>
    [Test]
    public async Task A_lapsed_credential_records_nothing() {
        using var noBridge = EnvScope.Exclusive(EnvVar, null);
        using var terminal = EnvScope.Exclusive("KCAP_RENDERED_AGENT", null);
        using var handler  = new Counting();
        var       http     = new RecordingCapacitorHttpClient(handler, AuthStatus.NotAuthenticated);

        var command = new PermissionRequestCommand(
            Config.Root, Resolutions.At("https://example.test", Config.Root), http);

        var exit = await command.Handle(
            """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"ls"}}""",
            selfHealWatcher: false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(handler.Requests).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "ForHookAsync" });
    }
}
