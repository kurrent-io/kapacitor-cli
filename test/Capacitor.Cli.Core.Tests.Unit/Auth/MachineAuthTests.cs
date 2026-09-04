using System.Net;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Machine-credential authentication for headless runners.
///
/// <para>These tests deliberately go through the REAL HTTP exchange against a stub server rather than
/// asserting shapes around it. The immediately preceding work on this feature shipped a `kcap machine`
/// whose every subcommand threw before a request left the process, with a clean build, sixteen green
/// tests and a clean review — because every test exercised help text. The thing that has to work here is
/// the exchange, so the exchange is what is tested, including the wire format, which is precisely what
/// would be silently wrong.</para>
///
/// <para><c>[NotInParallel]</c> throughout: these manipulate process-wide environment variables and the
/// provider/token caches are process-wide statics.</para>
/// </summary>
[NotInParallel]
public class MachineAuthTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Stop();
        Clear();
    }

    static void Clear() {
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, null);
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, null);
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, null);
        MachineTokenProvider.ResetForTesting();
        HttpClientExtensions.ResetProviderCacheForTesting();
    }

    void UseStubTokenEndpoint() =>
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, $"{_server.Urls[0]}/oauth2/token");

    void StubToken(string token, int expiresIn = 3600) =>
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"access_token\":\"{token}\",\"expires_in\":{expiresIn}}}"));

    /// <summary>
    /// The token endpoint is whatever <c>KCAP_WORKOS_TOKEN_URL</c> names — our own sign-in host by
    /// default, a self-hosted IdP when an operator overrides it. Either may answer a 307 before it
    /// issues anything. The credential travels in the body, which a 307 preserves, so refusing the hop
    /// would fail a mint the endpoint never rejected.
    /// </summary>
    [Test]
    public async Task A_redirect_before_the_token_is_followed_and_still_mints() {
        Clear();
        UseStubTokenEndpoint();

        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/oauth2/token/regional"));

        _server.Given(Request.Create().WithPath("/oauth2/token/regional").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_after_hop","expires_in":3600}"""));

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), rejectedToken: null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_after_hop");
    }

    // ── Credential reading ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Both_variables_present_reads_the_credential() {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC");
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, "sekrit");

        await Assert.That(MachineAuth.Intended).IsTrue();

        var credential = MachineAuth.TryRead(out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(credential!.ClientId).IsEqualTo("client_01ABC");
        await Assert.That(credential.ClientSecret).IsEqualTo("sekrit");
    }

    /// <summary>
    /// A half-configured runner must be told WHICH half is missing. Silently falling back would advise
    /// `kcap login` — impossible on a runner with no browser and no profile.
    /// </summary>
    [Test]
    [Arguments("client_01ABC", null, "KCAP_CLIENT_SECRET")]
    [Arguments(null, "sekrit", "KCAP_CLIENT_ID")]
    public async Task One_variable_present_is_reported_as_a_problem_naming_the_missing_one(
            string? id, string? secret, string expectedInProblem) {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, id);
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, secret);

        await Assert.That(MachineAuth.Intended).IsTrue()
            .Because("machine auth was clearly intended, so it must be diagnosed rather than skipped");

        var credential = MachineAuth.TryRead(out var problem);

        await Assert.That(credential).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains(expectedInProblem);
    }

    [Test]
    public async Task Neither_variable_present_is_not_machine_auth_at_all() {
        Clear();

        await Assert.That(MachineAuth.Intended).IsFalse();
        await Assert.That(MachineAuth.TryRead(out var problem)).IsNull();
        await Assert.That(problem).IsNull()
            .Because("an ordinary interactive user is not a misconfigured machine");
    }

    // ── The exchange itself ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The load-bearing test: a real POST, and the WIRE FORMAT asserted. Verified against the live
    /// WorkOS endpoint too, which answers this exact form with a credential rejection rather than
    /// `unsupported_grant_type`.
    /// </summary>
    [Test]
    public async Task Minting_posts_client_credentials_form_and_returns_the_token() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_minted");

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), rejectedToken: null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_minted");

        var requests = _server.LogEntries.ToList();

        await Assert.That(requests.Count).IsEqualTo(1);

        var body = requests[0].RequestMessage.Body ?? "";

        await Assert.That(body).Contains("grant_type=client_credentials");
        await Assert.That(body).Contains("client_id=client_01ABC");
        await Assert.That(body).Contains("client_secret=sekrit");

        // Review: some token endpoints reject other content types with misleading errors, so pin it.
        var contentType = requests[0].RequestMessage.Headers!["Content-Type"].First();

        await Assert.That(contentType).Contains("application/x-www-form-urlencoded");
    }

    /// <summary>Second call reuses the cached token — no second mint.</summary>
    [Test]
    public async Task A_cached_token_is_reused_without_a_second_request() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_cached");

        var credential = new MachineCredential("client_01ABC", "sekrit");

        await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);
        var second = await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);

        await Assert.That(second.Token).IsEqualTo("tok_cached");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(1)
            .Because("the whole point of the in-memory cache is not re-minting per call");
    }

    /// <summary>
    /// A 401 from the server comes back as `rejectedToken`, and that must force a re-mint. Without it a
    /// revoked-then-reissued credential would keep serving the dead token until its clock ran out.
    /// </summary>
    [Test]
    public async Task A_rejected_token_forces_a_fresh_mint() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_first");

        var credential = new MachineCredential("client_01ABC", "sekrit");
        var first      = await MachineTokenProvider.GetTokenAsync(credential, null, CancellationToken.None);

        await Assert.That(first.Token).IsEqualTo("tok_first");

        var refreshed = await MachineTokenProvider.GetTokenAsync(credential, rejectedToken: first.Token, CancellationToken.None);

        await Assert.That(refreshed.Token).IsNotNull();
        await Assert.That(_server.LogEntries.Count).IsEqualTo(2)
            .Because("the cached token was the one the server refused, so it had to be re-minted");
    }

    /// <summary>
    /// A rejection reports a problem — and the problem must NOT carry the secret. A token endpoint's
    /// error body is attacker-influenced and can reflect the request, which contains the secret.
    /// </summary>
    [Test]
    public async Task A_rejected_credential_reports_a_problem_without_leaking_the_secret() {
        Clear();
        UseStubTokenEndpoint();
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                // A hostile/naive endpoint reflecting the request back at us.
                .WithBody("{\"error\":\"unauthorized\",\"echo\":\"client_secret=hunter2-the-secret\"}"));

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "hunter2-the-secret"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(result.Problem!).Contains("401");
        await Assert.That(result.Problem!).DoesNotContain("hunter2-the-secret")
            .Because("the response body is never echoed — it can reflect the credential");
    }

    /// <summary>Success with no access_token must fail, not hand back an empty bearer.</summary>
    [Test]
    public async Task A_success_response_with_no_token_is_a_failure() {
        Clear();
        UseStubTokenEndpoint();
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{\"expires_in\":3600}"));

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("no access_token");
    }

    // ── The wiring: does an authenticated client actually carry the bearer? ─────────────────────

    /// <summary>
    /// End-to-end through the client-construction choke point every authenticated CLI call uses. This is
    /// the test whose absence let the last iteration of this feature ship completely non-functional: it
    /// is the only one that proves the branch is REACHED and the header attached.
    /// </summary>
    [Test]
    public async Task An_authenticated_client_carries_the_minted_bearer_with_no_profile_present() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_wired");

        // A runner's server is discovered over unauthenticated /auth/config — no profile, no token store.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC");
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, "sekrit");

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(Config.Root, Resolutions.None(Config.Root), _server.Urls[0]);

        await Assert.That(status).IsEqualTo(AuthStatus.Ok);
        await Assert.That(client.DefaultRequestHeaders.Authorization).IsNotNull();
        await Assert.That(client.DefaultRequestHeaders.Authorization!.Scheme).IsEqualTo("Bearer");
        await Assert.That(client.DefaultRequestHeaders.Authorization!.Parameter).IsEqualTo("tok_wired");

        // Review: without this the test would still pass if the branch moved ahead of provider
        // discovery — it would only be checking the final header, not that a mint actually happened.
        var mints = _server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/oauth2/token"));

        await Assert.That(mints).IsEqualTo(1);
    }

    /// <summary>
    /// The same path with a half-configured credential reports NotAuthenticated rather than silently
    /// producing an unauthenticated client that would 401 with no explanation.
    /// </summary>
    [Test]
    public async Task A_half_configured_runner_reports_not_authenticated() {
        Clear();
        UseStubTokenEndpoint();

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC"); // secret missing

        var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(Config.Root, Resolutions.None(Config.Root), _server.Urls[0]);

        await Assert.That(status).IsEqualTo(AuthStatus.NotAuthenticated);
        await Assert.That(client.DefaultRequestHeaders.Authorization).IsNull();
    }
    // ── The token-URL override is a redirect primitive ─────────────────────────────────────────

    /// <summary>
    /// Review found that KCAP_WORKOS_TOKEN_URL can point the mint at an attacker-chosen host, and the
    /// REQUEST carries the secret. Plaintext non-loopback must be refused — and refused rather than
    /// silently falling back to the default, which would send the real credential to the real endpoint
    /// while the developer believed they were pointed at a stub.
    /// </summary>
    [Test]
    public async Task A_plaintext_non_loopback_token_url_is_refused_without_sending_the_credential() {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, "http://evil.example.com/oauth2/token");

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("https");
        await Assert.That(result.Problem!).DoesNotContain("sekrit");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0)
            .Because("the credential must not leave the process at all when the endpoint is untrusted");
    }

    /// <summary>
    /// Review round 2: the loopback carve-out is for http ONLY. `Uri.IsLoopback` is host-only, so an
    /// odd-scheme loopback URL (ftp/ws/file) must NOT be admitted just for being loopback — it is not a
    /// credential-safe POST target.
    /// </summary>
    [Test]
    [Arguments("ftp://127.0.0.1/oauth2/token")]
    [Arguments("ws://localhost/oauth2/token")]
    public async Task An_odd_scheme_loopback_token_url_is_refused(string url) {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, url);

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("https");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>Loopback over http is the deliberate carve-out — a credential cannot leave the machine.</summary>
    [Test]
    public async Task A_loopback_http_token_url_is_allowed_so_stubs_stay_testable() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_loopback");

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_loopback");
    }

    [Test]
    public async Task A_malformed_token_url_is_refused() {
        Clear();
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, "not-a-url");

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains(MachineAuth.TokenUrlVar);
    }

    /// <summary>
    /// Review #3: the cache is keyed on client id AND token URL, so a second credential does not receive
    /// the first one's token.
    /// </summary>
    [Test]
    public async Task A_different_credential_does_not_receive_the_cached_token() {
        Clear();
        UseStubTokenEndpoint();
        StubToken("tok_for_A");

        var a = await MachineTokenProvider.GetTokenAsync(new MachineCredential("client_A", "s"), null, CancellationToken.None);

        await Assert.That(a.Token).IsEqualTo("tok_for_A");

        var b = await MachineTokenProvider.GetTokenAsync(new MachineCredential("client_B", "s"), null, CancellationToken.None);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(2)
            .Because("a different client id must mint its own token, not inherit the cached one");
        await Assert.That(b.Token).IsNotNull();
    }

    // ── Automatic 401 recovery on the constructed client (Qodo finding 1) ───────────────────────

    /// <summary>
    /// The gap the previous revision had: an authenticated machine client got NO 401-retry handler, so
    /// a token revoked mid-life produced repeated 401s. This drives a real request through the
    /// constructed client: the token endpoint issues tok_A, the API 401s it once, and the client must
    /// re-mint (tok_B) and resend WITHOUT the caller threading anything back.
    /// </summary>
    [Test]
    public async Task An_authenticated_client_automatically_re_mints_on_a_401() {
        Clear();
        UseStubTokenEndpoint();

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        // Two tokens in sequence from the mint endpoint, via a WireMock scenario state machine.
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .InScenario("mint").WhenStateIs(null!).WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"tok_1\",\"expires_in\":3600}"));
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .InScenario("mint").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"tok_2\",\"expires_in\":3600}"));

        // /api/ping: 401 the first token, 200 the second.
        _server.Given(Request.Create().WithPath("/api/ping").UsingGet().WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/ping").UsingGet().WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        Environment.SetEnvironmentVariable(MachineAuth.ClientIdVar, "client_01ABC");
        Environment.SetEnvironmentVariable(MachineAuth.ClientSecretVar, "sekrit");

        // The interactive builder is the one that installs the retry handler (autoRetryUnauthorized:true).
        var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(Config.Root, Resolutions.None(Config.Root), _server.Urls[0]);

        using var resp = await client.GetAsync($"{_server.Urls[0]}/api/ping");

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because("the client must re-mint after the first token is rejected and resend");

        var mints = _server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/oauth2/token"));
        await Assert.That(mints).IsEqualTo(2)
            .Because("exactly one re-mint: the initial token plus one recovery");
    }

    // ── Error hygiene: no userinfo, no control chars in the Problem (Qodo finding 2) ────────────

    /// <summary>
    /// A KCAP_WORKOS_TOKEN_URL carrying userinfo must not print the secret half in the Problem string.
    /// The URL reaches stderr, so it goes through the same Sanitize the rest of the CLI uses.
    /// </summary>
    [Test]
    public async Task A_token_url_with_userinfo_does_not_leak_it_into_the_problem() {
        Clear();
        // Loopback so the scheme check admits it and we reach the mint, which then fails (nothing
        // listening on that path) and builds the Problem string containing the URL.
        Environment.SetEnvironmentVariable(MachineAuth.TokenUrlVar, "http://id:supersecret@127.0.0.1:1/oauth2/token");

        var result = await MachineTokenProvider.GetTokenAsync(
            new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(result.Problem!).DoesNotContain("supersecret")
            .Because("userinfo in the token URL must be dropped before it reaches stderr");
    }

}
