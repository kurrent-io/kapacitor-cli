using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// The registered client, driven against a stub registry. <c>Reached</c> is the distinction the
/// caller's backoff rests on: a registry that answered without naming a version has not failed, and
/// arming a backoff for it would suppress the next check for an hour over a well-formed answer.
/// </summary>
public class NpmRegistryClientTests : IDisposable {
    readonly WireMockServer   _server = WireMockServer.Start();
    readonly ServiceProvider  _sp;

    /// The production registration, re-pointed at the stub. AddHttpClient configuration is additive,
    /// so the registry's own 5 s cap and agent name still apply to what these tests drive.
    public NpmRegistryClientTests() =>
        _sp = new ServiceCollection()
            .AddCapacitorForeignClients()
            .AddHttpClient<NpmRegistryClient>(c => c.BaseAddress = new Uri(_server.Url! + "/"))
            .Services.BuildServiceProvider();

    NpmRegistryClient Npm => _sp.GetRequiredService<NpmRegistryClient>();

    public void Dispose() {
        _sp.Dispose();
        _server.Stop();
    }

    void Stub(string channel, int status, string? body = null) {
        var response = Response.Create().WithStatusCode(status);
        if (body is not null) response = response.WithBody(body);

        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet()).RespondWith(response);
    }

    [Test]
    public async Task A_published_version_is_read_off_the_dist_tag() {
        Stub("latest", 200, """{"version":"1.2.3"}""");

        var result = await Npm.GetDistTagAsync("latest", CancellationToken.None);

        await Assert.That(result.Reached).IsTrue();
        await Assert.That(result.Version).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task An_answer_naming_no_version_is_still_an_answer() {
        Stub("latest", 200, """{"dist-tags":{}}""");

        var result = await Npm.GetDistTagAsync("latest", CancellationToken.None);

        await Assert.That(result.Reached).IsTrue()
            .Because("a backoff armed here would suppress the next check over a well-formed reply");
        await Assert.That(result.Version).IsNull();
    }

    [Test]
    public async Task A_non_success_status_did_not_reach_the_registry() {
        Stub("latest", 503);

        var result = await Npm.GetDistTagAsync("latest", CancellationToken.None);

        await Assert.That(result.Reached).IsFalse();
    }

    [Test]
    public async Task An_unreadable_body_did_not_reach_the_registry() {
        Stub("latest", 200, "not json");

        var result = await Npm.GetDistTagAsync("latest", CancellationToken.None);

        await Assert.That(result.Reached).IsFalse();
    }

    /// <summary>The agent name is set at registration now, so nothing on the calling path would
    /// notice it going missing.</summary>
    [Test]
    public async Task The_registered_client_names_itself_to_the_registry() {
        Stub("latest", 200, """{"version":"1.2.3"}""");

        await Npm.GetDistTagAsync("latest", CancellationToken.None);

        var sent = _server.LogEntries.Single().RequestMessage;

        await Assert.That(sent.Headers!["User-Agent"].Single()).IsEqualTo("kcap-cli");
    }
}
