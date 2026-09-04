using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The WorkOS lane as the container actually configures it. A client built by hand follows redirects,
/// so nothing short of resolving the registered one proves what the registration contributes.
/// </summary>
public class WorkOSClientTests : IDisposable {
    readonly WireMockServer  _server = WireMockServer.Start();
    readonly ServiceProvider _sp     = new ServiceCollection().AddCapacitorForeignClients().BuildServiceProvider();

    public void Dispose() {
        _server.Stop();
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A 307 answered before the token exists is followed, and the mint still succeeds. The endpoint
    /// is an operator override defaulting to our own sign-in host, so a scheme or path normalisation
    /// in front of it is the deployment's business, not a rejection of the credential. A 307 preserves
    /// method and body, which is what makes the second leg a mint rather than a bare GET.
    /// </summary>
    [Test]
    public async Task A_redirect_before_the_token_is_followed_and_still_mints() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/oauth2/token/regional"));

        _server.Given(Request.Create().WithPath("/oauth2/token/regional").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_after_hop","expires_in":3600}"""));

        var result = await _sp.GetRequiredService<WorkOSClient>().MintAsync(
            new MachineCredential("client_01ABC", "sekrit"),
            $"{_server.Urls[0]}/oauth2/token",
            CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_after_hop");
    }

    /// <summary>A mint reports the endpoint's status without ever quoting the body it came with.</summary>
    [Test]
    public async Task A_rejected_mint_reports_the_status_and_not_the_body() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"echo":"client_secret=sekrit"}"""));

        var result = await _sp.GetRequiredService<WorkOSClient>().MintAsync(
            new MachineCredential("client_01ABC", "sekrit"),
            $"{_server.Urls[0]}/oauth2/token",
            CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("403");
        await Assert.That(result.Problem!).DoesNotContain("sekrit");
    }
}
