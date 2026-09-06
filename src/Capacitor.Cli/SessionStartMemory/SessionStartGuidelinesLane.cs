using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The guidelines lane of the composite SessionStart context provider:
/// fetches judge-fact guideline clusters from
/// <c>GET /api/repositories/{hash}/guidelines</c> and renders them as a
/// marker-less <c>## Known patterns</c> / <c>## Guidance from past sessions</c>
/// block. The composite prepends the shared memory marker and orders the block
/// after the memory section — this lane returns raw guidelines text only.
///
/// <para>Deliberately does NOT inspect <see cref="SessionStartMemoryContextRequest.Disabled"/>
/// or <c>GuidelinesDisabled</c> — the composite decides which lanes run, so
/// invoking this lane always means "guidelines are enabled for this session".</para>
/// </summary>
internal sealed class SessionStartGuidelinesLane(
    Func<CancellationToken, Task<HttpClient>> client,
    Action<string>? diagnostic = null) {

    /// <summary>
    /// Fetches and renders the guidelines fragment for an already-resolved
    /// <paramref name="scope"/>. The caller (composite) owns the budget-linked
    /// <paramref name="ct"/>.
    /// </summary>
    public async Task<SessionStartMemoryContextResult> FetchWithScopeAsync(
            SessionStartMemoryScope scope, SessionStartMemoryContextRequest request, CancellationToken ct) {
        // Guidelines are repo-scoped by definition: no repo ⇒ nothing to fetch.
        if (scope.RepoHash is null) return SessionStartMemoryContextResult.Empty;

        var outcome = await SessionStartContextFetch.FetchAsync(
            await client(ct), BuildUrl(request.BaseUrl, scope.RepoHash), ct);

        if (outcome.Status == HttpStatusCode.NoContent) return SessionStartMemoryContextResult.Empty;
        if (outcome.Status == HttpStatusCode.BadRequest) {
            diagnostic?.Invoke("SessionStart guidelines endpoint contract mismatch: HTTP 400.");
            return SessionStartMemoryContextResult.Empty;
        }
        // Guidelines visibility race: a 404 here is NOT "no facts" — the endpoint 404s until the
        // caller's own session projects (IsRepoVisibleAsync). Retry rather than mapping to Empty, so
        // a repeat-callback session picks up the guidelines once the read model catches up. (The
        // memory lane maps 404 → Empty; this deliberate divergence is the point of a separate lane.)
        if (outcome.Status == HttpStatusCode.NotFound)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        var response = JsonSerializer.Deserialize(outcome.Body,
            SessionStartMemoryJsonContext.Default.GuidelinesResponse);
        var rows = response?.Guidelines;
        if (rows is null || rows.Length == 0) return SessionStartMemoryContextResult.Empty;

        var fragment = SessionGuidelinesEmitter.BuildFragment(rows);
        return fragment is null
            ? SessionStartMemoryContextResult.Empty
            : new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment);
    }

    internal static string BuildUrl(string baseUrl, string repoHash) =>
        baseUrl.TrimEnd('/') + "/api/repositories/" + Uri.EscapeDataString(repoHash) + "/guidelines";
}
