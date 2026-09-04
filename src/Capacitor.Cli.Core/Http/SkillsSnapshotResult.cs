using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Http;

/// <summary>What asking for a repository's skills snapshot can mean. <see cref="NotModified"/> is
/// only reachable when the caller supplied an etag.</summary>
public abstract record SkillsSnapshotResult {
    public sealed record Found(SkillsSnapshotResponse Snapshot) : SkillsSnapshotResult;

    public sealed record NotModified : SkillsSnapshotResult;

    /// <summary>The repository is not visible to this profile.</summary>
    public sealed record NotFound : SkillsSnapshotResult;
}
