using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What discovery established, up to but not including any durable write. <see cref="Ready"/>
/// carries the org-switched auth for the picked (or freshly created) tenant.
/// </summary>
public abstract record WorkOSDiscoveryFlow {
    public sealed record Ready(
        DiscoveredTenant[]  Tenants,
        DiscoveredTenant    Picked,
        WorkOSAuthResponse  SwitchedAuth,
        string              ClientId,
        // From the ORG-LESS login response: the org-switch response carries no profile fields.
        string              Username) : WorkOSDiscoveryFlow;

    public sealed record Retarget(string ServerInput) : WorkOSDiscoveryFlow;

    public sealed record NoTenants : WorkOSDiscoveryFlow;

    /// <param name="Message">Already emitted through <see cref="IAuthProgress"/>, except where the
    /// reason needs re-presenting in the caller's own words — see
    /// <see cref="AuthFailureReason.ProvisioningInProgress"/>.</param>
    public sealed record Failed(
        string            Message,
        AuthFailureReason Reason = AuthFailureReason.Other) : WorkOSDiscoveryFlow;
}

/// <summary>
/// WorkOS tenant discovery: authenticate org-less against the proxy's shared AuthKit app,
/// list the user's tenants via the proxy, let them pick, then org-switch into the chosen org
/// and save an org-scoped profile. The two browser/HTTP effects (org-less login, org-switch)
/// are injected so the orchestration (discover → pick → switch → save) is unit-testable;
/// production wiring passes <see cref="OAuthLoginFlow"/>'s loopback + switch helpers.
/// </summary>
public static class WorkOSDiscovery {
    /// <summary>Everything up to the commit boundary: sign in, enumerate, pick (or create), org-switch.</summary>
    public static async Task<WorkOSDiscoveryFlow> DiscoverAsync(
            string                                          proxyUrl,
            ProxyConfigResponse                             proxyConfig,
            IAuthProxyClient                                proxy,
            ITenantPicker                                   picker,
            Func<Task<WorkOSAuthResponse?>>                 orglessLogin,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch,     // args: refreshToken, organizationId
            Func<string, CancellationToken, Task<WorkOSAuthResponse?>>? orglessRefresh = null, // args: refreshToken, ct
            ITenantProvisioner?                             provisioner = null,
            CancellationToken                               ct = default,
            IAuthProgress?                                  progress = null,
            TenantPickContext?                              pickContext = null) {
        progress ??= ConsoleAuthProgress.Instance;

        if (string.IsNullOrEmpty(proxyConfig.WorkOSClientId)) {
            return Failed(progress, "This server isn't configured for WorkOS sign-in.");
        }

        var auth = await orglessLogin();
        if (auth is null || string.IsNullOrEmpty(auth.RefreshToken)) {
            // Anchored here, not on this method's return: by the time discovery returns, it has
            // also run tenant enumeration and (on the zero-tenant fork) provisioning, so keying
            // signin_completed/failed on the overall outcome would place signin_completed after
            // tenant_none/workspace_provisioned and make signin_failed fire for declined offers,
            // provisioning failures, and the deliberately-non-zero retarget path — none of which
            // are a sign-in failure.
            SetupFunnel.SigninFailed("workos_signin_failed");

            return Failed(progress, "WorkOS sign-in failed.", ct);
        }

        SetupFunnel.SigninCompleted(AuthProvider.WorkOS);

        var result = await proxy.DiscoverWorkOSTenantsAsync(proxyUrl, auth.AccessToken, ct);
        if (result.Error != DiscoveryError.None) {
            return Failed(progress, result.Error switch {
                DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
                DiscoveryError.TokenRejected    => "WorkOS rejected the authentication token. Please sign in again.",
                DiscoveryError.UpstreamError    => "Kurrent auth service returned an error. Try again later.",
                _                               => "Tenant discovery failed."
            }, ct);
        }

        if (result.Tenants.Length == 0) {
            return await OfferCreateAsync(proxyConfig, auth, orgSwitch, orglessRefresh, provisioner, ct, progress);
        }

        var picked = result.Tenants.Length == 1
            ? result.Tenants[0]
            : await picker.PickAsync(
                result.Tenants,
                (pickContext ?? TenantPickContext.None) with {
                    Bearer      = auth.AccessToken,
                    ViaLoopback = !auth.ViaDeviceGrant
                },
                ct);
        // Not through Failed: the picker has already said why, and a second line here would be the
        // one that contradicts it — "no tenant selected" reads as a choice on a session that had none.
        if (picked is null) return new WorkOSDiscoveryFlow.Failed("No tenant selected.");

        return await SwitchAsync(picked, result.Tenants, auth, proxyConfig.WorkOSClientId!, orgSwitch, progress);
    }

