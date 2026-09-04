using System.Text;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using NSubstitute;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// PublishAsync emits SetupFunnel events into CliTelemetry's process-global sink, so this class
// must not run beside a test asserting on that sink's contents.
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class WorkOSDiscoveryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string JwtWithExp(DateTimeOffset exp) {
        var json = $"{{\"exp\":{exp.ToUnixTimeSeconds()}}}";
        var b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{b64}.signature";
    }

    [Test]
    public async Task DiscoverAsync_org_switches_picked_tenant_and_PublishAsync_saves_workos_profile() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d", WorkOSAuthKitDomain = "" };

        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "contoso",   DisplayName = "Contoso",   Origin = "https://contoso.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(tenants, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(tenants, Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DiscoveredTenant?>(tenants[0])); // eventuous

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc",  RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" };

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, picker,
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched));

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Ready>();

        var result = await WorkOSDiscovery.PublishAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root),
            (WorkOSDiscoveryFlow.Ready)flow, new RecordingAuthProgress(), beforeCommit: null, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();

        var stored = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("eventuous");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.WorkOS);

        var cfg = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(cfg.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(cfg.Profiles["eventuous"].ServerUrl).IsEqualTo("https://eventuous.kcap.ai");
    }

    [Test]
    public async Task DiscoverAsync_errors_when_workos_not_configured() {
        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "" },
            Substitute.For<IAuthProxyClient>(), Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(null),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>();
    }

    [Test]
    public async Task DiscoverAsync_errors_when_picked_tenant_has_no_org_id() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" } // no OrganizationId
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(tenants, DiscoveryError.None)));

        var switchCalled = false;
        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); });

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>();
        await Assert.That(switchCalled).IsFalse(); // fail before the org-switch, not during it
    }

    [Test]
    public async Task DiscoverAsync_returns_NoTenants_when_no_tenants_and_no_provisioner() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.NoTenants>();
    }

    [Test]
    public async Task DiscoverAsync_provisions_when_no_tenants_and_provisioner_creates() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d" };
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        WorkOSTokenSource? handedTokens = null;
        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(ci => { handedTokens = ci.Arg<WorkOSTokenSource>(); return Task.FromResult(ProvisionOffer.Created(
                       new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"))); });

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_new", AccessToken = "acc2", RefreshToken = "rt2" };

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched),
            provisioner:  provisioner);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Ready>();

        var result = await WorkOSDiscovery.PublishAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root),
            (WorkOSDiscoveryFlow.Ready)flow, new RecordingAuthProgress(), beforeCommit: null, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();

        var stored = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");

        var cfg = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["acme"].ServerUrl).IsEqualTo("https://acme.kcap.ai");

        // The provisioner is handed a refreshing token source seeded with the org-less login token.
        await Assert.That(handedTokens).IsNotNull();
        await Assert.That(await handedTokens!.GetAsync(CancellationToken.None)).IsEqualTo("acc");
    }

    [Test]
    public async Task DiscoverAsync_uses_rotated_refresh_token_for_org_switch_when_poll_refreshed() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d" };
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        // Login-time access token is already near expiry, so the provisioner's first token pull
        // forces a refresh — exactly the long-provisioning case that consumes the org-less token.
        var nearExpiry = JwtWithExp(DateTimeOffset.UtcNow.AddSeconds(5));
        var orgless    = new WorkOSAuthResponse { User = new() { Id = "u" }, AccessToken = nearExpiry, RefreshToken = "R0" };
        var switched   = new WorkOSAuthResponse { User = new() { Id = "u" }, OrganizationId = "org_new", AccessToken = "acc2", RefreshToken = "R2" };

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(async ci => {
                       await ci.Arg<WorkOSTokenSource>().GetAsync(CancellationToken.None); // rotates R0 -> R1
                       return ProvisionOffer.Created(new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"));
                   });

        string? switchRefreshToken = null;
        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, Substitute.For<ITenantPicker>(),
            orglessLogin:   ()      => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:      (rt, _) => { switchRefreshToken = rt; return Task.FromResult<WorkOSAuthResponse?>(switched); },
            orglessRefresh: (_, _)  => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { AccessToken = JwtWithExp(DateTimeOffset.UtcNow.AddMinutes(5)), RefreshToken = "R1" }),
            provisioner:    provisioner);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Ready>();
        await Assert.That(switchRefreshToken).IsEqualTo("R1"); // rotated token, not the consumed login-time R0
    }

    [Test]
    public async Task DiscoverAsync_hands_back_the_workspace_the_user_already_has_instead_of_provisioning() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.ExistingWorkspace("kurrent")));

        var switchCalled = false;
        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); },
            provisioner: provisioner);

        // The input comes back unresolved — expanding "kurrent" to a URL, and picking the auth
        // provider from that server, both belong to the caller.
        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Retarget>();
        await Assert.That(((WorkOSDiscoveryFlow.Retarget)flow).ServerInput).IsEqualTo("kurrent");

        // Nothing WorkOS-shaped happened: no org-switch, no profile, no token.
        await Assert.That(switchCalled).IsFalse();
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("kurrent")).IsNull();
    }

    [Test]
    public async Task DiscoverAsync_treats_a_blank_existing_workspace_input_as_no_retarget() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.ExistingWorkspace("   ")));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            provisioner: provisioner);

        // Whitespace would resolve to "https://   .kcap.ai" downstream; refuse it here so the
        // caller never probes a nonsense host.
        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>();
    }

    [Test]
    public async Task DiscoverAsync_returns_Failed_without_legacy_error_when_provisioner_declines() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.Declined));

        var switchCalled = false;
        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); },
            provisioner: provisioner);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>();
        await Assert.That(switchCalled).IsFalse();
    }

    // A provisioning poll that outran its window is a pending workspace, not a failed sign-in — sign-in
    // already succeeded to get here. Undistinguished, every caller headlines it as a failure.
    [Test]
    public async Task DiscoverAsync_reports_a_provisioning_timeout_as_in_progress_and_names_the_slug() {
        var flow = await DiscoverWithOffer(ProvisionOffer.InProgress("acme"));

        var failed = (WorkOSDiscoveryFlow.Failed)flow;

        await Assert.That(failed.Reason).IsEqualTo(AuthFailureReason.ProvisioningInProgress);
        await Assert.That(failed.Message)
                    .Contains("acme")
                    .Because("the user is being told to come back to it, so it has to be named");
    }

    [Test]
    public async Task DiscoverAsync_reports_in_progress_even_when_no_slug_was_settled_on() {
        var failed = (WorkOSDiscoveryFlow.Failed)await DiscoverWithOffer(ProvisionOffer.InProgress());

        await Assert.That(failed.Reason).IsEqualTo(AuthFailureReason.ProvisioningInProgress);
        await Assert.That(failed.Message).IsNotEmpty();
    }

    // A provisioning FAILURE stays undistinguished: it really did fail, and the provisioner has said so.
    [Test]
    public async Task DiscoverAsync_leaves_a_provisioning_failure_generic() {
        var failed = (WorkOSDiscoveryFlow.Failed)await DiscoverWithOffer(ProvisionOffer.Failed);

        await Assert.That(failed.Reason).IsEqualTo(AuthFailureReason.Other);
    }

    static async Task<WorkOSDiscoveryFlow> DiscoverWithOffer(ProvisionOffer offer) {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(offer));

        return await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_p" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            provisioner: provisioner);
    }

    [Test]
    public async Task A_picker_that_chose_nothing_gets_no_second_message_from_discovery() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_x" };
        var proxy       = Substitute.For<IAuthProxyClient>();

        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme",   Origin = "https://acme.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "globex", Origin = "https://globex.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(tenants, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(tenants, Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DiscoveredTenant?>(null));

        var progress = new RecordingAuthProgress();
        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x" }, AccessToken = "acc", RefreshToken = "rt" };

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, picker,
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            progress:     progress);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>();
        await Assert.That(progress.Errors).IsEmpty();
    }

    /// <summary>
    /// The picker cannot see the login, so discovery is what tells it which channel produced the
    /// token and hands over the bearer to prepare a pick with. Both halves are pinned here because
    /// inverting either one opens a browser on a machine that has none, and the picker's own tests
    /// pass the value in by hand.
    /// </summary>
    [Test]
    [Arguments(true,  false)]
    [Arguments(false, true)]
    public async Task DiscoverAsync_tells_the_picker_which_channel_signed_in(bool viaDevice, bool expectLoopback) {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d", CliPickerVersion = 1 };

        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "acme",   Origin = "https://acme.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "globex", Origin = "https://globex.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new Cli.Core.Auth.DiscoveryResult(tenants, DiscoveryError.None)));

        TenantPickContext? seen = null;
        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>())
              .Returns(ci => {
                  seen = ci.Arg<TenantPickContext>();
                  return Task.FromResult<DiscoveredTenant?>(tenants[0]);
              });

        var orgless = new WorkOSAuthResponse {
            User = new() { Id = "user_x" }, AccessToken = "orgless-token", RefreshToken = "rt",
            ViaDeviceGrant = viaDevice
        };

        await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, picker,
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_a", AccessToken = "a2", RefreshToken = "r2" }),
            pickContext: new TenantPickContext(Proxy: proxy, ProxyUrl: "https://auth.kcap.ai", PickerVersion: 1));

        await Assert.That(seen).IsNotNull();
        await Assert.That(seen!.ViaLoopback).IsEqualTo(expectLoopback);
        await Assert.That(seen.Bearer).IsEqualTo("orgless-token");
        await Assert.That(seen.CanPickInBrowser).IsEqualTo(expectLoopback);
    }
}
