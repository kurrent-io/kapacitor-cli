using System.Net;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The passive capture path: a response carrying <c>X-Kcap-Server-Version</c> lands in
/// <see cref="ServerVersionStore"/>; one without it captures nothing. Drives the internal
/// <c>ServerVersionCaptureHandler</c> over a stub inner handler (no network).
/// </summary>
public class ServerVersionCaptureHandlerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    static async Task SendThrough(string serverUrl, HttpResponseMessage response, ConfigRoot config) {
        var capture = new ServerVersionCaptureHandler(serverUrl, config) { InnerHandler = new StubHandler(response) };
        using var client = new HttpClient(capture);
        using var _ = await client.GetAsync(serverUrl);
    }

    [Test]
    public async Task Response_WithHeader_CapturesServerVersion() {
        var url      = $"https://cap-{Guid.NewGuid():N}.example.com";
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add(HttpClientExtensions.ServerVersionHeader, "0.11.15");

        await SendThrough(url, response, Config.Root);

        await Assert.That(ServerVersionStore.Get(url, Config.Root)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Response_WithoutHeader_CapturesNothing() {
        var url      = $"https://cap-{Guid.NewGuid():N}.example.com";
        var response = new HttpResponseMessage(HttpStatusCode.OK);

        await SendThrough(url, response, Config.Root);

        await Assert.That(ServerVersionStore.Get(url, Config.Root)).IsNull();
    }
}
