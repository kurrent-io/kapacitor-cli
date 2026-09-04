using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The shape of the first request the GitHub device flow makes. github.com answers a form-encoded
/// body unless the request asks for JSON, and nothing downstream parses that; the exchange also
/// reads org membership, which <c>read:user</c> alone does not grant. Neither is visible in the
/// token the flow returns, so only the request as sent can show them.
/// </summary>
public class DeviceFlowRequestTests {
    sealed class Capturing : HttpMessageHandler {
        public string? Accept { get; private set; }

        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
            if (!request.RequestUri!.AbsolutePath.Contains("device/code")) return Json("""{"access_token":"tok"}""");

            Accept = request.Headers.Accept.ToString();
            Body   = await request.Content!.ReadAsStringAsync(ct);

            // Empty verification_uri keeps the best-effort browser open from launching anything;
            // interval 0 keeps the poll from waiting.
            return Json("""{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0}""");
        }

        static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Test]
    public async Task The_device_code_request_asks_for_json_and_carries_the_org_scope() {
        using var capture = new Capturing();
        using var http    = new HttpClient(capture);

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            http, "Iv1.abc", new RecordingBrowser(), progress: new RecordingAuthProgress());

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(capture.Accept).Contains("application/json");
        await Assert.That(capture.Body).Contains("scope=read%3Auser+read%3Aorg");
        await Assert.That(capture.Body).Contains("client_id=Iv1.abc");
    }
}
