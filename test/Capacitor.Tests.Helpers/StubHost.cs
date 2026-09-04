namespace Capacitor.Tests.Helpers;

/// <summary>
/// Sends every request to one stub, whatever host the client addressed, keeping the method, path,
/// query and body. A client that pins its host offers no URL to override; substituting the stub
/// underneath it means what the stub records is what production sends.
/// </summary>
public sealed class StubHost : DelegatingHandler {
    readonly Uri _stub;

    public StubHost(string baseUrl) {
        _stub        = new Uri(baseUrl);
        InnerHandler = new HttpClientHandler();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        request.RequestUri = new Uri(_stub, request.RequestUri!.PathAndQuery);

        return base.SendAsync(request, ct);
    }
}
