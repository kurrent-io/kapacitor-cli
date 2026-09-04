namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Records the connected server's version from each response's
/// <see cref="HttpClientExtensions.ServerVersionHeader"/> into <see cref="ServerVersionStore"/>, so the
/// passive update notice and <c>kcap status</c> can cap their recommendation at it. Must sit OUTERMOST
/// so it observes the final response after a recovery resend; best-effort, and never alters either.
/// </summary>
internal sealed class ServerVersionCaptureHandler(string serverUrl, ConfigRoot config) : DelegatingHandler {
    public ServerVersionCaptureHandler(CapacitorServer server) : this(server.Url, server.Config) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var response = await base.SendAsync(request, ct);

        try {
            if (response.Headers.TryGetValues(HttpClientExtensions.ServerVersionHeader, out var values))
                ServerVersionStore.Set(serverUrl, values.FirstOrDefault(), config);
        } catch {
            // Header capture must never affect the response the caller gets back.
        }

        return response;
    }
}
