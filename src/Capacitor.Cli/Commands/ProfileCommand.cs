using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

public sealed class ProfileCommand(ConfigRoot config, ICapacitorHttpClient http) {
    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await PrintUsage();

            return 1;
        }

        return args[1] switch {
            "add"                          => await HandleAdd(args),
            "list"                         => await HandleList(),
            "remove" when args.Length >= 3 => await RemoveProfile(args[2]),
            "show"                         => await HandleShow(args),
            _                              => await PrintUsage()
        };
    }

    async Task<int> HandleAdd(string[] args) {
        if (args.Length < 3) {
            await Console.Error.WriteLineAsync(
                "Usage: kcap profile add <name> --server-url <url> [--remote <pattern>]... [--no-probe]");

            return 1;
        }

        var name      = args[2];
        var serverUrl = GetArg(args, "--server-url");
        var skipProbe = args.Contains("--no-probe");

        if (serverUrl is null) {
            await Console.Error.WriteLineAsync("--server-url is required");

            return 1;
        }

        var remotes = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--remote" && i + 1 < args.Length)
                remotes.Add(args[++i]);
        }

        return await AddProfile(name, serverUrl, remotes.ToArray(), skipProbe);
    }

    internal async Task<int> AddProfile(string name, string serverUrl, string[] remotes, bool skipProbe = true) {
        var stored = await LoadConfig();

        if (stored.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' already exists. Remove it first.");

            return 1;
        }

        var normalized = await ServerUrlNormalizer.NormalizeAsync(
            serverUrl, skipProbe, CancellationToken.None, ServerUrlNormalizer.ProbeWith(http));

        if (normalized.Warning is not null)
            await Console.Error.WriteLineAsync($"Warning: {normalized.Warning}");

        await ConfigMutator.MutateAsync(config, c => c with {
            Profiles = new Dictionary<string, Profile>(c.Profiles) {
                [name] = new() {
                    ServerUrl = normalized.Url,
                    Remotes   = remotes
                }
            }
        });

        await Console.Out.WriteLineAsync($"Profile '{name}' added.");

        return 0;
    }

    async Task<int> HandleList() {
        var stored = await LoadConfig();

        foreach (var (name, profile) in stored.Profiles) {
            var active = name == stored.ActiveProfile ? " (active)" : "";
            var url    = profile.ServerUrl ?? "(no server URL)";
            await Console.Out.WriteLineAsync($"  {name}{active} — {url}");

            if (profile.Remotes is { Length: > 0 }) {
                foreach (var remote in profile.Remotes)
                    await Console.Out.WriteLineAsync($"    remote: {remote}");
            }
        }

        return 0;
    }

    internal async Task<int> RemoveProfile(string name) {
        if (name == "default") {
            await Console.Error.WriteLineAsync("Cannot remove the default profile.");

            return 1;
        }

        var stored = await LoadConfig();

        if (!stored.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");

            return 1;
        }

        await ConfigMutator.MutateAsync(config, c => {
            var profiles = new Dictionary<string, Profile>(c.Profiles);
            profiles.Remove(name);

            var bindings  = new Dictionary<string, string>(c.ProfileBindings);
            var staleKeys = bindings.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys) bindings.Remove(key);

            return c with {
                Profiles = profiles,
                ProfileBindings = bindings,
                ActiveProfile = c.ActiveProfile == name ? "default" : c.ActiveProfile
            };
        });

        await Console.Out.WriteLineAsync($"Profile '{name}' removed.");

        return 0;
    }

    async Task<int> HandleShow(string[] args) {
        var stored = await LoadConfig();
        var name   = args.Length >= 3 ? args[2] : stored.ActiveProfile;

        if (!stored.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Profile: {name}");
        await Console.Out.WriteLineAsync($"  server_url: {profile.ServerUrl ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  default_visibility: {profile.DefaultVisibility}");
        await Console.Out.WriteLineAsync($"  update_check: {profile.UpdateCheck}");
        await Console.Out.WriteLineAsync($"  daemon.name: {profile.Daemon?.Name            ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  daemon.max_agents: {profile.Daemon?.MaxAgents ?? 5}");

        if (profile.Remotes is { Length: > 0 }) {
            await Console.Out.WriteLineAsync("  remotes:");

            foreach (var r in profile.Remotes)
                await Console.Out.WriteLineAsync($"    - {r}");
        }

        if (profile.ExcludedRepos is { Length: > 0 }) {
            await Console.Out.WriteLineAsync($"  excluded_repos: {string.Join(", ", profile.ExcludedRepos)}");
        }

        if (profile.ExcludedPaths is { Length: > 0 }) {
            await Console.Out.WriteLineAsync("  excluded_paths:");

            foreach (var p in profile.ExcludedPaths)
                await Console.Out.WriteLineAsync($"    - {p}");
        }

        return 0;
    }

    async Task<ProfileConfig> LoadConfig() {
        var configPath = AppConfig.GetConfigPath(config);

        if (!File.Exists(configPath))
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new() } };

        var json = await File.ReadAllTextAsync(configPath);

        return ConfigMigration.MigrateIfNeeded(json).Config;
    }

    static async Task<int> PrintUsage() {
        await Console.Error.WriteLineAsync("Usage: kcap profile <add|list|remove|show>");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  add <name> --server-url <url> [--remote <pattern>]... [--no-probe]");
        await Console.Error.WriteLineAsync("  list                          Show all profiles");
        await Console.Error.WriteLineAsync("  remove <name>                 Remove a profile");
        await Console.Error.WriteLineAsync("  show [name]                   Show profile details");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("Flags:");
        await Console.Error.WriteLineAsync("  --no-probe                    Skip the reachability check when adding a profile");

        return 1;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