    static async Task<WorkOSDiscoveryFlow> OfferCreateAsync(
            ProxyConfigResponse                                         proxyConfig,
            WorkOSAuthResponse                                          auth,
            Func<string, string, Task<WorkOSAuthResponse?>>             orgSwitch,
            Func<string, CancellationToken, Task<WorkOSAuthResponse?>>? orglessRefresh,
            ITenantProvisioner?                                         provisioner,
            CancellationToken                                           ct,
            IAuthProgress                                               progress) {
        // Fires before the provisioner-null check below: a headless run (null provisioner,
        // "ask your admin" dead-end) still reached the fork and must count as such — this is
        // the denominator for "reached signup".
        SetupFunnel.TenantNone(AuthProvider.WorkOS);

        if (provisioner is null) {
            progress.Error("No Capacitor tenants are linked to your account. Ask your admin to invite you.");

            return new WorkOSDiscoveryFlow.NoTenants();
        }

        // Provisioning + polling can run for minutes, outliving WorkOS's ~5-minute access-token
        // TTL, so hand the provisioner a refreshing token source rather than the login-time token.
        var tokens = new WorkOSTokenSource(
            auth.AccessToken, auth.RefreshToken,
            orglessRefresh ?? ((_, _) => Task.FromResult<WorkOSAuthResponse?>(null)));
        var offer = await provisioner.OfferCreateAsync(tokens, ct);

        if (offer.Status == ProvisionOfferStatus.ExistingWorkspace) {
            // The user belongs to a workspace already and would rather point at it. Hand the
            // input back unresolved (trimmed, nothing else): only the caller knows how a bare
            // slug expands, and the target's own /auth/config — not this WorkOS lane — decides
            // how to log in. Blank input would resolve to a nonsense host, so decline instead.
            // Trimmed here as well as at the prompt because this interface is public.
            var target = offer.ExistingWorkspaceInput?.Trim();

            return string.IsNullOrEmpty(target)
                ? new WorkOSDiscoveryFlow.Failed("No workspace named.")
                : new WorkOSDiscoveryFlow.Retarget(target);
        }

        if (offer.Status == ProvisionOfferStatus.InProgress) {
            // Not a failure: the workspace is being created and the poll outran its window. Carried
            // with its own reason so the caller headlines a pending state rather than "sign-in failed".
            // Deliberately just the fact, no guidance — the provisioner's own line says what to do
            // next, and both land in front of the same reader.
            var pending = offer.PendingSlug is { Length: > 0 } slug ? $"'{slug}'" : "Your workspace";

            return new WorkOSDiscoveryFlow.Failed(
                $"{pending} is still being created.", AuthFailureReason.ProvisioningInProgress);
        }

        if (offer.Status != ProvisionOfferStatus.Created || offer.Tenant is null) {
            // Declined / Failed — the provisioner already printed the outcome-appropriate message;
            // don't stack the legacy dead-end on top.
            return new WorkOSDiscoveryFlow.Failed($"Workspace creation did not complete ({offer.Status}).");
        }

        var created = new DiscoveredTenant {
            Provider       = AuthProvider.WorkOS,
            OrganizationId = offer.Tenant.OrganizationId,
            Slug           = offer.Tenant.Slug,
            DisplayName    = offer.Tenant.DisplayName,
            Origin         = offer.Tenant.Origin
        };
        // Polling may have rotated the org-less refresh token; the org-switch must use the
        // current one (WorkOS invalidates the old on refresh) or the final switch would 401.
        var authForSwitch = auth with { RefreshToken = tokens.CurrentRefreshToken ?? auth.RefreshToken };

        return await SwitchAsync(created, [created], authForSwitch, proxyConfig.WorkOSClientId!, orgSwitch, progress);
    }

