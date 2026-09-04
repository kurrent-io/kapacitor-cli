using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// The in-process memo sitting in front of <c>AuthProviderCache</c>'s on-disk store. It
/// exists to spare a repeat lookup the <c>/auth/config</c> round trip.
/// </summary>
// The memo is process-global, so no peer may run while this test populates and reads it.
[NotInParallel]
public class AuthProviderDiscoveryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task A_repeat_discovery_against_one_server_does_not_probe_twice() {
        HttpClientExtensions.ResetProviderCacheForTesting();

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"WorkOS"}"""));

        var profiles = Resolutions.At(_server.Urls[0], Config.Root);

        await HttpClientExtensions.DiscoverProviderAsync(_server.Urls[0], Config.Root, profiles);
        await HttpClientExtensions.DiscoverProviderAsync(_server.Urls[0], Config.Root, profiles);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(1)
            .Because("the memo exists to skip the /auth/config round trip");
    }
}
