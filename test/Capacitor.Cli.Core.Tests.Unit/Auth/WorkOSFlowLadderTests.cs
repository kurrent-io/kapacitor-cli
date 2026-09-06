using System.Net;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>The WorkOS sign-in ladder: loopback, escape hatch, automatic fallback.</summary>
public class WorkOSFlowLadderTests {
    const string Device = """{"device_code":"dc","user_code":"WXYZ-1234","verification_uri":"https://signin.example/device","interval":0,"expires_in":900}""";

    /// <summary>
    /// Every device-rung test needs one. Nothing defaults to the system browser any more, so a fixture
    /// carrying a real https verification_uri opens actual tabs on a developer's machine.
    /// </summary>
    static WireMockServer DeviceGrantServer(string authenticate = """{"access_token":"acc","refresh_token":"rt","organization_id":"org_a"}""") {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authorize/device").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Device));
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(authenticate));

        return server;
    }

    /// <summary>
    /// The load-bearing claim of §5's ladder: nothing but an explicit request skips the browser. There
    /// is no environment input at all, because IsHeadless() is true for every SSH session including
    /// the ones where loopback works perfectly.
    /// </summary>
    [Test]
    [Arguments(false, WorkOSFlow.Browser)]
    [Arguments(true,  WorkOSFlow.Device)]
    public async Task Only_an_explicit_request_skips_loopback(bool forceDevice, WorkOSFlow expected) =>
        await Assert.That(OAuthLoginFlow.ChooseWorkOSFlow(forceDevice)).IsEqualTo(expected);

    /// <summary>
    /// The rung above the ladder, and the one thing that is decided before it: a console whose stdin is
    /// redirected cannot press <c>d</c>, and if a browser launches without being able to reach us at
    /// 127.0.0.1 there is nothing left but the listener timeout. Deliberately not read off the key
    /// watcher inside the ladder - a GUI has no keyboard either and must keep its browser.
    /// </summary>
    [Test]
    [Arguments(false, true,  false)]
    [Arguments(true,  true,  true)]
    [Arguments(false, false, true)]
    [Arguments(true,  false, true)]
    public async Task A_console_with_no_keyboard_takes_the_device_route(
            bool userAsked, bool consoleHasKeyboard, bool expected) =>
        await Assert.That(OAuthLoginFlow.DeviceRouteRequired(userAsked, consoleHasKeyboard)).IsEqualTo(expected);

    /// <summary>
    /// The hint is withheld rather than reworded when there is no keyboard: any wording of it names an
    /// action the default no-keyboard host - a GUI - cannot take.
    /// </summary>
    [Test]
    [Arguments(true, 1)]
    [Arguments(false, 0)]
    public async Task The_browser_offers_the_hint_only_when_one_is_supplied(bool withHint, int expectedNotices) {
        var progress = new RecordingAuthProgress();
        var browser  = new LoopbackBrowser(
            launcher: new RecordingBrowser(), progress: progress,
            hint: withHint ? OAuthLoginFlow.WorkOSBrowserHint() : null);

        var       port = OAuthLoginFlow.GetAvailablePort();
        using var cts  = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancelled up front: the announce happens before the wait, so this exercises the output and
        // then leaves rather than sitting on the listener.
        await Assert.That(async () => await browser.InvokeAsync(
                  new Duende.IdentityModel.OidcClient.Browser.BrowserOptions(
                      "https://example.test/authorize", $"http://127.0.0.1:{port}/callback") {
                      Timeout = TimeSpan.FromMinutes(5)
                  }, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(progress.BrowserOpenings).Count().IsEqualTo(1);
        await Assert.That(progress.Notices).Count().IsEqualTo(expectedNotices);
        await Assert.That(OAuthLoginFlow.WorkOSBrowserHint()).Contains("press d");
    }

    [Test]
    public async Task Explicit_device_request_never_opens_a_browser() {
        using var server  = DeviceGrantServer();
        using var stub    = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       browser = new FakeBrowser(_ => throw new InvalidOperationException("the browser must not be invoked"));

        var opener = new RecordingBrowser(opens: false);

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: true, opener, browser,
            progress: new RecordingAuthProgress(), keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        // The device page, never the authorize URL: "opens no browser" means no LOOPBACK browser.
        await Assert.That(opener.Urls).IsEquivalentTo(["https://signin.example/device"]);
    }

    [Test]
    public async Task The_escape_hatch_abandons_the_browser_for_the_device_grant() {
        using var server   = DeviceGrantServer();
        using var stub     = new StubHost(server.Urls[0]);
        var       workos   = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       keys     = new ScriptedKeyWatcher('d');
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: false, new RecordingBrowser(opens: false), new HangingBrowser(),
            progress: progress, keys: keys);

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(progress.DeviceCodes).Count().IsEqualTo(1);
    }

    /// <summary>
    /// Consumes whatever was already buffered alongside the <c>d</c>.
    ///
    /// <para><b>This is not what protects the next prompt, and believing it was cost a live bug.</b>
    /// The Return usually lands *after* this drain and then waits out the entire device-code approval,
    /// so the prompt is protected by draining at the prompt (<c>PromptHygiene.DiscardTypeAhead</c>),
    /// not here. No unit test covers that one: it needs a real terminal buffer, so it lives in
    /// flows-to-test.md.</para>
    /// </summary>
    [Test]
    public async Task Drains_what_is_still_buffered_before_handing_off() {
        using var server = DeviceGrantServer();
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       keys   = new ScriptedKeyWatcher('d', '\r', '\n');

        await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: false, new RecordingBrowser(opens: false), new HangingBrowser(),
            progress: new RecordingAuthProgress(), keys: keys);

        await Assert.That(keys.Drained).IsEqualTo(2);
        await Assert.That(keys.KeyAvailable).IsFalse();
    }

    /// <summary>Mirrors the GitHub arm: a browser flow that RAN and failed is an answer, not a reason
    /// to re-ask through another channel.</summary>
    [Test]
    public async Task A_cancelled_browser_sign_in_does_not_fall_through_to_the_device_grant() {
        using var server = DeviceGrantServer();
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: false, new RecordingBrowser(),
            FakeBrowser.NonSuccess(Duende.IdentityModel.OidcClient.Browser.BrowserResultType.UserCancel),
            progress: new RecordingAuthProgress(), keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result).IsNull();
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("authorize/device"))).IsFalse();
    }

    /// <summary>The third rung, and it is free: OAuthFlowTests pins that the bind failure propagates
    /// out of OidcClient rather than being folded into an error result.</summary>
    [Test]
    public async Task A_loopback_bind_failure_falls_through_to_the_device_grant() {
        using var server   = DeviceGrantServer();
        using var stub     = new StubHost(server.Urls[0]);
        var       workos   = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: false, new RecordingBrowser(opens: false),
            new FakeBrowser(_ => throw new HttpListenerException(5, "Access is denied")),
            progress: progress, keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(string.Join("\n", progress.Errors)).Contains("Could not bind loopback listener");
    }

    /// <summary>
    /// Found by driving it: opening the printed authorize URL from a browser on another machine
    /// completes the WorkOS half and then dies on `127.0.0.1:NNNNN` - connection refused - because the
    /// listener is in the container. Failing to launch a browser HERE is the evidence that the user's
    /// browser is elsewhere, so the loopback URL is worthless to them and a device code is not.
    /// </summary>
    [Test]
    public async Task No_browser_on_this_machine_falls_through_to_the_device_grant() {
        using var server   = DeviceGrantServer();
        using var stub     = new StubHost(server.Urls[0]);
        var       workos   = new WorkOSClient(new PlainHttpClientFactory(stub));
        var       progress = new RecordingAuthProgress();

        var result = await OAuthLoginFlow.AcquireWorkOSAsync(
            workos, "client_d", organizationId: null, forceDevice: false,
            new RecordingBrowser(opens: false), new FakeBrowser(_ => throw new BrowserLaunchException()),
            progress: progress, keys: ScriptedKeyWatcher.Blind());

        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(string.Join("\n", progress.Notices)).Contains("use anywhere");
        // Reported as news, not as a failure: nothing went wrong, the route just changed.
        await Assert.That(progress.Errors).IsEmpty();
    }

    /// <summary>
    /// The listener never waits out its timeout for a callback nothing can send, and says nothing at
    /// all on the way out: announcing before launching would print the browser narrative and a
    /// 300-character authorize URL, then retract both.
    /// </summary>
    [Test]
    public async Task The_loopback_browser_gives_up_silently_when_it_cannot_launch() {
        var progress = new RecordingAuthProgress();
        var browser  = new LoopbackBrowser(new RecordingBrowser(opens: false), progress);
        var port     = OAuthLoginFlow.GetAvailablePort();

        await Assert.That(async () => await browser.InvokeAsync(
                  new Duende.IdentityModel.OidcClient.Browser.BrowserOptions(
                      "https://example.test/authorize", $"http://127.0.0.1:{port}/callback") {
                      Timeout = TimeSpan.FromMinutes(5)
                  }))
            .Throws<BrowserLaunchException>();

        await Assert.That(progress.BrowserOpenings).IsEmpty();
        await Assert.That(progress.Notices).IsEmpty();
    }

    /// <summary>A caller cancel must not be mistaken for the escape hatch and rewarded with a device code.</summary>
    [Test]
    public async Task A_caller_cancel_propagates_rather_than_falling_through() {
        using var server = DeviceGrantServer();
        using var stub   = new StubHost(server.Urls[0]);
        var       workos = new WorkOSClient(new PlainHttpClientFactory(stub));
        using var cts    = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await OAuthLoginFlow.AcquireWorkOSAsync(
                  workos, "client_d", organizationId: null, forceDevice: false,
                  new RecordingBrowser(), new HangingBrowser(),
                  server.Urls[0], cts.Token, new RecordingAuthProgress(), ScriptedKeyWatcher.Blind()))
            .Throws<OperationCanceledException>();
    }
}
