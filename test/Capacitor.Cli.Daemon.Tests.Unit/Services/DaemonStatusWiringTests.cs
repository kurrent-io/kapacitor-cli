using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the non-obvious DI mechanism <see cref="DaemonRunner"/> depends on for the DaemonStatus
/// push to ever reach a real hub connection: <see cref="ServerConnection"/>'s trailing optional
/// <c>DaemonStatusNotifier?</c> parameter is resolved to the ONE registered singleton only because
/// its DI registration is a bare <c>AddSingleton&lt;ServerConnection&gt;()</c> — no factory
/// delegate. If a future change rewrites that registration with a factory that omits the
/// notifier (e.g. <c>AddSingleton(sp => new ServerConnection(...))</c> without the 5th arg),
/// <c>ServerConnection</c> falls back to a private notifier nobody subscribes to and every
/// hub-state pulse silently stops reaching StatusSubscribe clients — with no other test noticing,
/// since <see cref="ServerConnection.HubState"/> and every other observable behavior stay correct.
/// Uses the SAME bare registration shape <see cref="DaemonRunner"/> uses in production, not a
/// direct <c>new ServerConnection(...)</c> call — a direct construction wouldn't exercise the DI
/// resolution behavior this test exists to pin.
/// </summary>
public class DaemonStatusWiringTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task ServerConnection_resolved_via_DI_shares_the_one_registered_notifier() {
        var services = new ServiceCollection();
        services.AddSingleton(new DaemonConfig { Name = "wiring-test", ServerUrl = "http://127.0.0.1:1" });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(AuthFixtures.NewTokenStore(Config.Root));
        services.AddSingleton<DaemonStatusNotifier>();
        services.AddSingleton<ServerConnection>();

        await using var provider = services.BuildServiceProvider();

        var notifier   = provider.GetRequiredService<DaemonStatusNotifier>();
        var connection = provider.GetRequiredService<ServerConnection>();

        try {
            await Assert.That(ReferenceEquals(connection.StatusNotifierForTest, notifier)).IsTrue();
        } finally {
            await connection.DisposeAsync();
        }
    }

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
            ) => throw new NotSupportedException("DaemonStatusWiringTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// The AgentOrchestrator-side half of the same DI wiring hazard pinned above for
    /// ServerConnection: AgentOrchestrator's ctor also ends in an optional
    /// <c>DaemonStatusNotifier?</c> parameter, resolved to the ONE registered singleton only
    /// because its production registration (<c>DaemonRunner</c>) is a bare
    /// <c>AddSingleton&lt;AgentOrchestrator&gt;()</c> — no factory delegate. If a future change
    /// rewrites that registration with a factory that omits the notifier, every agent mutation
    /// (SetAgentStatus/PublishAgent/UnpublishAgent) would silently stop reaching StatusSubscribe
    /// clients — with no other test noticing, since every other observable behavior (including
    /// SnapshotAgentsForStatus's own contents) stays correct. Builds the full DI graph
    /// AgentOrchestrator's ctor needs, mirroring AgentStatusSnapshotTests's Build(), so the SAME
    /// bare-registration mechanism DaemonRunner relies on is exercised — a direct
    /// <c>new AgentOrchestrator(...)</c> call wouldn't exercise DI resolution at all.
    /// </summary>
    [Test]
    public async Task AgentOrchestrator_resolved_via_DI_shares_the_one_registered_notifier() {
        using var daemons   = new TempDaemonStore();
        using var worktrees = new TempDir();

        var services = new ServiceCollection();
        services.AddSingleton(new DaemonConfig {
            Name         = "wiring-orch-test",
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
        services.AddSingleton<LocalPermissionBridge>();
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
        services.AddSingleton<AgentOrchestrator>();

        await using var provider = services.BuildServiceProvider();

        var notifier     = provider.GetRequiredService<DaemonStatusNotifier>();
        var orchestrator = provider.GetRequiredService<AgentOrchestrator>();

        try {
            await Assert.That(ReferenceEquals(orchestrator.StatusNotifierForTest, notifier)).IsTrue();
        } finally {
            await orchestrator.DisposeAsync();
            await provider.GetRequiredService<ServerConnection>().DisposeAsync();
        }
    }
}
