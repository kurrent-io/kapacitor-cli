namespace Capacitor.Cli.Core.Auth;

/// <summary>A server that requires no authentication: no bearer, and nothing to rotate.</summary>
internal sealed class NoCredentials : ICredentialSource {
    public static readonly NoCredentials Instance = new();

    public Task<CredentialState> ResolveAsync(CancellationToken ct) =>
        Task.FromResult(new CredentialState(null, AuthStatus.NoAuthRequired));

    public Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) => ResolveAsync(ct);
}
