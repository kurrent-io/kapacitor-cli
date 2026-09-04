using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap login`: known-server sign-in, or (no configured server, or explicit <c>--discover</c>)
/// tenant discovery — both driven through <see cref="OnboardingFacade"/>. Every login/discovery
/// message is already rendered through the facade's <see cref="IAuthProgress"/> sink; this layer
/// only maps the result to an exit code and, on the discover path, appends today's final line.
/// </summary>
public sealed class LoginCommand(
        ConfigRoot config, ProfileContext profiles, TokenStore tokens, IHttpClientFactory httpFactory,
        IAuthProxyClient proxy, GitHubOAuthClient github, WorkOSClient workos, IBrowserLauncher browser,
        TenantProvisioningClient provisioning) {
    public Task<int> HandleAsync(string[] args, string? baseUrl) =>
        HandleAsync(args, baseUrl, profiles.Name, NewFacade(), ConsoleAuthProgress.Instance);

    /// <summary>The dispatch with its facade handed in — independent of the root, which only the
    /// entry point above needs to build one.</summary>
    internal static async Task<int> HandleAsync(
            string[] args, string? baseUrl, string profile, OnboardingFacade facade, IAuthProgress progress) {
        // Also when there is no keyboard: a redirected stdin cannot press the escape-hatch key, so a
        // loopback wait there can only end in the listener timeout. Not a headless guess - an
        // interactive SSH session has a keyboard and keeps the browser.
        var forceDevice = OAuthLoginFlow.DeviceRouteRequired(args.Contains("--device"), ConsoleKeyWatcher.Instance.CanWatch);

        // No configured server (or explicit --discover) → run tenant discovery (pick provider,
        // then your tenants). Otherwise log into the configured server.
        if (OAuthLoginFlow.ShouldDiscoverLogin(baseUrl, args)) {
            return await HandleDiscoverAsync(facade, args, forceDevice, progress);
        }

        // `kcap login` never adopts a foreign profile onto the server it just signed into — see
        // OnboardingFacade.LoginAsync's adoptServer doc.
        var result = await facade.LoginAsync(baseUrl!, forceDevice, profile, CancellationToken.None, adoptServer: false);

        return result is AuthResult.Committed ? 0 : 1;
    }

    static async Task<int> HandleDiscoverAsync(
            OnboardingFacade facade, string[] args, bool forceDevice, IAuthProgress progress) {
        var provider = OAuthLoginFlow.ChooseDiscoveryProvider(args);

        var result = await facade.DiscoverAsync(provider, forceDevice, CancellationToken.None);

        return MapDiscoverResult(result, progress);
    }

    /// <summary>
    /// Committed → 0 (the GitHub path gets today's trailing "Active profile" line here; WorkOS
    /// already printed its own "Logged in as … → …" inside the facade). Retarget → today's
    /// "run kcap setup" hint + 1. Failed/Cancelled → 1 with nothing further — already rendered.
    /// </summary>
    internal static int MapDiscoverResult(AuthResult result, IAuthProgress progress) {
        switch (result) {
            case AuthResult.Committed committed:
                if (committed.Provider != AuthProvider.WorkOS) {
                    progress.Notice($"Logged in. Active profile: {committed.ActiveProfile}.");
                }

                return 0;
            case AuthResult.Retarget retarget:
                progress.Error($"Run `kcap setup {retarget.ServerInput}` to configure that workspace.");

                return 1;
            default:
                return 1;
        }
    }

    /// <summary>
    /// The provisioner is not optional here. `kcap login --discover` is the reachable zero-workspace
    /// path, and since org SSO gained a device grant a user with no workspace completes the
    /// sign-in rather than failing before it — without this they would hold a live credential and be
    /// told to ask an admin.
    /// </summary>
    OnboardingFacade NewFacade() =>
        new(config, tokens, httpFactory, proxy, github, workos, ConsoleAuthProgress.Instance, browser,
            // The same composite `kcap setup` uses: one operation must not behave differently
            // for being reached by a different command.
            new BrowserTenantPicker(browser, new SpectreTenantPicker(), ConsoleAuthProgress.Instance),
            new SpectreTenantProvisioner(provisioning, ProvisioningEndpoint.Url),
            beforeCommit: null) {
            KeyWatcher = ConsoleKeyWatcher.Instance
        };
}
