namespace Capacitor.Cli.Core.Http;

/// <summary>Whether a pull request has review context on the server.</summary>
public abstract record ReviewContextResult {
    public sealed record Found : ReviewContextResult;

    public sealed record NotFound : ReviewContextResult;
}
