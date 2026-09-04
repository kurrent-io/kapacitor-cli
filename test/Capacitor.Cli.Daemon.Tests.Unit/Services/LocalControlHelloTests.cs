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
/// End-to-end coverage of the Hello/HelloReply frame pair over a REAL Unix-domain socket —
/// the same <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real
/// `kcap` client talks to. The harness mirrors <c>LaunchConsentIpcTests</c> (temp
/// per-test daemons directory, socket-file poll, Windows guard) but builds its own minimal
/// AgentOrchestrator, since none of these tests exercise Spawn/Attach/Stop — the
/// orchestrator (and the consent plumbing) only need to exist to satisfy
/// LocalControlServer's constructor.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlHelloTests {
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
            ) => throw new NotSupportedException("LocalControlHelloTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(TempDaemonStore Daemons, LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var daemons     = new TempDaemonStore();
        var stateRoot   = daemons.Store.StateDirectory(daemonName);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
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

    /// Wraps a test body with the harness lifecycle, mirroring LaunchConsentIpcTests's RunAsync. The harness owns
    /// its own daemons directory, so nothing here is shared between tests; each [Test] still
    /// carries its own Windows guard, which must be visible on the test method itself.
    async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token);
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

    [Test]
    public async Task Hello_with_client_info_gets_a_reply_naming_version_name_and_capabilities() {
        await RunAsync("hello-a", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            var clientHello = JsonSerializer.Serialize(
                new ClientHelloDto("kcap-cli", "1.2.3"), HelloIpcJsonContext.Default.ClientHelloDto);
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, clientHello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1" });
        });
    }

    [Test]
    public async Task Hello_with_empty_payload_gets_an_identical_reply() {
        await RunAsync("hello-b", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1" });
        });
    }

    [Test]
    public async Task Hello_with_malformed_json_payload_is_treated_as_empty_and_still_replies() {
        await RunAsync("hello-c", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // Payload is diagnostics-only — malformed JSON must not drop the connection or
            // change the reply in any way.
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.Hello, "{not json"), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull();
            await Assert.That(resp!.Type).IsEqualTo(FrameType.HelloReply);
            var dto = JsonSerializer.Deserialize(resp.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            await Assert.That(dto!.ProtocolVersion).IsEqualTo(1);
            await Assert.That(dto.DaemonVersion).IsNotEmpty();
            await Assert.That(dto.DaemonName).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1" });
        });
    }

    [Test]
    public async Task Hello_reply_carries_pid_and_instance_id() {
        await RunAsync("hello-id", async (h, ct) => {
            h.Config.InstanceId = "inst-test-1";
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var frame = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(frame!.Text, HelloIpcJsonContext.Default.HelloReplyDto);

            await Assert.That(dto!.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(dto.InstanceId).IsEqualTo("inst-test-1");
        });
    }

    [Test]
    public async Task List_still_returns_AgentList_alongside_the_new_Hello_route() {
        await RunAsync("hello-d", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.List), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
        });
    }

    [Test]
    public async Task Unrouted_frame_type_gets_an_error_reply_mentioning_hello() {
        await RunAsync("hello-e", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // Detach is a valid, decodable FrameType that LocalControlServer's switch doesn't
            // route anywhere — it falls into the default arm, which is what this pins: the Error
            // reply for a decodable-but-unrouted frame. It is NOT the down-level discovery signal —
            // that is hello-then-EOF (a pre-hello daemon can't even decode byte 15), never an Error
            // frame (§3.1 of the design doc).
            await FrameCodec.WriteAsync(s, LocalFrame.Detach(), ct);

            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(resp.Text).Contains("Hello");
        });
    }
}
