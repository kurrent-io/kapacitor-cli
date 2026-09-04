using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The wire contract the server reads: every request the CLI sends must carry the CLI's display
/// version, and must
/// carry the update-check opt-out header if and only if the active profile turned it off. Absence
/// of the opt-out header on a version-carrying request is read by the server as "on" — so both the
/// present and the absent case are asserted here, not just the header's shape when it does appear.
///
/// <para>Asserted on the request WireMock actually received, driven through a container built fresh
/// per test — a client the container never touched would carry none of the tags asserted here.</para>
/// </summary>
[NotInParallel("AuthProviderDiscoveryCache")]
public class ObservationHeaderTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    ServiceProvider? _sp;

    /// <summary>The real container, because the tags belong to its handler chain now: a stub client
    /// would carry none and the assertions below would pass on nothing.</summary>
    WhoamiCommand Whoami(ProfileContext profiles) {
        var services = new ServiceCollection();

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(_server.Urls[0], Config.Root, profiles));
        services.AddCapacitorHttp();
        _sp = services.BuildServiceProvider();

        return new WhoamiCommand(
            Config.Root, profiles, AuthFixtures.NewTokenStore(Config.Root), _sp.GetRequiredService<ICapacitorHttpClient>(),
            _sp.GetRequiredService<AuthProviderDiscovery>());
    }

    /// <summary>An interactive-command client from the real container, built against
    /// <paramref name="profiles"/>: the container is what stamps the observation headers now, so a
    /// hand-built <see cref="HttpClient"/> would carry none of them.</summary>
    async Task<HttpClient> CommandClientAsync(ProfileContext profiles) {
        var services = new ServiceCollection();

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(_server.Urls[0], Config.Root, profiles));
        services.AddCapacitorHttp();
        _sp = services.BuildServiceProvider();

        return await _sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
    }

    public void Dispose() {
        _sp?.Dispose();
        _server.Stop();
    }

    void StubDiscovery(string provider = "None") =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    // ── The observation-header handler, driven through the real container ─────────────────────

    [Test]
    public async Task Client_always_carries_the_display_version_with_no_build_suffix() {
        StubDiscovery();
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = await CommandClientAsync(Resolutions.None(Config.Root));
        using var response = await client.GetAsync($"{_server.Urls[0]}/ping");

        var value = _server.LogEntries.Single(e => e.RequestMessage.Path == "/ping")
            .RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single();

        await Assert.That(value).IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(value).DoesNotContain("+");
    }

    [Test]
    public async Task Off_header_is_sent_when_the_active_profile_disabled_update_check() {
        StubDiscovery();
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        var profiles = Resolutions.Of(new Profile { UpdateCheck = false }, "obs-headers-off", _server.Urls[0]);

        using var client = await CommandClientAsync(profiles);
        using var response = await client.GetAsync($"{_server.Urls[0]}/ping");

        var headers = _server.LogEntries.Single(e => e.RequestMessage.Path == "/ping").RequestMessage.Headers!;

        await Assert.That(headers.ContainsKey(HttpClientExtensions.UpdateCheckHeader)).IsTrue();
        await Assert.That(headers[HttpClientExtensions.UpdateCheckHeader].Single())
            .IsEqualTo(HttpClientExtensions.UpdateCheckOffValue);
    }

    /// <summary>
    /// The non-vacuous half of the pair above: update_check ON (the default) must NOT send the
    /// header at all — a server that treats absence as "on" would misread a stray "on" value sent
    /// defensively, so the implementation must omit it rather than send a truthy value.
    /// </summary>
    [Test]
    public async Task Off_header_is_absent_when_the_active_profile_has_update_check_on() {
        StubDiscovery();
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));
        var profiles = Resolutions.Of(new Profile { UpdateCheck = true }, "obs-headers-on", _server.Urls[0]);

        using var client = await CommandClientAsync(profiles);
        using var response = await client.GetAsync($"{_server.Urls[0]}/ping");

        var headers = _server.LogEntries.Single(e => e.RequestMessage.Path == "/ping").RequestMessage.Headers!;

        await Assert.That(headers.ContainsKey(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }

    [Test]
    public async Task Off_header_is_absent_when_no_profile_is_resolved_at_all() {
        StubDiscovery();
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Nothing resolved and no config.json under this root, so the effective profile is the
        // built-in default, whose update_check defaults to true.
        using var client = await CommandClientAsync(Resolutions.None(Config.Root));
        using var response = await client.GetAsync($"{_server.Urls[0]}/ping");

        var headers = _server.LogEntries.Single(e => e.RequestMessage.Path == "/ping").RequestMessage.Headers!;

        await Assert.That(headers.ContainsKey(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }

    // ── WhoamiCommand.ProbeAsync (the raw client that bypasses the choke point) ────────────────

    /// <summary>
    /// ProbeAsync is private (deliberately — it must send exactly the token whoami printed, with no
    /// refresh). Driving it through the real <see cref="WhoamiCommand.HandleAsync"/> and capturing
    /// the actual request WireMock received is the only way to prove the headers reached the wire,
    /// as opposed to merely being attached to some client that HandleAsync doesn't end up using.
    /// </summary>
    [Test]
    public async Task Whoami_probe_request_carries_both_headers_when_update_check_is_off() {
        const string profileName = "obs-headers-whoami-off";
        StubDiscovery(provider: "github_app");
        _server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = Resolutions.Of(new Profile { UpdateCheck = false }, profileName, _server.Urls[0]);
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-whoami-off",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        // The one resolution steers both: the header preference and the profile whose token is read.
        await Whoami(profiles).HandleAsync();

        var probe = _server.LogEntries.Single(e => e.RequestMessage.Path == WhoamiCommand.ProbePath);

        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.UpdateCheckHeader].Single())
            .IsEqualTo(HttpClientExtensions.UpdateCheckOffValue);
    }

    /// <summary>Same probe, opposite preference: the off-header must not reach the wire either.</summary>
    [Test]
    public async Task Whoami_probe_request_omits_the_off_header_when_update_check_is_on() {
        const string profileName = "obs-headers-whoami-on";
        StubDiscovery(provider: "github_app");
        _server.Given(Request.Create().WithPath(WhoamiCommand.ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = Resolutions.Of(new Profile { UpdateCheck = true }, profileName, _server.Urls[0]);
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-whoami-on",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        // The one resolution steers both: the header preference and the profile whose token is read.
        await Whoami(profiles).HandleAsync();

        var probe = _server.LogEntries.Single(e => e.RequestMessage.Path == WhoamiCommand.ProbePath);

        await Assert.That(probe.RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(probe.RequestMessage.Headers!.ContainsKey(HttpClientExtensions.UpdateCheckHeader)).IsFalse();
    }
}
