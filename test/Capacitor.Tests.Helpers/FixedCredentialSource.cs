using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A credential source that answers with the same bearer every time and rotates to itself, for a
/// subject whose behaviour does not turn on the credential.
/// </summary>
public sealed class FixedCredentialSource(string? bearer = null) : ICredentialSource {
    public Task<CredentialState> ResolveAsync(CancellationToken ct = default) =>
        Task.FromResult(new CredentialState(
            bearer, bearer is null ? AuthStatus.NoAuthRequired : AuthStatus.Ok));

    public Task<CredentialState> RotateAsync(string? refused, CancellationToken ct = default) => ResolveAsync(ct);
}
