using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Detached post-<c>sessionEnd</c> "finalize drain" for GitHub Copilot CLI
/// sessions.
/// </summary>
/// <remarks>
/// Copilot appends <c>session.shutdown</c> — the per-model input/cache token
/// aggregate the server consumes as <c>CopilotUsageBackfilled</c> — and
/// occasionally the final assistant turn, to <c>events.jsonl</c> only AFTER the
/// <c>sessionEnd</c> hook returns (verbatim live tail: <c>hook.start</c>,
/// <c>hook.end</c>, <c>session.shutdown</c>). The hook's pre-drain (watcher kill
/// + inline-drain) runs before that line exists, so nothing is left to deliver
/// it — input/cache totals stay 0 for live sessions. (Batch
/// <c>kcap import --copilot</c> is unaffected; it reads the complete file.)
///
/// The <c>sessionEnd</c> hook launches this command (via
/// <see cref="WatcherManager.SpawnCopilotFinalizeDrain"/>) detached as its FIRST
/// action — before the capped pre-drain and the retrying session-end POST — so
/// the drainer is guaranteed to exist even if a slow/unreachable server makes
/// the POST burn the hook timeout and Copilot SIGKILLs the hook; being detached
/// it survives that kill. It polls until <c>session.shutdown</c> is the terminal
/// transcript line (or its budget elapses), then performs one inline-drain.
/// Fully decoupled from the hook's return and the server's StopAndDrain, so it
/// cannot deadlock or stall them; the server watermark + deterministic-id dedup
/// make the late delivery idempotent. The timeout fallback also rescues a
/// dropped final assistant turn when <c>session.shutdown</c> never lands
/// (e.g. Copilot crash).
/// </remarks>
sealed class CopilotFinalizeDrainCommand(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    // The hook spawns this FIRST — before its capped pre-drain and the retrying
    // session-end POST — so the budget must outlast the worst-case hook lifetime
    // (PreHookDrainCap + Copilot's hook timeout, ~30s by default) and still be
    // polling when Copilot flushes session.shutdown after the hook returns or is
    // SIGKILLed. The process is idle while polling and exits the instant it sees
    // shutdown (the common case resolves in ~1-2s), so this cap only bites in the
    // crash fallback where session.shutdown never lands.
    static readonly TimeSpan DefaultPollBudget   = TimeSpan.FromSeconds(45);
    static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Process entry point: <c>kcap copilot-finalize &lt;sessionId&gt; &lt;transcriptPath&gt;</c>.
    /// </summary>
    public async Task<int> Run(string sessionId, string transcriptPath) {
        // The spawning hook closes our redirected std streams the instant it
        // starts us; writing to the dead pipe would fault. Redirect to a
        // per-session log file, mirroring WatchCommand.
        try {
            var logDir = config.Path("logs");
            Directory.CreateDirectory(logDir);
            var logWriter = new StreamWriter(Path.Combine(logDir, $"{sessionId}-finalize.log"), append: true) { AutoFlush = true };
            Console.SetOut(logWriter);
            Console.SetError(logWriter);
        } catch {
            // Logging is best-effort; never fail the drain because of it.
        }

        // Detach from the controlling terminal so closing the terminal right
        // after the session ends does not deliver SIGHUP before the tail lands.
        ProcessHelpers.DetachFromControllingTerminal();

        await RunAsync(sessionId, transcriptPath, DefaultPollBudget, DefaultPollInterval);

        return 0;
    }

    /// <summary>
    /// Testable core: wait for the terminal <c>session.shutdown</c> line (or
    /// <paramref name="pollBudget"/>), then deliver the tail exactly once. No
    /// process detachment or log redirection, so tests drive it directly.
    /// </summary>
    internal async Task RunAsync(
            string   sessionId,
            string   transcriptPath,
            TimeSpan pollBudget,
            TimeSpan pollInterval
        ) {
        var deadline    = DateTimeOffset.UtcNow + pollBudget;
        var sawShutdown = false;

        while (true) {
            if (LastLineIsShutdown(transcriptPath)) {
                sawShutdown = true;

                break;
            }

            if (DateTimeOffset.UtcNow >= deadline) {
                break;
            }

            try {
                await Task.Delay(pollInterval);
            } catch (OperationCanceledException) {
                break;
            }
        }

        Log(sawShutdown
            ? $"session.shutdown observed; draining tail for {sessionId}"
            : $"poll budget ({pollBudget.TotalSeconds:0}s) elapsed without session.shutdown; best-effort tail drain for {sessionId}");

        // Idempotent: resumes from the server watermark; deterministic event ids
        // dedupe anything the hook's inline-drain already delivered.
        await _watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "copilot");
    }

    /// <summary>
    /// True when the last non-blank line of the transcript is a Copilot
    /// <c>session.shutdown</c> envelope. Resume-safe: a prior run's shutdown sits
    /// mid-file (more events follow it), so only the genuine terminal shutdown
    /// makes this true. Fail-closed on missing / empty / malformed input.
    /// </summary>
    internal static bool LastLineIsShutdown(string transcriptPath) {
        try {
            if (!File.Exists(transcriptPath)) {
                return false;
            }

            string? lastNonBlank = null;

            using (var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) {
                while (reader.ReadLine() is { } line) {
                    if (!string.IsNullOrWhiteSpace(line)) {
                        lastNonBlank = line;
                    }
                }
            }

            if (lastNonBlank is null) {
                return false;
            }

            using var doc = JsonDocument.Parse(lastNonBlank);

            return doc.RootElement.Str("type") == "session.shutdown";
        } catch {
            return false;
        }
    }

    static void Log(string message) =>
        Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [copilot-finalize] {message}");
}
