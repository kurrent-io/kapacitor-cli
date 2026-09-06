namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/projects*</c> endpoints — see <see cref="ISessionsApi"/> for the
/// no-URL-composition, no-status-inspection contract every API interface in this namespace follows.</summary>
public interface IProjectsApi {
    Task<ProjectsResult> GetProjectsAsync(CancellationToken ct = default);

    Task<ProjectResult> GetProjectAsync(string slug, CancellationToken ct = default);
}
