using System.Net.Http.Json;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// The HTTP legs of GitHub sign-in: the device grant against github.com, and the code exchange
/// against the endpoint <c>/auth/config</c> names. Neither carries our credential or our observation
/// headers — github.com is not our server, and the exchange is what produces the credential.
///
/// <para>The exchange rides the same lane as github.com despite being our own endpoint: every leg
/// here carries something single-use — a device code, or the PKCE verifier — that a followed
/// redirect would hand to whatever host the hop names.</para>
///
/// <para>Takes the factory rather than an <see cref="HttpClient"/> for the reason
/// <see cref="WorkOSClient"/> does: a sign-in runs from long-lived hosts, and a client handed over at
/// construction would freeze the handler it was built with.</para>
/// </summary>
public sealed class GitHubOAuthClient(IHttpClientFactory httpFactory) {
    public const string ApiBase = "https://github.com";

    /// <summary>Opens a device grant. <c>read:org</c> is what makes org membership visible to the exchange.</summary>
    public Task<HttpResponseMessage> RequestDeviceCodeAsync(string clientId, CancellationToken ct) =>
        PostFormAsync($"{ApiBase}/login/device/code",
            new() { ["client_id"] = clientId, ["scope"] = "read:user read:org" }, ct);

    /// <summary>One poll of the device grant. The RFC 8628 loop that drives this lives with the flow.</summary>
    public Task<HttpResponseMessage> PollForTokenAsync(Dictionary<string, string> form, CancellationToken ct) =>
        PostFormAsync($"{ApiBase}/login/oauth/access_token", form, ct);

    /// <summary>
    /// Redeems a browser-flow authorization code. The URL is the server's, not GitHub's: the client
    /// secret lives there, so the CLI never holds one.
    /// </summary>
    public async Task<HttpResponseMessage> ExchangeCodeAsync(
            string codeExchangeUrl, GitHubCodeExchangeRequest body, CancellationToken ct) {
        using var http    = httpFactory.CreateClient(CapacitorClients.GitHub);
        using var request = new HttpRequestMessage(HttpMethod.Post, codeExchangeUrl) {
            Content = JsonContent.Create(body, CapacitorJsonContext.Default.GitHubCodeExchangeRequest)
        };

        request.Headers.Accept.Add(new("application/json"));

        return await http.SendAsync(request, ct);
    }

    // Accept rides on the request rather than the client: GitHub answers form-encoded by default, which
    // nothing downstream parses.
    async Task<HttpResponseMessage> PostFormAsync(
            string url, Dictionary<string, string> form, CancellationToken ct) {
        using var http    = httpFactory.CreateClient(CapacitorClients.GitHub);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) {
            Content = new FormUrlEncodedContent(form)
        };

        request.Headers.Accept.Add(new("application/json"));

        return await http.SendAsync(request, ct);
    }
}
