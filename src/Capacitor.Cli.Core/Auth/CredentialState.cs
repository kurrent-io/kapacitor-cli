namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// The bearer to send now, and why there is not one. <c>Resolution</c> is carried only by the
/// token-store path, <c>Problem</c> only by the machine path, whose diagnostic no status expresses.
/// </summary>
public readonly record struct CredentialState(
    string?          Bearer,
    AuthStatus       Status,
    TokenResolution? Resolution = null,
    string?          Problem    = null);
