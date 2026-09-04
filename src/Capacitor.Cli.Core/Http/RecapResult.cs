namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a session's recap can mean. A 404 means the session itself was never
/// found — distinct from an empty <see cref="Found"/> list, which means it was found and had none.</summary>
public abstract record RecapResult {
    public sealed record Found(List<RecapEntry> Entries) : RecapResult;

    public sealed record NotFound : RecapResult;
}
