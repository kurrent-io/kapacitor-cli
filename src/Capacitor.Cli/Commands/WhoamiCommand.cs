using System.Net;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Reports the stored identity and whether the server actually accepts it. Local metadata alone
/// is not an answer: "expires tomorrow" only says a clock hasn't passed, so a token the server
/// rejects still looks valid. Hence the probe.
/// </summary>
public sealed class WhoamiCommand(
        ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        AuthProviderDiscovery discovery) {
    /// <summary>Cheap authenticated GET used purely to ask "do you accept this token?".</summary>
    internal const string ProbePath = "/api/me/notification-prefs";

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The server's verdict on the token, and the exit code it implies.</summary>
    internal readonly record struct ProbeVerdict(string Line, int ExitCode);

    /// <summary>
    /// Maps a probe response to what we tell the user. ONLY 401/403 are verdicts about the token:
    /// everything else means we failed to ask, and reporting that as "rejected" would send people
    /// to re-run `kcap login` for an outage or an older server that lacks the endpoint.
    /// </summary>
    internal static ProbeVerdict Interpret(HttpStatusCode? status) => status switch {
        null                                    => new("could not verify (server unreachable)", 0),
        HttpStatusCode.Unauthorized             => new("REJECTS this token (run 'kcap login')", 1),
        HttpStatusCode.Forbidden                => new("REJECTS this token (run 'kcap login')", 1),
        HttpStatusCode.NotFound                 => new("could not verify (endpoint not available on this server)", 0),
        >= HttpStatusCode.OK and < (HttpStatusCode)300              => new("accepts this token", 0),
        >= (HttpStatusCode)300 and < (HttpStatusCode)400            => new("could not verify (unexpected redirect)", 0),
        { } other                               => new($"could not verify (server error {(int)other})", 0)
    };

    public async Task<int> HandleAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var provider = await discovery.DiscoverAsync(baseUrl, config, profiles, tokens);

        if (provider == "None") {
            await Console.Out.WriteLineAsync("Provider: None (no authentication)");
            await Console.Out.WriteLineAsync($"Server:   {baseUrl}");

            return 0;
        }

        // ONE raw snapshot for everything below — deliberately NOT the refresh-aware accessor.
        // Diagnosing your auth must not mutate it: routing this through a refresh could rotate a
        // WorkOS credential (single-use refresh token) as a side effect of merely running whoami,
        // and would let the expiry printed here describe a different token than the one probed.
        var profile  = profiles.Name;
        var snapshot = await tokens.LoadForProfileAsync(profile);

        if (snapshot is null) {
            Console.Error.WriteLine("Not authenticated. Run `kcap login`.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Username: {snapshot.GitHubUsername}");
        await Console.Out.WriteLineAsync($"Provider: {snapshot.Provider}");
        await Console.Out.WriteLineAsync($"Profile:  {profile}");
        await Console.Out.WriteLineAsync($"Expires:  {snapshot.ExpiresAt:u}");
        await Console.Out.WriteLineAsync($"Server:   {baseUrl}");
        await Console.Out.WriteLineAsync($"Expired:  {(snapshot.IsExpired ? "yes" : "no")}");

        // A token minted elsewhere can never be accepted here, and no refresh can change that —
        // say so instead of spending a request to be told 401.
        if (snapshot.ServerUrl is not null && !ServerIdentity.SameServer(snapshot.ServerUrl, baseUrl)) {
            await Console.Out.WriteLineAsync(
                $"Server:   token was issued by {snapshot.ServerUrl} — run 'kcap login'");

            return 1;
        }

        var verdict = Interpret(await ProbeAsync(baseUrl, snapshot.AccessToken));
        await Console.Out.WriteLineAsync($"Server:   {verdict.Line}");

        return verdict.ExitCode;
    }

    // Null means "we never got an answer" (transport failure or timeout), which is deliberately
    // distinct from any status code the server did return.
    async Task<HttpStatusCode?> ProbeAsync(string baseUrl, string accessToken) {
        try {
            // The lane sends exactly the token printed above: no rotation, because the question is
            // whether THIS token is accepted, and no redirect, because the hop would strip it and
            // return a 401 that reads as the server's verdict.
            using var client = http.Bearer();
            client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

            using var response = await client.GetOnceAsync(
                $"{AppConfig.NormalizeUrl(baseUrl)}{ProbePath}", ProbeTimeout);

            return response.StatusCode;
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
            return null;
        }
    }
}
