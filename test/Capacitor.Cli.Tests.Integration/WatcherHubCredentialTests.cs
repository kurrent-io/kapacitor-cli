using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The bearer the watcher's hub sends. SignalR asks its token provider once per connect attempt and
/// puts the answer on the negotiate request, so a stub that only answers negotiate is enough to see
/// what a real connect would have carried.
/// </summary>
public class WatcherHubCredentialTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    const string NegotiatePath = "/hubs/sessions/negotiate";

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    WatchCommand Watch(string? bearer) =>
        new(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home,
            new FixedCapacitorHttpClient(), new FixedCredentialSource(bearer));

    /// <summary>Refused, so the attempt ends at negotiate — which has already sent what we came for.</summary>
    void StubNegotiate() =>
        _server.Given(Request.Create().WithPath(NegotiatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

    async Task<WireMock.Logging.ILogEntry> NegotiateAfterConnectAsync(string? bearer) {
        StubNegotiate();

        await using var hub = Watch(bearer).BuildHubConnection($"{_server.Url}/hubs/sessions");

        // The connect is expected to fail: the credential rides on the negotiate request either way,
        // and letting it succeed would need a real hub to talk to.
        try {
            await hub.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        } catch (Exception ex) when (ex is not OperationCanceledException) { }

        return _server.FindLogEntries(Request.Create().WithPath(NegotiatePath).UsingPost())[0];
    }

    /// <summary>
    /// The watcher streams for the life of a session, so the credential it connects with is the one
    /// the source holds — not one read from the token store on its own.
    /// </summary>
    [Test]
    public async Task The_hub_connects_with_the_credential_sources_bearer() {
        var negotiate = await NegotiateAfterConnectAsync("tok_hub");

        await Assert.That(negotiate.RequestMessage.Headers!["Authorization"].Single())
            .IsEqualTo("Bearer tok_hub");
    }

    /// <summary>
    /// A server needing no auth resolves no bearer, and an absent header is what says so: sending
    /// <c>Bearer</c> with nothing after it would read as a malformed credential rather than none.
    /// </summary>
    [Test]
    public async Task No_credential_sends_no_authorization_header() {
        var negotiate = await NegotiateAfterConnectAsync(null);

        await Assert.That(negotiate.RequestMessage.Headers!.ContainsKey("Authorization")).IsFalse();
    }
}
