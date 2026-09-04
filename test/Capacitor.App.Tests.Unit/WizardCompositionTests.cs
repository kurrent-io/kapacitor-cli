using System.Net;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// End-to-end-shaped composition flows: the REAL WizardComposition.BuildGraph, the REAL step view
/// models and the REAL OnboardingViewModel — only externals (HTTP, process spawns, the daemon
/// socket) are scripted. WizardStartupTests already pins the composition SEAMS in isolation
/// (adoptServer:true on Paste via <c>A_pasted_server_is_adopted_and_the_arming_hook_runs_before_the_commit</c>,
/// the provisioner armed for Create/Discover-WorkOS via <c>Create_and_workos_discovery_route_through_the_auth_proxy</c>,
/// and beforeCommit==ArmingHook via <c>The_facades_beforeCommit_hook_arms_a_durable_claim_per_identity</c>
/// plus WizardAuthServiceTests) — those are not duplicated here.
/// </summary>
static class WizardCompositionFixtures {
    /// Answers every request with a None-provider /auth/config body, mirroring
    /// WizardStartupResolutionTests' StubAuthHandler — a Paste sign-in commits with no further round trip.
    internal sealed class NoneProviderAuthHandler : HttpMessageHandler {
        public readonly List<string> Requests = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"provider":"None"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    internal static string ConfigPath(ConfigRoot root) => AppConfig.GetConfigPath(root);

    internal static void WriteConfig(ConfigRoot root, ProfileConfig config) =>
        File.WriteAllText(ConfigPath(root), JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
}

/// (a) Fresh-machine happy path: a None-provider Paste sign-in through the REAL façade (the only
/// scripted external is the /auth/config HTTP call), driven through the REAL step VMs and the
/// REAL OnboardingViewModel all the way to the Done summary — with no kcap CLI resolved anywhere
/// in the composition, the fresh-machine shape spec §9 documents.
///
/// [NotInParallel]: the process-global headless session, since composing the wizard constructs
/// ReactiveUI VMs.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionHappyPathTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileName = "acme";
    const string ServerUrl   = "https://acme.example";

    [Test]
    public async Task Fresh_machine_paste_sign_in_reaches_a_correct_done_summary() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });

        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        harness.CliPath        = null; // no kcap CLI resolved anywhere on this fresh machine
        harness.Cli.CliPath    = null;
        harness.ShimTarget     = null;
        harness.ShimApplicable = false;
        harness.Identity       = (ProfileName, ServerUrl, "daemon-a");
        harness.Profile        = ProfileName;
        using var authHandler = new WizardCompositionFixtures.NoneProviderAuthHandler();

        var options = harness.Options() with {
            HttpFactory = new PlainHttpClientFactory(authHandler),
            Proxy       = new AuthProxyClient(new HttpClient(authHandler, disposeHandler: false)),
            Operation = spec => WizardSignInOperation.For(new OnboardingFacade(
                spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
                new RecordingBrowser(), spec.Picker, spec.Provisioner, spec.BeforeCommit), spec.Profile),
        };

        var summary = await AvaloniaSession.DispatchAsync(async () => {
            var graph = WizardComposition.BuildGraph(options);
            await graph.ViewModel.PendingEnterForTesting;

            var connect = graph.Steps.OfType<ConnectStepViewModel>().Single();
            connect.Choice          = ConnectChoice.Paste;
            connect.ServerInputText = ServerUrl;
            await Assert.That(connect.Satisfied).IsTrue(); // a valid intent is staged before Next

            await graph.ViewModel.NextCommand.Execute().ToTask(); // Connect -> Sign-in

            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await Assert.That(signIn.Satisfied).IsTrue();
            await Assert.That(authHandler.Requests.Any(r => r.Contains("/auth/config"))).IsTrue();
            await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([ProfileName]);

            await graph.ViewModel.NextCommand.Execute().ToTask(); // Sign-in -> Defaults
            await graph.ViewModel.NextCommand.Execute().ToTask(); // Defaults -> Agents (persists via ConfigMutator)

            var defaults = graph.Steps.OfType<DefaultsStepViewModel>().Single();
            await Assert.That(defaults.Satisfied).IsTrue();
            await Assert.That(defaults.Message).IsNull();

            await graph.ViewModel.NextCommand.Execute().ToTask(); // Agents -> Import (no CLI, nothing installable)
            await graph.ViewModel.NextCommand.Execute().ToTask(); // Import -> Daemon (no CLI, nothing runnable)

            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.CliMissing);

            await graph.ViewModel.NextCommand.Execute().ToTask(); // Daemon -> Done
            await Assert.That(graph.ViewModel.Current.Id).IsEqualTo(WizardStepId.Done);

            return graph.Steps.OfType<DoneStepViewModel>().Single().Summary;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        var byTitle = summary.ToDictionary(e => e.Title);

        await Assert.That(summary.Count).IsEqualTo(7); // every configured step but Done itself
        await Assert.That(byTitle["Command-line tool"].Satisfied).IsFalse();
        await Assert.That(byTitle["Command-line tool"].Note).IsEqualTo(WizardComposition.CliMissingNote);
        await Assert.That(byTitle["Connect to Capacitor"].Satisfied).IsTrue();
        await Assert.That(byTitle["Connect to Capacitor"].Note).IsNull();
        await Assert.That(byTitle["Sign in"].Satisfied).IsTrue();
        await Assert.That(byTitle["Sign in"].Note).IsNull();
        await Assert.That(byTitle["Defaults"].Satisfied).IsTrue();
        await Assert.That(byTitle["Defaults"].Note).IsNull();
        await Assert.That(byTitle["Coding agents"].Satisfied).IsFalse();
        await Assert.That(byTitle["Coding agents"].Note).IsEqualTo(WizardComposition.CliMissingNote);
        await Assert.That(byTitle["Import past sessions"].Satisfied).IsFalse();
        await Assert.That(byTitle["Import past sessions"].Note).IsEqualTo(WizardComposition.CliMissingNote);
        await Assert.That(byTitle["Enable the daemon"].Satisfied).IsFalse();
        // The dominant CLI-missing note, never the stale "requires sign-in" — sign-in DID commit.
        await Assert.That(byTitle["Enable the daemon"].Note).IsEqualTo(WizardComposition.CliMissingNote);

        var config = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(config.Profiles[ProfileName].ServerUrl).IsEqualTo(ServerUrl);
        await Assert.That(config.Profiles[ProfileName].AuthProvider?.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(config.Profiles[ProfileName].DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(config.Profiles[ProfileName].Daemon?.Name).IsEqualTo("daemon-a");

        // No daemon touchpoint reached with no CLI resolved: no lane traffic, no IPC, no CLI spawns.
        await Assert.That(harness.Lane.Requests).IsEmpty();
        await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
        await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
        await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
        await Assert.That(harness.Cli.PluginInstallCallCount).IsEqualTo(0);
        await Assert.That(harness.Cli.ImportCallCount).IsEqualTo(0);
    }
}

/// (b) Abandon before sign-in: staging a valid Connect intent and closing WITHOUT ever calling
/// Begin must leave nothing durable — no claim, no lane traffic, no CLI spawn, and the config
/// file untouched.
///
/// [NotInParallel]: same shared resources as WizardCompositionHappyPathTests, for the same reason.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionAbandonTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProfileName = "acme";

    [Test]
    public async Task Staging_connect_input_and_closing_without_signing_in_writes_nothing() {
        WizardCompositionFixtures.WriteConfig(Config.Root,
            new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });
        var configBefore = await File.ReadAllTextAsync(WizardCompositionFixtures.ConfigPath(Config.Root));

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var graph = WizardComposition.BuildGraph(harness.Options());
            await graph.ViewModel.PendingEnterForTesting;

            var connect = graph.Steps.OfType<ConnectStepViewModel>().Single();
            connect.Choice          = ConnectChoice.Paste;
            connect.ServerInputText = "https://acme.example";
            await Assert.That(connect.Satisfied).IsTrue(); // a valid intent is staged — Begin is never called

            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel())
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.PluginInstallCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.ImportCallCount).IsEqualTo(0);

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(await File.ReadAllTextAsync(WizardCompositionFixtures.ConfigPath(Config.Root))).IsEqualTo(configBefore);
    }
}

