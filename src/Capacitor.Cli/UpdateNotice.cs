using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli;

/// <summary>
/// Deterministic exit-time "update available" notice for human-facing commands. Shares a single
/// check (<see cref="GetSharedCheckAsync"/>) and a reported-once gate (<see cref="MarkReported"/>)
/// with <c>kcap status</c>'s own version line so the two surfaces don't double-print.
/// </summary>
internal static class UpdateNotice {
    static readonly object _gate = new();

    static Task<UpdateCommand.UpdateCheckResult?>? _sharedCheck;

    /// <summary>True once the notice has been surfaced (by <see cref="FlushAsync"/> itself, or by
    /// another exit-time surface via <see cref="MarkReported"/>) for this process invocation.</summary>
    static volatile bool _reported;

    /// <summary>
    /// The suppression predicate. False (suppressed) for:
    /// <see cref="CrashReporter.FailOpenCommands"/> (<c>hook</c>, <c>generate-whats-done</c>,
    /// <c>set-title</c>, <c>copilot-finalize</c>, <c>report-version</c> — agent/tooling-spawned,
    /// nobody reads their stderr);
    /// <c>mcp</c> (a stdio JSON-RPC server — stderr is not a terminal) and <c>watch</c> (a
    /// long-lived background process); the entire <c>daemon</c> command family (there is no
    /// separate <c>run</c> subcommand — the foreground shape is plain <c>kcap daemon start</c>
    /// without <c>-d</c>/<c>--detach</c>, which spawns the daemon child and blocks for its whole
    /// lifetime, exactly what <c>Capacitor.AppHost</c> runs on every dev-loop restart; every other
    /// <c>daemon</c> subcommand is infra/diagnostic and the "am I current?" nudge use-case is
    /// already served by <c>kcap status</c>); <c>update</c>/<c>uninstall</c> (nudging "run kcap
    /// update" from inside one of those is noise at best, and uninstall's cache-file write would
    /// race the command's own config-dir deletion); and an explicit <c>--no-update-check</c> flag.
    /// Everything else is human-facing and returns true.
    /// </summary>
    public static bool IsHumanFacing(string command, string[] args) {
        if (CrashReporter.FailOpenCommands.Contains(command)) return false;
        if (command is "mcp" or "watch" or "daemon") return false;
        if (command is "update" or "uninstall") return false;
        if (args.Contains("--no-update-check")) return false;

        return true;
    }

    /// <summary>
    /// Marks the notice as already surfaced by another exit-time surface (e.g. <c>kcap status</c>'s
    /// own inline version line), so a subsequent <see cref="FlushAsync"/> call for the same
    /// invocation prints nothing.
    /// </summary>
    public static void MarkReported() => _reported = true;

    /// <summary>
    /// Lazily starts (or returns the already-started) budgeted check for <paramref name="channel"/>,
    /// so at most one network round-trip happens per process no matter how many call sites
    /// (<see cref="FlushAsync"/>, <c>kcap status</c>) ask for the result.
    /// </summary>
    internal static Task<UpdateCommand.UpdateCheckResult?> GetSharedCheckAsync(
            string channel, ConfigRoot root, NpmRegistryClient npm) {
        lock (_gate) {
            return _sharedCheck ??= UpdateCommand.CheckForUpdateWithBudgetAsync(root, channel, npm);
        }
    }

    /// <summary>
    /// The single exit-path helper: awaited from <c>Program.cs</c>'s outer <c>finally</c> for every
    /// invocation. Does nothing (and touches neither disk nor network) unless
    /// <see cref="IsHumanFacing"/> says the command is human-facing, the active profile's
    /// <c>update_check</c> setting hasn't opted out, and nobody has already
    /// <see cref="MarkReported"/> this invocation. Never throws — an update notice must never break
    /// the command it's attached to.
    /// </summary>
    public static async Task FlushAsync(
            string command, string[] args, ProfileContext profiles, ConfigRoot config, Func<NpmRegistryClient> npm) {
        try {
            if (_reported || !IsHumanFacing(command, args)) return;

            var profile = profiles.Effective;
            if (profile?.UpdateCheck == false) return;

            var channel  = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
            // Asked for only once the notice is going to happen: this runs on the way out of EVERY
            // invocation, and a registry client built for a suppressed one costs a handler chain the
            // command never sends on.
            var result   = await GetSharedCheckAsync(channel, config, npm());

            // Cap the recommendation at the connected server's version (min(npm latest, server)) so we
            // never steer a user to a CLI newer than the server they talk to. Uncapped ⇒ today's copy.
            // Re-read when the startup resolution named no server: this runs at exit, and `kcap
            // setup` points a previously-unconfigured machine at its first one. Capping against
            // nothing would recommend a CLI newer than the server the user just connected to.
            var serverUrl = profiles.Resolution.ServerUrl ?? await CurrentServerUrlAsync(config, profiles.Name);
            var advisory  = UpdateAdvisoryResolver.Resolve(result, channel, serverUrl, config);

            // Re-check after the await: `kcap status` may have won the race and already reported
            // while this was in flight (both may share the same in-flight task via GetSharedCheckAsync).
            if (_reported || !advisory.Newer || advisory.Target is null || advisory.Current is null) return;

            _reported = true;

            await Console.Error.WriteLineAsync();

            if (advisory.ServerCapped) {
                // The server is behind npm latest; plain `kcap update` follows the dist-tag and would
                // overshoot the cap, so recommend the pinned install of the server's version.
                await Console.Error.WriteLineAsync(
                    $"Update available: {advisory.Current} {UpdateCommand.Arrow} {advisory.Target} (server version)");
                await Console.Error.WriteLineAsync($"Run: npm install -g @kurrent/kcap@{advisory.Target}");
            } else {
                await Console.Error.WriteLineAsync($"Update available: {advisory.Current} {UpdateCommand.Arrow} {advisory.Target}");
                await Console.Error.WriteLineAsync("Run `kcap update` to update");
            }
        } catch {
            // Best effort — an update notice must never break the command it's attached to.
        }
    }

    /// <summary>The server the cap is computed against when the startup resolution named none —
    /// this runs at exit, and <c>kcap setup</c> points a previously-unconfigured machine at its
    /// first server. Reads the profile the rest of the flow reads, so a resolution that named a
    /// profile without a <c>server_url</c> does not silently fall through to another one.</summary>
    static async Task<string?> CurrentServerUrlAsync(ConfigRoot config, string profileName) {
        try {
            var saved = await AppConfig.LoadProfileConfig(config);
            return saved.Profiles.GetValueOrDefault(profileName)?.ServerUrl;
        } catch {
            return null;   // best-effort: an unreadable config just means no cap
        }
    }
}
