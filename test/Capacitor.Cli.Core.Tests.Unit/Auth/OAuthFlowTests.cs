using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class OAuthFlowTests {
    // The code exchange rides the sign-in lane; against a stub, an unconfigured client is what
    // that lane resolves to.
    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());

    [Test]
    public async Task WorkOS_authorize_url_targets_api_domain_with_authkit_and_org() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel("org_a"));

        await Assert.That(state.StartUrl).StartsWith("https://api.workos.com/user_management/authorize");
        await Assert.That(state.StartUrl).Contains("provider=authkit");
        await Assert.That(state.StartUrl).Contains("organization_id=org_a");
        await Assert.That(state.StartUrl).Contains("code_challenge_method=S256");
        await Assert.That(options.LoadProfile).IsFalse();
    }

    /// <summary>
    /// The WorkOS browser runs INSIDE <c>OidcClient.LoginAsync</c>, unlike the GitHub flow
    /// which calls <c>InvokeAsync</c> itself. Duende does not wrap it, so a loopback bind failure
    /// surfaces as an exception rather than <c>result.IsError</c>, and the device-code fallback can
    /// catch it exactly as the GitHub arm already does. If a Duende upgrade starts folding it into an
    /// error result, that fallback goes silently dead and this test is what says so.
    /// </summary>
    [Test]
    public async Task WorkOS_login_lets_a_loopback_bind_failure_propagate() {
        using var server  = WireMockServer.Start();
        var       thrower = new FakeBrowser(_ => throw new System.Net.HttpListenerException(5, "Access is denied"));

        await Assert.That(async () => await OAuthLoginFlow.AuthenticateWorkOSAsync(
                  "client_d", "org_a", thrower, apiBase: server.Urls[0]))
            .Throws<System.Net.HttpListenerException>();
    }

    [Test]
    public async Task WorkOS_authorize_url_omits_org_when_null() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel(null));

        await Assert.That(state.StartUrl).DoesNotContain("organization_id");
    }

    [Test]
    public async Task AuthenticateWorkOS_maps_token_response_including_org_and_user() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x","first_name":"Ada"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt"}"""));

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", "org_a", FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
        await Assert.That(result.OrganizationId).IsEqualTo("org_a");
        await Assert.That(result.User!.FirstName).IsEqualTo("Ada");
    }

    [Test]
    public async Task AuthenticateWorkOS_handles_orgless_response_without_throwing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"access_token":"acc","refresh_token":"rt"}"""));   // no organization_id, no user

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", organizationId: null, FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.OrganizationId).IsNull();
        await Assert.That(result.User).IsNull();
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
    }

    [Test]
    public async Task AuthenticateWorkOS_returns_null_on_token_endpoint_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"invalid_grant"}"""));

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", "org_a", FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task WorkOSSignInError_preserves_actionable_detail() {
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("Timeout", null)).Contains("Timed out");
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("Invalid state.", null))
            .IsEqualTo("Sign-in failed: Invalid state.");
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("invalid_grant", "bad code"))
            .IsEqualTo("Sign-in failed: invalid_grant - bad code");
    }

    [Test]
    public async Task SwitchWorkOSOrg_posts_refresh_grant_with_org_and_returns_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt2"}"""));
        using var stub = new StubHost(server.Urls[0]);

        var auth = await new WorkOSClient(new PlainHttpClientFactory(stub))
            .SwitchOrganizationAsync("client_d", "rt1", "org_a", CancellationToken.None);

        await Assert.That(auth!.OrganizationId).IsEqualTo("org_a");
        await Assert.That(auth.RefreshToken).IsEqualTo("rt2");
        await Assert.That(auth.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task SwitchWorkOSOrg_returns_null_on_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));
        using var stub = new StubHost(server.Urls[0]);

        var auth = await new WorkOSClient(new PlainHttpClientFactory(stub))
            .SwitchOrganizationAsync("client_d", "rt1", "org_a", CancellationToken.None);

        await Assert.That(auth).IsNull();
    }

    [Test]
    public async Task RefreshWorkOSToken_posts_org_less_refresh_grant_and_returns_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"access_token":"acc","refresh_token":"rt2"}"""));
        using var stub = new StubHost(server.Urls[0]);

        var auth = await new WorkOSClient(new PlainHttpClientFactory(stub))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(auth!.AccessToken).IsEqualTo("acc");
        await Assert.That(auth.RefreshToken).IsEqualTo("rt2");

        var body = server.FindLogEntries(Request.Create().WithPath("/user_management/authenticate").UsingPost())[0].RequestMessage.Body!;
        await Assert.That(body).Contains("refresh_token=rt1");
        await Assert.That(body).DoesNotContain("organization_id");
    }

    [Test]
    public async Task RefreshWorkOSToken_returns_null_on_transport_failure() {
        // A network/timeout blip during a mid-poll refresh must degrade to null, not throw and
        // abort provisioning. Start then stop the server so the connection is refused.
        var server = WireMockServer.Start();
        var url = server.Urls[0];
        server.Stop();

        using var stub = new StubHost(url);

        var auth = await new WorkOSClient(new PlainHttpClientFactory(stub))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(auth).IsNull();
    }

    [Test]
    public async Task GitHubBrowser_exchanges_code_and_returns_access_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"gho_abc"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            Github, "Iv1.abc", $"{server.Urls[0]}/code-exchange", new RecordingBrowser(), FakeBrowser.WithCode("the_code"));

        await Assert.That(token).IsEqualTo("gho_abc");
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_state_mismatch_without_calling_proxy() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"nope"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            Github, "Iv1.abc", $"{server.Urls[0]}/code-exchange", new RecordingBrowser(),
            FakeBrowser.WithRawQuery("?code=the_code&state=attacker"));

        await Assert.That(token).IsNull();
        // The proxy must never be hit when the CSRF state doesn't match.
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == "/code-exchange")).IsFalse();
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_non_success_browser_result() {
        var progress = new RecordingAuthProgress();

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            Github, "Iv1.abc", "http://unused.test/code-exchange", new RecordingBrowser(),
            FakeBrowser.NonSuccess(BrowserResultType.Timeout), progress: progress);

        await Assert.That(token).IsNull();
        // The independent timeout keeps its classification and its message.
        await Assert.That(progress.Errors.Any(e => e.Contains("Timed out waiting for authorization"))).IsTrue();
    }

    // A caller cancel that the browser rendered as its own non-success result is still a cancel:
    // it propagates, and nothing is reported as a timeout.
    [Test]
    public async Task GitHubBrowser_caller_cancellation_propagates_and_renders_nothing() {
        using var cts      = new CancellationTokenSource();
        var       progress = new RecordingAuthProgress();

        await Assert.That(async () => await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
                Github, "Iv1.abc", "http://unused.test/code-exchange", new RecordingBrowser(),
                FakeBrowser.CancellingCaller(cts), ct: cts.Token, progress: progress))
            .Throws<OperationCanceledException>();

        await Assert.That(progress.Errors).IsEmpty();
    }

    [Test]
    public async Task ChooseDiscoveryProvider_honors_flags_and_default() {
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"])).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([])).IsEqualTo(AuthProvider.WorkOS);
    }

    // Discovery no longer consults the environment at all. It used to: a non-interactive session fell
    // back to GitHub App auth (the only provider with a device flow), which dead-ended because that
    // branch cannot provision — and when that implicit fallback went, `--device` was left behind as an
    // explicit way to reach it. WorkOS has a device grant of its own now, so neither is needed:
    // headless discovery stays on org SSO and signs in with a device code.
    [Test]
    public async Task Discovery_stays_on_org_sso_whatever_the_environment() {
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--device"])).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--no-prompt"])).IsEqualTo(AuthProvider.WorkOS);
    }

    // --github keeps its discovery meaning. It is the route for a GitHub-App-only user, and the one
    // flag that still moves discovery off org SSO.
    [Test]
    public async Task Explicit_github_flag_still_selects_the_legacy_provider() =>
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github", "--device"]))
            .IsEqualTo(AuthProvider.GitHubApp);

    [Test]
    public async Task ShouldDiscoverLogin_true_when_no_server_or_discover_flag() {
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, [])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, ["--device"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", ["--discover"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", [])).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_forced() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: true, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_headless() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: true, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_no_exchange_url() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: false);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_https_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("https://auth.example/auth/github/code-exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_http_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("http://localhost:8080/exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_null() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl(null)).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_empty_and_whitespace() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("   ")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_relative_path() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("/auth/exchange")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_non_http_scheme() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("javascript:alert(1)")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("file:///etc/passwd")).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_browser_when_interactive_and_server_supports_it() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Browser);
    }

    // ── the /auth/token error body WriteExchangeError renders from ──

    [Test]
    public async Task Installation_message_extracted_from_not_installed_error() {
        var msg = OAuthLoginFlow.TryParseInstallationMessage(
            """{"error":"The Kurrent Capacitor GitHub App is not installed on your organization."}""");

        await Assert.That(msg).IsEqualTo("The Kurrent Capacitor GitHub App is not installed on your organization.");
    }

    [Test]
    public async Task Installation_message_null_for_unrelated_error() {
        await Assert.That(OAuthLoginFlow.TryParseInstallationMessage("""{"error":"invalid_token"}""")).IsNull();
    }

    [Test]
    public async Task Installation_message_null_for_non_json_body() {
        await Assert.That(OAuthLoginFlow.TryParseInstallationMessage("Unauthorized")).IsNull();
        await Assert.That(OAuthLoginFlow.TryParseInstallationMessage("")).IsNull();
    }
}
