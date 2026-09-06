using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class SessionsCommand(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    public async Task<int> HandleAsync(string[] args) {
        var options = SessionsArgs.Parse(args, out var error);

        if (options is null) {
            await Console.Error.WriteLineAsync($"kcap sessions: {error}");
            await Console.Error.WriteLineAsync(SessionsArgs.Usage);

            return 1;
        }

        string repoHash;
        string label;

        if (options.Repo is null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(
                config, Directory.GetCurrentDirectory(), detectPullRequest: false);

            if (repo?.Owner is null || repo.RepoName is null) {
                await Console.Error.WriteLineAsync("Not in a git repository with a remote origin.");

                return 1;
            }

            repoHash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
            label    = $"{repo.Owner}/{repo.RepoName}";
        } else {
            repoHash = options.RepoHash!;
            label    = options.Repo;
        }

        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await http.ForCommandAsync();

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync(BuildUrl(baseUrl, repoHash, options));
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) return 1;

        if (resp.StatusCode == HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync("Session listing needs a newer server; ask your admin to update.");

            return 1;
        }

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}: {body}");

            return 1;
        }

        if (options.Json) {
            await Console.Out.WriteLineAsync(body);

            return 0;
        }

        var page = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.RepoSessionsResponse);

        if (page is null) {
            await Console.Error.WriteLineAsync("Unexpected response from the server.");

            return 1;
        }

        await Console.Out.WriteAsync(Render(page, label, options.State));

        return 0;
    }

    internal static string BuildUrl(string baseUrl, string repoHash, SessionsOptions options) {
        var qs = new List<string> { $"state={options.State}", $"limit={options.Limit}" };

        if (options.Mine) qs.Add("owner=me");

        if (options.Touching is { Length: > 0 } touching) qs.Add($"touching_path={Uri.EscapeDataString(touching)}");

        return $"{baseUrl}/api/repositories/{repoHash}/sessions?" + string.Join("&", qs);
    }

    internal static string Render(RepoSessionsResponse page, string repoLabel, string state) {
        var sb = new StringBuilder();

        if (page.Items.Count == 0) {
            sb.AppendLine($"No {state} sessions visible to you on {repoLabel}.");

            return sb.ToString();
        }

        sb.AppendLine($"{"SESSION",-33} {"STATUS",-7} {"ACCESS",-9} {"OWNER",-14} {"VENDOR",-8} {"BRANCH",-24} {"LAST ACTIVITY",-17} TITLE");

        foreach (var row in page.Items) {
            var status = row.Status == "active" && row.Stale ? "stale" : row.Status;
            var owner  = row.Owner?.Username ?? row.Owner?.UserId ?? "";
            var last   = row.LastActivityAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            sb.AppendLine(
                $"{Fit(row.SessionId, 32),-33} {status,-7} {row.AccessLevel,-9} {Fit(owner, 13),-14} {Fit(row.Vendor ?? "", 7),-8} {Fit(row.Branch ?? "", 23),-24} {last,-17} {row.Title ?? "(untitled)"}");
        }

        if (page.Total > page.Items.Count)
            sb.AppendLine($"Showing {page.Items.Count} of {page.Total}; raise --limit or narrow with --mine / --touching.");

        sb.AppendLine();
        sb.AppendLine("Details (full access): kcap recap --full <session-id>");

        return sb.ToString();
    }

    static string Fit(string value, int width) => value.Length <= width ? value : value[..(width - 1)] + "…";
}
