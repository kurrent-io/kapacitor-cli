using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>RFC 8628 against AuthKit's <c>user_management</c> endpoints.</summary>
public class WorkOSDeviceFlowTests {
    static WireMockServer WithAuthorize(string body, int status = 200) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authorize/device").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    static void Authenticated(WireMockServer server, string body, int status = 200) =>
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    const string Device = """{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"","interval":0,"expires_in":900}""";

    /// <summary>
    /// Live response, 2026-08-20: WorkOS returns both, and verification_uri points at signin.kcap.ai
    /// rather than a workos.com host. The complete form pre-fills the code, so it is what a browser
    /// should open — while the bare one is what a human retypes on another device.
    /// </summary>
    [Test]
    public async Task The_complete_uri_is_what_a_browser_opens() {
        var device = new DeviceCodeResponse {
            DeviceCode      = "dc",
            UserCode        = "ZFVC-JDNH",
            VerificationUri = "https://signin.kcap.ai/device",
            VerificationUriComplete = "https://signin.kcap.ai/device?user_code=ZFVC-JDNH"
        };

        await Assert.That(device.BrowserUri).IsEqualTo("https://signin.kcap.ai/device?user_code=ZFVC-JDNH");

        var withoutComplete = device with { VerificationUriComplete = null };

        await Assert.That(withoutComplete.BrowserUri).IsEqualTo("https://signin.kcap.ai/device");
    }

    [Test]
    public async Task Completes_and_returns_the_full_token_envelope() {
        using var server = WithAuthorize(Device);
        Authenticated(server,
            """{"user":{"id":"user_x","first_name":"Ada"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt"}""");
        using var stub = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var result = await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", new RecordingBrowser(), progress: new RecordingAuthProgress());

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        // The refresh token is what makes the org switch possible afterwards.
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
        await Assert.That(result.OrganizationId).IsEqualTo("org_a");
        // The channel, which the browser picker reads to decide whether opening one is any use.
        await Assert.That(result.ViaDeviceGrant).IsTrue();
    }

    /// <summary>
    /// The whole ticket rests on this grant being enabled for the AuthKit application, and step 1 is
    /// where "it is not" shows up. A generic failure here sends people hunting through the rest of setup.
    /// </summary>
    [Test]
    public async Task Says_so_when_the_authorize_endpoint_refuses() {
        using var server   = WithAuthorize("""{"error":"unauthorized_client"}""", status: 400);
        using var stub     = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", new RecordingBrowser(), progress: progress);

        await Assert.That(result).IsNull();
        await Assert.That(string.Join("\n", progress.Errors)).Contains("may not be enabled");
        // Ours to know, not theirs: no provider name, no internal endpoint.
        await Assert.That(string.Join("\n", progress.Errors)).DoesNotContain("WorkOS");
        await Assert.That(string.Join("\n", progress.Errors)).DoesNotContain("workos.com");
    }

    [Test]
    public async Task Polls_through_authorization_pending() {
        using var server = WithAuthorize(Device);
        var       polls  = 0;

        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ => {
                polls++;

                // RFC 8628 puts a pending poll in a 400, unlike GitHub's 200 — the loop must read the
                // body rather than the status to tell them apart.
                return new WireMock.ResponseMessage {
                    StatusCode = polls < 3 ? 400 : 200,
                    BodyData = new WireMock.Util.BodyData {
                        DetectedBodyType = WireMock.Types.BodyType.String,
                        BodyAsString = polls < 3
                            ? """{"error":"authorization_pending"}"""
                            : """{"access_token":"acc","refresh_token":"rt"}"""
                    }
                };
            }));

        using var stub     = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", new RecordingBrowser(), progress: progress);

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(progress.PollTicks).IsEqualTo(2);
    }

    /// <summary>The code is meant to be carried to another device; a silent clipboard copy invites
    /// pasting it into whatever page is already open. §5.5.</summary>
    [Test]
    public async Task Does_not_decorate_the_code_with_a_clipboard_suffix() {
        using var server = WithAuthorize(Device);
        Authenticated(server, """{"access_token":"acc"}""");
        using var stub     = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", new RecordingBrowser(), progress: progress);

        await Assert.That(progress.DeviceCodes).Count().IsEqualTo(1);
        await Assert.That(progress.DeviceCodes[0].Code).IsEqualTo("WXYZ-1234");
    }

    /// <summary>
    /// The URL reported and the instruction under it have to agree, and the two ways of getting that
    /// wrong are symmetric: print the bare URL after opening the complete one and "check the code
    /// shown" lands on an empty box; print the complete one when nothing opened and "enter the code"
    /// describes a box already filled in.
    /// </summary>
    [Test]
    [Arguments(true,  "https://signin.example/device?user_code=WXYZ-1234", true)]
    [Arguments(false, "https://signin.example/device",                     false)]
    public async Task The_uri_it_reports_matches_the_instruction(bool opened, string expectedUri, bool expectedPrefilled) {
        using var server = WithAuthorize(
            """{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://signin.example/device","verification_uri_complete":"https://signin.example/device?user_code=WXYZ-1234","interval":0,"expires_in":900}""");
        Authenticated(server, """{"access_token":"acc"}""");
        using var stub     = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", new RecordingBrowser(opens: opened), progress: progress);

        await Assert.That(progress.DeviceCodes[0].Uri).IsEqualTo(expectedUri);
        await Assert.That(progress.DeviceCodes[0].Prefilled).IsEqualTo(expectedPrefilled);
        await Assert.That(string.Join("\n", progress.Notices)).Contains(expectedUri);
    }

    /// <summary>The complete URI is the one handed to the browser either way — it is only the printed
    /// line that changes.</summary>
    [Test]
    public async Task It_always_opens_the_prefilled_uri() {
        using var server = WithAuthorize(
            """{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://signin.example/device","verification_uri_complete":"https://signin.example/device?user_code=WXYZ-1234","interval":0,"expires_in":900}""");
        Authenticated(server, """{"access_token":"acc"}""");
        using var stub    = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       browser = new RecordingBrowser(opens: false);

        await OAuthLoginFlow.RunWorkOSDeviceFlowAsync(
            workos, "client_d", browser, progress: new RecordingAuthProgress());

        await Assert.That(browser.Urls).IsEquivalentTo(["https://signin.example/device?user_code=WXYZ-1234"]);
    }
}
