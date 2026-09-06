using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Setup chooses a server part-way through its run, and the legs that follow the choice — the
/// browser first-run flow, the import step, the deferred daemon request — all have to reach THAT
/// server. The process container resolved its own at startup, before the choice existed and null on
/// a first run, so a client drawn from it authenticates against the wrong server or refuses to build.
/// </summary>
public class SetupChosenServerTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SetupCommand Started(ProfileContext startup) {
        var factory = new PlainHttpClientFactory();

        return new SetupCommand(
            Config.Root, startup, AuthFixtures.NewTokenStore(Config.Root), factory,
            new AuthProxyClient(new HttpClient()), new WorkOSClient(factory), new GitHubOAuthClient(factory),
            new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), new TenantProvisioningClient(new HttpClient()),
            new AuthProviderDiscovery(factory));
    }

    /// A first run: nothing resolved a server before the command started, which is the case that
    /// makes the process container unusable rather than merely wrong.
    [Test]
    public async Task A_server_chosen_mid_run_is_reachable_though_startup_resolved_none() {
        using var scoped = Started(Resolutions.None(Config.Root)).HttpForChosenServer("https://chosen.test");

        var server = scoped.GetRequiredService<CapacitorServer>();

        await Assert.That(server.Url).IsEqualTo("https://chosen.test");
        await Assert.That(server.Usable).IsTrue();
    }

    /// Re-pointing an already-configured install: the request must not go to the old server, and the
    /// credential must not be looked up against it either.
    [Test]
    public async Task A_re_pointed_server_replaces_the_one_resolved_at_startup() {
        using var scoped = Started(Resolutions.At("https://startup.test", Config.Root))
            .HttpForChosenServer("https://chosen.test");

        var server = scoped.GetRequiredService<CapacitorServer>();

        await Assert.That(server.Url).IsEqualTo("https://chosen.test");
        await Assert.That(server.Profiles.Resolution.ServerUrl).IsEqualTo("https://chosen.test");
    }

    /// The profile name is the process's: setup writes to the profile the user is on, so the token
    /// lookup has to name that same one rather than inventing a fresh resolution.
    [Test]
    public async Task The_chosen_server_keeps_the_profile_the_run_writes_to() {
        var startup = Resolutions.At("https://startup.test", Config.Root);

        using var scoped = Started(startup).HttpForChosenServer("https://chosen.test");

        await Assert.That(scoped.GetRequiredService<CapacitorServer>().Profiles.Name).IsEqualTo(startup.Name);
    }
}
