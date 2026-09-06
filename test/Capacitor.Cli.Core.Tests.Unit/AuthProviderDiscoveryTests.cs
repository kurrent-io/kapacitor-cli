using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="AuthProviderDiscovery"/>'s in-process memo. It sits in front of
/// <see cref="AuthProviderCache"/>'s on-disk store, so if it answers for the wrong server the
/// correctly-keyed disk lookup never gets a chance to.
/// </summary>
public class AuthProviderDiscoveryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _a = WireMockServer.Start();
    readonly WireMockServer _b = WireMockServer.Start();

    // One per test, which is the whole isolation story: the memo lives here rather than on a static,
    // so a value cached by one test cannot be observed by another.
    readonly AuthProviderDiscovery _discovery = new(new PlainHttpClientFactory());

    public void Dispose() {
        _a.Stop();
        _b.Stop();
    }

    static void StubProvider(WireMockServer server, string provider) =>
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"provider\":\"{provider}\"}}"));

    Task<string> Discover(WireMockServer server) =>
        _discovery.DiscoverAsync(
                server.Urls[0], Config.Root, Resolutions.At(server.Urls[0], Config.Root),
                AuthFixtures.NewTokenStore(Config.Root));

    /// <summary>
    /// Two servers in one process must each get their own answer. `kcap setup` reaches this whenever
    /// auth returns a Retarget: it resolves one server, then resolves the tenant it was pointed at.
    /// </summary>
    [Test]
    public async Task Discovery_is_memoized_per_base_url() {
        StubProvider(_a, "WorkOS");
        StubProvider(_b, "GitHub");

        await Assert.That(await Discover(_a)).IsEqualTo("WorkOS");
        await Assert.That(await Discover(_b)).IsEqualTo("GitHub")
            .Because("a memo shared across base URLs hands the first server's provider to the second");
    }

    /// <summary>The memo must still spare a repeat call its round trip.</summary>
    [Test]
    public async Task A_repeat_discovery_against_one_server_does_not_probe_twice() {
        StubProvider(_a, "WorkOS");

        await Discover(_a);
        await Discover(_a);

        await Assert.That(_a.LogEntries.Count).IsEqualTo(1)
            .Because("the memo exists to skip the /auth/config round trip");
    }
}
