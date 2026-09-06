using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ProfileCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task AddProfile_CreatesNewProfile() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient()).AddProfile(
            "contoso", "https://contoso.kcap.io",
            ["github.com/contoso/*"]
        );

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).ContainsKey("contoso");
        await Assert.That(config.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
        await Assert.That(config.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
    }

    [Test]
    public async Task RemoveProfile_DeletesProfile() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" },
                ["contoso"] = new() { ServerUrl = "https://contoso.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient()).RemoveProfile("contoso");

        await Assert.That(result).IsEqualTo(0);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;

        await Assert.That(config.Profiles).DoesNotContainKey("contoso");
    }

    [Test]
    public async Task AddProfile_SchemeLessInput_AddsHttpsAndStoresNormalizedUrl() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        // skipProbe defaults to true → no network, falls back to loopback heuristic.
        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient()).AddProfile(
            "contoso", "contoso.kcap.io", remotes: []);

        await Assert.That(result).IsEqualTo(0);

        var saved = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(configPath),
            ProfileConfigJsonContextIndented.Default.ProfileConfig)!;

        await Assert.That(saved.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
    }

    [Test]
    public async Task RemoveProfile_CannotRemoveDefault() {
        var configPath = AppConfig.GetConfigPath(Config.Root);

        var initial = new ProfileConfig {
            Profiles = new() {
                ["default"] = new() { ServerUrl = "https://default.com" }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(initial, ProfileConfigJsonContextIndented.Default.ProfileConfig));

        var result = await new ProfileCommand(Config.Root, new FixedCapacitorHttpClient()).RemoveProfile("default");

        await Assert.That(result).IsEqualTo(1);

        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig)!;
        await Assert.That(config.Profiles).ContainsKey("default");
    }
}
