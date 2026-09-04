namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Our own server's session-scoped endpoints, so no caller composes a URL or reads a status code. Only
/// outcomes a caller acts on differently are returned; a refusal or an unreachable server is an
/// exception — <see cref="CapacitorApiException"/> and <see cref="HttpRequestException"/> respectively.
/// </summary>
public interface ISessionsApi {
    Task<DeleteSessionResponse> DeleteSessionAsync(string sessionId, CancellationToken ct = default);

    Task HideSessionAsync(string sessionId, CancellationToken ct = default);

    Task SetSessionTitleAsync(string sessionId, string title, CancellationToken ct = default);

    Task<ErrorsResult> GetErrorsAsync(string sessionId, bool chain, CancellationToken ct = default);

    Task<RecapResult> GetRecapAsync(string sessionId, bool chain, CancellationToken ct = default);

    Task<TurnsResult> GetTurnsAsync(string sessionId, CancellationToken ct = default);

    Task<TurnDetailResult> GetTurnAsync(string sessionId, int turnIndex, CancellationToken ct = default);

    /// <summary>Always chain-widened — the discovered plan set spans the session's whole chain.</summary>
    Task<PlanArtifactsResult> GetPlanArtifactsAsync(string sessionId, CancellationToken ct = default);
}
