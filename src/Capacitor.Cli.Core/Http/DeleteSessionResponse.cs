namespace Capacitor.Cli.Core.Http;

/// <summary>
/// What deleting a session can mean. Anything else is a failure and throws.
/// </summary>
public abstract record DeleteSessionResponse {
    public sealed record Deleted : DeleteSessionResponse;

    /// <summary>The server holds nothing for this session — it may already have been deleted.</summary>
    public sealed record NotFound : DeleteSessionResponse;
}
