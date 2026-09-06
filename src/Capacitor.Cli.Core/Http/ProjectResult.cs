namespace Capacitor.Cli.Core.Http;

/// <summary>What reading one project's detail can mean. See <see cref="ProjectsResult.Forbidden"/> for
/// why the plan-gate refusal carries the server's <c>error</c> code rather than the typed error body.</summary>
public abstract record ProjectResult {
    public sealed record Found(CliProjectDetail Project) : ProjectResult;

    public sealed record Forbidden(string? ErrorCode) : ProjectResult;

    public sealed record NotFound : ProjectResult;
}
