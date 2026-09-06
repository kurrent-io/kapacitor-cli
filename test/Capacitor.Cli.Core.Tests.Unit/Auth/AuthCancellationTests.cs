using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// CancellationToken threading through the auth flows: the device-poll loop actually stops on
/// cancellation, WorkOSDiscovery.DiscoverAsync forwards its own ct into both OfferCreateAsync and
/// the orgless-refresh delegate, and TenantDiscovery awaits the new async picker instead of the sync one.
/// </summary>
public class AuthCancellationTests {
    static string JwtWithExpiry(DateTimeOffset exp) {
        var json = $"{{\"exp\":{exp.ToUnixTimeSeconds()}}}";
        var b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{b64}.signature";
    }

    // The device-code and poll endpoints are hardcoded to github.com with no URL seam, so
    // cancellation is exercised against a fake handler behind the injected client instead.
    sealed class FakeGitHubDeviceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();

            return Task.FromResult(respond(request));
        }
    }

    static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Test]
    public async Task RunDeviceFlowAsync_cancelling_during_the_poll_throws_operation_canceled() {
        using var cts = new CancellationTokenSource();
        var pollCount = 0;

        using var handler = new FakeGitHubDeviceHandler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) {
                // Empty verification_uri: Process.Start throws synchronously on an empty file name,
                // so the best-effort browser-open never actually launches anything during the test.
                return JsonResponse("""{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0}""");
            }

            // Poll endpoint: pin on authorization_pending, then cancel mid-loop.
            if (++pollCount == 3) cts.Cancel();

            return JsonResponse("""{"error":"authorization_pending"}""");
        });
        var github = new GitHubOAuthClient(new PlainHttpClientFactory(handler));

        await Assert.That(async () => await OAuthLoginFlow.RunDeviceFlowAsync(
                github, "client_id", new RecordingBrowser(), cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(pollCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task WorkOSDiscovery_DiscoverAsync_threads_its_ct_into_OfferCreateAsync_and_the_orgless_refresh_delegate() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        using var cts = new CancellationTokenSource();

        var offerCt   = CancellationToken.None;
        var refreshCt = CancellationToken.None;

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(async ci => {
                       offerCt = ci.Arg<CancellationToken>();
                       // Near-expiry access token forces a refresh, exercising the orgless-refresh delegate.
                       await ci.Arg<WorkOSTokenSource>().GetAsync(offerCt);

                       return ProvisionOffer.Declined;
                   });

        var nearExpiry = JwtWithExpiry(DateTimeOffset.UtcNow.AddSeconds(5));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin:   () => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = nearExpiry, RefreshToken = "rt" }),
            orgSwitch:      (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            orglessRefresh: (_, refreshedCt) => { refreshCt = refreshedCt; return Task.FromResult<WorkOSAuthResponse?>(null); },
            provisioner:    provisioner,
            ct:             cts.Token);

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Failed>(); // Declined -> non-legacy failure

        // Identity, not mere structural default-equality: cancel AFTER capture and confirm the SAME
        // token observes it — proving DiscoverAsync forwarded its real ct into both call sites
        // rather than a fresh default(CancellationToken).
        await Assert.That(offerCt.CanBeCanceled).IsTrue();
        await Assert.That(refreshCt.CanBeCanceled).IsTrue();
        await cts.CancelAsync();
        await Assert.That(offerCt.IsCancellationRequested).IsTrue();
        await Assert.That(refreshCt.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task TenantDiscovery_RunAsync_awaits_PickAsync_and_never_calls_the_sync_Pick() {
        DiscoveredTenant[] list = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(list, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.Pick(Arg.Any<DiscoveredTenant[]>())
              .Returns(_ => throw new InvalidOperationException("TenantDiscovery must await PickAsync, not call the sync Pick"));
        picker.PickAsync(list, Arg.Any<TenantPickContext>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<DiscoveredTenant?>(list[1]));

        var discovery = new TenantDiscovery(proxy, picker);
        var outcome   = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked!.OrgLogin).IsEqualTo("contoso");
    }
}
