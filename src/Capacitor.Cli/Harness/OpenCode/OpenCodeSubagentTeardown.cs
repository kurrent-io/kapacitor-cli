using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Harness.Gemini;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.OpenCode;

/// <summary>
/// Shared session-end teardown for live OpenCode subagents. OpenCode
/// fires no subagent-stop hook and the child watchers the parent spawns carry no
/// parent-pid watchdog, so the parent session-end is the ONLY thing that finalizes them:
/// for each nested child transcript the plugin wrote, kill its child watcher (no-op if
/// already gone), drain its tail (resumes from the server watermark), and POST
/// <c>/hooks/subagent-stop</c> so the server writes <c>SubagentCompleted</c> + the agent
/// summary. Mirrors <see cref="GeminiSubagentTeardown"/>.
///
/// Enumerates the on-disk files rather than an in-memory set, so it recovers subagents
/// even after a parent-watcher restart/crash. Invoked from the watcher's parent-exit
/// fallback (<see cref="WatchCommand.PostSessionEndOnParentExitAsync"/>) — the only
/// session-end path OpenCode has. Best-effort per step (a failure on one subagent — or
/// one step — never skips the rest).
/// </summary>
sealed class OpenCodeSubagentTeardown(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    /// <summary>
    /// Shared budget for the best-effort kill+drain cleanup ACROSS all children, so a slow
    /// first child can't consume it and starve later children. <c>subagent-stop</c> is ALWAYS
    /// attempted per child regardless of this budget (the critical <c>SubagentCompleted</c>, and a
    /// missed one leaves the child open until something else closes it). Self-bounding —
    /// the caller awaits <see cref="DrainAsync"/> directly without an outer time cap.
    /// </summary>
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Hard ceiling for the WHOLE teardown (all children) so a polluted or huge nested dir can't
    /// make the parent-exit session-end path scale linearly without bound. Past it, remaining
    /// children are left unfinalized so the watchdog can terminate.
    /// </summary>
    internal static readonly TimeSpan OverallBudget = TimeSpan.FromSeconds(20);

    /// <summary>Returns the number of discovered children left UNFINALIZED because the overall
    /// budget elapsed (0 in the normal case) so the caller can log it.</summary>
    internal async Task<int> DrainAsync(string sessionId, string parentTranscriptPath) {
        var subFiles = OpenCodeSubagentDiscovery.EnumerateSubagentFiles(parentTranscriptPath);
        if (subFiles.Count == 0) return 0;

        // Deadline-aware across ALL children (each step is also individually capped — KillWatcher
        // alone waits up to 5s for graceful exit). The cleanup deadline drops kill+drain for later
        // children (they STILL get subagent-stop); the overall deadline is a hard ceiling on the
        // whole teardown so an arbitrary file count can't delay session-end — past it, remaining
        // children are left unfinalized (counted + returned for the caller to log).
        var start           = DateTimeOffset.UtcNow;
        var cleanupDeadline = start + CleanupBudget;
        var overallDeadline = start + OverallBudget;

        var stopped = 0;
        foreach (var subFile in subFiles) {
            if (DateTimeOffset.UtcNow >= overallDeadline) break;

            var childId   = Path.GetFileNameWithoutExtension(subFile);
            var agentId   = OpenCodeSubagentDiscovery.CanonicalAgentId(childId);
            var agentType = OpenCodeSubagentDiscovery.ResolveAgentType(subFile);

            if (DateTimeOffset.UtcNow < cleanupDeadline) {
                // InlineDrain overlaps harmlessly with any still-live watcher (server dedupes by
                // deterministic event id); both capped so neither blocks process termination.
                await CappedAsync(() => _watchers.KillWatcher($"{sessionId}-{agentId}"),                               TimeSpan.FromSeconds(1.5));
                await CappedAsync(() => _watchers.InlineDrainAsync(sessionId, subFile, agentId, vendor: "opencode"), TimeSpan.FromSeconds(2.5));
            }

            // The critical SubagentCompleted — attempted for every child within the overall budget.
            await CappedAsync(() => PostStopAsync(sessionId, agentId, agentType, subFile), TimeSpan.FromSeconds(2.5));
            stopped++;
        }

        return subFiles.Count - stopped;
    }

    async Task PostStopAsync(string sessionId, string agentId, string agentType, string subFile) {
        var       baseUrl = profiles.Resolution.ServerUrl!;
        using var client  = await http.ForBackgroundAsync();
        var       payload = OpenCodeSubagentDiscovery.BuildStopPayload(sessionId, agentId, agentType, subFile);
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        await client.PostWithRetryAsync($"{baseUrl}/hooks/subagent-stop", content);
    }

    /// <summary>
    /// Runs a best-effort teardown step bounded by <paramref name="cap"/>; swallows errors
    /// and timeouts so one subagent (or one slow step) never blocks the rest of the shutdown
    /// path. On timeout the step is detached and its fault observed (no unobserved-exception).
    /// </summary>
    static async Task CappedAsync(Func<Task> op, TimeSpan cap) {
        Task task;
        try { task = op(); } catch { return; }

        if (await Task.WhenAny(task, Task.Delay(cap)) != task) {
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        try { await task; } catch { /* best effort — kcap import recovers anything missed */ }
    }
}
