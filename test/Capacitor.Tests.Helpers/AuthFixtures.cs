using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Duende.IdentityModel.OidcClient.Browser;
using NSubstitute;

namespace Capacitor.Tests.Helpers;

// Shared by the Core façade tests and the CLI login/setup parity tests, which sit in different
// assemblies and so cannot reach a fixture declared in either one.
public static class AuthFixtures {
    /// <summary>
    /// A real <see cref="TokenStore"/> over unconfigured clients — the one construction site for a
    /// constructor the suites reach from eighty-odd files. A test that scripts what the refresh lanes
    /// answer passes a handler.
    /// </summary>
    public static TokenStore NewTokenStore(ConfigRoot root, HttpMessageHandler? handler = null) {
        var factory = new PlainHttpClientFactory(handler);

        return new TokenStore(root, factory, new WorkOSClient(factory));
    }

    public static OnboardingFacade NewFacade(
            ConfigRoot                                                  root,
            IAuthProgress                                               progress,
            HttpMessageHandler                                          handler,
            ITenantPicker?                                              picker        = null,
            ITenantProvisioner?                                         provisioner   = null,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit  = null,
            Func<CancellationToken, Task<WorkOSAuthResponse?>>?         workosLogin   = null,
            IBrowser?                                                   workosBrowser = null,
            string?                                                     workosApiBase = null,
            IBrowserLauncher?                                           browser       = null) {
        var factory = new PlainHttpClientFactory(handler);

        return new OnboardingFacade(
                root, NewTokenStore(root), factory,
                new AuthProxyClient(factory.CreateClient(CapacitorClients.Anonymous)),
                new GitHubOAuthClient(factory), new WorkOSClient(factory),
                progress, browser ?? new RecordingBrowser(),
                picker ?? Substitute.For<ITenantPicker>(), provisioner, beforeCommit) {
            WorkOSOrglessLogin    = workosLogin,
            WorkOSBrowser         = workosBrowser,
            WorkOSApiBaseOverride = workosApiBase
        };
    }

    public static ITenantPicker PickerReturningFirst() {
        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>())
              .Returns(ci => Task.FromResult<DiscoveredTenant?>(ci.Arg<DiscoveredTenant[]>()[0]));

        return picker;
    }

    public const string TwoGitHubTenants = """
        [{"org_id":1,"org_login":"acme","origin":"https://acme.kcap.ai"},
         {"org_id":2,"org_login":"contoso","origin":"https://contoso.kcap.ai"}]
        """;
}
