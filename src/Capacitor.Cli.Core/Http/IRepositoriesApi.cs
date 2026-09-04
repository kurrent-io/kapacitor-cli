namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/repositories/*</c> endpoints — see <see cref="ISessionsApi"/> for
/// the no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IRepositoriesApi {
    /// <summary>No distinct outcome a caller renders differently — a refusal or an unreachable server
    /// throws, and an empty result is just an empty list.</summary>
    Task<List<RepoRecapEntry>> GetRecapsAsync(string repoHash, int limit, CancellationToken ct = default);

    /// <summary>The curation decisions promoted for this repository, heaviest first. A caller that
    /// receives exactly <paramref name="limit"/> items has hit the page boundary.</summary>
    Task<CurationResult> GetPromotedCurationAsync(string repoHash, int limit, CancellationToken ct = default);

    /// <summary>
    /// The versioned skill-doc snapshot for one harness tree. A null <paramref name="vendor"/> asks
    /// for the shared snapshot, which excludes every vendor-restricted doc.
    /// </summary>
    /// <param name="etag">Makes the request conditional, so an unchanged snapshot answers
    /// <see cref="SkillsSnapshotResult.NotModified"/>. Null asks unconditionally — which is what a
    /// caller whose local materialization has drifted needs, since a 304 would otherwise report
    /// "up to date" over a missing file forever.</param>
    Task<SkillsSnapshotResult> GetSkillsSnapshotAsync(
        string repoHash, string? vendor, string? etag, CancellationToken ct = default);
}
