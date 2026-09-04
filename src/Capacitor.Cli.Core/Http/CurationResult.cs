namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a repository's promoted curation can mean. <see cref="NotFound"/> is the
/// repository being invisible to this profile, not an absence of decisions — that is an empty
/// <see cref="Found"/>.</summary>
public abstract record CurationResult {
    public sealed record Found(IReadOnlyList<CurationApplyItem> Items) : CurationResult;

    public sealed record NotFound : CurationResult;
}
