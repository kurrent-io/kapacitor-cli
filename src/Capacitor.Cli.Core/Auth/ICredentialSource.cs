namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Where a bearer comes from and how it is replaced once refused. A stored profile token and a
/// minted machine token differ in nothing else, which lets one recovery handler serve both.
/// </summary>
public interface ICredentialSource {
    Task<CredentialState> ResolveAsync(CancellationToken ct);

    /// <summary>
    /// A null <paramref name="refused"/> means nothing was sent, so re-read rather than rotate.
    /// Rotation is conditional on it still being what is held, so a peer's fresh credential survives.
    /// </summary>
    Task<CredentialState> RotateAsync(string? refused, CancellationToken ct);
}
