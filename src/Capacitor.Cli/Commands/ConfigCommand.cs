using System.Text.Json;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core;
using ProfileConfigJsonContextIndented = Capacitor.Cli.Core.Config.ProfileConfigJsonContextIndented;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

public sealed class ConfigCommand(ConfigRoot config, ICapacitorHttpClient http) {
    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kcap config <show|set|unset> [key] [value]");

            return 1;
        }

        var subcommand = args[1];
        var skipProbe  = args.Contains("--no-probe");

        return subcommand switch {
            "show"                        => await Show(),
            "set" when args.Length >= 4   => await Set(args[2], args[3], skipProbe),
            "set"                         => SetUsage(),
            "unset" when args.Length >= 3 => await Unset(args[2]),
            "unset"                       => UnsetUsage(),
            _                             => UnknownSubcommand(subcommand)
        };
    }

    async Task<int> Show() {
        var profileConfig = await AppConfig.LoadProfileConfig(config);
        var json          = JsonSerializer.Serialize(profileConfig, ProfileConfigJsonContextIndented.Default.ProfileConfig);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath(config)}");

        // Telemetry is machine-scoped (see TryApplyTelemetry), so it isn't part of the profile
        // JSON above. Reporting the Reason alongside the state is what lets a user with an
        // inherited DO_NOT_TRACK tell that apart from their own `config set telemetry off`.
        var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled(config));
        await Console.Out.WriteLineAsync($"  Telemetry: {(decision.Enabled ? "on" : "off")} (source: {decision.Reason})");

        return 0;
    }

    async Task<int> Set(string key, string value, bool skipProbe) {
        // Telemetry consent is a property of the machine, not of whichever profile happens to be
        // active — switching profiles must never silently re-enable reporting. So it is
        // special-cased ahead of the profile load, the same way server_url is normalized below.
        if (TryApplyTelemetry(key, value)) {
            await Console.Out.WriteLineAsync(
                $"Set telemetry = {(TelemetryState.PersistedEnabled(config) is true ? "on" : "off")} (machine-wide)");

            return 0;
        }

        if (key == "server_url") {
            var result = await ServerUrlNormalizer.NormalizeAsync(
                value, skipProbe, CancellationToken.None, ServerUrlNormalizer.ProbeWith(http));

            if (result.Warning is not null)
                await Console.Error.WriteLineAsync($"Warning: {result.Warning}");

            value = result.Url;
        }

        var profileName = "default";

        await ConfigMutator.MutateAsync(config, c => {
            profileName = c.ActiveName;
            var profile = ApplySet(c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), key, value);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = profile } };
        });

        // Echo what was STORED, not what was typed: flows.reviewer_vendor is canonicalized on the way
        // in, and confirming "Set flows.reviewer_vendor = Codex" while holding "codex" invites a bug
        // report about a value that is actually correct.
        var stored = key == "flows.reviewer_vendor" ? ReviewerVendors.Normalize(value) : value;

        await Console.Out.WriteLineAsync($"Set {key} = {stored} (profile: {profileName})");

        // Advisory only, and deliberately after the success line: the server owns the authoritative
        // vendor list, so an unrecognized token here is a typo hint, not a rejection — refusing it
        // would break the first user of a vendor newer than their CLI.
        if (key == "flows.reviewer_vendor" && !ReviewerVendors.IsKnown(stored))
            await Console.Error.WriteLineAsync(
                $"Warning: '{stored}' is not a vendor this kcap version knows ({ReviewerVendors.Tokens}); " +
                "the server has the authoritative list and will reject an unknown vendor at start time.");

        return 0;
    }

    async Task<int> Unset(string key) {
        var profileName = "default";

        await ConfigMutator.MutateAsync(config, c => {
            profileName = c.ActiveName;
            var profile = ApplyUnset(c.Profiles.GetValueOrDefault(profileName) ?? new Profile(), key);

            return c with { Profiles = new Dictionary<string, Profile>(c.Profiles) { [profileName] = profile } };
        });

        await Console.Out.WriteLineAsync($"Unset {key} (profile: {profileName})");

        return 0;
    }

    /// <summary>
    /// Handles the machine-scoped <c>telemetry</c> key. Unlike every other config key, telemetry
    /// consent is deliberately NOT stored in the active <see cref="Profile"/>: it is a decision
    /// about the machine being measured, not about whichever workspace happens to be selected, and
    /// having it flip when a person switches profiles would be surprising at best and a dark
    /// pattern at worst. So it is intercepted here, ahead of <see cref="ApplySet"/> and the profile
    /// load, the same way <c>server_url</c> is intercepted in <see cref="Set"/> for normalisation.
    /// Returns false for any other key so <see cref="Set"/> falls through to the profile path
    /// unchanged, and <see cref="ApplySet"/> itself never learns the key exists.
    /// </summary>
    public bool TryApplyTelemetry(string key, string value) {
        if (key != "telemetry") return false;

        var enabled = TryParseTelemetryToggle(value)
            ?? throw new ArgumentException($"Invalid value for telemetry: '{value}'. Must be on or off.");

        TelemetryState.SetEnabled(enabled, config);

        // Belt-and-braces, not the primary defence: Program.cs's pre-Initialize check (see
        // Program.cs, right before CliTelemetry.Initialize) already stops telemetry from ever
        // activating for a plain `config set telemetry off`, so there is normally nothing left to
        // discard by the time this runs. But KCAP_TELEMETRY=1 legitimately overrides a persisted
        // "off" (finding: env outranks config), so Initialize can still have come up live despite
        // the value being applied here — tear it down in that case too.
        if (!enabled) CliTelemetry.DiscardAndDisable();

        return true;
    }

    /// <summary>
    /// Pure recognizer for the "telemetry" value vocabulary. Shared by <see cref="TryApplyTelemetry"/>
    /// (which throws on an unrecognized value — invalid input is this command's problem to report)
    /// and Program.cs's pre-<c>CliTelemetry.Initialize</c> short-circuit (which must NOT throw: an
    /// invalid value there is reported normally once the command actually dispatches). Returns
    /// null for anything unrecognized.
    /// </summary>
    internal static bool? TryParseTelemetryToggle(string value) =>
        value.Trim().ToLowerInvariant() switch {
            "on" or "true" or "1" or "yes"  => true,
            "off" or "false" or "0" or "no" => false,
            _                                => null,
        };

    /// <summary>
    /// Applies a single <c>key = value</c> update to a <see cref="Profile"/>. Pure function, exposed for testing.
    /// Throws <see cref="ArgumentException"/> on unknown keys or invalid values.
    /// </summary>
    public static Profile ApplySet(Profile profile, string key, string value) =>
        key switch {
            "server_url" => profile with { ServerUrl = value },
            "daemon.name" => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { Name = value } },
            "daemon.max_agents" when int.TryParse(value, out var n) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { MaxAgents = n } },
            "daemon.claude_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { ClaudePath = value } },
            "daemon.claude_path" => throw new ArgumentException("Invalid value for daemon.claude_path: must not be empty."),
            "daemon.codex_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { CodexPath = value } },
            "daemon.codex_path" => throw new ArgumentException("Invalid value for daemon.codex_path: must not be empty."),
            "update_check" when bool.TryParse(value, out var b) => profile with { UpdateCheck = b },
            "update_check" => throw new ArgumentException($"Invalid value for update_check: '{value}'. Must be true or false."),
            "disable_session_guidelines" when bool.TryParse(value, out var b) => profile with { DisableSessionGuidelines = b },
            "disable_session_guidelines" => throw new ArgumentException($"Invalid value for disable_session_guidelines: '{value}'. Must be true or false."),
            "disable_memory_index" when bool.TryParse(value, out var b) => profile with { DisableMemoryIndex = b },
            "disable_memory_index" => throw new ArgumentException($"Invalid value for disable_memory_index: '{value}'. Must be true or false."),
            "disable_coordination_notices" when bool.TryParse(value, out var b) => profile with { DisableCoordinationNotices = b },
            "disable_coordination_notices" => throw new ArgumentException($"Invalid value for disable_coordination_notices: '{value}'. Must be true or false."),
            "disable_workitems_nudge" when bool.TryParse(value, out var b) => profile with { DisableWorkItemsNudge = b },
            "disable_workitems_nudge" => throw new ArgumentException($"Invalid value for disable_workitems_nudge: '{value}'. Must be true or false."),
            "disable_harness_nudge" when bool.TryParse(value, out var b) => profile with { DisableHarnessNudge = b },
            "disable_harness_nudge" => throw new ArgumentException($"Invalid value for disable_harness_nudge: '{value}'. Must be true or false."),
            "use_provider_api_key" when bool.TryParse(value, out var b) => profile with { UseProviderApiKey = b },
            "use_provider_api_key" => throw new ArgumentException($"Invalid value for use_provider_api_key: '{value}'. Must be true or false."),
            "default_visibility" when value is "private" or "project" or "org_public" or "public" => profile with { DefaultVisibility = value },
            "default_visibility" => throw new ArgumentException("Invalid value. Must be: private, project, org_public, or public"),
            "excluded_repos" => profile with { ExcludedRepos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
            "flows.reviewer_vendor" when !string.IsNullOrWhiteSpace(value) =>
                profile with { Flows = (profile.Flows ?? new FlowsSettings()) with { ReviewerVendor = ReviewerVendors.Normalize(value) } },
            "flows.reviewer_vendor" => throw new ArgumentException(
                "Invalid value for flows.reviewer_vendor: must not be empty. Use 'kcap config unset flows.reviewer_vendor' to remove it."),
            "skills.auto_sync" when bool.TryParse(value, out var b) =>
                profile with { Skills = (profile.Skills ?? new SkillsSettings()) with { AutoSync = b } },
            "skills.auto_sync" => throw new ArgumentException($"Invalid value for skills.auto_sync: '{value}'. Must be true or false."),
            _ => throw new ArgumentException($"Unknown config key: {key}")
        };

    /// <summary>
    /// Applies a single key removal to a <see cref="Profile"/>. Pure function, exposed for testing.
    /// Throws <see cref="ArgumentException"/> on unknown or non-unsettable keys.
    /// </summary>
    public static Profile ApplyUnset(Profile profile, string key) =>
        key switch {
            "flows.reviewer_vendor" => profile with { Flows = (profile.Flows ?? new FlowsSettings()) with { ReviewerVendor = null } },
            "skills.auto_sync" => profile with { Skills = (profile.Skills ?? new SkillsSettings()) with { AutoSync = null } },
            _ => throw new ArgumentException($"Unknown or non-unsettable config key: {key}")
        };

    static int SetUsage() {
        Console.Error.WriteLine("Usage: kcap config set <key> <value> [--no-probe]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Keys:");
        Console.Error.WriteLine("  server_url                  Server URL");
        Console.Error.WriteLine("  daemon.name                 Daemon name");
        Console.Error.WriteLine("  daemon.max_agents           Max concurrent hosted coding agents");
        Console.Error.WriteLine("  daemon.claude_path          Path to claude binary (default: claude)");
        Console.Error.WriteLine("  daemon.codex_path           Path to codex binary (default: codex)");
        Console.Error.WriteLine("  update_check                All kcap update nudging: stderr hint, server headers (banner/notification), in-agent nudge (true/false)");
        Console.Error.WriteLine("  default_visibility          Default session visibility (private, project, org_public, public)");
        Console.Error.WriteLine("  disable_session_guidelines  Skip injecting recurring-lessons context at SessionStart (true/false)");
        Console.Error.WriteLine("  disable_workitems_nudge     Skip injecting the work-items nudge at SessionStart (true/false)");
        Console.Error.WriteLine("  disable_coordination_notices  Skip injecting coordination notices (others' overlapping work) at SessionStart (true/false)");
        Console.Error.WriteLine("  disable_harness_nudge       Skip new-harness setup nudges (in-session + CLI stderr) (true/false)");
        Console.Error.WriteLine("  use_provider_api_key        Keep ANTHROPIC_API_KEY/OPENAI_API_KEY in headless agent spawns (true/false)");
        Console.Error.WriteLine("  excluded_repos              Excluded repos, comma-separated (owner/repo,owner/repo)");
        Console.Error.WriteLine("  flows.reviewer_vendor       Preferred review-flow reviewer vendor (used only when the definition names none)");
        Console.Error.WriteLine("  skills.auto_sync            Background skills refresh at Claude session start (true/false, default false)");
        Console.Error.WriteLine("  telemetry                   Anonymous CLI usage reporting, machine-wide (on/off)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Flags:");
        Console.Error.WriteLine("  --no-probe                  Skip the reachability check when setting server_url");

        return 1;
    }

    static int UnsetUsage() {
        Console.Error.WriteLine("Usage: kcap config unset <key>");

        return 1;
    }

    static int UnknownSubcommand(string subcommand) {
        Console.Error.WriteLine($"Unknown config subcommand: {subcommand}");

        return 1;
    }
}
