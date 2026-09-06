namespace Capacitor.Cli.Core.Http;

/// <summary>
/// A client and why it is — or is not — carrying a bearer, for a caller with no budget to spend
/// earning a 401 it could have predicted. Deconstructs to the pair every existing site destructures.
/// </summary>
public readonly record struct AuthAttempt(
        HttpClient Client, AuthStatus Status, string? Problem = null, string? IssuedServerUrl = null) {
    /// <summary>Whether sending is worth attempting: a server needing no auth is as usable as one
    /// whose credential resolved.</summary>
    public bool Usable => Status is AuthStatus.Ok or AuthStatus.NoAuthRequired;

    public void Deconstruct(out HttpClient client, out AuthStatus status) {
        client = Client;
        status = Status;
    }
}
