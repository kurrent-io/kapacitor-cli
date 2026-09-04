namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/review/*</c> endpoints — see <see cref="ISessionsApi"/> for the
/// no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IReviewApi {
    /// <summary>Whether the server holds review context for a pull request. The context itself is
    /// served to the reviewer over MCP, so only its presence is answered here.</summary>
    Task<ReviewContextResult> GetPullRequestContextAsync(
        string owner, string repo, int prNumber, CancellationToken ct = default);
}
