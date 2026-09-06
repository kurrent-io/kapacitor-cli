using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Services;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Completely removes kcap from the local machine: stops daemons, kills
/// watcher processes, strips kcap entries from user-level Claude / Codex /
/// Cursor / Gemini hook files, deletes the kcap-owned Copilot hooks file, the
/// kcap Kiro agent (~/.kiro/agents/kcap.json) — restoring the default agent it
/// replaced — and the Pi live-ingest extension (~/.pi/agent/extensions/kcap.ts),
/// removes agent skills (and legacy Codex skills), and deletes the kcap config
/// directory.
///
/// With <c>--project</c>, also strips kcap entries from the cwd's git-root
/// <c>.claude/settings.local.json</c> and <c>.codex/hooks.json</c>. Project-scope
/// Cursor hooks are not a thing — Cursor only reads its user-scope hooks.json.
/// Per-agent selective cleanup is intentionally out of scope here; this command
/// covers all known agents.
/// </summary>
public sealed class UninstallCommand(
        DaemonStore store, ConfigRoot config, ProfileContext profiles, UserHome home,
        ICapacitorHttpClient http) {
    readonly HarnessPaths _paths = HarnessPaths.FromEnvironment(home);

    public async Task<int> HandleAsync(string[] args) {
        var skipPrompt     = args.Contains("--yes") || args.Contains("-y");
        var keepConfig     = args.Contains("--keep-config");
        var includeProject = args.Contains("--project");

        string? projectRoot = null;

        if (includeProject) {
            projectRoot = GitRepository.FindRoot(Environment.CurrentDirectory);

            if (projectRoot is null) {
                await Console.Error.WriteLineAsync(
                    $"--project requires a git working tree, but '{Environment.CurrentDirectory}' is not inside one.");
                await Console.Error.WriteLineAsync(
                    "Re-run from inside your repo, or drop --project to only remove user-level configuration.");

                return 1;
            }
        }

        var configDir = config.Directory;

        await Console.Out.WriteLineAsync("This will remove kcap from your machine:");
        await Console.Out.WriteLineAsync("  • Stop any running daemons and watcher processes");
        await Console.Out.WriteLineAsync($"  • Remove kcap entries from {_paths.Claude.UserSettings}");
        await Console.Out.WriteLineAsync($"  • Remove kcap entries from {_paths.Codex.UserHooksJson}");
        await Console.Out.WriteLineAsync($"  • Remove kcap entries from {_paths.Cursor.UserHooksJson}");
        await Console.Out.WriteLineAsync($"  • Remove {_paths.Copilot.KcapHooksJson}");
        await Console.Out.WriteLineAsync($"  • Remove kcap entries from {_paths.Gemini.SettingsJson}");
        await Console.Out.WriteLineAsync($"  • Remove {_paths.Kiro.KcapAgentJson} and restore the previous default Kiro agent");
        await Console.Out.WriteLineAsync($"  • Remove {_paths.Pi.KcapExtension}");
        await Console.Out.WriteLineAsync($"  • Remove {_paths.OpenCode.KcapPlugin}");
        await Console.Out.WriteLineAsync($"  • Remove the kcap plugin from {_paths.Antigravity.PluginDir}");
        await Console.Out.WriteLineAsync($"  • Remove agent skills under {_paths.Agents.UserSkillsDir}");

        if (projectRoot is not null) {
            await Console.Out.WriteLineAsync(
                $"  • Remove kcap entries from {Path.Combine(projectRoot, ".claude", "settings.local.json")}");
            await Console.Out.WriteLineAsync(
                $"  • Remove kcap entries from {Path.Combine(projectRoot, ".codex", "hooks.json")}");
        }

        if (!keepConfig) {
            await Console.Out.WriteLineAsync($"  • Delete the kcap config directory ({configDir})");
        }

        if (!skipPrompt) {
            await Console.Out.WriteAsync("Proceed? [y/N] ");
            var reply = await Console.In.ReadLineAsync();

            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");

                return 0;
            }
        }

        // Track every step's success so we can surface failures in the exit
        // code AND keep ~/.config/kcap in place when something went wrong
        // (so the user can re-run rather than losing local state on a partial
        // removal). Individual remove commands keep printing their own output
        // — this flag captures the boolean for the final decision only.
        var hadFailures = false;

        // Deregister any OS-managed daemon services FIRST. `daemon stop` defers to
        // the supervisor for service-managed daemons (a raw kill would be
        // auto-restarted), so without this the launchd/systemd/Scheduled-Task unit
        // would survive uninstall and keep relaunching a daemon whose config we're
        // about to delete. Uninstalling the unit also stops the running instance
        // (launchctl bootout / systemctl disable --now), after which the plain
        // `daemon stop --yes` below mops up any non-service daemons.
        try {
            var services = ServiceManagerFactory.ForCurrentOs(config, home);
            foreach (var id in services.ListInstalled()) {
                if (services.Uninstall(id, out var error)) {
                    await Console.Out.WriteLineAsync($"  • Removed daemon service '{id}' ({services.Describe()})");
                } else {
                    await Console.Error.WriteLineAsync($"  • Could not remove daemon service '{id}': {error}");
                    hadFailures = true;
                }
            }
        } catch (PlatformNotSupportedException) {
            // No service backend on this OS — nothing to deregister.
        }

        // Stop daemons first — they hold lock files inside the config dir we're
        // about to delete. --yes silences the multi-daemon confirmation so this
        // works non-interactively. A non-zero exit code means at least one
        // daemon couldn't be stopped; we leave the config dir alone in that case.
        if (await new DaemonCommands(store, config, profiles, home).HandleAsync(["daemon", "stop", "--yes"]) != 0) hadFailures = true;

        // Kill any orphaned watcher PIDs that the daemon stop didn't catch.
        if (await new CleanupCommand(config, profiles, http).HandleCleanup() != 0) hadFailures = true;

        var env           = PluginEnvironment.FromProcess(await AppConfig.LoadProfileConfig(config), home);
        var pluginCommand = new PluginCommand(env);

        // User-level agent integrations. Each remove command is idempotent and
        // no-ops if the target file doesn't exist, so it's safe to call all of
        // them unconditionally without sniffing which agents are installed.
        if (await pluginCommand.HandleAsync(["plugin", "remove"])                  != 0) hadFailures = true; // Claude
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--codex"])       != 0) hadFailures = true; // Codex hooks + skills + legacy
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--cursor"])      != 0) hadFailures = true;
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--copilot"])     != 0) hadFailures = true;
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--gemini"])      != 0) hadFailures = true; // shared ~/.gemini/settings.json
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--kiro"])        != 0) hadFailures = true; // ~/.kiro/agents/kcap.json + restore previous default agent
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--pi"])          != 0) hadFailures = true; // Pi extension (~/.pi/agent/extensions/kcap.ts)
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--opencode"])    != 0) hadFailures = true; // OpenCode plugin (~/.config/opencode/plugins/kcap.ts)
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--antigravity"]) != 0) hadFailures = true; // Antigravity kcap plugin (~/.gemini/config/plugins/kcap/)

        // Skills are removed by --codex above, but call --skills explicitly in
        // case the user only ever installed Cursor / agent-agnostic skills and
        // never had Codex hooks (the --codex path short-circuits on a missing
        // hooks file).
        if (await pluginCommand.HandleAsync(["plugin", "remove", "--skills"]) != 0) hadFailures = true;

        // Belt-and-braces marker cleanup. These hooks installers delete their
        // marker only when JSON entries changed; if the user manually pruned the
        // entries earlier (or installed via a pre-marker build that later
        // wrote a marker on first refresh), the marker survives and IsInstalled
        // still reports kcap as installed. uninstall promises a full
        // wipe, so always nuke the markers regardless of what the JSON state
        // looked like going in.
        //
        // The Cursor MCP marker is intentionally NOT swept here: `plugin remove
        // --cursor` above owns it via JsonMcpConfigWriter.Unregister, which clears
        // it on any non-Failed outcome (including hand-pruned entries) but
        // deliberately RETAINS it when the unregister fails, so a retry can still
        // identify the kcap-owned entries. An unconditional clear here would
        // defeat that recovery path.
        ClaudePluginInstaller.DeleteMarker(_paths.Claude.UserSettings);
        CodexHooksInstaller.DeleteMarker(_paths.Codex.UserHooksJson);
        CursorHooksInstaller.DeleteMarker(_paths.Cursor.UserHooksJson);
        GeminiHooksInstaller.DeleteMarker(_paths.Gemini.SettingsJson);
        KiroHooksInstaller.DeleteMarker(_paths.Kiro.KcapAgentJson);

        // Skill installer Remove uses the current SourceNames list, so any
        // kcap-* folder from an older release (renamed/retired skill)
        // would survive. Sweep the directory for our prefix to catch those.
        // Same for legacy ~/.codex/skills/.
        if (!SweepCapacitorPrefixedDirs(_paths.Agents.UserSkillsDir))            hadFailures = true;
        if (!SweepCapacitorPrefixedDirs(_paths.Codex.SkillsDir)) hadFailures = true;

        if (projectRoot is not null) {
            var claudeProject = Path.Combine(projectRoot, ".claude", "settings.local.json");
            var codexProject  = Path.Combine(projectRoot, ".codex", "hooks.json");

            try {
                var claudeOutcome = PluginCommand.RemoveClaudePlugin(claudeProject);

                if (claudeOutcome == PluginCommand.ClaudeRemovalOutcome.Removed) {
                    await Console.Out.WriteLineAsync($"Plugin removed (project: {claudeProject})");
                }
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync($"Could not update {claudeProject}: {ex.Message}");
                hadFailures = true;
            }

            if (File.Exists(codexProject)) {
                try {
                    if (PluginCommand.RemoveCodexHooks(codexProject)) {
                        await Console.Out.WriteLineAsync($"Codex hooks removed (project: {codexProject})");
                    }
                } catch (Exception ex) {
                    await Console.Error.WriteLineAsync($"Could not update Codex hooks at {codexProject}: {ex.Message}");
                    hadFailures = true;
                }
            }

            // Same marker-survives-after-manual-edit story as the user scope.
            ClaudePluginInstaller.DeleteMarker(claudeProject);
            CodexHooksInstaller.DeleteMarker(codexProject);
        }

        if (!keepConfig) {
            if (hadFailures) {
                await Console.Error.WriteLineAsync(
                    $"Skipping config-directory delete because earlier steps failed: {configDir}");
                await Console.Error.WriteLineAsync(
                    "Investigate the errors above, then re-run `kcap uninstall` to finish.");
            } else if (Directory.Exists(configDir)) {
                try {
                    Directory.Delete(configDir, recursive: true);
                    await Console.Out.WriteLineAsync($"Removed config directory: {configDir}");
                } catch (Exception ex) {
                    await Console.Error.WriteLineAsync($"Could not remove config directory {configDir}: {ex.Message}");
                    hadFailures = true;
                }
            }
        }

        if (hadFailures) {
            await Console.Error.WriteLineAsync("kcap uninstall finished with errors — see above.");

            return 1;
        }

        await Console.Out.WriteLineAsync("kcap uninstalled.");

        return 0;
    }

    /// <summary>
    /// Deletes every <c>kcap-*</c> directory directly under
    /// <paramref name="root"/>. Catches the cases where the installer's
    /// fixed name list doesn't match what's on disk: a skill renamed,
    /// retired, or added between releases. <c>kcap-</c> is our
    /// namespace prefix so this is safe; user-authored folders without it
    /// are untouched. Returns true on full success, false when any deletion
    /// (or the enumeration itself) failed — callers feed the result into
    /// their failure aggregator so a stuck folder doesn't get masked by
    /// a "kcap uninstalled." exit.
    /// </summary>
    static bool SweepCapacitorPrefixedDirs(string root) {
        if (!Directory.Exists(root)) return true;

        IEnumerable<string> dirs;
        try {
            dirs = Directory.EnumerateDirectories(root, "kcap-*").ToArray();
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not enumerate {root}: {ex.Message}");
            return false;
        }

        var ok = true;
        foreach (var dir in dirs) {
            try {
                Directory.Delete(dir, recursive: true);
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove {dir}: {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }
}
