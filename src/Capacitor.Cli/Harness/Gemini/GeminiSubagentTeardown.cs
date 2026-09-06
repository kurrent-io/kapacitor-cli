using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Gemini;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Gemini;

/// <summary>
/// Shared session-end teardown for live Gemini subagents. Gemini fires no
/// subagent-stop hook, and the child watchers the parent spawns carry no parent-pid
/// watchdog, so the parent session-end is the ONLY thing that finalizes them: for each
/// nested subagent transcript, kill its child watcher (no-op if already gone), drain its
/// tail (resumes from the server watermark), and POST <c>/hooks/subagent-stop</c> so the
/// server writes <c>SubagentCompleted</c> + the agent summary.
///
/// Enumerates the on-disk files rather than an in-memory set, so it recovers subagents
/// even after a parent-watcher restart/crash. Invoked from BOTH session-end paths — the
/// normal Gemini hook (<see cref="GeminiHookCommand"/>) and the watcher's parent-exit
/// fallback (<see cref="WatchCommand.PostSessionEndOnParentExitAsync"/>) — so a crash that
/// bypasses the hook still finalizes subagents instead of leaving them with
/// <c>SubagentStarted</c> + content but no <c>SubagentCompleted</c>. Best-effort per step
/// (a failure on one subagent — or one step — never skips the rest; re-import recovers).
/// </summary>
sealed class GeminiSubagentTeardown(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    /// <summary>
    /// Time budget for the teardown on a shutdown path (the parent-exit watchdog), so a slow
    /// or retrying drain can't block process termination. Mirrors
    /// <c>GeminiHookCommand.PreHookDrainCap</c>.
    /// </summary>
    internal static readonly TimeSpan DrainCap = TimeSpan.FromSeconds(8);

    internal async Task DrainAsync(string sessionId, string transcriptPath) {
        var subFiles = GeminiSubagentDiscovery.EnumerateSubagentFiles(transcriptPath);
        if (subFiles.Count == 0) return;

        var types = GeminiSubagentDiscovery.ResolveAgentTypes(transcriptPath);

        foreach (var subFile in subFiles) {
            var subId = Path.GetFileNameWithoutExtension(subFile);
            if (!Guid.TryParse(subId, out _)) continue;

            var agentId   = GeminiSubagentDiscovery.CanonicalAgentId(subId);
            var agentType = types.GetValueOrDefault(subId) ?? "subagent";

            // Each step best-effort + independent so subagent-stop (→ SubagentCompleted) is
            // always attempted even if the kill or drain hiccups; re-import recovers the rest.
            await SafeAsync(() => _watchers.KillWatcher($"{sessionId}-{agentId}"));
            await SafeAsync(() => _watchers.InlineDrainAsync(sessionId, subFile, agentId, vendor: "gemini"));
            await SafeAsync(() => PostStopAsync(sessionId, agentId, agentType, subFile));
        }
    }

    async Task PostStopAsync(string sessionId, string agentId, string agentType, string subFile) {
        var       baseUrl = profiles.Resolution.ServerUrl!;
        using var client  = await http.ForBackgroundAsync();
        var       payload = GeminiSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", content);
    }

    static async Task SafeAsync(Func<Task> op) {
        try { await op(); } catch { /* best effort — kcap import --gemini recovers anything missed */ }
    }
}
