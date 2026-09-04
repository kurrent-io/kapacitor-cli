using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration.Config;

/// <summary>
/// End-to-end verification that the default HTTP probe issued by
/// <see cref="ServerUrlNormalizer.NormalizeAsync"/> reaches a real HTTP server
/// (here, WireMock) and resolves the scheme correctly.
/// </summary>
public class ServerUrlProbeIntegrationTests : IDisposable {
    static readonly Func<string, TimeSpan, CancellationToken, Task<bool>> Probe =
        ServerUrlNormalizer.ProbeWith(new FixedCapacitorHttpClient());

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SchemeMissing_HttpServerResponds_PicksHttp() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var port  = new Uri(_server.Url!).Port;
        var input = $"localhost:{port}";

        var result = await ServerUrlNormalizer.NormalizeAsync(
            input, skipProbe: false, CancellationToken.None, Probe);

        await Assert.That(result.Url).IsEqualTo($"http://localhost:{port}");
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task SchemePresent_ProbeHitsAuthConfig() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var result = await ServerUrlNormalizer.NormalizeAsync(
            _server.Url!,
            skipProbe: false,
            CancellationToken.None,
            Probe
        );

        await Assert.That(result.Url).IsEqualTo(_server.Url!.TrimEnd('/'));
        await Assert.That(result.Warning).IsNull();

        var calls = _server.FindLogEntries(Request.Create().WithPath("/auth/config").UsingGet());
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task UnreachableHost_FallsBackToLoopbackDefault_Warns() {
        // 127.0.0.1:1 is reserved (tcpmux) and reliably unbound on dev machines.
        var input = "127.0.0.1:1";

        var result = await ServerUrlNormalizer.NormalizeAsync(
            input,
            skipProbe: false,
            CancellationToken.None,
            Probe
        );

        await Assert.That(result.Url).IsEqualTo("http://127.0.0.1:1");
        await Assert.That(result.Warning).IsNotNull();
    }
}
