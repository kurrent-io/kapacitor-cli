using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

internal sealed class SessionsApi(ICapacitorHttpClient http, CapacitorServer server) : ISessionsApi {
    public async Task<DeleteSessionResponse> DeleteSessionAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.DeleteWithRetryAsync($"{server.Url}/api/sessions/{sessionId}", ct: token), ct);

        if (response.IsSuccessStatusCode) return new DeleteSessionResponse.Deleted();
        if (response.StatusCode == HttpStatusCode.NotFound) return new DeleteSessionResponse.NotFound();

        throw await FailureAsync(response);
    }

    public async Task HideSessionAsync(string sessionId, CancellationToken ct = default) {
        using var body = Json(new JsonObject { ["visibility"] = "none" });
        using var response = await SendAsync(
                (c, token) => c.PutWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/visibility", body, ct: token), ct);

        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);
    }

    public async Task SetSessionTitleAsync(string sessionId, string title, CancellationToken ct = default) {
        using var body = Json(new JsonObject { ["session_id"] = sessionId, ["title"] = title });
        using var response = await SendAsync((c, token) => c.PostWithRetryAsync($"{server.Url}/hooks/set-title", body, ct: token), ct);

        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);
    }

    public async Task<ErrorsResult> GetErrorsAsync(string sessionId, bool chain, CancellationToken ct = default) {
        var query = chain ? "?chain=true" : "";
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/errors{query}", ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var errors = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListErrorEntry, ct);
            return new ErrorsResult.Found(errors ?? []);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new ErrorsResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<RecapResult> GetRecapAsync(string sessionId, bool chain, CancellationToken ct = default) {
        var query = chain ? "?chain=true" : "";
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/recap{query}", ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var entries = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListRecapEntry, ct);
            return new RecapResult.Found(entries ?? []);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new RecapResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<TurnsResult> GetTurnsAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/turns", ct: token), ct);

        if (response.IsSuccessStatusCode) return new TurnsResult.Found(await response.Content.ReadAsStringAsync(ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return new TurnsResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<TurnDetailResult> GetTurnAsync(string sessionId, int turnIndex, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/turns/{turnIndex}", ct: token), ct);

        if (response.IsSuccessStatusCode) return new TurnDetailResult.Found(await response.Content.ReadAsStringAsync(ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return new TurnDetailResult.NotFound();

        throw await FailureAsync(response);
    }

    public async Task<PlanArtifactsResult> GetPlanArtifactsAsync(string sessionId, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/sessions/{sessionId}/plan-artifacts?chain=true", ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var dto = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PlanArtifactsResponseDto, ct);
            return new PlanArtifactsResult.Found(dto);
        }
        if (response.StatusCode == HttpStatusCode.NotFound) return new PlanArtifactsResult.NotFound();

        throw await FailureAsync(response);
    }

    static StringContent Json(JsonObject payload) => new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    Task<HttpResponseMessage> SendAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        CapacitorApiRequests.SendAsync(http, server, send, ct);

    static Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        CapacitorApiRequests.FailureAsync(response);
}
