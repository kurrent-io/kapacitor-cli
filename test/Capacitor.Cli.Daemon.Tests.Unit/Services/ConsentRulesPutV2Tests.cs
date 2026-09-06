using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of ConsentRulesPutV2 over a REAL Unix-domain socket — the same
/// <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real `kcap` client
/// talks to. The harness mirrors <see cref="LocalControlHelloTests"/> (per-test daemons
/// directory, socket-file poll, Windows guard) but builds its own minimal AgentOrchestrator,
/// since none of these tests exercise Spawn/Attach/List/Stop — the orchestrator only needs to
/// exist to satisfy LocalControlServer's constructor.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class ConsentRulesPutV2Tests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40
            ) => throw new NotSupportedException("ConsentRulesPutV2Tests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(TempDaemonStore Daemons, LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    async Task<Harness> StartAsync(string daemonName, CancellationToken ct, string serverUrl = "http://127.0.0.1:1") {
        var daemons     = new TempDaemonStore();
        var stateRoot   = daemons.Store.StateDirectory(daemonName);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = serverUrl,
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };
        var consentIpc  = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, tokens, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate);

        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, new DaemonStatusNotifier());
        var restart = RestartCoordinator.ForTest(daemons.Store, daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = daemons.Store.SocketPath(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(daemons, server, orchestrator, connection, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
        h.Daemons.Dispose();
    }

    /// Wraps a test body with the harness lifecycle, mirroring LocalControlHelloTests's RunAsync. The harness owns
    /// its own daemons directory, so nothing here is shared between tests; each [Test] still
    /// carries its own Windows guard, which must be visible on the test method itself.
    async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body, string serverUrl = "http://127.0.0.1:1") {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token, serverUrl);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<ConsentAckDto> ReadAck(NetworkStream s, CancellationToken ct) {
        var frame = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Type).IsEqualTo(FrameType.ConsentAck);
        var ack = JsonSerializer.Deserialize(frame.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
        await Assert.That(ack).IsNotNull();
        return ack!;
    }

    static string? ReadConsentFile(Harness h) {
        // The per-name state root, not the daemons directory itself — that is where the store writes.
        var path = Path.Combine(h.Config.Store.StateDirectory(h.Config.Name), "consent.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    [Test]
    public async Task V2_put_with_matching_identity_mutates_and_acks_ok() {
        await RunAsync("putv2-a", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-a", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ReadConsentFile(h)).Contains("prompt");
        });
    }

    [Test]
    public async Task V2_put_with_wrong_server_acks_identity_mismatch_and_mutates_nothing() {
        await RunAsync("putv2-b", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("putv2-b", "https://other-server.example",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    // ── ServerIdentity.Matches canonicalization: host-case/trailing-slash keep matching, default
    // ports converge, but a path-case difference is still a real mismatch. ──

    [Test]
    public async Task V2_put_with_trailing_slash_and_host_case_difference_still_matches() {
        await RunAsync("putv2-caseslash", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-caseslash", "HTTP://127.0.0.1:1/",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
        }, serverUrl: "http://127.0.0.1:1");
    }

    [Test]
    public async Task V2_put_with_default_port_equivalence_matches() {
        await RunAsync("putv2-defaultport", async (h, ct) => {
            var dto = new ConsentPolicyPutV2Dto("putv2-defaultport", "https://x.example:443",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsTrue();
        }, serverUrl: "https://x.example");
    }

    [Test]
    public async Task V2_put_with_path_case_difference_is_identity_mismatch() {
        await RunAsync("putv2-pathcase", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("putv2-pathcase", "https://x.example/tenant",
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        }, serverUrl: "https://x.example/Tenant");
    }

    [Test]
    public async Task V2_put_with_wrong_name_acks_identity_mismatch_and_mutates_nothing() {
        await RunAsync("putv2-name", async (h, ct) => {
            var before = ReadConsentFile(h);
            var dto = new ConsentPolicyPutV2Dto("some-other-daemon", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    [Test]
    public async Task V2_put_with_missing_expected_fields_acks_malformed() {
        await RunAsync("putv2-malformed", async (h, ct) => {
            var before = ReadConsentFile(h);
            // Expected_name/expected_server_url omitted entirely — a syntactically valid JSON
            // object that STJ source-gen deserializes with those non-nullable members left "".
            var json = JsonSerializer.Serialize(
                new ConsentPolicyDto("prompt", 45, []), ConsentIpcJsonContext.Default.ConsentPolicyDto);
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
            var ack = await ReadAck(s, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("malformed policy payload");
            await Assert.That(ReadConsentFile(h)).IsEqualTo(before);
        });
    }

    [Test]
    public async Task Capabilities_advertise_consent3() {
        await RunAsync("putv2-c", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var frame = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.Capabilities!).Contains("consent/3");
        });
    }
}
