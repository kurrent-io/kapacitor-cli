using System.Diagnostics;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class ReviewCommand(ProfileContext profiles, IReviewApi review) {
    public async Task<int> HandleReview(string prIdentifier) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Parse PR identifier
        if (!PrRefParser.TryParse(prIdentifier, out var owner, out var repo, out var prNumber)) {
            await Console.Error.WriteLineAsync($"Could not parse PR identifier: {prIdentifier}");
            await Console.Error.WriteLineAsync("Expected formats:");
            await Console.Error.WriteLineAsync("  GitHub URL:  https://github.com/owner/repo/pull/123");
            await Console.Error.WriteLineAsync("  GitLab URL:  https://gitlab.com/owner/repo/-/merge_requests/123");
            await Console.Error.WriteLineAsync("  Shorthand:   owner/repo#123");

            return 1;
        }

        await Console.Error.WriteLineAsync($"Reviewing PR #{prNumber} in {owner}/{repo}...");

        // Launching is only worth it once the server actually holds context for this PR.
        try {
            if (await review.GetPullRequestContextAsync(owner, repo, prNumber) is ReviewContextResult.NotFound) {
                await Console.Error.WriteLineAsync($"No review context found for {owner}/{repo}#{prNumber}.");
                await Console.Error.WriteLineAsync("Make sure the PR has sessions tracked in Capacitor.");

                return 1;
            }
        } catch (CapacitorApiException ex) {
            // An unreachable server and a 401 both phrase themselves; every other status needs saying
            // what the CLI was doing when it got one.
            await Console.Error.WriteLineAsync(
                ex.Status is null or 401 ? ex.Message : $"Server returned HTTP {ex.Status} when checking review context.");

            return 1;
        }

        var launch = await ReviewLaunchBuilder.BuildAsync(
            "claude", Environment.ProcessPath ?? "kcap", baseUrl, owner, repo, prNumber);

        try {
            await Console.Error.WriteLineAsync("Launching claude with review MCP server...");

            var psi = new ProcessStartInfo {
                FileName        = "claude",
                UseShellExecute = true
            };

            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(launch.McpConfigPath!);
            psi.ArgumentList.Add("--system-prompt");
            psi.ArgumentList.Add(launch.SystemPrompt);

            var process = Process.Start(psi);

            if (process is null) {
                await Console.Error.WriteLineAsync("Failed to start claude. Make sure it is installed and on your PATH.");

                return 1;
            }

            await process.WaitForExitAsync();

            return process.ExitCode;
        } finally {
            try {
                File.Delete(launch.McpConfigPath!);
            } catch {
                // Best effort cleanup
            }
        }
    }

}
