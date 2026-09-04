using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Orchestrator construction for the AgentOrchestrator suite. These used to be merged into every
/// test file by a 30-fragment <c>partial class AgentOrchestratorVendorTests</c>; they live here so
/// each test file can declare a class named after itself. See also <see cref="GitRepo"/>
/// and <see cref="WaitHarness"/>.
/// </summary>
internal static class AgentOrchestratorHarness {
    internal static AgentOrchestrator BuildOrchestrator(
            ServerConnection                                    server,
            IPtyProcessFactory                                  ptyFactory,
            IReadOnlyDictionary<string, IHostedAgentLauncher>   launchers,
            string?                                             allowedRepoPath        = null,
            IEnumerable<IHostedAgentRuntimeFactory>?            extraRuntimeFactories  = null,
            Action<DaemonConfig>?                               configure              = null,
            // Defaults to NullLogger; a test that asserts on the orchestrator's own diagnostics
            // (e.g. the graceful-stop timeout warning) passes a capturing logger instead.
            ILogger<AgentOrchestrator>?                         logger                 = null,
            // Consent: defaults to an allow-all gate over the factory's own temp state dir so every
            // pre-existing test (none of which know about consent) keeps passing unchanged. A test
            // exercising a deny/prompt policy passes its own gate (e.g. built with a Deny-default
            // LaunchConsentStore) instead.
            LaunchConsentGate?                                  consentGate            = null,
            // §3.3: defaults to the fixed never-cancels StubHostLifetime every pre-existing test relies
            // on. A test that needs to simulate shutdown firing mid-launch (e.g. a gate OCE parked on a
            // consent prompt) passes its own lifetime with a controllable ApplicationStopping token.
            IHostApplicationLifetime?                           lifetime               = null,
            // §3.3: leaves the sequenced processor unpublished, so a test can drive the pre-settlement
            // inline arm and the publication barrier. Production never has that window.
            bool                                                deferProcessorPublication = false
        ) {
        var daemonStore    = new TempDaemonStore();
        var configRoot   = new TempConfigRoot();
        var home         = new TempHome();
        var config = new DaemonConfig {
            Name                = "test",
            ServerUrl           = "http://127.0.0.1:1",
            ClaudePath          = "claude",
            MaxConcurrentAgents = 5,
            WorktreeRoot        = daemonStore.CreateDir("worktree"),
            // Phase B (D4): the PID-record store, consent policy and decision log all hang off this
            // directory, so each harness gets its own and nothing reaches the real daemons dir.
            Store               = daemonStore.Store,
            ConfigRoot          = configRoot.Root
        };

        if (allowedRepoPath is not null) {
            config.AllowedRepoPaths = [allowedRepoPath];
        }

        configure?.Invoke(config); // Phase B: let a test tweak the config (e.g. reviewer TTL bounds)

        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var httpFactory      = new StubHttpClientFactory();
        var http             = new FixedCapacitorHttpClient();
        var tokens           = AuthFixtures.NewTokenStore(configRoot.Root);
        var permissionBridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);

        // Mirror DaemonRunner's DI wiring: one PtyHostedAgentRuntimeFactory per registered launcher,
        // all sharing the same (spied) IPtyProcessFactory so SpyPtyProcessFactory's
        // SpawnCalls/LastCommand assertions stay valid through the runtime-selection seam.
        // extraRuntimeFactories lets a test inject a non-PTY factory (e.g. a fake "cursor" ACP
        // factory) without disturbing every other call site of this helper.
        IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories = launchers.Values
            .Select(l => (IHostedAgentRuntimeFactory) new PtyHostedAgentRuntimeFactory(l, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance))
            .Concat(extraRuntimeFactories ?? [])
            .ToDictionary(f => f.Vendor);

        consentGate ??= new LaunchConsentGate(
            new LaunchConsentStore(config.Store.StateDirectory(config.Name), NullLogger.Instance),
            new LaunchConsentDecisionLog(config.Store.StateDirectory(config.Name), NullLogger.Instance),
            prompter: null, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        return new HarnessOrchestrator(
            daemonStore,
            configRoot,
            home,
            config,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpFactory,
            http,
            tokens,
            permissionBridge,
            launchers,
            runtimeFactories,
            lifetime ?? new StubHostLifetime(),
            logger ?? NullLogger<AgentOrchestrator>.Instance,
            consentGate,
            deferProcessorPublication
        );
    }

