using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using static Capacitor.Tests.Helpers.AuthFixtures;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using Spectre.Console;
using TUnit.Assertions.Enums;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// `SetupCommand`'s Step 1 (<see cref="SetupCommand.RunDiscoveryAsync"/>) / Step 2
/// (<see cref="SetupCommand.RunLoginStepAsync"/>) re-plumb onto <see cref="OnboardingFacade"/>:
/// GitHub multi-tenant token publication, the funnel mapping the adapter now owns (WorkOS's own
/// funnel events fire from inside Core regardless of caller — nothing to pin here), and Step 2's
/// three branches. Shares the OnboardingFacadeTests sink key — it drives the same telemetry sink.
/// Every discovery test forces `--github`: provider selection with no flag
/// depends on <c>HeadlessEnvironment.IsHeadless()</c>, which reads live env/platform state and
/// differs between the ubuntu-latest and windows-latest CI legs — not something a unit test can
/// pin (mirrors why LoginFacadeParityTests always passes --github too).
/// </summary>
// Bare, and one level only: these drive Console and SetupCommand.FacadeOverride, both process-global,
// so a group key that names neither is not exclusion. A method constraint would shadow this one.
[NotInParallel]
public class SetupFacadeParityTests {
    /// <summary>
    /// Captures what setup writes through Spectre. It writes its step banners via <c>AnsiConsole</c>
    /// rather than <c>IAuthProgress</c> or plain <c>Console.Out</c>, and the static console caches its
    /// writer at first use, so redirecting <c>Console.Out</c> does not reach it — the singleton itself
    /// has to be swapped.
    ///
    /// <para>Restoring is a call rather than a <c>using</c> so a test can put the console back before
    /// it asserts: a failure raised while the singleton is swapped renders into the buffer.</para>
    /// </summary>
    sealed class SpectreCapture {
        readonly IAnsiConsole  _original = AnsiConsole.Console;
        readonly StringBuilder _text     = new();

        public static SpectreCapture Start() {
            var capture = new SpectreCapture();

            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
                Ansi        = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out         = new AnsiConsoleOutput(new StringWriter(capture._text)),
            });

            // A runner's console reports its own width, and an unpinned layout wraps the lines these
            // tests read back.
            AnsiConsole.Profile.Width = 120;

