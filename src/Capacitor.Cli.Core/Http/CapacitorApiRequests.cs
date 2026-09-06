using System.Net;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The wire-error handling every API implementation in this namespace shares: a transport fault
/// becomes a status-less <see cref="CapacitorApiException"/>, and a non-success response becomes one
/// carrying the server's own text for a 401 or a generic status line otherwise. Callers act on a
/// response only when it is a success or a status they branch on; everything else goes through
/// <see cref="FailureAsync"/>.
/// </summary>
static class CapacitorApiRequests {
    public static async Task<HttpResponseMessage> SendAsync(
            ICapacitorHttpClient http, CapacitorServer server,
            Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) {
        using var client = await http.ForCommandAsync(ct);

        try {
            return await send(client, ct);
        } catch (HttpRequestException ex) {
            throw new CapacitorApiException(null, HttpClientExtensions.UnreachableErrorText(server.Url, ex), ex);
        }
    }

    public static async Task<CapacitorApiException> FailureAsync(HttpResponseMessage response) =>
        new((int)response.StatusCode,
            response.StatusCode == HttpStatusCode.Unauthorized
                ? await HttpClientExtensions.UnauthorizedMessageAsync(response)
                : $"Server returned HTTP {(int)response.StatusCode}");
}
