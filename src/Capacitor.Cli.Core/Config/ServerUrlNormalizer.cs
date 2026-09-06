using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Config;

/// <summary>
/// Resolves the scheme for user-supplied server URLs. Used on save paths
/// (config set / profile add / setup) so v1-style scheme-less values do
/// not silently land on disk.
/// </summary>
public static class ServerUrlNormalizer {
    /// <summary>
    /// Outcome of normalizing a user-supplied server URL.
    /// </summary>
    /// <param name="Url">The URL to persist.</param>
    /// <param name="Warning">Caller-agnostic diagnostic. Null when nothing notable happened.</param>
    /// <param name="Reachable">
    /// <c>false</c> only when probing failed on every attempted scheme. Save-path callers
    /// typically save anyway and print the warning; interactive setup uses this as the
    /// strict reachability gate.
    /// </param>
    public record Result(string Url, string? Warning, bool Reachable = true);

    static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "host.docker.internal"];

    /// <summary>
    /// Returns the input with an inferred scheme: <c>http://</c> for well-known
    /// loopback hosts, <c>https://</c> otherwise. Trims trailing slashes. Pure;
    /// does no I/O. Used as a fallback when probing fails or is skipped.
    /// </summary>
    public static string WithLoopbackDefault(string input) {
        var trimmed = input.TrimEnd('/');

        if (HasScheme(trimmed)) return trimmed;

        var host = ExtractHost(trimmed);
        var scheme = LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ? "http" : "https";
        return $"{scheme}://{trimmed}";
    }

    static bool HasScheme(string input) =>
        input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    static string ExtractHost(string input) {
        // Strip path. For bracketed IPv6 the '/' separator only appears after ']'.
        var bracketEnd = input.StartsWith('[') ? input.IndexOf(']') : -1;
        var searchFrom = bracketEnd > 0 ? bracketEnd + 1 : 0;
        var pathStart = input.IndexOf('/', searchFrom);
        var hostAndPort = pathStart >= 0 ? input[..pathStart] : input;

        // Bracketed IPv6 literal: "[::1]" or "[::1]:5108" → "::1".
        if (bracketEnd > 0) return hostAndPort[1..bracketEnd];

        // Bare IPv6 (contains "::"): no port stripping — colons are part of the host.
        if (hostAndPort.Contains("::")) return hostAndPort;

        // Strip port — but only the last ":N" if N is digits.
        var lastColon = hostAndPort.LastIndexOf(':');
        if (lastColon > 0 && hostAndPort[(lastColon + 1)..].All(char.IsDigit))
            return hostAndPort[..lastColon];

        return hostAndPort;
    }

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Resolves a server URL the user just supplied: applies the loopback default
    /// for scheme-less input and probes the result (or both schemes) via
    /// <c>GET /auth/config</c>. Never throws — on probe failure, returns the
    /// loopback-default URL with <c>Reachable = false</c> so the caller can decide
    /// what to do.
    /// </summary>
    public static async Task<Result> NormalizeAsync(
        string                                                  input,
        bool                                                    skipProbe,
        CancellationToken                                       ct,
        Func<string, TimeSpan, CancellationToken, Task<bool>>   probe) {

        var trimmed = input.TrimEnd('/');

        if (skipProbe) return new(WithLoopbackDefault(trimmed), null);

        if (HasScheme(trimmed)) {
            return await probe(trimmed, ProbeTimeout, ct)
                ? new(trimmed, null)
                : new(trimmed, $"could not reach {trimmed}", Reachable: false);
        }

        var httpsCandidate = $"https://{trimmed}";
        if (await probe(httpsCandidate, ProbeTimeout, ct))
            return new(httpsCandidate, null);

        var httpCandidate = $"http://{trimmed}";
        if (await probe(httpCandidate, ProbeTimeout, ct)) {
            // Loopback hosts default to http anyway, so the "downgrade" is expected.
            // For everything else, warn so the user notices they're now on insecure HTTP.
            var host       = ExtractHost(trimmed);
            var isLoopback = LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
            return isLoopback
                ? new(httpCandidate, null)
                : new(httpCandidate, $"https probe failed; saved as {httpCandidate}");
        }

        var fallback = WithLoopbackDefault(trimmed);
        return new(fallback, $"could not reach {trimmed} on https or http", Reachable: false);
    }

    /// <summary>The real probe. Anonymous by necessity, not by preference: the candidate is a server
    /// the caller has not adopted, so there is no credential for it and sending one would be a
    /// credential spent on an address the user may reject.</summary>
    public static Func<string, TimeSpan, CancellationToken, Task<bool>> ProbeWith(ICapacitorHttpClient http) =>
        (url, timeout, ct) => HttpProbeAsync(http, url, timeout, ct);

    static async Task<bool> HttpProbeAsync(
            ICapacitorHttpClient http, string url, TimeSpan timeout, CancellationToken ct) {
        using var client = http.Anonymous();
        client.Timeout = timeout;

        try {
            using var resp = await client.GetAsync($"{url}/auth/config", ct);
            // Any HTTP response means the server is reachable. We do not require
            // 200 — older servers without /auth/config still count as "up".
            return true;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            return false;
        }
    }
}
