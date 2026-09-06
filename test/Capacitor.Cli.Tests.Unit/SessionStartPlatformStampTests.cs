using Capacitor.Cli.Core;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

public class SessionStartPlatformStampTests {
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Stamp_carries_the_normalized_platform() {
        // The field feeds the server's live applicability gate; the vocabulary is closed
        // (macos/linux/windows) and every CI host is one of them.
        var body = new JsonObject();
        SessionStartInventory.Stamp(body, Config.Root, Home);

        var platform = (string?)body["platform"];
        await Assert.That(platform).IsEqualTo(HostPlatform.Normalized);
        await Assert.That(new[] { "macos", "linux", "windows" }).Contains(platform!);
    }
}
