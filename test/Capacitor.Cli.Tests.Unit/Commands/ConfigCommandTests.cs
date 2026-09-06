using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ConfigCommandTests {
    // ── daemon.claude_path ───────────────────────────────────────────────────

    [Test]
    public async Task ApplySet_DaemonClaudePath_StoresValue() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "daemon.claude_path", "/opt/claude/bin/claude");

        await Assert.That(updated.Daemon!.ClaudePath).IsEqualTo("/opt/claude/bin/claude");
    }

    [Test]
    public async Task ApplySet_DaemonClaudePath_EmptyString_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "daemon.claude_path", ""))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_DaemonClaudePath_PreservesOtherDaemonSettings() {
        var profile = new Profile { Daemon = new DaemonSettings { Name = "mybot", MaxAgents = 3 } };

        var updated = ConfigCommand.ApplySet(profile, "daemon.claude_path", "/usr/local/bin/claude");

        await Assert.That(updated.Daemon!.Name).IsEqualTo("mybot");
        await Assert.That(updated.Daemon.MaxAgents).IsEqualTo(3);
        await Assert.That(updated.Daemon.ClaudePath).IsEqualTo("/usr/local/bin/claude");
    }

    // ── daemon.codex_path ────────────────────────────────────────────────────

    [Test]
    public async Task ApplySet_DaemonCodexPath_StoresValue() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "daemon.codex_path", "/opt/codex/bin/codex");

        await Assert.That(updated.Daemon!.CodexPath).IsEqualTo("/opt/codex/bin/codex");
    }

    [Test]
    public async Task ApplySet_DaemonCodexPath_EmptyString_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "daemon.codex_path", ""))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_DaemonCodexPath_PreservesOtherDaemonSettings() {
        var profile = new Profile { Daemon = new DaemonSettings { Name = "bot2", MaxAgents = 7 } };

        var updated = ConfigCommand.ApplySet(profile, "daemon.codex_path", "/usr/bin/codex");

        await Assert.That(updated.Daemon!.Name).IsEqualTo("bot2");
        await Assert.That(updated.Daemon.MaxAgents).IsEqualTo(7);
        await Assert.That(updated.Daemon.CodexPath).IsEqualTo("/usr/bin/codex");
    }

    // ── DaemonSettings JSON round-trip ───────────────────────────────────────

    [Test]
    public async Task DaemonSettings_JsonRoundTrip_PreservesClaudeAndCodexPaths() {
        var profile = new Profile {
            Daemon = new DaemonSettings {
                Name       = "test",
                ClaudePath = "/custom/claude",
                CodexPath  = "/custom/codex"
            }
        };
        var profileConfig = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> { ["default"] = profile }
        };

        var json    = JsonSerializer.Serialize(profileConfig, ProfileConfigJsonContextIndented.Default.ProfileConfig);
        var decoded = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        var daemon = decoded?.Profiles["default"].Daemon;
        await Assert.That(daemon?.ClaudePath).IsEqualTo("/custom/claude");
        await Assert.That(daemon?.CodexPath).IsEqualTo("/custom/codex");
    }

    [Test]
    public async Task DaemonSettings_JsonRoundTrip_OldProfileWithoutPaths_DefaultsToNull() {
        // Simulate an old config.json that has no claude_path / codex_path keys
        const string json = """
            {
              "version": 2,
              "active_profile": "default",
              "profiles": {
                "default": {
                  "server_url": "https://example.com",
                  "daemon": { "name": "bot", "max_agents": 2 }
                }
              },
              "profile_bindings": {}
            }
            """;

        var decoded = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        var daemon = decoded?.Profiles["default"].Daemon;
        await Assert.That(daemon?.ClaudePath).IsNull();
        await Assert.That(daemon?.CodexPath).IsNull();
    }

    // ── existing tests ───────────────────────────────────────────────────────

    [Test]
    public async Task ApplySet_DisableSessionGuidelines_True_UpdatesProfile() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "disable_session_guidelines", "true");

        await Assert.That(updated.DisableSessionGuidelines).IsTrue();
    }

    [Test]
    public async Task ApplySet_DisableSessionGuidelines_False_UpdatesProfile() {
        var profile = new Profile { DisableSessionGuidelines = true };

        var updated = ConfigCommand.ApplySet(profile, "disable_session_guidelines", "false");

        await Assert.That(updated.DisableSessionGuidelines).IsFalse();
    }

    [Test]
    public async Task ApplySet_DisableSessionGuidelines_InvalidValue_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "disable_session_guidelines", "maybe"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_DisableCoordinationNotices_True_UpdatesProfile() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "disable_coordination_notices", "true");

        await Assert.That(updated.DisableCoordinationNotices).IsTrue();
    }

    [Test]
    public async Task ApplySet_DisableCoordinationNotices_False_UpdatesProfile() {
        var profile = new Profile { DisableCoordinationNotices = true };

        var updated = ConfigCommand.ApplySet(profile, "disable_coordination_notices", "false");

        await Assert.That(updated.DisableCoordinationNotices).IsFalse();
    }

    [Test]
    public async Task ApplySet_DisableCoordinationNotices_InvalidValue_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "disable_coordination_notices", "nope"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_UnknownKey_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "made_up_key", "x"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplySet_UpdateCheck_PreservesExistingBehavior() {
        var profile = new Profile { UpdateCheck = true };

        var updated = ConfigCommand.ApplySet(profile, "update_check", "false");

        await Assert.That(updated.UpdateCheck).IsFalse();
    }

    [Test]
    public async Task ApplySet_ServerUrl_StoresValueVerbatim() {
        // ApplySet itself stays pure — normalization happens in Set, not here.
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "server_url", "https://example.com");

        await Assert.That(updated.ServerUrl).IsEqualTo("https://example.com");
    }

    // ── default_visibility ("project" added to the ladder) ────────────

    [Test]
    [Arguments("private")]
    [Arguments("project")]
    [Arguments("org_public")]
    [Arguments("public")]
    public async Task ApplySet_DefaultVisibility_AcceptsEachValidValue(string value) {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "default_visibility", value);

        await Assert.That(updated.DefaultVisibility).IsEqualTo(value);
    }

    [Test]
    public async Task ApplySet_DefaultVisibility_InvalidValue_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "default_visibility", "team"))
            .Throws<ArgumentException>();
    }

    // ── skills.auto_sync ─────────────────────────────────────────────────────

    [Test]
    public async Task Set_WithBlankActiveProfile_WritesTheDefaultProfile() {
        // Runtime profile resolution normalizes a blank active_profile to "default"; the write
        // path must land on the SAME identity, or the set reports success while the effective
        // profile never sees the value.
        using var tmp  = new TempDir();
        var root = new ConfigRoot(tmp.Path);
        Directory.CreateDirectory(tmp.Path);
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(root), """{"active_profile":"","profiles":{}}""");

        await new ConfigCommand(root, new FixedCapacitorHttpClient()).HandleAsync(["config", "set", "skills.auto_sync", "true"]);

        var cfg = await AppConfig.LoadProfileConfig(root);
        await Assert.That(cfg.Profiles.ContainsKey("")).IsFalse();
        await Assert.That(cfg.Profiles["default"].Skills!.AutoSync!.Value).IsTrue();

        await new ConfigCommand(root, new FixedCapacitorHttpClient()).HandleAsync(["config", "unset", "skills.auto_sync"]);
        var after = await AppConfig.LoadProfileConfig(root);
        await Assert.That(after.Profiles["default"].Skills!.AutoSync).IsNull();
    }

    [Test]
    public async Task ApplySet_SkillsAutoSync_ParsesBoolean() {
        var updated = ConfigCommand.ApplySet(new Profile(), "skills.auto_sync", "true");
        await Assert.That(updated.Skills!.AutoSync!.Value).IsTrue();
        var off = ConfigCommand.ApplySet(updated, "skills.auto_sync", "false");
        await Assert.That(off.Skills!.AutoSync!.Value).IsFalse();
    }

    [Test]
    public async Task ApplySet_SkillsAutoSync_RejectsNonBoolean() {
        await Assert.That(() => ConfigCommand.ApplySet(new Profile(), "skills.auto_sync", "yes"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplyUnset_SkillsAutoSync_ClearsTheKey() {
        var set = ConfigCommand.ApplySet(new Profile(), "skills.auto_sync", "true");
        var cleared = ConfigCommand.ApplyUnset(set, "skills.auto_sync");
        await Assert.That(cleared.Skills!.AutoSync).IsNull();
    }

    // ── flows.reviewer_vendor ────────────────────────────────────────────────

    [Test]
    public async Task ApplySet_FlowsReviewerVendor_StoresTrimmedLowercase() {
        var profile = new Profile();

        var updated = ConfigCommand.ApplySet(profile, "flows.reviewer_vendor", "  Claude ");

        await Assert.That(updated.Flows!.ReviewerVendor).IsEqualTo("claude");
    }

    [Test]
    public async Task ApplySet_FlowsReviewerVendor_BlankValue_Throws() {
        var profile = new Profile();

        await Assert.That(() => ConfigCommand.ApplySet(profile, "flows.reviewer_vendor", "   "))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ApplyUnset_FlowsReviewerVendor_ClearsThePreference() {
        var profile = ConfigCommand.ApplySet(new Profile(), "flows.reviewer_vendor", "codex");

        var updated = ConfigCommand.ApplyUnset(profile, "flows.reviewer_vendor");

        await Assert.That(updated.Flows!.ReviewerVendor).IsNull();
    }

    [Test]
    public async Task ApplyUnset_UnknownKey_Throws() {
        await Assert.That(() => ConfigCommand.ApplyUnset(new Profile(), "nope"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task EffectiveReviewerVendorPreference_BlankStoredValue_ReadsAsNoPreference() {
        var profile = new Profile { Flows = new FlowsSettings { ReviewerVendor = "  " } };

        await Assert.That(profile.EffectiveReviewerVendorPreference()).IsNull();
    }

    [Test]
    public async Task EffectiveReviewerVendorPreference_NoFlowsSettings_ReturnsNull() {
        var profile = new Profile();

        await Assert.That(profile.EffectiveReviewerVendorPreference()).IsNull();
    }

    [Test]
    public async Task EffectiveReviewerVendorPreference_TrimmedValue_ReturnsTrimmed() {
        var profile = new Profile { Flows = new FlowsSettings { ReviewerVendor = "  codex  " } };

        await Assert.That(profile.EffectiveReviewerVendorPreference()).IsEqualTo("codex");
    }
}
