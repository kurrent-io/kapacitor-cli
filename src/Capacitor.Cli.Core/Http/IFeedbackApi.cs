namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>POST /api/feedback</c> — see <see cref="ISessionsApi"/> for the
/// no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IFeedbackApi {
    Task<FeedbackResult> SubmitAsync(string category, string message, CancellationToken ct = default);
}
