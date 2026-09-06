using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// Console redirection is process-global state; keep every test in this file serialized against
// each other (and against anything else asserting on captured stdout/stderr).
[NotInParallel]
public class AuthProgressTests {
    sealed class FakeGitHubDeviceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task RunDeviceFlowAsync_reports_progress_through_the_sink_not_console() {
        var pollCount = 0;

        using var handler = new FakeGitHubDeviceHandler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) {
                // Empty verification_uri: Process.Start throws synchronously on an empty file name,
                // so the best-effort browser-open never actually launches anything during the test.
                return JsonResponse("""{"device_code":"dc","user_code":"UC123","verification_uri":"","interval":0}""");
            }

            pollCount++;

            return pollCount < 3
                ? JsonResponse("""{"error":"authorization_pending"}""")
                : JsonResponse("""{"access_token":"tok"}""");
        });
        var github   = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var progress = new RecordingAuthProgress();

        using var capture = ConsoleOutput.StartCapture();

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(), progress: progress);

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(progress.DeviceCodes).Count().IsEqualTo(1);
        // A successful clipboard copy (environment-dependent) appends a suffix to the code.
        await Assert.That(progress.DeviceCodes[0].Code).StartsWith("UC123");
        await Assert.That(progress.PollTicks).IsEqualTo(2); // 2 "authorization_pending" polls before success
        await Assert.That(progress.Notices).Contains(" done!");
        // Nothing reached Console — everything routed through the recording sink.
        await Assert.That(capture.GetCapturedOutput()).IsEmpty();
    }

    [Test]
    public async Task DiscoverAsync_zero_tenant_headless_emits_through_progress_not_console() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var progress = new RecordingAuthProgress();

        using var capture = ConsoleOutput.StartFullCapture();

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: () => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            orgSwitch: (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            progress: progress);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.NoTenants>();
        // Today's code writes this line to stderr — pinned so a future stream swap is deliberate.
        await Assert.That(progress.Errors).Contains("No Capacitor tenants are linked to your account. Ask your admin to invite you.");
        await Assert.That(capture.GetCapturedOutput()).IsEmpty();
        await Assert.That(capture.GetCapturedError()).IsEmpty();
    }

    [Test]
    public async Task ConsoleAuthProgress_DeviceCode_matches_todays_banner_lines() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress().DeviceCode("UC123", "https://github.com/login/device", "GitHub", prefilled: false);

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "  2. Enter the code: UC123" + Environment.NewLine
          + "  3. Approve access when GitHub asks." + Environment.NewLine
          + Environment.NewLine
          + "Waiting for you to authorize...");
    }

    /// <summary>
    /// The two variations the sink is allowed. A pre-filled code is checked rather than
    /// typed, because pre-filling removes the comparison the code exists to allow; and our own
    /// provider goes unnamed, because WorkOS is a white-label supplier the user has no use for.
    /// </summary>
    [Test]
    public async Task ConsoleAuthProgress_DeviceCode_checks_a_prefilled_code_and_names_no_provider() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress().DeviceCode("UC123", "https://signin.kcap.ai/device", provider: null, prefilled: true);

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "  2. Check the code shown is UC123" + Environment.NewLine
          + "  3. Approve access when asked." + Environment.NewLine
          + Environment.NewLine
          + "Waiting for you to authorize...");
    }

    [Test]
    public async Task ConsoleAuthProgress_BrowserOpening_matches_todays_notice_lines() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress().BrowserOpening("https://example.test/authorize");

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "Opening browser for authentication..." + Environment.NewLine
          + "  If the browser doesn't open, visit: https://example.test/authorize" + Environment.NewLine);
    }

    [Test]
    public async Task ConsoleAuthProgress_PollTick_writes_dot_without_newline() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress().PollTick();

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(".");
    }

    /// <summary>Pins the shift on copy this class composes rather than receives, which is the whole
    /// reason the indent lives here: a decorator around <see cref="IAuthProgress"/> sees only the
    /// strings it is handed, so indenting out there moves the notices and leaves these at column 0.
    /// </summary>
    [Test]
    public async Task ConsoleAuthProgress_indent_shifts_the_copy_it_composes_itself() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress("  ").BrowserOpening("https://example.test/authorize");

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "  Opening browser for authentication..." + Environment.NewLine
          + "    If the browser doesn't open, visit: https://example.test/authorize" + Environment.NewLine);
    }

    /// <summary>The numbered lines carry their own two spaces, so an indent has to shift them on top
    /// of that rather than replace it — the list stays a step inside the line introducing it.</summary>
    [Test]
    public async Task ConsoleAuthProgress_indent_shifts_the_device_code_block() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress("  ").DeviceCode("UC123", "https://example.test", null, prefilled: false);

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(
            "    2. Enter the code: UC123" + Environment.NewLine
          + "    3. Approve access when asked." + Environment.NewLine
          + Environment.NewLine
          + "  Waiting for you to authorize...");
    }

    /// <summary>The login flow spaces its output with empty notices, and an indented one is a line of
    /// trailing whitespace rather than a separator.</summary>
    [Test]
    public async Task ConsoleAuthProgress_indent_leaves_an_empty_notice_empty() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress("  ").Notice("");

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(Environment.NewLine);
    }

    /// <summary>The dots continue the "Waiting…" line rather than starting one, so shifting them would
    /// break the line they are appended to.</summary>
    [Test]
    public async Task ConsoleAuthProgress_indent_does_not_shift_a_poll_dot() {
        using var capture = ConsoleOutput.StartCapture();

        new ConsoleAuthProgress("  ").PollTick();

        await Assert.That(capture.GetCapturedOutput()).IsEqualTo(".");
    }
}
