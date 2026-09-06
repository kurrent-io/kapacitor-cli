using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Mcp;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

// VendorEnvOverrides: uninstall resolves every vendor layout through HarnessPaths, so it reads
// each override variable a peer suite clears.
[NotInParallel(["VendorEnvOverrides", "CwdMutation"])]
public class UninstallCommandTests {
    // Uninstall runs `daemon stop --yes`, which enumerates this directory and kills the PIDs it
    // finds; a shared one holding the test runner's own PID makes it kill its own tree.
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task User_level_uninstall_removes_kcap_entries_and_preserves_user_data() {
        await using var fixture = await Fixture.CreateAsync();

        // Seed Claude user settings with kcap + user-authored content.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {
              "userKey": "must-survive",
              "extraKnownMarketplaces": {
                "kcap":  { "source": { "source": "directory", "path": "/p" } },
                "userMarket": { "source": { "source": "directory", "path": "/u" } }
              },
              "enabledPlugins": { "kcap@kcap": true, "user@market": true }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        // Codex user hooks with a non-kcap entry alongside kcap.
        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexHooks = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(codexHooks, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        // Cursor user hooks similarly.
        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var cursorHooks = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(cursorHooks, """
            {
              "version": 1,
              "hooks": {
                "sessionStart": [
                  { "command": "user-script" },
                  { "command": "kcap hook --cursor" }
                ]
              }
            }
            """);

        // Gemini shared settings.json: kcap hook + user hook + unrelated setting, plus marker.
        var geminiDir = Path.Combine(fixture.Home, ".gemini");
        Directory.CreateDirectory(geminiDir);
        var geminiSettings = Path.Combine(geminiDir, "settings.json");
        await File.WriteAllTextAsync(geminiSettings, """
            {
              "theme": "keep-me",
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script" }] },
                  { "hooks": [{ "type": "command", "command": "kcap hook --gemini" }] }
                ]
              }
            }
            """);
        GeminiHooksInstaller.WriteMarker(geminiSettings);

        // Skills present in ~/.agents/skills with marker.
        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            Directory.CreateDirectory(Path.Combine(skillsDir, $"kcap-{name}"));
        }
        var userSkill = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(userSkill);
        await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), "user content");

        // Seed config dir with a real file so we can verify deletion.
        await File.WriteAllTextAsync(Path.Combine(fixture.ConfigDir, "profiles.json"), "{}");

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes"]);
        await Assert.That(exit).IsEqualTo(0);

        // Claude: kcap entries gone, user entries preserved, marker removed.
        var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(claudeRoot["userKey"]!.GetValue<string>()).IsEqualTo("must-survive");
        await Assert.That(claudeRoot["enabledPlugins"]!["kcap@kcap"]).IsNull();
        await Assert.That(claudeRoot["enabledPlugins"]!["user@market"]!.GetValue<bool>()).IsTrue();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["kcap"]).IsNull();
        await Assert.That(claudeRoot["extraKnownMarketplaces"]!["userMarket"]).IsNotNull();
        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();

        // Codex: kcap entry gone, user entry preserved.
        var codexRoot = JsonNode.Parse(await File.ReadAllTextAsync(codexHooks))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Cursor: kcap entry gone, user entry preserved.
        var cursorRoot   = JsonNode.Parse(await File.ReadAllTextAsync(cursorHooks))!.AsObject();
        var sessionStart2 = cursorRoot["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(sessionStart2.Count).IsEqualTo(1);
        await Assert.That(sessionStart2[0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");

        // Gemini: kcap hook gone, user hook + unrelated setting preserved, marker removed.
        var geminiRoot  = JsonNode.Parse(await File.ReadAllTextAsync(geminiSettings))!.AsObject();
        await Assert.That(geminiRoot["theme"]!.GetValue<string>()).IsEqualTo("keep-me");
        var geminiStart = geminiRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(geminiStart.Count).IsEqualTo(1);
        await Assert.That(geminiStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
        await Assert.That(GeminiHooksInstaller.IsInstalled(geminiSettings)).IsFalse();

        // Skills: kcap-* folders removed, user-authored folder intact.
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            await Assert.That(Directory.Exists(Path.Combine(skillsDir, $"kcap-{name}"))).IsFalse();
        }
        await Assert.That(Directory.Exists(userSkill)).IsTrue();

        // Config dir fully deleted.
        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_removes_pi_extension() {
        // Pi has no hook file — its live-ingest integration is the
        // ~/.pi/agent/extensions/kcap.ts extension. uninstall must delete it
        // (+ the version marker) or pi keeps auto-loading kcap.ts after the user
        // removed kcap. A user-authored sibling extension must survive.
        await using var fixture = await Fixture.CreateAsync();

        var extDir = Path.Combine(fixture.Home, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var kcapTs    = Path.Combine(extDir, "kcap.ts");
        var markerPi  = Path.Combine(extDir, ".kcap-extension-version");
        var userExt   = Path.Combine(extDir, "user-ext.ts");
        await File.WriteAllTextAsync(kcapTs, "export default function(pi){}");
        await File.WriteAllTextAsync(markerPi, CapacitorVersion.Current());
        await File.WriteAllTextAsync(userExt, "export default function(pi){}");

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(kcapTs)).IsFalse();
        await Assert.That(File.Exists(markerPi)).IsFalse();
        await Assert.That(File.Exists(userExt)).IsTrue();
    }

    [Test]
    public async Task User_level_uninstall_removes_kiro_agent() {
        // Kiro has no shell hooks — its integration is the kcap-owned agent file
        // ~/.kiro/agents/kcap.json (+ version marker), and install flips
        // chat.defaultAgent to it. uninstall must delete the agent file + marker
        // (otherwise `kiro` keeps loading kcap's hooks). A user-authored sibling
        // agent must survive. No ~/.kiro/settings/cli.json is seeded, so the
        // default-agent restore is correctly skipped (no kiro-cli needed).
        await using var fixture = await Fixture.CreateAsync();

        var agentsDir = Path.Combine(fixture.Home, ".kiro", "agents");
        Directory.CreateDirectory(agentsDir);
        var kcapAgent = Path.Combine(agentsDir, "kcap.json");
        var marker    = Path.Combine(agentsDir, KiroHooksInstaller.MarkerFileName);
        var userAgent = Path.Combine(agentsDir, "my-agent.json");
        await File.WriteAllTextAsync(kcapAgent, """
            {"name":"kcap","hooks":{"agentSpawn":[{"command":"kcap hook --kiro --event agentSpawn"}]}}
            """);
        await File.WriteAllTextAsync(marker, CapacitorVersion.Current());
        await File.WriteAllTextAsync(userAgent, """{"name":"my-agent"}""");

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(kcapAgent)).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
        await Assert.That(File.Exists(userAgent)).IsTrue();
        await Assert.That(KiroHooksInstaller.IsInstalled(kcapAgent)).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_codex_hooks() {
        // Regression: a user who installed via the pre-rename `kapacitor` CLI
        // has hooks.json entries pointing at `kapacitor codex-hook`. Running
        // `kcap uninstall` must clean those up — the rename PR did not migrate
        // the detection marker, so legacy entries previously survived.
        await using var fixture = await Fixture.CreateAsync();

        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        var codexHooks = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(codexHooks, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var codexRoot    = JsonNode.Parse(await File.ReadAllTextAsync(codexHooks))!.AsObject();
        var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_cursor_hooks() {
        // Mirror of the Codex legacy regression: a user who installed via the
        // pre-rename `kapacitor` CLI has cursor hooks.json entries pointing at
        // `kapacitor hook --cursor`. Uninstall must remove them while keeping
        // user-authored sibling entries intact.
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var cursorHooks = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(cursorHooks, """
            {
              "version": 1,
              "hooks": {
                "sessionStart": [
                  { "command": "user-script" },
                  { "command": "kapacitor hook --cursor" }
                ]
              }
            }
            """);

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var cursorRoot   = JsonNode.Parse(await File.ReadAllTextAsync(cursorHooks))!.AsObject();
        var sessionStart = cursorRoot["hooks"]!["sessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
        await Assert.That(sessionStart[0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
    }

    [Test]
    public async Task User_level_uninstall_removes_legacy_kapacitor_claude_settings() {
        // Regression: a user who installed via the pre-rename `kapacitor` CLI
        // has `kapacitor@kapacitor` / `kapacitor@kurrent` in enabledPlugins and
        // a `kapacitor` marketplace entry. Uninstall must strip those while
        // preserving sibling settings.
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {
              "userKey": "keep",
              "enabledPlugins": {
                "kapacitor@kapacitor": true,
                "kapacitor@kurrent":   true,
                "other-plugin@vendor": true
              },
              "extraKnownMarketplaces": {
                "kapacitor": { "source": { "source": "directory", "path": "/legacy" } },
                "other":     { "source": { "source": "directory", "path": "/other" } }
              }
            }
            """);

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root         = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        var enabled      = root["enabledPlugins"]!.AsObject();
        var marketplaces = root["extraKnownMarketplaces"]!.AsObject();

        await Assert.That(enabled["kapacitor@kapacitor"]).IsNull();
        await Assert.That(enabled["kapacitor@kurrent"]).IsNull();
        await Assert.That(enabled["other-plugin@vendor"]!.GetValue<bool>()).IsTrue();

        await Assert.That(marketplaces["kapacitor"]).IsNull();
        await Assert.That(marketplaces["other"]).IsNotNull();

        await Assert.That(root["userKey"]!.GetValue<string>()).IsEqualTo("keep");
    }

    [Test]
    public async Task Keep_config_preserves_config_dir_but_removes_integrations() {
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var claudeSettings = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(claudeSettings, """
            {"enabledPlugins": {"kcap@kcap": true}}
            """);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"keep"}""");

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(claudeSettings))!.AsObject();
        await Assert.That(root["enabledPlugins"]!["kcap@kcap"]).IsNull();

        await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("keep");
    }

    [Test]
    public async Task Project_flag_strips_project_scope_entries() {
        await using var fixture = await Fixture.CreateAsync();

        // Fake project: a temp dir with a .git directory makes GitRepository.FindRoot
        // return it as the working tree root.
        using var tmp = new TempDir();
        tmp.CreateDir(".git");

        var projectClaude = tmp.PathTo(".claude", "settings.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectClaude)!);
        await File.WriteAllTextAsync(projectClaude, """
            {
              "userLocal": "keep",
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var projectCodex = tmp.PathTo(".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectCodex)!);
        await File.WriteAllTextAsync(projectCodex, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "user-script", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var originalCwd = Environment.CurrentDirectory;
        try {
            Environment.CurrentDirectory = tmp.Path;

            var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--project", "--keep-config"]);
            await Assert.That(exit).IsEqualTo(0);

            var claudeRoot = JsonNode.Parse(await File.ReadAllTextAsync(projectClaude))!.AsObject();
            await Assert.That(claudeRoot["userLocal"]!.GetValue<string>()).IsEqualTo("keep");
            await Assert.That(claudeRoot["enabledPlugins"]!["kcap@kcap"]).IsNull();

            var codexRoot    = JsonNode.Parse(await File.ReadAllTextAsync(projectCodex))!.AsObject();
            var sessionStart = codexRoot["hooks"]!["SessionStart"]!.AsArray();
            await Assert.That(sessionStart.Count).IsEqualTo(1);
            await Assert.That(sessionStart[0]!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("user-script");
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    // Globally sequential rather than keyed. The old "ConsoleStreams" key had no other member, so it
    // serialized this Console.Error capture against nothing: another capturing test could still run
    // concurrently, adopt this test's writer as its "original", and restore it mid-flight. Bare
    // Bare NotInParallel subsumes the VendorEnvOverrides / CwdMutation groups this test also
    // needs, since nothing runs alongside it.
    [Test, NotInParallel]
    public async Task Project_flag_errors_when_not_inside_git_tree() {
        await using var fixture = await Fixture.CreateAsync();

        // A scratch dir with NO .git anywhere up the tree.
        using var tmp = new TempDir();
        var originalCwd = Environment.CurrentDirectory;
        using var capture = ConsoleOutput.StartErrorCapture();

        try {
            Environment.CurrentDirectory = tmp.Path;

            var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--project"]);
            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capture.GetCapturedError()).Contains("--project requires a git working tree");
        } finally {
            Environment.CurrentDirectory = originalCwd;
        }
    }

    [Test]
    public async Task Marker_only_install_is_purged_even_when_json_has_no_kcap_entries() {
        // Regression for the "marker survives manual JSON edit" leak: the
        // upstream plugin removers delete the marker only when JSON entries
        // changed. If a user removed kcap entries by hand earlier, the
        // marker survives and IsInstalled keeps returning true. uninstall
        // must always nuke the markers.
        await using var fixture = await Fixture.CreateAsync();

        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        // settings.json present but no kcap entries.
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), """{"userKey":"x"}""");
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var codexDir = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        await File.WriteAllTextAsync(Path.Combine(codexDir, "hooks.json"), """{"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        await File.WriteAllTextAsync(Path.Combine(cursorDir, "hooks.json"), """{"version":1,"hooks":{}}""");
        await File.WriteAllTextAsync(
            Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(codexDir,  CodexHooksInstaller.MarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(cursorDir, CursorHooksInstaller.MarkerFileName))).IsFalse();
    }

    [Test]
    public async Task User_level_uninstall_purges_cursor_mcp_marker_even_when_json_has_no_kcap_entries() {
        // If a user hand-edited ~/.cursor/mcp.json to remove the kcap entries,
        // `plugin remove --cursor` still clears the sidecar ownership marker:
        // JsonMcpConfigWriter.Unregister clears it on any non-Failed outcome,
        // including when there were no entries left to remove. (The failure
        // path — where the marker is deliberately retained for retry — is
        // covered by User_level_uninstall_keeps_cursor_mcp_marker_when_unregister_fails.)
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var mcpPath = Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, """{"mcpServers":{}}""");

        var home   = fixture.UserHome;
        var marker = new McpMarker("cursor", home);
        marker.Record(mcpPath, ["kcap-review"]); // simulates a marker surviving a manual JSON edit
        await Assert.That(marker.Owned(mcpPath).ToArray()).IsNotEmpty();

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), home, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(new McpMarker("cursor", home).Owned(mcpPath).ToArray()).IsEmpty();
    }

    [Test]
    public async Task User_level_uninstall_keeps_cursor_mcp_marker_when_unregister_fails() {
        // Regression (self-review): when ~/.cursor/mcp.json can't be
        // parsed/rewritten, JsonMcpConfigWriter.Unregister fails-closed and
        // deliberately RETAINS the ownership marker so a later retry can still
        // identify the kcap-* entries as kcap-owned. uninstall records the
        // failure but must NOT then wipe the marker — doing so would orphan
        // those entries (a retry would preserve them as user-authored). The
        // Cursor MCP marker is owned by `plugin remove --cursor`, not the sweep.
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var mcpPath = Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, "{ this is not valid json"); // → Unregister returns Failed

        var home   = fixture.UserHome;
        var marker = new McpMarker("cursor", home);
        marker.Record(mcpPath, ["kcap-review"]);
        await Assert.That(marker.Owned(mcpPath).ToArray()).IsNotEmpty();

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), home, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsNotEqualTo(0); // the failed cursor MCP unregister propagates

        // Marker retained → a retry after the user fixes the file can still find + remove the kcap entries.
        await Assert.That(new McpMarker("cursor", home).Owned(mcpPath).ToArray()).IsNotEmpty();
    }

    [Test]
    public async Task Sweep_removes_kcap_prefixed_skill_folders_not_in_current_source_list() {
        // Regression for the "retired/renamed skill folder survives" leak:
        // AgentsSkillsInstaller.Remove uses the current SourceNames list, so
        // a kcap-* folder from an older release isn't matched. uninstall
        // must sweep the directory for our prefix to catch those.
        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);

        // A skill from the current list (handled by Remove) and a retired one
        // (only handled by the sweep). Plus a user-authored folder that must
        // survive both.
        var currentSkill = Path.Combine(skillsDir, $"kcap-{AgentsSkillsInstaller.SourceNames[0]}");
        var retiredSkill = Path.Combine(skillsDir, "kcap-retired-from-v0.42");
        var userSkill    = Path.Combine(skillsDir, "user-authored");
        Directory.CreateDirectory(currentSkill);
        Directory.CreateDirectory(retiredSkill);
        Directory.CreateDirectory(userSkill);

        // Legacy Codex skills dir: same story.
        var legacyDir = Path.Combine(fixture.Home, ".codex", "skills");
        Directory.CreateDirectory(legacyDir);
        var legacyRetired = Path.Combine(legacyDir, "kcap-also-retired");
        Directory.CreateDirectory(legacyRetired);

        var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes", "--keep-config"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(Directory.Exists(currentSkill)).IsFalse();
        await Assert.That(Directory.Exists(retiredSkill)).IsFalse();
        await Assert.That(Directory.Exists(legacyRetired)).IsFalse();
        await Assert.That(Directory.Exists(userSkill)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Config_dir_is_preserved_when_user_level_steps_fail() {
        // Regression for the "discarded return codes" issue: when a step
        // returns non-zero, uninstall must NOT delete ~/.config/kcap —
        // doing so destroys the only state that lets the user re-run and
        // finish, and it would silently report success.
        //
        // Windows file ACLs would need an entirely different setup, so this
        // case is Unix-only. The aggregation logic is the same on every
        // platform; covering Unix is sufficient for regression purposes.
        await using var fixture = await Fixture.CreateAsync();

        // Force PluginCommand's Claude remove to return 1 by leaving a valid
        // kcap entry behind a read-only file: File.Exists passes, JSON
        // parses cleanly, then File.WriteAllText hits UnauthorizedAccessException
        // — which the remover's catch translates into exit code 1.
        var claudeDir = Path.Combine(fixture.Home, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {"enabledPlugins": {"kcap@kcap": true}}
            """);
        File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var sentinel = Path.Combine(fixture.ConfigDir, "profiles.json");
        await File.WriteAllTextAsync(sentinel, """{"sentinel":"survives-partial-failure"}""");

        try {
            var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).Contains("survives-partial-failure");
        } finally {
            // Restore write so the test fixture can be deleted.
            File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Cursor_hooks_write_failure_propagates_to_exit_code() {
        // Cursor's RemoveCursor previously returned 0 even when the helper's
        // inner try/catch swallowed a write failure. The helper now throws,
        // RemoveCursor catches + returns 1, and uninstall aggregates that
        // into hadFailures.
        await using var fixture = await Fixture.CreateAsync();

        var cursorDir = Path.Combine(fixture.Home, ".cursor");
        Directory.CreateDirectory(cursorDir);
        var hooksPath = Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);
        File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        try {
            var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            // Failure path skips the config-dir delete so the user can re-run.
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            File.SetUnixFileMode(hooksPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Sweep_failure_propagates_to_exit_code() {
        // SweepCapacitorPrefixedDirs previously logged Directory.Delete errors
        // but never flipped hadFailures, so a stuck kcap-* folder under
        // ~/.agents/skills/ would leave uninstall claiming success.
        await using var fixture = await Fixture.CreateAsync();

        var skillsDir = Path.Combine(fixture.Home, ".agents", "skills");
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(Path.Combine(skillsDir, "kcap-stuck"));

        // Strip write permission on the parent. Directory.Delete on a child
        // needs write+exec on the parent on every Unix; without write the
        // unlink fails with EACCES and Directory.Delete surfaces it.
        File.SetUnixFileMode(skillsDir,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try {
            var exit = await new UninstallCommand(Daemons.Store, fixture.Root, Resolutions.None(fixture.Root), fixture.UserHome, new FixedCapacitorHttpClient()).HandleAsync(["uninstall", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(Directory.Exists(fixture.ConfigDir)).IsTrue();
        } finally {
            // Restore so the fixture can be cleaned up.
            File.SetUnixFileMode(skillsDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    sealed class Fixture : IAsyncDisposable {
        TempHome? _tempHome;

        public required string Home      { get; init; }
        public required string ConfigDir { get; init; }

        public ConfigRoot Root     => new(ConfigDir);
        public UserHome   UserHome => new(Home);

        public static Task<Fixture> CreateAsync() {
            var tmp = new TempHome();
            var configDir = tmp.CreateDir(".config", "kcap").Path;

            return Task.FromResult(new Fixture {
                Home      = tmp.Path,
                ConfigDir = configDir,
                _tempHome = tmp
            });
        }

        public ValueTask DisposeAsync() {
            _tempHome?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
