using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the resolved-title override on the status snapshot: a title resolved after launch
/// (native extraction, generation, or the server's) replaces the prompt-derived seed in
/// <c>AgentStatusDto.Title</c>, and applying one pulses the status so clients repaint.
/// </summary>
public class AgentResolvedTitleTests {
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
            ) => throw new NotSupportedException("AgentResolvedTitleTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed record Fixture(AgentOrchestrator Orchestrator, DaemonStatusNotifier Notifier, TempDaemonStore Daemons) {
        public async Task CleanupAsync() {
            await Orchestrator.DisposeAsync();
            Daemons.Dispose();
        }
    }

    Fixture Build() {
        var daemons = new TempDaemonStore();

        var config = new DaemonConfig {
            Name         = "resolved-title-test",
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };

        var store       = new LaunchConsentStore(config.Store.StateDirectory(config.Name), NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(config.Store.StateDirectory(config.Name), NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, tokens, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);
        var notifier         = new DaemonStatusNotifier();

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        return new Fixture(orchestrator, notifier, daemons);
    }

    [Test]
    public async Task Resolved_title_replaces_the_prompt_seed_in_the_snapshot() {
        var fx = Build();
        try {
            var agent = fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test\nmore context");

            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");

            var dto = fx.Orchestrator.SnapshotAgentsForStatus().Single();
            await Assert.That(dto.Title).IsEqualTo("Stabilize the daemon lease test");
        } finally { await fx.CleanupAsync(); }
    }

    [Test]
    public async Task Without_a_resolved_title_the_prompt_seed_stands() {
        var fx = Build();
        try {
            fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test");

            var dto = fx.Orchestrator.SnapshotAgentsForStatus().Single();
            await Assert.That(dto.Title).IsEqualTo("Fix the flaky test");
        } finally { await fx.CleanupAsync(); }
    }

    [Test]
    public async Task Applying_a_resolved_title_pulses_the_status_once() {
        var fx = Build();
        try {
            var agent = fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test");

            var v0 = fx.Notifier.Version;
            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");
            await Assert.That(fx.Notifier.Version).IsGreaterThan(v0);

            // Re-applying the same title is a no-op — no spurious repaint.
            var v1 = fx.Notifier.Version;
            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");
            await Assert.That(fx.Notifier.Version).IsEqualTo(v1);
        } finally { await fx.CleanupAsync(); }
    }
}
