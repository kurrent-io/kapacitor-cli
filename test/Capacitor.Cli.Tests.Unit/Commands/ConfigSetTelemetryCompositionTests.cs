using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Drives `kcap config set telemetry` through the real public entry point,
/// <see cref="ConfigCommand.HandleAsync"/>, to cover the COMPOSITION of
/// <see cref="ConfigCommand.TryApplyTelemetry"/> and <c>ConfigCommand.Set</c> that
/// <see cref="ConfigTelemetryKeyTests"/> cannot: that class calls <c>TryApplyTelemetry</c>
/// directly, so it stays green even if the early <c>return 0;</c> right after the telemetry
/// branch in <c>Set</c> went missing. Without that return, execution would fall through into
/// <c>ApplySet(profile, "telemetry", …)</c> — which throws "Unknown config key" — AFTER the
/// telemetry flag had already been persisted. A user would see a confusing crash right after
/// their opt-out silently took effect, and every existing test would stay green.
///
/// <para>Holds the <see cref="CliTelemetry.TestSink"/> key: the path it drives reaches
/// <see cref="CliTelemetry.DiscardAndDisable"/>, which clears whatever sink is live — including a
/// concurrently-running funnel test's. The on-disk state needs no key: config.json, telemetry.json
/// and the device id all live under this test's own root.</para>
/// </summary>
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class ConfigSetTelemetryCompositionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Test]
    public async Task Set_telemetry_off_returns_zero_and_leaves_an_existing_profile_on_disk_byte_for_byte_unchanged() {
        // Seed a distinctive profile so ANY write — not only a crash — would show up in the diff.
        var seeded = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new Profile { ServerUrl = "https://sentinel.invalid" }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => seeded);
        var before = await File.ReadAllTextAsync(ConfigPath);

        var exit = await new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        var after = await File.ReadAllTextAsync(ConfigPath);
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task Set_telemetry_off_never_creates_a_profile_config_when_none_existed() {
        // No config.json at all going in — proves the telemetry path never reaches
        // LoadProfileConfig/SaveProfileConfig, not merely that it round-trips one unchanged.
        await Assert.That(File.Exists(ConfigPath)).IsFalse();

        var exit = await new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }
}
