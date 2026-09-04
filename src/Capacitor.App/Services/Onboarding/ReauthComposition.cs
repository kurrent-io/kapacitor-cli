using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Onboarding;

/// What the steady-state sign-in dialog runs on: the wizard's sign-in step, and the driver whose
/// quiesce the close path awaits.
internal sealed record ReauthGraph(SignInStepViewModel SignIn, WizardAuthService Auth) {
    /// The dialog's one close path, mirroring the wizard's handoff: a live attempt is cancelled
    /// and its run awaited (CanLeaveAsync never vetoes), then the driver is quiesced so nothing
    /// commits after the window is gone.
    public async Task CloseAsync(CancellationToken ct) {
        await SignIn.CanLeaveAsync(WizardNavigation.Next, ct).ConfigureAwait(true);
        await Auth.QuiescedAsync().ConfigureAwait(true);
    }
}

/// <summary>
/// The re-auth half of the composition root: the wizard's sign-in step composed alone, with the
/// intent pre-staged as a Paste of the profile's own server. Pasting the SAME origin is what keeps
/// the "sign-in never repoints server_url" rule intact while reusing the wizard's commit boundary
/// unchanged — and a Paste intent can never produce the Retarget answer, so the dialog needs no
/// Connect step to come back to.
/// </summary>
internal static class ReauthComposition {
    internal static ReauthGraph Build(
            ConfigRoot root, TokenStore tokenStore, IHttpClientFactory httpFactory, IAuthProxyClient proxy,
            GitHubOAuthClient github, WorkOSClient workos,
            string profile, string serverUrl, WizardBridges bridges,
            ConsentFlipClaims claims, IAppStateStore appState, IUrlOpener urlOpener,
            Func<WizardFacadeSpec, Func<ConnectIntent, CancellationToken, Task<AuthResult>>> operation) {
        var auth = new WizardAuthService(WizardComposition.BuildOperation(
            root, tokenStore, httpFactory, proxy, github, workos, profile, bridges, claims, operation));
        var connect = new ConnectStepViewModel();
        connect.Prefill(serverUrl);

        return new ReauthGraph(new SignInStepViewModel(auth, connect, bridges, claims, appState, urlOpener), auth);
    }
}
