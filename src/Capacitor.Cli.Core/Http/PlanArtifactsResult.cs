namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a session's discovered plan artifacts can mean. <see cref="NotFound"/> is an
/// older server without the route, or a session/candidate the route can't resolve — not an error, a
/// caller falls back to the recap-only rendering for it.</summary>
public abstract record PlanArtifactsResult {
    public sealed record Found(PlanArtifactsResponseDto? Response) : PlanArtifactsResult;

    public sealed record NotFound : PlanArtifactsResult;
}
