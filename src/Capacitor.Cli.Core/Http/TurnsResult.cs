namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a session's turn list can mean. Carries the raw response body rather than a
/// parsed shape — callers parse it with <see cref="System.Text.Json.JsonDocument"/> themselves, since
/// the server's turn payload has no client-side DTO.</summary>
public abstract record TurnsResult {
    public sealed record Found(string Json) : TurnsResult;

    public sealed record NotFound : TurnsResult;
}
