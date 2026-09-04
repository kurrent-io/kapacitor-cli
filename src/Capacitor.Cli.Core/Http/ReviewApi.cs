using System.Net;

namespace Capacitor.Cli.Core.Http;

internal sealed class ReviewApi(ICapacitorHttpClient http, CapacitorServer server) : IReviewApi {
    public async Task<ReviewContextResult> GetPullRequestContextAsync(
            string owner, string repo, int prNumber, CancellationToken ct = default) {
        using var response = await CapacitorApiRequests.SendAsync(
            http, server,
            (c, token) => c.GetAsync($"{server.Url}/api/review/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{prNumber}", token),
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return new ReviewContextResult.NotFound();
        if (!response.IsSuccessStatusCode) throw await CapacitorApiRequests.FailureAsync(response);

        return new ReviewContextResult.Found();
    }
}
