namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// A logged-in profile's stored token. Rotation force-refreshes against the server that issued the
/// refused bearer, which is why it is not expressible as a plain re-read.
/// </summary>
internal sealed class TokenStoreCredentials(TokenStore tokens, string profile, string baseUrl) : ICredentialSource {
    public async Task<CredentialState> ResolveAsync(CancellationToken ct) {
        var resolution = await tokens.GetValidTokensForServerAsync(profile, baseUrl, ct);

        return resolution is { Status: AuthStatus.Ok, Tokens: not null }
            ? new(resolution.Tokens.AccessToken, AuthStatus.Ok, resolution)
            : new(null, resolution.Status, resolution);
    }

    public async Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) {
        if (refused is null) return await ResolveAsync(ct);

        var recovered = await tokens.RecoverForServerAsync(profile, baseUrl, refused, ct);

        return recovered is null ? new(null, AuthStatus.Expired) : new(recovered.AccessToken, AuthStatus.Ok);
    }
}
