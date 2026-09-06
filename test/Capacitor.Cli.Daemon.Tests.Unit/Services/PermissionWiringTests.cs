using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The permission-graph counterpart of <see cref="DaemonStatusWiringTests"/>: LocalPermissionBridge's
/// and AgentOrchestrator's trailing optional <c>PermissionPromptBroker?</c> parameters, and
/// PermissionIpc's required one, are resolved to the ONE registered singleton only because
/// DaemonRunner's registrations for all three are bare <c>AddSingleton&lt;T&gt;()</c> — no factory
/// delegate. A future change to a factory registration that omits the broker would silently split
/// the permission graph into disconnected brokers, with no other test noticing: the bridge's
/// HandleAsync, PermissionIpc's subscribe/resolve frames, and the orchestrator's
/// withdraw-on-agent-gone would each hold their own private broker instead of sharing one pending
/// set. Likewise the bridge's decision log must be the registered PermissionDecisionLog instance,
/// not the null default a bare private construction would fall back to.
/// </summary>
public class PermissionWiringTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

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
            ) => throw new NotSupportedException("PermissionWiringTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    [Test]
    public async Task Permission_graph_resolved_via_DI_shares_the_one_registered_broker_and_decision_log() {
        using var daemons   = new TempDaemonStore();
        using var worktrees = new TempDir();

        var services = new ServiceCollection();
        services.AddSingleton(new DaemonConfig {
            Name         = "wiring-permission-test",
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = worktrees.PathTo("wt"),
        });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Config.Root);
        services.AddSingleton(AuthFixtures.NewTokenStore(Config.Root));
        services.AddSingleton<DaemonStatusNotifier>();
        services.AddSingleton(Home.Home);
        services.AddSingleton<ServerConnection>();
        services.AddSingleton<WorktreeManager>();
        services.AddSingleton<RepoMatcher>();
        services.AddSingleton<IPtyProcessFactory>(new NoopPtyProcessFactory());
        services.AddSingleton<IHttpClientFactory>(new NoopHttpClientFactory());
        services.AddSingleton<ICapacitorHttpClient>(new FixedCapacitorHttpClient());
        services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(
            new Dictionary<string, IHostedAgentLauncher>());
        services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>>(
            new Dictionary<string, IHostedAgentRuntimeFactory>());
        services.AddSingleton<IHostApplicationLifetime>(new NoopHostLifetime());
        services.AddSingleton(sp => new LaunchConsentGate(
            new LaunchConsentStore(daemons.Directory, NullLogger.Instance),
            new LaunchConsentDecisionLog(daemons.Directory, NullLogger.Instance),
            prompter: null,
            TimeProvider.System,
            sp.GetRequiredService<ILogger<LaunchConsentGate>>()));

        // The exact bare-registration shape DaemonRunner uses for the permission graph.
        services.AddSingleton<PermissionPromptBroker>();
        services.AddSingleton<PermissionIpc>();
        services.AddSingleton(sp => new PermissionDecisionLog(
            Tmp.Path, sp.GetRequiredService<ILogger<PermissionDecisionLog>>()));
        services.AddSingleton<LocalPermissionBridge>();
        services.AddSingleton<AgentOrchestrator>();

        await using var provider = services.BuildServiceProvider();

        var broker       = provider.GetRequiredService<PermissionPromptBroker>();
        var bridge       = provider.GetRequiredService<LocalPermissionBridge>();
        var orchestrator = provider.GetRequiredService<AgentOrchestrator>();
        var ipc          = provider.GetRequiredService<PermissionIpc>();

        try {
            await Assert.That(ReferenceEquals(bridge.BrokerForTest, broker)).IsTrue();
            await Assert.That(ReferenceEquals(orchestrator.PermissionBrokerForTest, broker)).IsTrue();
            await Assert.That(ReferenceEquals(ipc.BrokerForTest, broker)).IsTrue();

            await Assert.That(bridge.DecisionLogForTest).IsNotNull();
            await Assert.That(ReferenceEquals(
                bridge.DecisionLogForTest, provider.GetRequiredService<PermissionDecisionLog>())).IsTrue();
        } finally {
            await orchestrator.DisposeAsync();
            await bridge.DisposeAsync();
            await provider.GetRequiredService<ServerConnection>().DisposeAsync();
        }
    }
}
