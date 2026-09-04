using System.Net;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The transport outcome of one SessionStart context GET. <see cref="Body"/> is
/// the bounded response bytes on a 2xx (empty array for 204), and null on any
/// non-success status. Status mapping is deliberately left to the caller: the
/// memory lane and the guidelines lane treat 404 differently (the guidelines visibility-race rule), so
/// this helper carries the raw status through rather than pre-deciding.
/// </summary>
internal readonly record struct SessionStartFetchOutcome(HttpStatusCode Status, byte[]? Body, TimeSpan? RetryAfter);

/// <summary>
/// Shared HTTP mechanics for the SessionStart context lanes: a GET read at
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, a 256 KiB bounded body read on success,
/// and <c>Retry-After</c> parsing. The client arrives already carrying whatever credential its lane
/// applies, and a 401 is recovered below this — so nothing here inspects auth.
/// </summary>
internal static class SessionStartContextFetch {
    public static async Task<SessionStartFetchOutcome> FetchAsync(
            HttpClient client, string url, CancellationToken ct) {
        // Headers-read, so the bounded read below decides how much of the body is ever pulled.
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

        var retryAfter = ParseRetryAfter(response);

        if (!response.IsSuccessStatusCode)
            return new SessionStartFetchOutcome(response.StatusCode, Body: null, retryAfter);

        var bytes = await ReadBoundedAsync(response.Content, ct);

        return new SessionStartFetchOutcome(response.StatusCode, bytes, RetryAfter: null);
    }

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct) {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[SessionStartMemoryConstants.MaxResponseBytes + 1];
        var total  = 0;
        while (total < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        if (total > SessionStartMemoryConstants.MaxResponseBytes)
            throw new InvalidDataException("SessionStart context response exceeded 256 KiB.");
        return buffer.AsSpan(0, total).ToArray();
    }

    static TimeSpan? ParseRetryAfter(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.TooManyRequests || response.Headers.RetryAfter is null) return null;
        if (response.Headers.RetryAfter.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter.Date is { } date) {
            var value = date - DateTimeOffset.UtcNow;
            return value > TimeSpan.Zero ? value : null;
        }
        return null;
    }
}