    // Org-switch into the chosen tenant. Shared by the picked-tenant path and the
    // freshly-provisioned-tenant path; nothing durable is written here.
    static async Task<WorkOSDiscoveryFlow> SwitchAsync(
            DiscoveredTenant                                picked,
            DiscoveredTenant[]                              tenants,
            WorkOSAuthResponse                              auth,
            string                                          clientId,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch,
            IAuthProgress                                   progress) {
        if (string.IsNullOrEmpty(picked.OrganizationId)) {
            return Failed(progress, $"Tenant {picked.Label} is missing an organization id; cannot complete sign-in.");
        }

        // Org-switch once into the chosen org. The resulting refresh token stays org-bound
        // (spike-confirmed), so later refreshes need no organization_id.
        var switched = await orgSwitch(auth.RefreshToken!, picked.OrganizationId);
        if (switched is null) {
            return Failed(progress, $"Could not switch to organization {picked.Label}.");
        }

        return new WorkOSDiscoveryFlow.Ready(tenants, picked, switched, clientId, OAuthLoginFlow.WorkOSDisplayName(auth.User));
    }

    /// <summary>Publishes a <see cref="WorkOSDiscoveryFlow.Ready"/> through the ordered commit boundary.</summary>
    internal static async Task<AuthResult> PublishAsync(
            ConfigRoot                                                  root,
            TokenStore                                                  store,
            WorkOSDiscoveryFlow.Ready                                   ready,
            IAuthProgress                                               progress,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
            CancellationToken                                           ct) {
        var picked = ready.Picked;

        if (!ServerIdentity.TryCanonicalizeForStamping(picked.Origin, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return new AuthResult.Failed(identityError);
        }

        var tokens = new StoredTokens {
            AccessToken    = ready.SwitchedAuth.AccessToken,
            RefreshToken   = ready.SwitchedAuth.RefreshToken,
            ExpiresAt      = TokenStore.JwtExpiry(ready.SwitchedAuth.AccessToken),
            GitHubUsername = ready.Username,
            Provider       = AuthProvider.WorkOS,
            ClientId       = ready.ClientId,
            // The tenant's own origin: this token is org-scoped to the tenant we just switched
            // into, and only that tenant's server will accept it.
            ServerUrl      = canonical
        };

        var request = new CommitRequest(
            [new AuthIdentity(picked.ProfileName, canonical)], AuthProvider.WorkOS, picked.ProfileName, canonical,
            ConfigMutation: config => TenantDiscovery.MergeProfiles(config, ready.Tenants, picked),
            PublishTokens: async saved => {
                await store.SaveAsync(picked.ProfileName, tokens, CancellationToken.None);
                saved();

                return ready.Username;
            });

        var result = await CommitBoundary.CommitAsync(root, request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) progress.Notice($"Logged in as {ready.Username} → {picked.Label}");

        return result;
    }

    // A live cancel suppresses the line: these two arms map an OperationCanceledException onto a
    // transport failure the user never caused, and the façade answers Cancelled from the result.
    static WorkOSDiscoveryFlow.Failed Failed(IAuthProgress progress, string message, CancellationToken ct = default) {
        if (!ct.IsCancellationRequested) progress.Error(message);

        return new WorkOSDiscoveryFlow.Failed(message);
    }
}
