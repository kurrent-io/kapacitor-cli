using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Harness.Gemini;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>
/// Shared session-end teardown for live Codex collab subagents. Codex fires no
/// subagent-stop hook, and the child watchers the parent spawns carry no parent-pid
/// watchdog, so the parent session-end is the BACKSTOP that finalizes them (the common
/// path is the child watcher's own live stop on turn-complete + idle —
/// <see cref="CodexSubagentTurnTracker"/> — whose duplicate
/// stop this teardown's POST dedupes against server-side): for each
/// child rollout still linked to this parent on disk, kill its child watcher (no-op if
/// already gone), drain its tail (resumes from the server watermark), and POST
/// <c>/hooks/subagent-stop</c> so the server writes <c>SubagentCompleted</c> + the agent
/// summary.
///
/// Enumerates the on-disk rollouts rather than an in-memory set, so it recovers subagents
/// even after a parent-watcher restart/crash. Invoked from the watcher's session-end
/// synthesis path (<see cref="WatchCommand.PostSessionEndOnParentExitAsync"/>), which for
/// Codex covers every end reason — idle_timeout, parent_exited and the wedged-ceiling exit —
/// since Codex has no session-end hook of its own. Best-effort per step (a failure on one
/// subagent — or one step — never skips the rest; re-import recovers). Mirrors
/// <see cref="GeminiSubagentTeardown"/>.
/// </summary>
sealed class CodexSubagentTeardown(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    /// <summary>
    /// Time budget for the teardown on a shutdown path (the parent-exit watchdog), so a slow
    /// or retrying drain can't block process termination. Mirrors
    /// <see cref="GeminiSubagentTeardown.DrainCap"/>.
    /// </summary>
    internal static readonly TimeSpan DrainCap = TimeSpan.FromSeconds(8);

    internal async Task DrainAsync(string sessionId, string transcriptPath) {
        var subs = CodexSubagentDiscovery.EnumerateSubagentRollouts(transcriptPath, sessionId);
        if (subs.Count == 0) return;

        foreach (var sub in subs) {
            var agentId   = sub.ChildDashlessId;
            var agentType = CodexSubagentDiscovery.AgentTypeFrom(sub.AgentPath, sub.AgentNickname);

            // Each step best-effort + independent so subagent-stop (→ SubagentCompleted) is
            // always attempted even if the kill or drain hiccups; re-import recovers the rest.
            await SafeAsync(() => _watchers.KillWatcher($"{sessionId}-{agentId}"));
            await SafeAsync(() => _watchers.InlineDrainAsync(sessionId, sub.FilePath, agentId, vendor: "codex"));
            await SafeAsync(() => PostStopAsync(sessionId, agentId, agentType, sub.FilePath));
        }
    }

    async Task PostStopAsync(string sessionId, string agentId, string agentType, string subFile) {
        // baseUrl is threaded into auth resolution so token/server selection matches the URL
        // actually posted to (a process configured for a different default server must not
        // resolve the wrong credential).
        var       baseUrl = profiles.Resolution.ServerUrl!;
        using var client  = await http.ForBackgroundAsync();
        var       payload = CodexSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", content);
    }

    static async Task SafeAsync(Func<Task> op) {
        try { await op(); } catch { /* best effort — kcap import --codex recovers anything missed */ }
    }
}
