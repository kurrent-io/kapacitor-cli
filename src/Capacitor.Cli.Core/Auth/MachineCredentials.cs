namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// A runner's bearer, minted from its machine credential. Resolving and rotating are the same mint —
/// <see cref="MachineTokenProvider"/> separates them by the refused token alone.
/// </summary>
internal sealed class MachineCredentials(
        MachineTokenProvider minter, WorkOSClient workos, MachineCredential credential) : ICredentialSource {
    public Task<CredentialState> ResolveAsync(CancellationToken ct) => MintAsync(null, ct);

    public Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) => MintAsync(refused, ct);

    async Task<CredentialState> MintAsync(string? refused, CancellationToken ct) {
        var minted = await minter.GetTokenAsync(workos, credential, refused, ct);

        return minted.Token is null
            ? new(null, AuthStatus.NotAuthenticated, Problem: minted.Problem)
            : new(minted.Token, AuthStatus.Ok);
    }
}
