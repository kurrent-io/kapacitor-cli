namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a session's tool errors can mean. A 404 means the session itself was never
/// found — distinct from an empty <see cref="Found"/> list, which means it was found and had none.</summary>
public abstract record ErrorsResult {
    public sealed record Found(List<ErrorEntry> Errors) : ErrorsResult;

    public sealed record NotFound : ErrorsResult;
}