            return capture;
        }

        public string Text => _text.ToString();

        public void Restore() => AnsiConsole.Console = _original;
    }

    // Never reached: these tests drive the import and discovery steps, which do not provision.
    static readonly TenantProvisioningClient Provisioning = new(new HttpClient());
    static readonly WorkOSClient Workos = new(new PlainHttpClientFactory());
    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
    static readonly IHttpClientFactory HttpFactory = new PlainHttpClientFactory();
    static readonly IAuthProxyClient Proxy = new AuthProxyClient(new HttpClient());
    static readonly AuthProviderDiscovery Discovery = new(HttpFactory);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Before(Test)]
    public void Cleanup() {
        CliTelemetry.Reset();
        SetupCommand.FacadeOverride = null;
    }

    [After(Test)]
    public void ResetFacadeOverride() => SetupCommand.FacadeOverride = null;

    ProfileConfig ReadConfig() => ConfigMutator.LoadPure(ConfigPath);

    bool TokenFileExists(string profile) => File.Exists(Path.Combine(TokensDir, $"{profile}.json"));

    List<TelemetryEvent> StartCapturingFunnel() {
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false, Config.Root);

        TelemetryTestGuards.AssertEnabled("setup", Config.Root);

        sink.Clear(); // drop cli_first_run

        return sink;
    }

    // ── Step 1: RunDiscoveryAsync (GitHub) ──────────────────────────────────

    [Test]
    public async Task RunDiscoveryAsync_github_two_tenants_publishes_both_and_marks_loginComplete() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        SetupCommand.FacadeOverride = _ =>
            NewFacade(Config.Root, new RecordingAuthProgress(), handler, PickerReturningFirst());

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNotNull();
        await Assert.That(discovered!.Value.LoginComplete).IsTrue();
        await Assert.That(discovered.Value.Provider).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(discovered.Value.ServerUrl).IsEqualTo("https://acme.kcap.ai");

        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsTrue();

        var cfg = ReadConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.ai");
    }

    [Test]
    public async Task RunDiscoveryAsync_github_committed_fires_signin_opened_then_signin_completed() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        SetupCommand.FacadeOverride = _ =>
            NewFacade(Config.Root, new RecordingAuthProgress(), handler, PickerReturningFirst());

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNotNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_zero_tenants_emits_signin_completed_and_tenant_none() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""", tenants: "[]");

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        // Today's setup fires SigninCompleted unconditionally once the token is acquired, and
        // TenantNone additionally when discovery then finds nothing — the two co-occur here.
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed", "cli_setup_tenant_none" },
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_post_acquisition_discovery_error_still_emits_signin_completed() {
        var sink = StartCapturingFunnel();

        // No `tenants:` stub — /discover-tenants 500s AFTER the device flow already handed out a
        // token, landing AuthFailureReason.Other (not NoTenantsFound).
        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""");

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_signin_denied_emits_signin_failed() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            devicePoll: () => AuthHttp.Json("""{"error":"access_denied"}"""));

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_failed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_unreachable_proxy_fires_no_extra_funnel_event() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(); // no /config route — proxy unreachable

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        // Other/Unreachable failures map to nothing beyond SigninOpened — only SigninDenied and
        // NoTenantsFound get a second event.
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened" }, CollectionOrdering.Matching);
    }

    // The façade owns the reason line; setup still owns the guidance tail its old single line carried.
    [Test]
    public async Task RunDiscoveryAsync_unreachable_proxy_still_prints_the_legacy_guidance_tail() {
        using var handler = AuthHttp.Script(); // no /config route — proxy unreachable

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        using var console = ConsoleOutput.StartErrorCapture("\n");

        var discovered = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        await Assert.That(console.GetCapturedError()).Contains(SetupAuthProgress.UnreachableGuidance);
    }

    // The flags must reach the provisioner discovery is given, and the workspace it lands on must be
    // checked against them once it commits.

    [Test]
    public async Task RunDiscoveryAsync_hands_the_requested_workspace_to_the_provisioner() {
        using var handler = AuthHttp.Script(); // no /config route — discovery fails after construction

        ITenantProvisioner? captured = null;
        SetupCommand.FacadeOverride = provisioner => {
            captured = provisioner;
            return NewFacade(Config.Root, new RecordingAuthProgress(), handler);
        };

        await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery)
            .RunDiscoveryAsync([], forceDevice: true, new RequestedWorkspace("Acme", "acme"));

        await Assert.That(captured).IsTypeOf<SpectreTenantProvisioner>();
        await Assert.That(((SpectreTenantProvisioner)captured!).Scripted).IsTrue();
    }

    // The guard is only worth anything if the boundary is the thing that refuses, so this drives the
    // real facade with it attached and asserts nothing durable was written.
    [Test]
    public async Task A_commit_that_would_publish_another_workspace_writes_nothing() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(Config.Root, progress, handler, PickerReturningFirst(),
            beforeCommit: SetupCommand.WorkspaceGuard(new RequestedWorkspace("Globex", "globex")));

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(TokenFileExists("acme")).IsFalse();
        await Assert.That(TokenFileExists("contoso")).IsFalse();

        var cfg = ReadConfig();

        await Assert.That(cfg.Profiles.ContainsKey("acme")).IsFalse();
        await Assert.That(cfg.ActiveProfile).IsNotEqualTo("acme");
        await Assert.That(string.Join("\n", progress.Errors)).Contains("globex");
    }

    // ── the setup-scoped progress sink ───────────────────────────────────────

    /// <summary>Pins the pass-through. The renderer underneath carries the step indent, so shifting
    /// here as well would double it on the lines this sink is handed while the copy the renderer
    /// composes for itself stayed one step out.</summary>
    [Test]
    public async Task SetupAuthProgress_passes_facade_text_through_untouched() {
        var inner    = new RecordingAuthProgress();
        var progress = new SetupAuthProgress(inner);

        progress.Notice("Server has no authentication configured — login not required.");
        progress.Error("Cannot reach the Kurrent auth service.");
        progress.Notice("");
        progress.Notice("  1. Open https://github.com/login/device in a browser");
        progress.BrowserOpening("https://auth.example/authorize");
        progress.DeviceCode("UC", "https://github.com/login/device", "GitHub", prefilled: false);
        progress.PollTick();

        await Assert.That(inner.Notices).IsEquivalentTo(new[] {
            "Server has no authentication configured — login not required.",
            "",
            "  1. Open https://github.com/login/device in a browser",
        });
        await Assert.That(inner.Errors).IsEquivalentTo(new[] { "Cannot reach the Kurrent auth service." });
        await Assert.That(inner.BrowserOpenings).IsEquivalentTo(new[] { "https://auth.example/authorize" });
        await Assert.That(inner.DeviceCodes).Count().IsEqualTo(1);
        await Assert.That(inner.PollTicks).IsEqualTo(1);
    }

    /// <summary>Pins the wiring rather than either half of it: a step's own lines and the auth copy
    /// beside them land on one margin only if the sink handed to the façade is built with the step
    /// indent, and neither class's own test can show that.</summary>
    [Test]
    public async Task StepProgress_renders_facade_copy_on_the_step_margin() {
        using var capture = ConsoleOutput.StartCapture();

        SetupCommand.StepProgress.BrowserOpening("https://auth.example/authorize");

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "  Opening browser for authentication..." + Environment.NewLine
          + "    If the browser doesn't open, visit: https://auth.example/authorize" + Environment.NewLine);
    }

    [Test]
    public async Task SetupAuthProgress_appends_the_guidance_tail_only_for_an_unreachable_failure() {
        var inner    = new RecordingAuthProgress();
        var progress = new SetupAuthProgress(inner);

        progress.ReportFailure(new AuthResult.Failed("nope", AuthFailureReason.SigninDenied));
        progress.ReportFailure(new AuthResult.Cancelled());
        await Assert.That(inner.Errors).IsEmpty();

        progress.ReportFailure(new AuthResult.Failed("Cannot reach the Kurrent auth service.", AuthFailureReason.Unreachable));
        await Assert.That(inner.Errors).IsEquivalentTo(new[] { SetupAuthProgress.UnreachableGuidance });
    }

    // ── Step 2: RunLoginStepAsync ────────────────────────────────────────────

    [Test]
    public async Task RunLoginStepAsync_loginComplete_reports_the_already_published_identity_without_a_facade_call() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles      = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.kcap.ai" } },
            ActiveProfile = "acme",
        });
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", new StoredTokens {
            AccessToken     = "tok",
            ExpiresAt       = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername  = "alice",
            Provider        = AuthProvider.GitHubApp,
            ServerUrl       = "https://acme.kcap.ai",
        });

        SetupCommand.FacadeOverride = _ => throw new InvalidOperationException("loginComplete must not call the façade");

        var console = SpectreCapture.Start();

        int exitCode;

        try {
            exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
                loginComplete: true, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
                forceDevice: false, activeProfile: "acme");
        } finally {
            console.Restore();
        }

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(console.Text).Contains("Logged in as alice");
    }

    // A None-provider setup must still mint the auth_provider stamp, and only inside the commit boundary.
    [Test]
    public async Task RunLoginStepAsync_none_provider_commits_the_stamp_through_the_facade() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"None"}""");

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.None, serverUrl: "https://none.example",
            forceDevice: false, activeProfile: "default");

        await Assert.That(exitCode).IsEqualTo(0);

        var profile = ReadConfig().Profiles["default"];
        await Assert.That(profile.AuthProvider).IsNotNull();
        await Assert.That(profile.AuthProvider!.Provider).IsEqualTo(AuthProvider.None);
        // Gate-complete at config level: the stamp's server must match the canonicalized profile server.
        await Assert.That(ServerIdentity.SameServer(profile.AuthProvider.ServerUrl, profile.ServerUrl)).IsTrue();
        await Assert.That(TokenFileExists("default")).IsFalse();
    }

    [Test]
    public async Task RunLoginStepAsync_none_provider_failure_prints_login_failed_and_returns_one() {
        // No /auth/config route at all — the façade's re-fetch fails, mirroring the explicit-login
        // failure test below. A None-provider server that becomes unreachable must fail the same way.
        using var handler = AuthHttp.Script();

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.None, serverUrl: "https://none.example",
            forceDevice: false, activeProfile: "default");

        await Assert.That(exitCode).IsEqualTo(1);
    }

    [Test]
    public async Task RunLoginStepAsync_explicit_login_commits_and_adopts_the_server_onto_the_active_profile() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
            forceDevice: true, activeProfile: "acme");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(TokenFileExists("acme")).IsTrue();
        // adoptServer: true — setup's whole job is configuring the active profile for this server.
        await Assert.That(ReadConfig().Profiles["acme"].ServerUrl).IsEqualTo("https://acme.kcap.ai");
    }

    /// <summary>
    /// One report per sign-in. The façade's notice is the shared one — <c>kcap login</c> and the desktop
    /// wizard render from it — so setup adding its own puts the same sentence on screen twice. Pinned on
    /// both halves: exactly one notice, and nothing of setup's own on stdout.
    /// </summary>
    [Test]
    public async Task RunLoginStepAsync_reports_the_sign_in_once() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var       progress = new RecordingAuthProgress();

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, progress, handler);

        using var console = ConsoleOutput.StartCapture();

        var exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
            forceDevice: true, activeProfile: "acme");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(progress.Notices.Count(n => n.StartsWith("Logged in as", StringComparison.Ordinal)))
                    .IsEqualTo(1);
        await Assert.That(console.GetCapturedOutput()).DoesNotContain("Logged in as");
    }

    /// <summary>
    /// Discovery's WorkOS leg reports the sign-in with the workspace it picked, so step 2 has nothing to
    /// add. Every other provider's discovery reports a tenant count and no identity, which is why the
    /// carve-out cannot simply drop the line for everyone.
    /// </summary>
    [Test]
    [Arguments(AuthProvider.WorkOS, false)]
    [Arguments(AuthProvider.GitHubApp, true)]
    public async Task A_completed_discovery_names_the_identity_only_where_discovery_did_not(
            string provider, bool named) {
        var console = SpectreCapture.Start();

        int exitCode;

        try {
            exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
                loginComplete: true, provider: provider, serverUrl: "https://acme.kcap.ai",
                forceDevice: false, activeProfile: "acme");
        } finally {
            console.Restore();
        }

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(console.Text.Contains("Logged in as")).IsEqualTo(named);
    }

    [Test]
    public async Task RunLoginStepAsync_explicit_login_failure_prints_login_failed_and_returns_one() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"martian"}""");

        SetupCommand.FacadeOverride = _ => NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        // The provider param is Step 1's resolved value; Step 2 re-fetches /auth/config for the
        // actual login, so a server reporting an unrelated/unknown provider by then still fails.
        var exitCode = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
            forceDevice: false, activeProfile: "acme");

        await Assert.That(exitCode).IsEqualTo(1);
    }
}
