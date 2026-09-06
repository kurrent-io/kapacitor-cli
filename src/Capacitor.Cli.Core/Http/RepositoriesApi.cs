using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.Http;

internal sealed class RepositoriesApi(ICapacitorHttpClient http, CapacitorServer server) : IRepositoriesApi {
    public async Task<List<RepoRecapEntry>> GetRecapsAsync(string repoHash, int limit, CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/repositories/{repoHash}/recaps?limit={limit}", ct: token), ct);

        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);

        var entries = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListRepoRecapEntry, ct);

        return entries ?? [];
    }

    public async Task<CurationResult> GetPromotedCurationAsync(string repoHash, int limit, CancellationToken ct = default) {
        using var response = await SendAsync(
            (c, token) => c.GetWithRetryAsync(
                $"{server.Url}/api/repositories/{repoHash}/curation?status=promoted&minWeight=1&limit={limit}", ct: token), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return new CurationResult.NotFound();
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);

        var dto = await ReadAsync(response, CapacitorJsonContext.Default.CurationApplyResponse,
                                  "Malformed response from server (could not parse curation payload).", ct);

        return new CurationResult.Found(dto.Items ?? []);
    }

    public async Task<SkillsSnapshotResult> GetSkillsSnapshotAsync(
            string repoHash, string? vendor, string? etag, CancellationToken ct = default) {
        var url = $"{server.Url}/api/repositories/{repoHash}/skills"
                + (vendor is { } v ? $"?vendor={v}" : "?")
                + (HostPlatform.Normalized is { } platform ? $"&platform={platform}" : "");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (etag is { Length: > 0 }) request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{etag}\"");

        using var response = await SendAsync((c, token) => c.SendAsync(request, token), ct);

        if (response.StatusCode == HttpStatusCode.NotModified) return new SkillsSnapshotResult.NotModified();
        if (response.StatusCode == HttpStatusCode.NotFound)    return new SkillsSnapshotResult.NotFound();
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response);

        var dto = await ReadAsync(response, CapacitorJsonContext.Default.SkillsSnapshotResponse,
                                  "Malformed response from server (could not parse skills snapshot).", ct);

        return new SkillsSnapshotResult.Found(dto);
    }

    /// <summary>A body that will not parse — or parses to nothing — is a failed call, not an empty
    /// result: the caller cannot tell "the server sent no skills" from "the server sent something we
    /// could not read", and acting on the first when it was the second deletes what it owns.
    ///
    /// <para>Parses the text rather than the content, so a JSON body labelled with some other media
    /// type still reads.</para></summary>
    static async Task<T> ReadAsync<T>(
            HttpResponseMessage response, JsonTypeInfo<T> type, string malformed, CancellationToken ct) {
        T? value;

        try {
            value = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), type);
        } catch (JsonException) {
            value = default;
        }

        return value ?? throw new CapacitorApiException((int)response.StatusCode, malformed);
    }

    Task<HttpResponseMessage> SendAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        CapacitorApiRequests.SendAsync(http, server, send, ct);

    static Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        CapacitorApiRequests.FailureAsync(response);
}