    /// <summary>Owns the scratch directories its config points at — the daemon store, the config
    /// root and the home — so disposing the orchestrator, which every call site already does, reaps them at test
    /// end. BuildOrchestrator is called from many sites, so no per-test fixture could own them
    /// instead.</summary>
    sealed class HarnessOrchestrator : AgentOrchestrator {
        readonly TempDaemonStore _tmp;
        readonly TempConfigRoot  _config;
        readonly TempHome        _home;

        internal HarnessOrchestrator(
                TempDaemonStore                                         tmp,
                TempConfigRoot                                          configRoot,
                TempHome                                                home,
                DaemonConfig                                            config,
                ServerConnection                                        server,
                WorktreeManager                                         worktreeManager,
                RepoMatcher                                             repoMatcher,
                IPtyProcessFactory                                      ptyFactory,
                IHttpClientFactory                                      httpClientFactory,
                ICapacitorHttpClient                                    http,
                TokenStore                                              tokens,
                LocalPermissionBridge                                   permissionBridge,
                IReadOnlyDictionary<string, IHostedAgentLauncher>       launchers,
                IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories,
                IHostApplicationLifetime                                lifetime,
                ILogger<AgentOrchestrator>                              logger,
                LaunchConsentGate                                       consentGate,
                bool                                                    deferProcessorPublication
            ) : base(
            config,
            configRoot.Root,
            home,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpClientFactory,
            http,
            tokens,
            permissionBridge,
            launchers,
            runtimeFactories,
            lifetime,
            logger,
            consentGate,
            deferProcessorPublication,
            // Wired unconditionally so the launch path builds a snapshot exactly as production does.
            // The scratch config root and a fresh checkout carry no approval documents, so every test
            // that writes none still gets an empty snapshot and no upload.
            policySnapshots: new PolicySnapshotProvider(configRoot.Root)
        ) {
            _tmp    = tmp;
            _config = configRoot;
            _home   = home;
        }

        public override async ValueTask DisposeAsync() {
            try {
                await base.DisposeAsync();
            } finally {
                _tmp.Dispose();
                _config.Dispose();
                _home.Dispose();
            }
        }
    }

    internal sealed class StubHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    internal static LaunchConsentGate DenyDefaultGate(string dir) {
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Deny, 5, []), out _);
        return new LaunchConsentGate(store, new LaunchConsentDecisionLog(dir, NullLogger.Instance),
            prompter: null, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
    }

    internal static LaunchAgentCommand NewCursorLaunch(string agentId, string repoPath) => new(
        AgentId: agentId,
        Prompt: "do work",
        Model: "auto",
        Effort: null,
        RepoPath: repoPath,
        Tools: null,
        AttachmentIds: null,
        Vendor: "cursor"
    );

    internal static Dictionary<string, IHostedAgentLauncher> Launcher(string vendor) =>
        new() { [vendor] = new SpyHostedAgentLauncher(vendor, cliPath: $"spy-{vendor}") { SupportsUnattended = true } };

    /// <summary>Constructs an <see cref="AgentInstance"/> directly — <c>SeedAgentForTest</c> only
    /// builds PTY runtimes — wrapping the given ACP runtime, and registers it via
    /// <c>RegisterAgentForTest</c> so it is reachable exactly like a real launch's registration.
    /// <paramref name="activityClock"/> mirrors <c>SeedAgentForTest</c>'s own optional clock override
    /// — a test that needs a controllable (<see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>-backed)
    /// clock passes one; omitting it keeps every existing caller's real-time default.</summary>
    internal static AgentInstance SeedAcpAgent(
            AgentOrchestrator orch, string agentId, IHostedAgentRuntime runtime, string status = "Running",
            AgentActivityClock? activityClock = null) {
        var agent = new AgentInstance(
            agentId, "review this", "default", null, "/repo", "cursor",
            runtime,
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Status = status,
            ActivityClock = activityClock ?? new AgentActivityClock(TimeProvider.System)
        };

        orch.RegisterAgentForTest(agent);

        return agent;
    }

    /// <summary>(id, reason) projection of a selection. The decision-table tests below assert on the
    /// RULE that fired; the rest of <see cref="AgentOrchestrator.ReapCandidate"/> is claim evidence
    /// (the captured activity generation and whether the rule is activity-fenced), which the claim
    /// tests at the bottom of this file pin instead.</summary>
    internal static IEnumerable<(string Id, string Reason)> Verdicts(
            IEnumerable<AgentOrchestrator.ReapCandidate> selection) =>
        selection.Select(c => (c.Id, c.Reason));

}