/// (c) Sign-in commit -> claims armed -> relaunch fixture: a claim armed by the wizard's own
/// composition (the REAL ArmingHook, wired exactly as WizardComposition.BuildGraph wires it) must
/// still be found and applied by a FRESH ConsentFlipCoordinator over the SAME durable claims file —
/// modelling a wizard session followed by the normal app's next startup, never a shared in-memory instance.
[NotInParallel("AvaloniaSession")]
public class WizardCompositionRelaunchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task A_claim_armed_during_the_wizard_is_applied_by_a_relaunched_coordinator() {
        using var harness = new WizardFixtures.GraphHarness(Config.Root);
        var canonicalServer = ServerIdentity.Canonicalize("https://acme.example")!;
        const string profileName = "acme";
        const string daemonName  = "daemon-a";

        // Scripted rather than the full network-backed façade: this proves the RELAUNCH seam, not
        // the boundary itself (already pinned by WizardStartupTests' beforeCommit-wiring tests).
        // The hook invoked here IS the composition's own WizardFacadeSpec.BeforeCommit.
        harness.Operation = async (_, ct) => {
            await harness.Spec!.BeforeCommit([new AuthIdentity(profileName, canonicalServer)], ct);
            return new AuthResult.Committed(profileName, canonicalServer, AuthProvider.None, null, []);
        };

        var result = await AvaloniaSession.DispatchAsync(async () => {
            var graph   = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            return await attempt.Result;
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([profileName]);

        // The relaunch: a fresh ConsentFlipClaims instance over the SAME file, and a fresh
        // coordinator — never the wizard's own claims object or WizardAuthService.
        var relaunchClaims = new ConsentFlipClaims(harness.ClaimsRoot);
        var client   = new FakeDaemonClientService();
        var ops      = new ScriptedLocalControlOps();
        var surface  = new FakeLifecycleSurface();
        var appState = new WizardFixtures.NoopAppStateStore();
        var coordinator = new ConsentFlipCoordinator(
            client, ops, relaunchClaims, () => (profileName, canonicalServer, daemonName), surface, appState,
            CancellationToken.None);

        ops.QueueGet(new ConsentPolicyDto("allow", 30, []));
        ops.QueuePutV2(true, null);

        coordinator.Start();
        client.StatusSubject.OnNext(new AttachStatus(
            AttachState.Connected, null, [ConsentFlipCoordinator.ConsentV3Capability]));

        await WizardFixtures.WaitUntilAsync(
            () => relaunchClaims.Pending().Count == 0, what: "the relaunched coordinator to consume the claim");

        await Assert.That(ops.PutV2Calls).IsEqualTo(1);
        var put = ops.PutV2Payloads[0];
        await Assert.That(put.ExpectedName).IsEqualTo(daemonName);
        await Assert.That(put.ExpectedServerUrl).IsEqualTo(canonicalServer);
        await Assert.That(put.Policy.Default).IsEqualTo("prompt");
        // Same durable file: the wizard's own (now-stale) claims handle agrees the claim is gone.
        await Assert.That(harness.Claims.Pending()).IsEmpty();
    }
}
