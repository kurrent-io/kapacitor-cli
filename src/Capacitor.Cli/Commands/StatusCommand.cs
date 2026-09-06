using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

public sealed class StatusCommand(
        DaemonStore store, ProfileContext profiles, ConfigRoot config, TokenStore tokenStore, HarnessRegistry harnesses,
        ICapacitorHttpClient http, NpmRegistryClient npm) {

    public async Task<int> HandleAsync(string[] args) {
        var baseUrl = profiles.Resolution.ServerUrl;
        // Version line reuses UpdateNotice's shared check and marks-reported so the exit footer
        // doesn't double-print; respects the same opt-outs.
        await WriteVersionLineAsync(args);

        // Server
        Console.Write("  Server:  ");

        if (baseUrl is null) {
            await Console.Out.WriteLineAsync("not configured");
        } else {
            Console.Write($"{baseUrl} ");

            try {
                // Reachability, not authorization: a bearer would turn an unauthenticated-but-running
                // server into a failure line, and this probe reports the connection.
                using var client = http.Anonymous();
                client.Timeout = TimeSpan.FromSeconds(5);
                var resp = await client.GetAsync($"{baseUrl}/auth/config");
                await Console.Out.WriteLineAsync(resp.IsSuccessStatusCode ? "✓ reachable" : $"✗ HTTP {(int)resp.StatusCode}");
            } catch {
                await Console.Out.WriteLineAsync("✗ unreachable");
            }
        }

        // Auth
        // A machine-credential diversion REPLACES the token-store line rather than appending to it:
        // with KCAP_CLIENT_ID/KCAP_CLIENT_SECRET in the environment, MachineAuth.Intended bypasses
        // the token store entirely, so its state is not what this CLI authenticates with — printing
        // both would show a headless runner as "records as the machine" AND "not authenticated (run:
        // kcap login)", contradictory and with irrelevant remediation.
        var machineLine = MachineAuth.DescribeDiversion(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MachineAuth.ClientIdVar)),
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MachineAuth.ClientSecretVar)));

        if (machineLine is not null) {
            Console.WriteLine($"  Auth:    {machineLine}");
        } else {
            Console.Write("  Auth:    ");
            var tokens = await tokenStore.GetValidTokensForProfileAsync(profiles.Name);

            if (tokens is not null) {
                var remaining = tokens.ExpiresAt - DateTimeOffset.UtcNow;

                var expiryText = remaining.TotalHours > 1
                    ? $"expires in {remaining.TotalHours:F0}h"
                    : $"expires in {remaining.TotalMinutes:F0}m";
                await Console.Out.WriteLineAsync($"{tokens.GitHubUsername} ✓ token valid ({expiryText})");
            } else {
                var rawTokens = await tokenStore.LoadForProfileAsync(profiles.Name);

                await Console.Out.WriteLineAsync(
                    rawTokens is not null
                        ? $"{rawTokens.GitHubUsername} ✗ token expired (run: kcap login)"
                        : "not authenticated (run: kcap login)"
                );
            }
        }

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var line = BuildHooksStatusLine(harnesses.Select(h => (h.Id, h.Signals.IsWired)));

        await Console.Out.WriteLineAsync(line);

        // Newly-installed-but-unconfigured harnesses. Ledger-independent (a dismissed vendor is
        // still surfaced here) — status always tells the truth, unlike the nudge which respects
        // dismissals. Shares the wired-check with the Hooks line above, so the two never disagree.
        foreach (var h in harnesses) {
            if (!harnesses.Detected(h.Id)) continue;
            if (h.Signals.IsWired) continue;
            var flag    = h.Id.PluginInstallFlag;
            var install = flag is null ? "kcap plugin install" : $"kcap plugin install {flag}";
            await Console.Out.WriteLineAsync($"           {h.Label} installed but kcap not configured — run `{install}`");
        }

        // Daemon: read per-name PID files under
        // ~/.config/kcap/daemons/ instead of the legacy singleton
        // at ~/.config/kcap/agent.pid. The top-level `kcap status`
        // must agree with `kcap daemon status`; previously this
        // command kept saying "not running" while `daemon status` reported
        // a healthy daemon because new daemons no longer write the legacy
        // singleton.
        Console.Write("  Daemon:  ");
        await WriteAgentStatusAsync(store);

        return 0;
    }

    async Task WriteVersionLineAsync(string[] args) {
        Console.Write("  Version: ");

        var current = CapacitorVersion.CurrentDisplay();

        // Opt-out: an explicit --no-update-check flag or a disabled profile setting means no
        // check is performed at all (never force one the user turned off) — the line still
        // prints the bare version.
        if (args.Contains("--no-update-check")) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var profile = profiles.Effective;

        if (profile?.UpdateCheck == false) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var channel  = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
        var result   = await UpdateNotice.GetSharedCheckAsync(channel, config, npm);

        // Cap the recommendation at the connected server's version (min(npm latest, server)).
        var advisory = UpdateAdvisoryResolver.Resolve(result, channel, profiles.Resolution.ServerUrl, config);

        await Console.Out.WriteLineAsync(FormatVersionLine(current, advisory));

        if (advisory.Newer) {
            // Surfaced inline already — the exit-time footer (UpdateNotice.FlushAsync) must not
            // print the same information a second time.
            UpdateNotice.MarkReported();
        }
    }

    /// <summary>
    /// Pure formatting for the Version line: <c>kcap {current}</c>, with an inline
    /// <c>(update available: {target})</c> annotation appended only when <paramref name="advisory"/>
    /// reports a newer version — and, when the target was capped at the server's version, a
    /// <c>, server version</c> marker. Split out from <see cref="WriteVersionLineAsync"/> so the exact
    /// text is unit-testable without any I/O.
    /// </summary>
    internal static string FormatVersionLine(string current, UpdateAdvisory advisory) =>
        advisory is { Newer: true, Target: { } target }
            ? advisory.ServerCapped
                ? $"kcap {current} (update available: {target}, server version)"
                : $"kcap {current} (update available: {target})"
            : $"kcap {current}";

    static async Task WriteAgentStatusAsync(DaemonStore store) {
        if (!Directory.Exists(store.Directory)) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var pidFiles = Directory.EnumerateFiles(store.Directory, "*.pid")
            .OrderBy(f => f)
            .ToList();

        if (pidFiles.Count == 0) {
            await Console.Out.WriteLineAsync("not running");

            return;
        }

        var entries = new List<(string Name, int Pid, bool Alive)>(pidFiles.Count);

        foreach (var pidFile in pidFiles) {
            var name = Path.GetFileNameWithoutExtension(pidFile);

            if (string.IsNullOrEmpty(name)) continue;

            var firstLine = (await File.ReadAllTextAsync(pidFile))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!int.TryParse(firstLine, out var pid)) continue;

            var alive = false;

            try {
                System.Diagnostics.Process.GetProcessById(pid);
                alive = true;
            } catch (ArgumentException) {
                // process gone; treated as stale below
            }

            entries.Add((name, pid, alive));
        }

        var live = entries.Where(e => e.Alive).ToList();

        switch (live.Count) {
            case 0:
                await Console.Out.WriteLineAsync(
                    entries.Count == 0
                        ? "not running"
                        : "not running (stale PID files; `kcap daemon doctor --clean` to remove)"
                );

                return;
            case 1:
                await Console.Out.WriteLineAsync($"running — {live[0].Name} (PID {live[0].Pid})");

                return;
            default: {
                var summary = string.Join(", ", live.Select(e => $"{e.Name} (PID {e.Pid})"));
                await Console.Out.WriteLineAsync($"running ({live.Count}) — {summary}");

                break;
            }
        }
    }

    /// <summary>
    /// Renders the Hooks status line: every harness, wired or not, in registry order. What "wired"
    /// means is each vendor's own — Gemini merges its hooks into the shared
    /// <c>~/.gemini/settings.json</c>, while Pi and OpenCode track a live-ingest extension file
    /// rather than hooks — but all share the line for at-a-glance parity. Pure: the probing happens
    /// in the caller.
    /// </summary>
    internal static string BuildHooksStatusLine(IEnumerable<(HarnessId Id, bool Wired)> wiring) =>
        string.Join("  ", wiring.Select(w => $"{ShortLabel(w.Id)} {(w.Wired ? "✓" : "✗")}"));

    /// <summary>Every vendor shares one line, so the one label carrying a product suffix is
    /// shortened to fit beside the rest.</summary>
    static string ShortLabel(HarnessId id) => id is HarnessId.Claude ? "Claude" : HarnessRegistry.LabelOf(id);
}
