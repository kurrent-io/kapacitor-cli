namespace Capacitor.Tests.Helpers;

/// <summary>
/// An unconfigured client per call, whatever the lane is named — what the production factory itself
/// yields for a name nobody registered. A test that needs to script the responses passes a handler.
/// </summary>
public sealed class PlainHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory {
    public HttpClient CreateClient(string name) => handler is null ? new() : new(handler, disposeHandler: false);
}
