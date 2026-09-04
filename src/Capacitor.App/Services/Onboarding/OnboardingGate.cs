using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Onboarding;

public abstract record GateResult {
    public sealed record Complete : GateResult;
    public sealed record Incomplete(GateReason Reason) : GateResult;
}

// EvaluationFailed is the degrade for an unexpected exception: from EvaluateAsync when the
// evaluation throws (the resolution survives), from App.EvaluateGateSafelyAsync when the resolve does.
public enum GateReason { NoProfile, InvalidServerUrl, NoToken, TokenUnusableBinding, TokenUnusableExpired, EvaluationFailed }

/// <summary>
/// Decision-1 first-run trigger: local, side-effect-free except the shared resolution path's own
/// v1→v2 migration write on legacy configs (kept intentionally shared with the normal daemon
/// graph — decision 2 — rather than a purity-motivated divergent read that could resolve a
/// different profile than the graph builds), no refresh. Whether the wizard opens is the exact
/// inverse of "does TokenStore already consider this profile authenticated" — so every branch
/// here mirrors a specific TokenStore rule rather than inventing its own.
/// </summary>
public sealed class OnboardingGate(ConfigRoot config, TokenStore tokenStore) {
    /// <summary>
    /// The ONE shared validator for "is this usable as a server identity" — also used by
    /// <c>App.ValidProfileName</c> so the gate and the lifecycle-controller precondition can
    /// never disagree on what counts as a valid <c>server_url</c> (e.g. both reject
    /// <c>file://</c>). Delegates to <see cref="ServerIdentity.Canonicalize"/>, which restricts
    /// to absolute http/https origins with no userinfo/query/fragment.
    /// </summary>
    public static bool ValidServerUrl(string? url) => ServerIdentity.Canonicalize(url) is not null;

    /// The ONE resolve-then-evaluate composition (App.ResolveAndEvaluateGateAsync wraps this in its
    /// never-brick degrade). It hands the resolution back with the verdict, and the daemon graph is
    /// built from that very value — which is what stops the verdict and the graph identity naming
    /// different profiles.
    public async Task<(GateResult Result, ProfileContext Profiles)> EvaluateAsync(CancellationToken ct) {
        // Daemon-style resolution — no repo/git discovery, matching decision 1's "local" scope.
        var profiles = await AppConfig.ResolveActiveProfile([], config);
        GateResult result;
        try {
            result = await EvaluateResolvedAsync(profiles.Resolution.ProfileName, profiles.Resolution.Profile, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // The resolution outlives a failed evaluation. It names the daemon to attach to and the
            // profile a sign-in writes to; discarding it falls back to the OS username and
            // "default", so a token file that cannot be read would silently move both.
            await Console.Error.WriteLineAsync($"kcap: onboarding gate evaluation failed — degrading to Incomplete: {ex.Message}");
            result = new GateResult.Incomplete(GateReason.EvaluationFailed);
        }
        return (result, profiles);
    }

    /// Evaluates a resolution the caller already holds instead of resolving a second time — two
    /// independent resolves racing a concurrent active-profile change could otherwise evaluate the
    /// gate against a different profile than the one the daemon graph builds for.
    public async Task<GateResult> EvaluateResolvedAsync(string? profileName, Profile? profile, CancellationToken ct) {
        if (profile is null || string.IsNullOrEmpty(profileName)) {
            return new GateResult.Incomplete(GateReason.NoProfile);
        }

        if (!ValidServerUrl(profile.ServerUrl)) {
            return new GateResult.Incomplete(GateReason.InvalidServerUrl);
        }

        var stamp = profile.AuthProvider;

        // Case-insensitive: the stamp writer emits the AuthProvider.None constant verbatim
        // ("None"), not a lowercased literal — an ordinal-exact "none" compare would silently
        // never satisfy the gate for a real stamp.
        if (stamp is not null
                && string.Equals(stamp.Provider, AuthProvider.None, StringComparison.OrdinalIgnoreCase)
                && ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl)) {
            return new GateResult.Complete();
        }

        // Raw, refresh-free read — a stale/expiring token must not be rotated just to answer
        // "is the wizard needed", which would spend a rotating WorkOS refresh token for nothing.
        var tokens = await tokenStore.LoadForProfileAsync(profileName, ct);

        if (tokens is null) {
            return new GateResult.Incomplete(GateReason.NoToken);
        }

        if (!BoundToProfile(tokens, profile.ServerUrl)) {
            return new GateResult.Incomplete(GateReason.TokenUnusableBinding);
        }

        if (!tokens.IsExpired) {
            return new GateResult.Complete();
        }

        return RefreshCapable(tokens)
            ? new GateResult.Complete()
            : new GateResult.Incomplete(GateReason.TokenUnusableExpired);
    }

    // Mirrors TokenStore.BoundToTarget exactly: a legacy (pre-upgrade) token carries no
    // ServerUrl stamp, so there is nothing to contradict — treated as usable for any server.
    static bool BoundToProfile(StoredTokens tokens, string? serverUrl) =>
        tokens.ServerUrl is null || ServerIdentity.SameServer(tokens.ServerUrl, serverUrl);

    // Mirrors GetValidTokensForProfileAsync's refresh gating: GitHubApp always refreshes via the
    // server's /auth/refresh; WorkOS needs its own rotating RefreshToken plus ClientId.
    static bool RefreshCapable(StoredTokens tokens) =>
        tokens.Provider is AuthProvider.GitHubApp
     || tokens is { Provider: AuthProvider.WorkOS, RefreshToken: not null, ClientId: not null };
}
