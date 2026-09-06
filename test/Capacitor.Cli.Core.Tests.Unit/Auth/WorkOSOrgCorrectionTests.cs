using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Check-then-correct. The device grant's authorize leg takes no organization_id, so the
/// human picks at the AuthKit screen and the CLI cannot constrain it; the token that comes back names
/// whichever org they chose.
/// </summary>
public class WorkOSOrgCorrectionTests {
    static WireMockServer Switching(string body, int status = 200) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    static WorkOSAuthResponse SignedInTo(string org) => new() {
        AccessToken    = "wrong-org-token",
        RefreshToken   = "rt",
        OrganizationId = org,
        User           = new WorkOSUserInfo { Id = "user_x", FirstName = "Ada", LastName = "Lovelace" }
    };

    [Test]
    public async Task Moves_the_session_onto_the_tenants_org() {
        using var server = Switching("""{"access_token":"right","refresh_token":"rt2","organization_id":"org_wanted"}""");
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var corrected = await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            workos, "client_d", SignedInTo("org_picked"), "org_wanted", CancellationToken.None);

        await Assert.That(corrected!.AccessToken).IsEqualTo("right");
        await Assert.That(corrected.OrganizationId).IsEqualTo("org_wanted");
    }

    /// <summary>The refresh_token grant answers without a user, so the display name would otherwise
    /// collapse from the signed-in human to "unknown".</summary>
    [Test]
    public async Task Carries_the_signed_in_user_across_the_switch() {
        using var server = Switching("""{"access_token":"right","organization_id":"org_wanted"}""");
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var corrected = await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            workos, "client_d", SignedInTo("org_picked"), "org_wanted", CancellationToken.None);

        await Assert.That(OAuthLoginFlow.WorkOSDisplayName(corrected!.User)).IsEqualTo("Ada Lovelace");
    }

    /// <summary>A 200 naming a different organization has corrected nothing; storing it would leave a
    /// token the server rejects on every call.</summary>
    [Test]
    public async Task Refuses_a_switch_that_lands_somewhere_else() {
        using var server = Switching("""{"access_token":"right","organization_id":"org_other"}""");
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var corrected = await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            workos, "client_d", SignedInTo("org_picked"), "org_wanted", CancellationToken.None);

        await Assert.That(corrected).IsNull();
    }

    /// <summary>Membership is what gates the switch, so a user with no claim to the org lands here.</summary>
    [Test]
    public async Task Refuses_when_the_switch_is_denied() {
        using var server = Switching("""{"error":"invalid_grant"}""", status: 401);
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var corrected = await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            workos, "client_d", SignedInTo("org_picked"), "org_wanted", CancellationToken.None);

        await Assert.That(corrected).IsNull();
    }

    /// <summary>
    /// The whole ladder is a public-client flow: the CLI ships no secret and could not send one. WorkOS
    /// documents client_secret as required on the refresh_token grant, and ours works without it — so
    /// pin the request shape, because a change here breaks the correction with a 401 that reads like a
    /// membership problem.
    /// </summary>
    [Test]
    public async Task Switches_without_a_client_secret() {
        using var server = Switching("""{"access_token":"right","organization_id":"org_wanted"}""");
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            workos, "client_d", SignedInTo("org_picked"), "org_wanted", CancellationToken.None);

        var body = server.FindLogEntries(
            Request.Create().WithPath("/user_management/authenticate").UsingPost())[0].RequestMessage.Body!;

        await Assert.That(body).DoesNotContain("client_secret");
        await Assert.That(body).Contains("grant_type=refresh_token");
        await Assert.That(body).Contains("organization_id=org_wanted");
    }

    [Test]
    public async Task Refuses_when_there_is_no_refresh_token_to_switch_with() {
        // No stub: the switch is refused before a request is built.
        var corrected = await OAuthLoginFlow.CorrectWorkOSOrgAsync(
            new WorkOSClient(new PlainHttpClientFactory()), "client_d",
            SignedInTo("org_picked") with { RefreshToken = null }, "org_wanted", CancellationToken.None);

        await Assert.That(corrected).IsNull();
    }
}
