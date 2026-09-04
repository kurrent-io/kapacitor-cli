using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryContextProvider(
    ISessionStartMemoryScopeResolver scopeResolver,
    Func<CancellationToken, Task<HttpClient>> client,
    Action<string>? diagnostic = null) : ISessionStartContextProvider {

    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        if (request.Disabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        cts.CancelAfter(request.Budget);
        try {
            var scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
            return await FetchWithScopeAsync(scope, request, cts.Token);
        } catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                                     OperationCanceledException or UnauthorizedAccessException or InvalidDataException) {
            diagnostic?.Invoke($"SessionStart memory fetch skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    /// <summary>
    /// Fetches and renders the memory-index fragment for an already-resolved
    /// <paramref name="scope"/>. Separated from <see cref="GetAsync"/> so
    /// <see cref="SessionStartCompositeContextProvider"/> can resolve the scope
    /// ONCE and drive both lanes with it, instead of walking the repo twice
    /// (matters under Cursor's tight budget). The caller owns the
    /// budget-linked <paramref name="ct"/> and the fail-open exception handling.
    /// </summary>
    public async Task<SessionStartMemoryContextResult> FetchWithScopeAsync(
            SessionStartMemoryScope scope, SessionStartMemoryContextRequest request, CancellationToken ct) {
        var outcome = await SessionStartContextFetch.FetchAsync(
            await client(ct), BuildUrl(request.BaseUrl, scope), ct);

        if (outcome.Status == HttpStatusCode.NoContent) return SessionStartMemoryContextResult.Empty;
        if (outcome.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) {
            diagnostic?.Invoke($"SessionStart memory endpoint contract mismatch: HTTP {(int)outcome.Status}.");
            return SessionStartMemoryContextResult.Empty;
        }
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        var index = ParseIndex(outcome.Body);
        if (index is null) return SessionStartMemoryContextResult.Retry;
        var entries  = index.Entries  ?? [];
        var projects = index.Projects ?? [];
        if (entries.Length == 0 && projects.Length == 0) return SessionStartMemoryContextResult.Empty;
        var fragment = MemoryIndexEmitter.BuildFragment(entries, projects);
        return fragment is null
            ? SessionStartMemoryContextResult.Empty
            : new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment);
    }

    /// <summary>
    /// Reads the index body in either shape. A server that predates <c>include=projects</c> ignores
    /// the parameter and answers with the bare entry array; one that honours it answers with an
    /// object carrying the same entries plus the repo's projects. The shape is decided by sniffing
    /// the opening token, not by attempting one parse and falling back: a failed deserialize is
    /// indistinguishable from a corrupt body, which is a retryable failure, and the retry schedule
    /// has no attempt ceiling — so an old server would be polled indefinitely for an answer it is
    /// never going to give differently.
    /// </summary>
    static SessionStartMemoryIndexResponse? ParseIndex(byte[] body) {
        var reader = new Utf8JsonReader(body);
        if (!reader.Read()) return null;
        return reader.TokenType switch {
            JsonTokenType.StartArray => JsonSerializer.Deserialize(body,
                SessionStartMemoryJsonContext.Default.SessionStartMemoryEntryArray) is { } entries
                    ? new SessionStartMemoryIndexResponse(entries, null)
                    : null,
            // An object carrying neither member is not an empty index, it is not an index at all:
            // treating it as one would report a successful empty fetch and SPEND the once-per-session
            // lease, so no later callback of that session ever asks again. Empty arrays are a real
            // answer and stay distinct from absent ones.
            JsonTokenType.StartObject => JsonSerializer.Deserialize(body,
                SessionStartMemoryJsonContext.Default.SessionStartMemoryIndexResponse) is { } response &&
                (response.Entries is not null || response.Projects is not null)
                    ? response
                    : null,
            _ => null
        };
    }

    /// <summary>
    /// <c>include=projects</c> rides every call, with or without a resolved repo: it declares that
    /// THIS CLI can read the object body, not that a repo is in hand. Making it conditional would
    /// give the server two shapes to answer one request with.
    /// </summary>
    internal static string BuildUrl(string baseUrl, SessionStartMemoryScope scope) {
        var query = new List<string>();
        if (scope.RepoHash is not null) query.Add("repo=" + Uri.EscapeDataString(scope.RepoHash));
        if (scope.MachineTag is not null) query.Add("machine=" + Uri.EscapeDataString(scope.MachineTag));
        query.Add("include=projects");
        return baseUrl.TrimEnd('/') + "/api/memories/index?" + string.Join('&', query);
    }
}
