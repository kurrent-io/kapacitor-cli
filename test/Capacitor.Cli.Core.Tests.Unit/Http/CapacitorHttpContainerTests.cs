using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// <see cref="CapacitorHttpServices.AddCapacitorHttp"/> assembled for real and driven against a stub
/// server. <c>AddHttpMessageHandler</c> resolves each handler on the FIRST REQUEST, not at container
/// build, so a missing registration cannot be caught by anything short of a real call.
///
/// <para><c>[NotInParallel]</c>: the machine-credential variables are process-wide environment. The
/// discovery memo and the machine-token cache are not — each container owns its own.</para>
/// </summary>
[NotInParallel]
public class CapacitorHttpContainerTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    EnvScope? _clientId;
    EnvScope? _clientSecret;
    string    _profile = "";

    string Url => _server.Urls[0];

    [Before(Test)]
    public void Isolate() {
        // Cleared so the credential source picks the token store: a runner's machine credential would
        // otherwise win over it and mint against an endpoint no test here stubs.
        _clientId     = EnvScope.Exclusive(MachineAuth.ClientIdVar, null);
        _clientSecret = EnvScope.Exclusive(MachineAuth.ClientSecretVar, null);
        _profile      = Resolutions.At(Url, Config.Root).Name;
    }

    public void Dispose() {
        _clientId?.Dispose();
        _clientSecret?.Dispose();
        _server.Stop();
    }

    ServiceProvider Container(string? serverUrl = null) {
        var target   = serverUrl ?? Url;
        var services = new ServiceCollection();

        var profiles = Resolutions.At(target, Config.Root);

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(target, Config.Root, profiles));
        services.AddCapacitorHttp();

        return services.BuildServiceProvider();
    }

    void StubProvider(string provider) =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    Task SeedTokenAsync(string accessToken) =>
        AuthFixtures.NewTokenStore(Config.Root).SaveAsync(_profile, new StoredTokens {
            AccessToken    = accessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = Url,
        });

    /// <summary>A stubbed login: a provider to discover and a token bound to this server.</summary>
    async Task AuthenticateAsync(string accessToken) {
        StubProvider(AuthProvider.GitHubApp);
        await SeedTokenAsync(accessToken);
    }

    WireMock.Logging.ILogEntry RequestTo(string path) =>
        _server.LogEntries.Single(e => e.RequestMessage.Path == path);

    // ── The container itself ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client from the registered factory reaches the wire, which is the only thing that proves every
    /// handler in the chain is registered: the factory resolves them per request, so an unregistered one
    /// throws here and nowhere earlier.
    /// </summary>
    [Test]
    public async Task Every_handler_in_the_chain_resolves_on_the_first_request() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await Assert.That(sp.GetRequiredService<ISessionsApi>()).IsNotNull();

        using var client   = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        using var response = await client.GetAsync($"{Url}/ping");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // ── Verb, path and body per call ───────────────────────────────────────────────────────────

    [Test]
    public async Task Deleting_a_session_deletes_its_own_path() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-1");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.Deleted>();
        await Assert.That(RequestTo("/api/sessions/sess-1").RequestMessage.Method).IsEqualTo("DELETE");
    }

    [Test]
    public async Task Hiding_a_session_puts_a_visibility_of_none() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().HideSessionAsync("sess-2");

        var sent = RequestTo("/api/sessions/sess-2/visibility").RequestMessage;

        await Assert.That(sent.Method).IsEqualTo("PUT");
        await Assert.That(sent.Body).IsEqualTo("""{"visibility":"none"}""");
    }

    [Test]
    public async Task Setting_a_title_posts_the_session_id_beside_it() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().SetSessionTitleAsync("sess-3", "A better title");

        var sent = RequestTo("/hooks/set-title").RequestMessage;

        await Assert.That(sent.Method).IsEqualTo("POST");
        await Assert.That(sent.Body).IsEqualTo("""{"session_id":"sess-3","title":"A better title"}""");
    }

    /// <summary>A session the server holds nothing for is an outcome, not a failure.</summary>
    [Test]
    public async Task Deleting_an_absent_session_is_not_found_rather_than_a_throw() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/gone").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("gone");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.NotFound>();
    }

    // ── Failures ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_refused_call_throws_carrying_the_status() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-4/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var sp = Container();
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.HideSessionAsync("sess-4")).Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsEqualTo(500);
    }

    /// <summary>
    /// A 401 the server explained is shown as the server explained it: the CLI's own re-auth wording is
    /// a fallback for a body that says nothing, not a replacement for one that does.
    /// </summary>
    [Test]
    public async Task A_401_carries_the_servers_own_message() {
        // No bearer is sent under the None provider, so there is nothing to rotate and the refusal
        // reaches the caller on the first attempt.
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Your seat was revoked. Ask an admin to re-invite you."}"""));

        using var sp  = Container();
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.SetSessionTitleAsync("sess-5", "t"))
            .Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsEqualTo(401);
        await Assert.That(ex.Message).IsEqualTo("Your seat was revoked. Ask an admin to re-invite you.");
    }

    /// <summary>
    /// A server that never answered is the same one exception with no status, so a caller needs no
    /// second catch. Slow by construction: the whole 30s retry budget is spent before the transport
    /// failure is final.
    /// </summary>
    [Test]
    public async Task An_unreachable_server_throws_with_no_status_at_all() {
        const string dead = "http://127.0.0.1:1";

        using var sp  = Container(dead);
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.DeleteSessionAsync("sess-6"))
            .Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsNull();
        await Assert.That(ex.InnerException).IsTypeOf<HttpRequestException>();
    }

    // ── The handler chain ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_request_carries_the_bearer_and_the_cli_version() {
        await AuthenticateAsync("tok_wired");
        _server.Given(Request.Create().WithPath("/api/sessions/sess-7").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-7");

        var headers = RequestTo("/api/sessions/sess-7").RequestMessage.Headers!;

        await Assert.That(headers["Authorization"].Single()).IsEqualTo("Bearer tok_wired");
        await Assert.That(headers[HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    [Test]
    public async Task The_servers_version_header_lands_in_the_store() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-8").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "0.42.0"));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-8");

        await Assert.That(ServerVersionStore.Get(Url, Config.Root)).IsEqualTo("0.42.0");
    }

    // ── The background lane ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The lapsed-login hint is the whole difference between the two verbs, so it is asserted as a
    /// pair: a watcher or teardown prints nothing, while an interactive command prints the
    /// actionable line. One hint per flush on a stderr nobody reads is what the background verb
    /// exists to avoid.
    /// </summary>
    [Test]
    public async Task The_background_lane_is_silent_where_an_interactive_command_prints_the_hint() {
        StubProvider(AuthProvider.GitHubApp);

        using var sp = Container();
        var       http = sp.GetRequiredService<ICapacitorHttpClient>();

        using (var quiet = ConsoleOutput.StartErrorCapture()) {
            using var background = await http.ForBackgroundAsync();

            await Assert.That(quiet.GetCapturedError()).IsEmpty();
        }

        using var loud = ConsoleOutput.StartErrorCapture();
        using var interactive = await http.ForCommandAsync();

        await Assert.That(loud.GetCapturedError()).Contains("kcap login");
    }

    /// <summary>
    /// A vendor reads hook stderr as the hook's own result, so the lapse must reach the caller as a
    /// value and never as a line: the status is what lets a hook skip a send it would only lose.
    /// </summary>
    [Test]
    public async Task The_hook_lane_reports_a_lapse_as_a_status_and_never_on_stderr() {
        StubProvider(AuthProvider.GitHubApp);

        using var sp      = Container();
        using var capture = ConsoleOutput.StartErrorCapture();

        var attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        using var client = attempt.Client;

        await Assert.That(capture.GetCapturedError()).IsEmpty();
        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.NotAuthenticated);
        await Assert.That(attempt.Usable).IsFalse();
    }

    // ── The anonymous lane ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A seeded, valid token is the point: the anonymous lane must stay bare when a credential IS
    /// available, not merely when none exists. The version tag still rides, because the handler that
    /// stamps it sits in a chain no caller can opt out of.
    /// </summary>
    [Test]
    public async Task The_anonymous_lane_sends_no_bearer_but_still_carries_the_cli_version() {
        await AuthenticateAsync("tok_must_not_travel");
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Anonymous();

        using var response = await client.GetAsync($"{Url}/auth/config");

        var headers = RequestTo("/auth/config").RequestMessage.Headers!;

        await Assert.That(headers.ContainsKey("Authorization")).IsFalse();
        await Assert.That(headers[HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    /// <summary>
    /// The authenticated lane refuses redirects because a bearer does not survive one. With no bearer
    /// that reason is gone, and a release download that lands on an object store needs the hop taken —
    /// so the two lanes must genuinely differ here.
    /// </summary>
    [Test]
    public async Task The_anonymous_lane_follows_a_redirect() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/moved").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{Url}/landed"));
        _server.Given(Request.Create().WithPath("/landed").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("arrived"));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Anonymous();

        using var response = await client.GetAsync($"{Url}/moved");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("arrived");
    }

    /// <summary>
    /// The store is keyed by the configured server, and this lane exists to reach one the caller has
    /// not adopted — so capturing a version here would file the candidate's under the incumbent's key
    /// and cap the update recommendation against a server the user never chose.
    /// </summary>
    [Test]
    public async Task The_anonymous_lane_files_no_server_version() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "9.9.9"));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Anonymous();

        using var response = await client.GetAsync($"{Url}/auth/config");

        await Assert.That(ServerVersionStore.Get(Url, Config.Root)).IsNull();
    }

    /// <summary>
    /// Sign-in's own unauthenticated legs draw the same lane rather than a client of their own. Only a
    /// container can show it: a hand-built factory answers every lane name with one unconfigured
    /// client, so the name the facade asks for is invisible to every other test.
    /// </summary>
    [Test]
    public async Task The_onboarding_facade_reads_auth_config_on_the_anonymous_lane() {
        StubProvider(AuthProvider.None);

        using var sp = Container();

        var facade = new OnboardingFacade(
            Config.Root, sp.GetRequiredService<TokenStore>(),
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IAuthProxyClient>(),
            sp.GetRequiredService<GitHubOAuthClient>(), sp.GetRequiredService<WorkOSClient>(),
            new RecordingAuthProgress(), new RecordingBrowser(), AuthFixtures.PickerReturningFirst(),
            provisioner: null, beforeCommit: null);

        await facade.LoginAsync(Url, forceDevice: false, _profile, CancellationToken.None, adoptServer: true);

        var headers = RequestTo("/auth/config").RequestMessage.Headers!;

        await Assert.That(headers[HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    /// <summary>
    /// The authenticated lane follows a redirect. The runtime drops Authorization only when a hop
    /// crosses origin, so refusing one protects no bearer and instead turns every 3xx into a hard
    /// failure — which the hook callers classify as unretryable and never spool.
    /// </summary>
    [Test]
    public async Task The_authenticated_lane_follows_a_redirect() {
        await AuthenticateAsync("tok_redirect");
        _server.Given(Request.Create().WithPath("/api/sessions/sess-r").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{Url}/api/sessions/moved"));
        _server.Given(Request.Create().WithPath("/api/sessions/moved").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("arrived"));

        using var sp     = Container();
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();

        using var response = await client.GetAsync($"{Url}/api/sessions/sess-r");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("arrived");
    }

    // ── The loopback lane ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A capability URL IS the credential, so a followed hop would hand the grant to whatever host
    /// the 3xx names. This is the one place the anonymous rule inverts: no bearer to strip, and the
    /// redirect still must not be taken.
    /// </summary>
    [Test]
    public async Task The_loopback_lane_refuses_to_follow_a_redirect() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/capability/submit").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{Url}/elsewhere"));
        _server.Given(Request.Create().WithPath("/elsewhere").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("leaked"));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Loopback();

        using var response = await client.GetAsync($"{Url}/capability/submit");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/elsewhere")).IsFalse();
    }

    /// <summary>
    /// The borrowed reviewer this lane serves runs with HOME redirected, so a token store read is
    /// what fails silently. A seeded token proves the lane stays bare even where one is readable.
    /// </summary>
    [Test]
    public async Task The_loopback_lane_sends_no_bearer() {
        await AuthenticateAsync("tok_must_not_travel");
        _server.Given(Request.Create().WithPath("/capability/submit").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Loopback();

        using var response = await client.GetAsync($"{Url}/capability/submit");

        await Assert.That(RequestTo("/capability/submit").RequestMessage.Headers!.ContainsKey("Authorization"))
            .IsFalse();
    }

    /// <summary>
    /// The one guarantee this lane cannot show on the wire: an ambient proxy would route a grant
    /// minted for 127.0.0.1 off the machine entirely, and a test server cannot observe its own
    /// absence from a proxy. Read off the built chain instead.
    /// </summary>
    [Test]
    public async Task The_loopback_lane_bypasses_any_ambient_proxy() {
        using var sp = Container();

        HttpMessageHandler handler =
            sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(CapacitorClients.Loopback);

        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;

        await Assert.That(((HttpClientHandler)handler).UseProxy).IsFalse();
    }

    // ── The caller's-own-bearer lane ───────────────────────────────────────────────────────────

    /// <summary>
    /// A login redirect would otherwise masquerade as the server's verdict: the hop strips the
    /// hand-set Authorization, so the caller reads the resulting 401 as "this token is refused"
    /// when the truth is that it was never presented.
    /// </summary>
    [Test]
    public async Task The_bearer_lane_refuses_to_follow_a_redirect() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", $"{Url}/login"));
        _server.Given(Request.Create().WithPath("/login").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Bearer();

        client.DefaultRequestHeaders.Authorization = new("Bearer", "tok_caller_supplied");

        using var response = await client.GetAsync($"{Url}/api/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(RequestTo("/api/me").RequestMessage.Headers!["Authorization"].Single())
            .IsEqualTo("Bearer tok_caller_supplied");
    }

    /// <summary>
    /// The lane must not rotate: the caller is asking whether THIS token is accepted, so a silent
    /// swap to a fresher one answers a different question and reports the wrong verdict.
    /// </summary>
    [Test]
    public async Task The_bearer_lane_does_not_rotate_a_refused_token() {
        await AuthenticateAsync("tok_in_the_store");
        _server.Given(Request.Create().WithPath("/api/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var sp     = Container();
        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Bearer();

        client.DefaultRequestHeaders.Authorization = new("Bearer", "tok_caller_supplied");

        using var response = await client.GetAsync($"{Url}/api/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/api/me")).IsEqualTo(1);
    }

    /// <summary>
    /// The hook lane carries the credential but answers a 401 with the refusal itself. A hook POSTs
    /// once inside a budget it cannot extend, and a rotation it never asked for spends that budget on
    /// a round trip whose answer arrives too late to use — its caller spools instead.
    /// </summary>
    [Test]
    public async Task The_hook_lane_sends_the_bearer_but_does_not_rotate_on_a_401() {
        await AuthenticateAsync("tok_in_the_store");

        _server.Given(Request.Create().WithPath("/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_rotated","expires_in":3600}"""));

        _server.Given(Request.Create().WithPath("/api/hook").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var sp = Container();

        var (client, status, _, _) = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        using var hookClient = client;
        using var response   = await hookClient.PostAsync($"{Url}/api/hook", new StringContent("{}"));

        await Assert.That(status).IsEqualTo(AuthStatus.Ok);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var attempts = _server.LogEntries.Where(e => e.RequestMessage.Path == "/api/hook").ToList();

        await Assert.That(attempts.Count).IsEqualTo(1);
        await Assert.That(attempts[0].RequestMessage.Headers!["Authorization"].Single())
            .IsEqualTo("Bearer tok_in_the_store")
            .Because("the lane omits the rotation, not the credential");
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/auth/refresh")).IsFalse();
    }

    /// <summary>
    /// One hint per process. A command that talks to several endpoints resolves the same lapsed
    /// credential once per client it builds, and repeating the line per target tells the user nothing
    /// the first one did not.
    /// </summary>
    [Test]
    public async Task The_lapse_hint_is_printed_once_however_many_clients_a_command_builds() {
        StubProvider(AuthProvider.GitHubApp);

        using var sp   = Container();
        var       http = sp.GetRequiredService<ICapacitorHttpClient>();

        using var captured = ConsoleOutput.StartErrorCapture("\n");

        (await http.ForCommandAsync()).Dispose();
        (await http.ForCommandAsync()).Dispose();
        (await http.ForCommandAsync()).Dispose();

        var lines = captured.GetCapturedError().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("Not authenticated");
    }

    /// <summary>
    /// A session client is built once and held for the process's life, so its handler — and the
    /// credential it resolved — outlive any login that lands afterwards. A first send made before
    /// there was anything to send must not fix that absence in place: the MCP servers reach this
    /// exact state whenever an agent starts them before the user has logged in.
    /// </summary>
    [Test]
    public async Task A_login_after_a_held_client_went_out_anonymous_reaches_its_next_send() {
        StubProvider(AuthProvider.GitHubApp);

        _server.Given(Request.Create().WithPath("/api/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/me").UsingGet()
                    .WithHeader("Authorization", "Bearer tok_after_login"))
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp     = Container();
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForSessionAsync();

        using (var refused = await client.GetAsync($"{Url}/api/me")) {
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized)
                .Because("nothing is stored yet, so the send goes out with no bearer");
        }

        await SeedTokenAsync("tok_after_login");

        using var accepted = await client.GetAsync($"{Url}/api/me");

        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var sent = _server.LogEntries.Where(e => e.RequestMessage.Path == "/api/me").ToList();

        await Assert.That(sent[0].RequestMessage.Headers!.ContainsKey("Authorization")).IsFalse();
        await Assert.That(sent[^1].RequestMessage.Headers!["Authorization"].Single())
            .IsEqualTo("Bearer tok_after_login");
    }

    /// <summary>
    /// The waiting verb reports the status like a hook's does, but recovers like a command: its
    /// caller blocks on a human in a browser, which outlasts a short-lived token, so ending the wait
    /// on the first 401 would discard a leg that was still answerable.
    /// </summary>
    [Test]
    public async Task The_waiting_lane_reports_its_status_and_still_recovers_a_401() {
        await AuthenticateAsync("tok_1");

        _server.Given(Request.Create().WithPath("/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_2","expires_in":3600}"""));

        _server.Given(Request.Create().WithPath("/api/flow").UsingGet()
                    .WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/flow").UsingGet()
                    .WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        var (client, status, _, _) = await sp.GetRequiredService<ICapacitorHttpClient>().ForWaitAsync();

        using var waiting  = client;
        using var response = await waiting.GetAsync($"{Url}/api/flow");

        await Assert.That(status).IsEqualTo(AuthStatus.Ok)
            .Because("the caller gates on the status before it starts waiting");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flow")).IsEqualTo(2);
    }

    /// <summary>
    /// Handler order, which nothing but a live 401-then-200 exchange can pin. Recovery is INNERMOST, so
    /// the two handlers outside it see the exchange once: version capture observes only the response the
    /// caller gets — the discarded 401's header must not reach the store — and the observation headers
    /// are stamped once, not again on the resend.
    /// </summary>
    [Test]
    public async Task Recovery_sits_inside_both_outer_handlers() {
        await AuthenticateAsync("tok_1");

        _server.Given(Request.Create().WithPath("/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_2","expires_in":3600}"""));

        _server.Given(Request.Create().WithPath("/api/sessions/sess-9").UsingDelete()
                    .WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "0.1.0-preretry"));

        _server.Given(Request.Create().WithPath("/api/sessions/sess-9").UsingDelete()
                    .WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-9");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.Deleted>();

        var attempts = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/api/sessions/sess-9")
            .ToList();

        await Assert.That(attempts.Count).IsEqualTo(2);
        await Assert.That(attempts[1].RequestMessage.Headers!["Authorization"].Single()).IsEqualTo("Bearer tok_2");

        await Assert.That(ServerVersionStore.Get(Url, Config.Root)).IsNull()
            .Because("capture is outside recovery, so it never sees the response recovery threw away");

        var stamped = string.Join(",", attempts[1].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader]);

        await Assert.That(stamped).IsEqualTo(CapacitorVersion.CurrentDisplay())
            .Because("the observation handler is outside recovery, so the resend is not stamped twice");
    }

    /// <summary>
    /// A container holding only the foreign clients — no server, no credential source, none of what
    /// the authenticated lanes resolve. That is all the desktop wizard has: it signs a workspace up
    /// before there is a server to authenticate against.
    /// </summary>
    [Test]
    public async Task A_foreign_client_resolves_without_the_authenticated_lanes() {
        using var sp = new ServiceCollection().AddCapacitorForeignClients().BuildServiceProvider();

        await Assert.That(sp.GetService<TenantProvisioningClient>()).IsNotNull();
        await Assert.That(sp.GetService<ICapacitorHttpClient>()).IsNull();
    }

    /// <summary>Our version tags are for our own server, and the signup control plane is not it.</summary>
    [Test]
    public async Task A_foreign_client_carries_none_of_our_observation_headers() {
        _server.Given(Request.Create().WithPath("/api/signup/availability").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"available":true}"""));

        using var sp = Container();

        await sp.GetRequiredService<TenantProvisioningClient>()
            .CheckAvailabilityAsync(Url, "tok_signup", "acme", CancellationToken.None);

        var sent = _server.LogEntries
            .Single(e => e.RequestMessage.Path == "/api/signup/availability").RequestMessage;

        await Assert.That(sent.Headers!.ContainsKey(HttpClientExtensions.CliVersionHeader)).IsFalse();
        await Assert.That(sent.Headers["Authorization"].Single()).IsEqualTo("Bearer tok_signup")
            .Because("the caller's own token is the only credential this lane knows about");
    }

    /// The store sits under the credential source, so it takes the client factory rather than the
    /// client: injecting the façade would close the loop and the container would refuse to build.
    [Test]
    public async Task The_token_store_resolves_from_the_same_container_as_the_lanes() {
        using var sp = Container();

        await Assert.That(sp.GetService<TokenStore>()).IsNotNull();
        await Assert.That(sp.GetService<ICapacitorHttpClient>()).IsNotNull();
    }

    [Test]
    public async Task The_auth_proxy_resolves_through_its_interface() {
        using var sp = new ServiceCollection().AddCapacitorForeignClients().BuildServiceProvider();

        await Assert.That(sp.GetService<IAuthProxyClient>()).IsTypeOf<AuthProxyClient>();
    }

    // ── An unusable server URL ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Driven with an absolute wrong-scheme URL rather than a scheme-less one: that is the class
    /// that discriminates, since an implementation validating only <c>UriKind.Absolute</c> accepts
    /// <c>ftp://host</c> while still being unable to send to it.
    /// </summary>
    [Test]
    public async Task A_command_refuses_an_unusable_server_url() {
        using var sp = Container("ftp://host");

        await Assert.That(async () => await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync())
            .Throws<UnusableServerUrlException>();
    }

    /// <summary>
    /// A hook is told, not thrown at: vendors read hook stderr as the hook's own result, and an
    /// exception escaping here would cost the harness its session.
    ///
    /// <para>A token is seeded first, so the status cannot come from an empty store — without the
    /// check the credential source would answer for a server it can never reach.</para>
    /// </summary>
    [Test]
    public async Task A_hook_is_told_the_url_is_unusable_rather_than_thrown_at() {
        await AuthenticateAsync("tok_seeded");

        using var sp      = Container("ftp://host");
        var       attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        using var client = attempt.Client;

        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.UnusableServerUrl);
        await Assert.That(attempt.Usable).IsFalse();
    }

    /// <summary>Nothing is spent answering: no discovery call leaves the process.</summary>
    [Test]
    public async Task An_unusable_url_reaches_no_discovery() {
        StubProvider(AuthProvider.GitHubApp);

        using var sp      = Container("ftp://host");
        var       attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        using var client = attempt.Client;

        await Assert.That(_server.LogEntries).IsEmpty();
    }

    /// <summary>The refusal is about the URL, not a standing refusal of every server.</summary>
    [Test]
    public async Task A_usable_url_still_resolves_its_credential() {
        await AuthenticateAsync("tok_usable");

        using var sp      = Container();
        var       attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();

        using var client = attempt.Client;

        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.Ok);
    }

    /// <summary>
    /// A None verdict can come from discovery's outage fallback, which deliberately does not cache
    /// it, so the credential source has to ask again rather than pin it. Pinned, a daemon or MCP
    /// process that started while the server was down stays unauthenticated until it restarts —
    /// past the server's recovery and past a later login.
    /// </summary>
    [Test]
    public async Task A_None_reached_while_the_server_was_down_is_re_discovered() {
        // Nothing stubs /auth/config and no token is on disk, so discovery falls through to None.
        using var sp   = Container();
        var       http = sp.GetRequiredService<ICapacitorHttpClient>();

        var down = await http.ForHookAsync();

        down.Client.Dispose();

        await Assert.That(down.Status).IsEqualTo(AuthStatus.NoAuthRequired);

        await AuthenticateAsync("tok_recovered");

        var recovered = await http.ForHookAsync();

        recovered.Client.Dispose();

        await Assert.That(recovered.Status).IsEqualTo(AuthStatus.Ok);
    }

    // ── The server URL itself ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every caller builds a path by interpolating onto <see cref="CapacitorServer.Url"/>, so a
    /// configured trailing slash would reach the server as a doubled one. Trimmed once here rather
    /// than at each of those sites, where one omission is invisible until a route 404s.
    /// </summary>
    [Test]
    [Arguments("https://host/", "https://host")]
    [Arguments("https://host//", "https://host")]
    [Arguments("https://host", "https://host")]
    [Arguments("https://host/base/", "https://host/base")]
    public async Task A_configured_trailing_slash_never_reaches_a_path(string configured, string expected) {
        var server = new CapacitorServer(configured, Config.Root, Resolutions.At(configured, Config.Root));

        await Assert.That(server.Url).IsEqualTo(expected);
        await Assert.That(server.Usable).IsTrue();
    }

    /// <summary>Nothing resolved is not a URL: empty rather than null, and refused either way.</summary>
    [Test]
    public async Task An_unresolved_server_is_empty_and_unusable() {
        var server = new CapacitorServer(null, Config.Root, Resolutions.None(Config.Root));

        await Assert.That(server.Url).IsEqualTo("");
        await Assert.That(server.Usable).IsFalse();
    }
}
