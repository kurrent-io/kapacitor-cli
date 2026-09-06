using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

// TryApplyTelemetry reaches CliTelemetry.DiscardAndDisable, whose statics are process-global.
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class ConfigTelemetryKeyTests {
    // One root per case covers both files: SetEnabled(false) deletes the device id as a side effect.
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("no")]
    public async Task Telemetry_off_persists_disabled(string value) {
        await Assert.That(new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();
    }

    [Test]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("yes")]
    public async Task Telemetry_on_persists_enabled(string value) {
        await Assert.That(new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsTrue();
    }

    [Test]
    public async Task Other_keys_are_not_claimed() {
        await Assert.That(new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).TryApplyTelemetry("server_url", "https://acme.kcap.ai")).IsFalse();
    }

    [Test]
    public async Task Invalid_telemetry_value_throws_with_an_actionable_message() {
        var ex = Assert.Throws<ArgumentException>(() => new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).TryApplyTelemetry("telemetry", "banana"));

        await Assert.That(ex!.Message.Contains("on")).IsTrue();
        await Assert.That(ex.Message.Contains("off")).IsTrue();
    }

    // Machine-scoped, so it must not have been written into the active profile.
    [Test]
    public async Task Telemetry_is_not_a_profile_key() {
        var ex = Assert.Throws<ArgumentException>(() => ConfigCommand.ApplySet(new Profile(), "telemetry", "off"));

        await Assert.That(ex!.Message.Contains("Unknown config key")).IsTrue();
    }
}
