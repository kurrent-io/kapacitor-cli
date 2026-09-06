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
/// End-to-end coverage of <see cref="LocalControlProbe.ProbeAsync"/> over a REAL Unix-domain
/// socket. Mirrors <see cref="LocalControlHelloTests"/>'s harness (per-test daemons
/// directory, socket-file poll, Windows guard) as a style-copy rather than a shared helper, per
/// that file's own note about not disturbing its structure.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlProbeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

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
            ) => throw new NotSupportedException("LocalControlProbeTests never spawns a PTY");
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

    /// Wraps a test body with the harness lifecycle, mirroring LocalControlHelloTests's RunAsync.
    /// The harness owns its own daemons directory, so nothing here is shared between tests; each
    /// [Test] still carries its own Windows guard, which must be visible on the test method itself.
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

    [Test]
    public async Task Probe_returns_hello_and_first_snapshot_with_consistent_identity() {
        await RunAsync("probe-a", async (h, ct) => {
            h.Config.InstanceId = "inst-p1";
            var r = await LocalControlProbe.ProbeAsync(h.Daemons.Store, "probe-a", TimeSpan.FromSeconds(5), ct);

            await Assert.That(r.Reachable).IsTrue();
            await Assert.That(r.Hello!.DaemonName).IsEqualTo("probe-a");
            await Assert.That(r.Snapshot!.Daemon.InstanceId).IsEqualTo("inst-p1");
            await Assert.That(r.IdentityConsistent).IsTrue();
        });
    }

    [Test]
    public async Task Probe_on_missing_socket_reports_unreachable_without_throwing() {
        using var daemons = new TempDaemonStore();

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, "no-such-daemon-xyz", TimeSpan.FromMilliseconds(500));
        await Assert.That(r.Reachable).IsFalse();
        await Assert.That(r.Hello).IsNull();
    }

    // ---- a reachable peer answering well-formed-but-structurally-degenerate JSON ----

    delegate Task ConnScript(NetworkStream s, CancellationToken ct);

    /// Minimal scripted-connection UDS listener — a trimmed copy of
    /// <c>LocalControlClientTests.ScriptedServer</c> (that one is a private nested type there,
    /// so it can't be referenced directly), sized for exactly what this file needs: one script
    /// per accepted connection, in order.
    sealed class ScriptedServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        int _served;
        readonly Task _accept;

        public ScriptedServer(string sockPath, params ConnScript[] scripts) {
            _scripts = scripts;
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        var idx = Interlocked.Increment(ref _served) - 1;
                        if (idx >= _scripts.Length) { conn.Dispose(); continue; }
                        var script = _scripts[idx];
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            try { await _accept; } catch { }
        }
    }

    [Test]
    public async Task Probe_treats_a_structurally_degenerate_snapshot_as_a_snapshot_failure_not_a_throw() {
        using var daemons = new TempDaemonStore();
        var name = "probe-degenerate";
        var helloJson = JsonSerializer.Serialize(
            new HelloReplyDto(1, "1.0", name, ["status/1"], 111, "inst-x"),
            HelloIpcJsonContext.Default.HelloReplyDto);

        ConnScript helloThen = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.Hello)
                await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, helloJson), ct);
        };
        // Well-formed JSON, but structurally degenerate: daemon/agents both null. STJ source-gen
        // leaves declared-non-nullable reference members at their default on null/absent JSON
        // rather than throwing, so this deserializes to a NON-null DaemonStatusDto with a null
        // Daemon — exactly the shape that must go through DaemonStatusValidator.IsValid instead
        // of a bare null-check, or ProbeAsync either NREs on snapshot.Daemon.Pid or silently
        // returns a null-riddled Snapshot.
        ConnScript subscribeDegenerate = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.StatusSubscribe)
                await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, """{"daemon":null,"agents":null}"""), ct);
        };

        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), helloThen, subscribeDegenerate);

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, name, TimeSpan.FromSeconds(5));

        await Assert.That(r.Reachable).IsTrue();
        await Assert.That(r.Hello).IsNotNull();
        await Assert.That(r.Snapshot).IsNull();
        await Assert.That(r.IdentityConsistent).IsFalse();
    }

    /// <summary>Fail-closed (spec §4): a hello carrying no pid/instance_id (an older daemon that
    /// predates those fields, per <see cref="HelloReplyDto"/>'s own doc comment) must never default
    /// to "consistent" just because one side has nothing to disagree with — both sides must
    /// carry BOTH ids and agree, or the two dials cannot be proven to have landed on the same
    /// process.</summary>
    [Test]
    public async Task Hello_without_ids_is_never_consistent_even_with_a_valid_snapshot() {
        using var daemons = new TempDaemonStore();
        var name = "probe-noids";
        var helloJson = JsonSerializer.Serialize(
            new HelloReplyDto(1, "0.1.0", name, ["status/1"], Pid: null, InstanceId: null),
            HelloIpcJsonContext.Default.HelloReplyDto);
        var snapshotJson = JsonSerializer.Serialize(
            new DaemonStatusDto(
                new DaemonInfoDto("probe-noids", "1.0.0", "https://s", "connected", 5, 0, Pid: 999, InstanceId: "inst-real"),
                []),
            StatusIpcJsonContext.Default.DaemonStatusDto);

        ConnScript helloThen = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.Hello)
                await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, helloJson), ct);
        };
        ConnScript subscribeValid = async (s, ct) => {
            var f = await FrameCodec.ReadAsync(s, ct);
            if (f?.Type == FrameType.StatusSubscribe)
                await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, snapshotJson), ct);
        };

        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), helloThen, subscribeValid);

        var r = await LocalControlProbe.ProbeAsync(daemons.Store, name, TimeSpan.FromSeconds(5));

        await Assert.That(r.Reachable).IsTrue();
        await Assert.That(r.Hello).IsNotNull();
        await Assert.That(r.Snapshot).IsNotNull(); // the snapshot itself is perfectly valid...
        await Assert.That(r.IdentityConsistent).IsFalse(); // ...but consistency still fails closed
    }
}
