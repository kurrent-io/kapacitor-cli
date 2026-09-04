using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The defects in the RFC 8628 poll loop, all of which shipped in the GitHub flow first.
/// </summary>
public class DeviceGrantPollTests {
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // interval 0 so the loop does not really wait; the clock only has to move the deadline.
    static HttpResponseMessage DeviceCode(int expiresIn) =>
        Json($$"""{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0,"expires_in":{{expiresIn}}}""");

    /// <summary>
    /// The loop was <c>while (true)</c>: a code the server had already discarded was polled forever,
    /// so a user who abandoned the browser left the CLI running until they noticed.
    /// </summary>
    [Test]
    public async Task Stops_polling_once_the_device_code_has_expired() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 2);

            polls++;

            return Json("""{"error":"authorization_pending"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));

        // Every clock read moves a second, so the 2-second deadline is reached within a few polls.
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(), progress: new RecordingAuthProgress(), time: time);

        await Assert.That(token).IsNull();
        await Assert.That(polls).IsLessThan(5);   // bounded, not forever
    }

    /// <summary>
    /// The response was deserialized without checking the status and force-unwrapped, so a 429 or a
    /// 5xx HTML error page was a NullReferenceException mid-sign-in rather than a backoff.
    /// </summary>
    [Test]
    public async Task Backs_off_and_keeps_polling_when_the_token_endpoint_returns_an_unreadable_body() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 900);

            polls++;

            // A gateway's HTML, not JSON — exactly what used to NRE.
            return polls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                      Content = new StringContent("<html><body>Too Many Requests</body></html>", Encoding.UTF8, "text/html")
                  }
                : Json("""{"access_token":"tok"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(), progress: new RecordingAuthProgress(),
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(polls).IsEqualTo(2);
    }

    /// <summary>A handler that hangs until its attempt is cancelled, then answers on the next try.</summary>
    sealed class HangsOnceHandler : HttpMessageHandler {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (++Calls == 1) await Task.Delay(Timeout.Infinite, ct);   // observes the per-attempt bound

            return Json("""{"access_token":"acc"}""");
        }
    }

    /// <summary>
    /// One hung POST used to reach HttpClient's 100-second default and escape as a
    /// TaskCanceledException at the top level, killing a sign-in the user was still completing.
    /// </summary>
    [Test]
    public async Task A_hung_poll_is_retried_rather_than_ending_the_sign_in() {
        var       handler  = new HangsOnceHandler();
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var       progress = new RecordingAuthProgress();
        var       device   = new DeviceCodeResponse { DeviceCode = "dc", ExpiresIn = 900, Interval = 0 };

        var token = await OAuthLoginFlow.PollDeviceGrantAsync(
            github.PollForTokenAsync, new() { ["client_id"] = "c" },
            CapacitorJsonContext.Default.GitHubTokenResponse,
            r => (r.AccessToken, r.Error),
            device, interval: 0, CancellationToken.None, progress,
            attemptTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.That(token).IsEqualTo("acc");
        await Assert.That(handler.Calls).IsEqualTo(2);
        // Reported as a tick, not a failure: nothing is wrong, the request just did not answer.
        await Assert.That(progress.Errors).IsEmpty();
    }

    /// <summary>
    /// A parsed body is not a verdict. A 5xx that happens to be JSON deserializes into a response with
    /// every field defaulted, so it reads as neither a token nor a known RFC 8628 error - and only the
    /// status separates "the service is having a moment" from "the service answered you".
    /// </summary>
    [Test]
    public async Task Keeps_polling_when_a_readable_5xx_carries_no_device_error() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 900);

            polls++;

            return polls == 1
                ? Json("""{"message":"temporarily unavailable"}""", HttpStatusCode.InternalServerError)
                : Json("""{"access_token":"tok"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var       progress = new RecordingAuthProgress();

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(opens: false), progress: progress,
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(polls).IsEqualTo(2);
        await Assert.That(progress.Errors).IsEmpty();
    }

    /// <summary>The other half of the same rule: on a 2xx an unrecognised code IS the answer.</summary>
    [Test]
    public async Task An_unrecognised_error_on_a_success_status_still_ends_the_sign_in() {
        using var handler = new Handler(request =>
            request.RequestUri!.AbsolutePath.Contains("device/code")
                ? DeviceCode(expiresIn: 900)
                : Json("""{"error":"device_flow_disabled"}"""));
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));
        var       progress = new RecordingAuthProgress();

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            github, "client_id", new RecordingBrowser(opens: false), progress: progress,
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsNull();
        await Assert.That(string.Join("\n", progress.Errors)).Contains("device_flow_disabled");
    }
}
