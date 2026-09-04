namespace Capacitor.Cli.Core.Http;

/// <summary>What reading the project list can mean. Every <c>/api/projects*</c> route 403s the same
/// way when the tenant plan doesn't include projects (Free); <see cref="Forbidden"/> carries the
/// server's <c>error</c> code so a caller can tell that refusal apart from any other 403 shape.</summary>
public abstract record ProjectsResult {
    public sealed record Found(List<CliProjectSummary> Projects) : ProjectsResult;

    public sealed record Forbidden(string? ErrorCode) : ProjectsResult;
}
