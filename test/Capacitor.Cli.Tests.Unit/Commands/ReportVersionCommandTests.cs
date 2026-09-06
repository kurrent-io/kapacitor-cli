using System.Diagnostics;
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
/// The hidden <c>kcap report-version</c> command, invoked by the npm wrapper right after
/// `kcap update` installs a new binary. Its whole purpose is to make ONE authenticated,
/// side-effect-free GET through <see cref="ICapacitorHttpClient.ForHookAsync"/> — whose client
/// pipeline attaches <see cref="HttpClientExtensions.CliVersionHeader"/> — so the server's version
/// observer sees the new version immediately instead of waiting for the next incidental request.
/// It reuses <see cref="WhoamiCommand.ProbePath"/> (the same read-only identity GET
/// <c>kcap whoami</c> uses) rather than any write-side-effecting endpoint. It must never surface
/// an error: not-authenticated makes no request at all, and a server fault, a slow server, or no
/// server configured at all still returns 0.
/// </summary>
// Bare, not keyed: KCAP_URL is read by every resolution in the assembly and inherited by every
// child, so no cohort of key-holders can exclude its observers.
[NotInParallel]
public class ReportVersionCommandTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ProbePath = WhoamiCommand.ProbePath;

    readonly WireMockServer _server = WireMockServer.Start();

    ServiceProvider? _sp;

    /// <summary>The real container: every case here turns on the auth status the credential source
    /// actually resolves, which a stub client cannot produce.</summary>
    ReportVersionCommand Command(ProfileContext profiles) {
        var services = new ServiceCollection();

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        // No stand-in when resolution found none: an unusable server is exactly the state the
        // no-base-url case has to exercise, and a placeholder would hand it a reachable one.
        services.AddSingleton(
            new CapacitorServer(profiles.Resolution.ServerUrl ?? "", Config.Root, profiles));
        services.AddCapacitorHttp();
        _sp = services.BuildServiceProvider();

        return new ReportVersionCommand(
            _sp.GetRequiredService<CapacitorServer>(), _sp.GetRequiredService<ICapacitorHttpClient>());
    }

    public void Dispose() {
        _sp?.Dispose();
        _server.Stop();
    }

    void StubDiscovery(string provider) =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    // The command reads its token through the resolution's profile name, so the seeded profile has
    // to be the one the resolution names.
    ProfileContext Profiles(string profileName) =>
        Resolutions.Of(new Profile { ServerUrl = _server.Urls[0] }, profileName, _server.Urls[0]);

    async Task<ProfileContext> SeedValidTokenAsync(string profileName) {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-" + profileName,
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        return Profiles(profileName);
    }

    // ── Authenticated: exactly one GET, carrying the version header, no write side effect ──────

    [Test]
    public async Task Authenticated_MakesOneGetCarryingTheVersionHeader() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = await SeedValidTokenAsync("report-version-ok");

        var result = await Command(profiles).HandleAsync();

        await Assert.That(result).IsEqualTo(0);

        var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ProbePath).ToList();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RequestMessage.Method).IsEqualTo("GET");
        await Assert.That(requests[0].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    /// <summary>
    /// An <c>Auth:Provider=None</c> tenant: no bearer token exists (there is nothing to log
    /// into), but the request still authenticates via the server's synthetic principal, so the
    /// middleware still observes it — <see cref="AuthStatus.NoAuthRequired"/> must proceed exactly
    /// like <see cref="AuthStatus.Ok"/>, not be treated as "not authenticated".
    /// </summary>
    [Test]
    public async Task NoAuthTenant_MakesOneGetCarryingTheVersionHeader() {
        StubDiscovery("None");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var result = await Command(Resolutions.At(_server.Urls[0], Config.Root)).HandleAsync();

        await Assert.That(result).IsEqualTo(0);

        var requests = _server.LogEntries.Where(e => e.RequestMessage.Path == ProbePath).ToList();
        await Assert.That(requests.Count).IsEqualTo(1);
        await Assert.That(requests[0].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    // ── Not authenticated: no request at all, still returns 0 ─────────────────────────────────

    [Test]
    public async Task NotAuthenticated_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        // No token stored, no resolved profile — AuthStatus resolves to NotAuthenticated.
        var result = await Command(Resolutions.At(_server.Urls[0], Config.Root)).HandleAsync();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ProbePath)).IsFalse();
    }

    [Test]
    public async Task ExpiredToken_MakesNoRequest_AndReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        const string profileName = "report-version-expired";
        var profiles = Profiles(profileName);
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profileName, new StoredTokens {
            AccessToken    = "tok-expired",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(-1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = _server.Urls[0],
        });

        var result = await Command(profiles).HandleAsync();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == ProbePath)).IsFalse();
    }

    // ── Server-side failures: fail-open, never throws ──────────────────────────────────────────

    [Test]
    public async Task ServerErrorResponse_StillReturnsZero() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var profiles = await SeedValidTokenAsync("report-version-500");

        var result = await Command(profiles).HandleAsync();

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task SlowServer_StillReturnsZero_WithinItsOwnBudget() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(10)));

        var profiles = await SeedValidTokenAsync("report-version-slow");

        var sw     = Stopwatch.StartNew();
        var result = await Command(profiles).HandleAsync();
        sw.Stop();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// One source for the base URL, and both the discovery call and the probe must land on it:
    /// a probe rebuilt from anything else sends the bearer token to a host the client never
    /// authenticated against. <c>KCAP_URL</c> pointing elsewhere must not divert either half.
    /// </summary>
    [Test]
    public async Task Explicit_base_url_carries_both_the_discovery_and_the_probe() {
        StubDiscovery("github_app");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        var profiles = await SeedValidTokenAsync("report-version-one-host");

        using var _ = EnvScope.Exclusive("KCAP_URL", "http://127.0.0.1:1");

        await Assert.That(await Command(profiles).HandleAsync()).IsEqualTo(0);

        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/auth/config")).IsEqualTo(1);
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == ProbePath)).IsEqualTo(1);
    }

    /// <summary>A null baseUrl means resolution found no server (Program.cs already folded in
    /// <c>KCAP_URL</c>), so the probe must go nowhere real and still exit 0 — not fall back to an
    /// environment variable a second time and reach a server the caller did not name.</summary>
    [Test]
    public async Task No_base_url_probes_nothing_and_still_returns_zero() {
        StubDiscovery("None");
        _server.Given(Request.Create().WithPath(ProbePath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var _ = EnvScope.Exclusive("KCAP_URL", _server.Urls[0]);

        await Assert.That(await Command(Resolutions.None(Config.Root)).HandleAsync()).IsEqualTo(0);

        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == ProbePath)).IsEqualTo(0);
    }

    /// <summary>Discovery itself has no deadline; the command's own budget must still bound it.</summary>
    [Test]
    public async Task SlowDiscovery_StillReturnsZero_WithinBudget() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"github_app"}""")
                .WithDelay(TimeSpan.FromSeconds(10)));

        var profiles = await SeedValidTokenAsync("report-version-slow-discovery");

        var sw     = Stopwatch.StartNew();
        var result = await Command(profiles).HandleAsync();
        sw.Stop();

        await Assert.That(result).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(8));
    }

    [Test]
    public async Task UnreachableServer_StillReturnsZero() {
        // No discovery stub, no listener at all on this port — provider discovery's own catch
        // falls back to local tokens, finds none, resolves NotAuthenticated; even if it somehow
        // resolved Ok, the command's own try/catch must still swallow the failure.
        var result = await Command(Resolutions.At("http://127.0.0.1:1", Config.Root)).HandleAsync();

        await Assert.That(result).IsEqualTo(0);
    }

    /// <summary>
    /// Mirrors <c>Program.cs</c>'s dispatch: <c>report-version</c> is in <c>offlineCommands</c>,
    /// so a host with no server configured at all reaches this command with a null
    /// <c>baseUrl</c> — it must still hit this command's own fail-open logic and return 0 silently
    /// rather than the generic "No server configured" exit 1 the pre-dispatch gate would
    /// otherwise produce for any command not on that list.
    /// </summary>
    [Test]
    public async Task NoServerConfigured_MakesNoRequest_AndReturnsZero() {
        // Nothing resolved, no KCAP_URL: the command falls back to its hardcoded
        // "http://localhost:5108" default, which nothing is listening on here.
        var result = await Command(Resolutions.None(Config.Root)).HandleAsync();

        await Assert.That(result).IsEqualTo(0);
    }
}
