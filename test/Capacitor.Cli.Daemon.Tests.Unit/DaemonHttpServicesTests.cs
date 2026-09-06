using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// The daemon's lanes resolve over the context it actually registers — mirrored here, and nothing
/// more, so a lane needing something <see cref="DaemonRunner"/> never registers still fails. It
/// assembles them partway through a method no test can enter, so that gap would otherwise surface
/// only as a crash on a real boot.
/// </summary>
public class DaemonHttpServicesTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    ServiceProvider Provider(string serverUrl) {
        var profiles = Resolutions.At(serverUrl.TrimEnd('/'), Config.Root);

        return new ServiceCollection()
            .AddSingleton(Config.Root)
            .AddSingleton(profiles)
            .AddDaemonHttp(Config.Root, new DaemonConfig { ServerUrl = serverUrl, Profiles = profiles })
            .BuildServiceProvider();
    }

    [Test]
    public async Task The_daemon_resolves_the_same_lanes_the_cli_does() {
        using var sp = Provider("https://example.test");

        await Assert.That(sp.GetService<ICapacitorHttpClient>()).IsNotNull();
        await Assert.That(sp.GetService<ISessionsApi>()).IsNotNull();
    }

    /// Resolving a lane is not enough: its handlers are transient and constructed only when a client
    /// is, so one the daemon fails to register first shows up on a live request, not at boot.
    [Test]
    public async Task Building_a_client_constructs_the_lane_handlers() {
        using var sp = Provider("https://example.test");

        using var client = sp.GetRequiredService<ICapacitorHttpClient>().Anonymous();

        await Assert.That(client).IsNotNull();
    }

    /// A trailing slash survives into every URL the lanes build, so it is trimmed once here rather
    /// than at each caller.
    [Test]
    public async Task The_server_the_lanes_target_carries_no_trailing_slash() {
        using var sp = Provider("https://example.test/");

        await Assert.That(sp.GetRequiredService<CapacitorServer>().Url).IsEqualTo("https://example.test");
    }
}
