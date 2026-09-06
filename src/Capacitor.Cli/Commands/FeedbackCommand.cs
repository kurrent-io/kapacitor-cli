using System.Diagnostics;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap feedback (--bug | --feedback) [-m|--message &lt;text&gt;]</c> — files a bug report or
/// sends feedback to Kurrent support through the tenant's <c>POST /api/feedback</c>, which resolves
/// the reporter's email server-side and forwards to the auth proxy's Plain sink.
///
/// <para>Follows <see cref="ValidatePlanCommand"/>'s <c>Handle</c>/<c>HandleCore</c> split: <c>Handle</c>
/// owns argument parsing and the interactive prompt; <c>HandleCore</c> takes an already-resolved
/// category and message so tests can drive it without a real stdin.</para>
/// </summary>
public sealed class FeedbackCommand(IFeedbackApi feedbackApi) {
    /// <summary>
    /// The pinned success line (post-spike variant: promises email replies). Printed to stdout so
    /// <c>kcap feedback ... | ...</c> can capture it; everything else in this command writes to stderr.
    /// </summary>
    internal const string SuccessPrefix = "✓ Sent to Kurrent support as ";

    const string BugFlag      = "--bug";
    const string FeedbackFlag = "--feedback";

    const string InteractivePrompt = "What's going on? (end with an empty line)";

    public Task<int> HandleAsync(string[] args) =>
        HandleAsync(args, Console.IsInputRedirected, Console.ReadLine);

    /// <summary>
    /// Test-friendly entry point: <paramref name="stdinIsRedirected"/> and <paramref name="readLine"/>
    /// stand in for <see cref="Console.IsInputRedirected"/>/<see cref="Console.ReadLine"/> so the
    /// TTY-vs-piped branch and the interactive prompt's line collection are exercised without a real
    /// terminal or process stdin.
    /// </summary>
    internal async Task<int> HandleAsync(string[] args, bool stdinIsRedirected, Func<string?> readLine) {
        var isBug      = args.Contains(BugFlag);
        var isFeedback = args.Contains(FeedbackFlag);

        // Exactly one of --bug/--feedback: neither and both are the same usage error, naming both
        // flags so the fix is obvious either way.
        if (isBug == isFeedback) {
            await Console.Error.WriteLineAsync("Usage: kcap feedback (--bug | --feedback) [-m|--message <text>]");
            await Console.Error.WriteLineAsync("  Pass exactly one of --bug or --feedback.");

            return 1;
        }

        var category   = isBug ? "bug" : "feedback";
        var rawMessage = GetMessageArg(args);

        if (rawMessage is null) {
            // stdin is not a TTY (piped/redirected): there is no one to prompt, so -m is mandatory.
            if (stdinIsRedirected) {
                await Console.Error.WriteLineAsync("A message is required.");

                return 1;
            }

            await Console.Error.WriteLineAsync(InteractivePrompt);
            rawMessage = ReadInteractiveMessage(readLine);
        }

        var message = rawMessage.Trim();

        if (message.Length == 0) {
            await Console.Error.WriteLineAsync("A message is required.");

            return 1;
        }

        return await HandleCore(feedbackApi, category, message);
    }

    /// <summary>Test-friendly core: caller owns the <see cref="IFeedbackApi"/>. <paramref name="category"/>
    /// is already "bug"/"feedback" and <paramref name="message"/> is already trimmed and non-empty.</summary>
    internal static async Task<int> HandleCore(IFeedbackApi feedbackApi, string category, string message) {
        try {
            return await ReportResultAsync(await feedbackApi.SubmitAsync(category, message));
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }
    }

    static async Task<int> ReportResultAsync(FeedbackResult result) {
        switch (result) {
            case FeedbackResult.Sent(var reporterEmail):
                await Console.Out.WriteLineAsync($"{SuccessPrefix}{reporterEmail} — replies will reach you by email.");

                return 0;

            case FeedbackResult.NotConfigured:
                await Console.Error.WriteLineAsync("This server doesn't have support intake enabled.");

                return 1;

            case FeedbackResult.Unavailable:
                await Console.Error.WriteLineAsync("Support intake isn't configured on this server — ask your admin.");

                return 1;

            case FeedbackResult.NoEmailOnFile:
                await Console.Error.WriteLineAsync(
                    "Your account has no email on file — sign in to the web app once, then retry.");

                return 1;

            case FeedbackResult.RateLimited:
                await Console.Error.WriteLineAsync("You've sent several reports recently — try again in a few minutes.");

                return 1;

            case FeedbackResult.TemporarilyUnavailable(var retryAfter):
                var suffix = retryAfter is { } delta
                    ? $" in {(int)Math.Ceiling(delta.TotalSeconds)}s."
                    : ".";
                await Console.Error.WriteLineAsync($"Couldn't reach Kurrent support (temporary) — try again{suffix}");

                return 1;

            case FeedbackResult.Invalid(var invalidMessage):
                await Console.Error.WriteLineAsync(invalidMessage);

                return 1;

            default:
                throw new UnreachableException();
        }
    }

    static string? GetMessageArg(string[] args) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] is "-m" or "--message") return args[i + 1];
        }

        return null;
    }

    /// <summary>
    /// Collects lines from <paramref name="readLine"/> until an empty line (or end of input,
    /// e.g. Ctrl+D) and joins them with newlines. Pure and injectable so tests can drive the
    /// multi-line collection without a real TTY.
    /// </summary>
    internal static string ReadInteractiveMessage(Func<string?> readLine) {
        var lines = new List<string>();

        while (true) {
            var line = readLine();

            if (string.IsNullOrEmpty(line)) break;

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }
}
