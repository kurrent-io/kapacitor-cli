using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Client-side round trip for <see cref="LocalControlOps.PutConsentPolicyV2Async"/> against a
/// REAL daemon stack (not a scripted stub) — the identity-mismatch behaviour under test lives in
/// <see cref="LaunchConsentIpc.HandleRulesPutV2Async"/>, so a fake server that just echoes back an
/// ack would prove nothing. Harness mirrors
/// <see cref="ConsentRulesPutV2Tests"/> (per-test daemons
/// directory, socket-file poll, Windows guard, minimal AgentOrchestrator) but drives the daemon
/// through <see cref="LocalControlOps"/>, the Core client under test, instead of hand-rolled
/// frames.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlOpsV2PutTests {
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
            ) => throw new NotSupportedException("LocalControlOpsV2PutTests never spawns a PTY");
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
        var consentIpc = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

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

    /// Wraps a test body with the harness lifecycle, mirroring
    /// ConsentRulesPutV2Tests.RunAsync, and hands the body a LocalControlOps pointed at the same
    /// daemon name so it can drive Put/Get through the real client under test.
    async Task RunAsync(string daemonName, Func<Harness, LocalControlOps, CancellationToken, Task> body, string serverUrl = "http://127.0.0.1:1") {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token, serverUrl);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            var ops = new LocalControlOps(h.Daemons.Store, daemonName) {
                ConnectTimeout = TimeSpan.FromSeconds(2),
                ReplyTimeout = TimeSpan.FromSeconds(5),
            };
            await body(h, ops, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    [Test]
    public async Task Identity_match_applies_and_follow_up_get_sees_it() {
        await RunAsync("lcov2put-a", async (h, ops, ct) => {
            var put = new ConsentPolicyPutV2Dto("lcov2put-a", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsTrue();
            await Assert.That(ack.Error).IsNull();

            var policy = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(policy.Default).IsEqualTo("prompt");
            await Assert.That(policy.PromptTimeoutSeconds).IsEqualTo(45);
        });
    }

    [Test]
    public async Task Name_mismatch_acks_identity_mismatch_and_leaves_policy_unchanged() {
        await RunAsync("lcov2put-b", async (h, ops, ct) => {
            var before = await ops.GetConsentPolicyAsync(ct);

            var put = new ConsentPolicyPutV2Dto("some-other-daemon", h.Config.ServerUrl,
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");

            var after = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(after.Default).IsEqualTo(before.Default);
            await Assert.That(after.PromptTimeoutSeconds).IsEqualTo(before.PromptTimeoutSeconds);
        });
    }

    [Test]
    public async Task Server_mismatch_acks_identity_mismatch_and_leaves_policy_unchanged() {
        await RunAsync("lcov2put-c", async (h, ops, ct) => {
            var before = await ops.GetConsentPolicyAsync(ct);

            var put = new ConsentPolicyPutV2Dto("lcov2put-c", "https://other-server.example",
                new ConsentPolicyDto("prompt", 45, []));
            var ack = await ops.PutConsentPolicyV2Async(put, ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Error).IsEqualTo("identity_mismatch");

            var after = await ops.GetConsentPolicyAsync(ct);
            await Assert.That(after.Default).IsEqualTo(before.Default);
            await Assert.That(after.PromptTimeoutSeconds).IsEqualTo(before.PromptTimeoutSeconds);
        });
    }
}
