using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.Core.Http;

internal sealed class ProjectsApi(ICapacitorHttpClient http, CapacitorServer server) : IProjectsApi {
    public async Task<ProjectsResult> GetProjectsAsync(CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/projects", ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var projects = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListCliProjectSummary, ct);
            return new ProjectsResult.Found(projects ?? []);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) return new ProjectsResult.Forbidden(await TryReadErrorCodeAsync(response, ct));

        throw await FailureAsync(response);
    }

    public async Task<ProjectResult> GetProjectAsync(string slug, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/projects/{Uri.EscapeDataString(slug)}", ct: token), ct);

        if (response.IsSuccessStatusCode) {
            var project = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.CliProjectDetail, ct);
            return project is null ? new ProjectResult.NotFound() : new ProjectResult.Found(project);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden) return new ProjectResult.Forbidden(await TryReadErrorCodeAsync(response, ct));
        if (response.StatusCode == HttpStatusCode.NotFound) return new ProjectResult.NotFound();

        throw await FailureAsync(response);
    }

    /// <summary>The plan-gate body is a best-effort read for its "error" code alone — a body
    /// carrying no "message" (the coded 403 shape does not require one) must still be read, so this
    /// stays a loose <see cref="JsonDocument"/> lookup rather than the typed <see cref="CliProjectError"/>,
    /// whose required <c>Message</c> would throw on that shape.</summary>
    static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return doc.RootElement.Str("error");
        } catch (JsonException) {
            return null;
        }
    }

    Task<HttpResponseMessage> SendAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        CapacitorApiRequests.SendAsync(http, server, send, ct);

    static Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        CapacitorApiRequests.FailureAsync(response);
}
