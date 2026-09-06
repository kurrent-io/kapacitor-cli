using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core;

/// <summary>
/// Outcome of building an authenticated client. Lets hook callers decide whether a POST is even
/// worth attempting and whether to surface a re-auth prompt, instead of blindly POSTing a request
/// the server will reject with 401.
/// </summary>
public enum AuthStatus {
    /// <summary>A valid Bearer token is attached to the client.</summary>
    Ok,

    /// <summary>The server requires no auth ("None" provider) — the client is usable as-is.</summary>
    NoAuthRequired,

    /// <summary>A token is stored but expired and could not be refreshed — re-login required.</summary>
    Expired,

    /// <summary>No token is stored at all — login required.</summary>
    NotAuthenticated,

    /// <summary>
    /// A token is stored, but it was minted by a different server than the one being targeted.
    /// No refresh can heal this (the server validates the token's own signature) — only a login
    /// against the target server, or switching to the profile that owns it.
    /// </summary>
    WrongServer,

    /// <summary>
    /// The configured server URL is not one anything can be sent to, so nothing was attempted: no
    /// token-store read, no discovery, no socket. Repaired by config, never by a login.
    /// </summary>
    UnusableServerUrl,
}

public static class HttpClientExtensions {
    /// <summary>Wire header naming the installed CLI's display version (see <see cref="CapacitorVersion.CurrentDisplay"/>).</summary>
    public const string CliVersionHeader = "X-Kcap-Cli-Version";

    /// <summary>Response header carrying the connected server's own version, captured by
    /// <see cref="ServerVersionCaptureHandler"/> so the passive update notice can cap its
    /// recommendation at <c>min(npm latest, server version)</c>.</summary>
    public const string ServerVersionHeader = "X-Kcap-Server-Version";

    /// <summary>
    /// Wire header sent ONLY to declare the active profile's update-check preference is off. Its
    /// ABSENCE on a version-carrying request means the preference is on (the default) — never send
    /// an "on" value, only omit the header.
    /// </summary>
    public const string UpdateCheckHeader = "X-Kcap-Update-Check";

    /// <summary>Value <see cref="UpdateCheckHeader"/> carries when sent.</summary>
    public const string UpdateCheckOffValue = "off";

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxDelay       = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Per-attempt cap on a single HTTP call inside <see cref="SendWithRetryAsync(Func{CancellationToken, Task{HttpResponseMessage}}, TimeSpan, CancellationToken, bool)"/>.
    /// Enforced via a linked <see cref="CancellationTokenSource"/> so the wall-clock cap
    /// is observable on the token we pass to <see cref="HttpClient"/> — not on the
    /// client's own <see cref="HttpClient.Timeout"/> (default 100s), which would
    /// otherwise raise an unhandled <see cref="TaskCanceledException"/> at every call
    /// site whose <c>catch</c> only covers <see cref="HttpRequestException"/>.
    /// </summary>
    internal static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(60);

    const string UnreachableHint =
        "Kurrent Capacitor API cannot be reached, is it running? "                    +
        "Make sure the URL is correctly configured and the service is running. "      +
        "Check https://github.com/kurrent-io/claude-remember#setup for instructions." +
        "\rError connecting to: ";

    internal const string SchemeMissingHint =
        "server_url is missing a scheme. Run: kcap config set server_url https://<host>";

    /// <summary>Whether a URL can be sent to at all: absolute, and http or https.</summary>
    public static bool IsAcceptableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    extension(HttpClient client) {
        /// <param name="retryStatuses">Retry a retryable status as a transport fault is retried — see
        /// <see cref="IsRetryableStatus"/>. Off by default, and set only where a lost call is counted and
        /// shown to someone.</param>
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout       = null,
                CancellationToken ct            = default,
                bool              retryStatuses = false
            ) =>
            SendWithRetryAsync(token => client.PostAsync(url, content, token), timeout ?? DefaultTimeout, ct, retryStatuses);

        public Task<HttpResponseMessage> GetWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) =>
            SendWithRetryAsync(token => client.GetAsync(url, token), timeout ?? DefaultTimeout, ct);

        /// <param name="retryStatuses">Retry a retryable status as a transport fault is retried — see
        /// <see cref="IsRetryableStatus"/>. Off by default, and set only where a lost call is counted and
        /// shown to someone.</param>
        public Task<HttpResponseMessage> PutWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout       = null,
                CancellationToken ct            = default,
                bool              retryStatuses = false
            ) =>
            SendWithRetryAsync(token => client.PutAsync(url, content, token), timeout ?? DefaultTimeout, ct, retryStatuses);

        public Task<HttpResponseMessage> DeleteWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) =>
            SendWithRetryAsync(token => client.DeleteAsync(url, token), timeout ?? DefaultTimeout, ct);

        /// <summary>
        /// Single-attempt POST with a hard per-call timeout. No retry, no
        /// backoff. Used by hook-path call sites where retries would burst
        /// the shared dispatcher budget. <paramref name="ct"/> is honoured;
        /// expiry of <paramref name="timeout"/> surfaces as
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        public async Task<HttpResponseMessage> PostOnceAsync(
                string            url,
                HttpContent       content,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.PostAsync(url, content, linkedCts.Token);
        }

        /// <summary>Single-attempt GET — see <see cref="PostOnceAsync"/>.</summary>
        public async Task<HttpResponseMessage> GetOnceAsync(
                string            url,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.GetAsync(url, linkedCts.Token);
        }
    }

    /// <summary>
    /// Renders the one stderr line written when the API is unreachable after every retry.
    ///
    /// <para>The URL goes through <see cref="UnusableUrlDiagnostic.Sanitize"/> — the same helper, in the
    /// same assembly, that the unusable-URL guard has always used. A <c>server_url</c> may carry userinfo
    /// credentials, and this line is reachable from the HOOK path (<c>AgentHookPoster</c> calls it on any
    /// transport fault), so echoing it raw printed them on every lifecycle POST for every vendor. The host
    /// survives sanitization, which is the part that makes the line actionable.</para>
    ///
    /// <para>Control characters are stripped from EVERY variable component, not just the URL: this line
    /// goes to a stream harnesses parse — Gemini reads hook stderr as the hook's own result when stdout
    /// is empty — so either half of the interpolation could otherwise fabricate a line. The fixed hint's
    /// own <c>\r</c> is left alone; it is not attacker-reachable, and changing it would alter output
    /// every existing call site produces.</para>
    /// </summary>
    public static string RenderUnreachableError(string? baseUrl, string? exceptionMessage) =>
        $"{UnreachableHint} {UnusableUrlDiagnostic.Sanitize(baseUrl)} {StripControlCharacters(exceptionMessage)}";

    internal static string StripControlCharacters(string? value) =>
        string.IsNullOrEmpty(value) ? "" : new string(value.Where(c => !char.IsControl(c)).ToArray());

    /// <summary>
    /// Writes the unreachable-API diagnostic to stderr. See <see cref="RenderUnreachableError"/> for why
    /// nothing here may be interpolated raw.
    /// </summary>
    public static void WriteUnreachableError(string baseUrl, HttpRequestException ex) {
        Console.Error.WriteLine(RenderUnreachableError(baseUrl, ex.Message));
    }

    /// <summary>String-returning twin of <see cref="WriteUnreachableError"/>, for callers that route output through a progress sink instead of Console.</summary>
    public static string UnreachableErrorText(string baseUrl, HttpRequestException ex) =>
        RenderUnreachableError(baseUrl, ex.Message);

    /// <summary>
    /// Checks if the response is a 401 and prints the server's error message.
    /// Returns true if the response was a 401 (caller should return early).
    /// </summary>
    public static async Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.Unauthorized) {
            return false;
        }

        await Console.Error.WriteLineAsync(await UnauthorizedMessageAsync(response));

        return true;
    }

    /// <summary>The server's own 401 text where it sent one, else the standard re-auth guidance.</summary>
    public static async Task<string> UnauthorizedMessageAsync(HttpResponseMessage response) {
        const string fallback = "Authentication failed. Run 'kcap login' to re-authenticate.";

        try {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            return doc.RootElement.Str("message") ?? fallback;
        } catch {
            return fallback;
        }
    }

    /// <summary>
    /// Statuses worth a second attempt: a request timeout, a rate limit, and anything the server calls
    /// its own fault.
    ///
    /// <para><b>Opt-in per call site, never the default.</b> Every hook, watch, daemon and MCP path
    /// shares this helper, and retrying a 5xx for all of them at once would change the timing of paths
    /// whose budgets are shaped around a single attempt.</para>
    /// </summary>
    internal static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    /// <summary>How long the server asked us to wait, in either header form — a proxy may rewrite
    /// delta-seconds as an HTTP date, and reading only the delta would treat that as no header at all. A
    /// date is measured against the response's own Date header so clock skew cannot invert the wait.</summary>
    static TimeSpan? RetryAfterOf(HttpResponseMessage resp) => resp.Headers.RetryAfter switch {
        { Delta: { } delta } => delta,
        { Date:  { } date  } => date - (resp.Headers.Date ?? DateTimeOffset.UtcNow) is { Ticks: > 0 } wait
                                    ? wait
                                    : TimeSpan.Zero,
        _                    => null
    };

    internal static Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send,
            TimeSpan                                           totalTimeout,
            CancellationToken                                  ct,
            bool                                               retryStatuses = false
        ) => SendWithRetryAsync(send, totalTimeout, PerAttemptTimeout, ct, retryStatuses);

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send,
            TimeSpan                                           totalTimeout,
            TimeSpan                                           perAttemptTimeout,
            CancellationToken                                  ct,
            bool                                               retryStatuses = false
        ) {
        var        sw        = Stopwatch.StartNew();
        var        delayMs   = 250;
        Exception? lastError = null;

        // The last retryable response, held so that running out of budget returns the status the server
        // actually sent. Throwing instead would report a transport failure about a server that answered
        // every time, and the call sites catch only HttpRequestException.
        //
        // Nulled when handed to the caller, so the finally disposes only what nobody received.
        HttpResponseMessage? refused   = null;
        TimeSpan?            honourFor = null;

        try {
            while (true) {
                // Hard wall-clock guard: never start a new attempt (or sleep) past totalTimeout,
                // even when perAttemptTimeout would otherwise allow it. Without this, a default
                // call (total=30s, per-attempt=60s) against a hung server still blocks for ~60s.
                var remaining = totalTimeout - sw.Elapsed;

                if (remaining <= TimeSpan.Zero)
                    return Answered() ?? throw BudgetExhausted(totalTimeout, perAttemptTimeout, lastError);

                var attemptCap = remaining < perAttemptTimeout ? remaining : perAttemptTimeout;

                using var attemptCts = new CancellationTokenSource(attemptCap);
                using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, attemptCts.Token);

                try {
                    var resp = await send(linkedCts.Token);

                    if (!retryStatuses || !IsRetryableStatus(resp.StatusCode)) return resp;

                    // Replaces rather than accumulates: only the newest refusal is worth returning, and
                    // the one it replaces holds a connection until it is dropped.
                    refused?.Dispose();
                    refused   = resp;
                    honourFor = RetryAfterOf(resp);
                    lastError = null;
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    // Caller cancelled — surface as cancellation, never retry.
                    throw;
                } catch (HttpRequestException ex) when (sw.Elapsed < totalTimeout) {
                    // Transient transport error within retry budget — back off and try again.
                    lastError = ex;
                } catch (OperationCanceledException ex) when (sw.Elapsed < totalTimeout) {
                    // Per-attempt timeout fired (linked CTS, not caller's ct) and retry budget
                    // remains — back off and try again. Without this branch the same condition
                    // would surface as an unhandled TaskCanceledException at every call site
                    // that only catches HttpRequestException (import probes, transcript POSTs,
                    // session-start hooks, ...).
                    lastError = ex;
                } catch (HttpRequestException ex) {
                    // A refusal already in hand outranks a late transport fault: the server did answer,
                    // and that answer is what the caller counts on.
                    if (Answered() is { } answer) return answer;

                    // Budget exhausted on transport error — surface as HttpRequestException so
                    // existing `catch (HttpRequestException)` handlers degrade gracefully.
                    throw new HttpRequestException(
                        $"Request failed after exhausting the {totalTimeout.TotalSeconds:F0}s retry budget.",
                        ex
                    );
                } catch (OperationCanceledException ex) {
                    if (Answered() is { } answer) return answer;

                    throw BudgetExhausted(totalTimeout, perAttemptTimeout, ex);
                }

                // Cap the backoff sleep to the remaining budget so a retry delay can never push
                // us past totalTimeout. If nothing's left, jump back to the loop top so the
                // hard-guard above throws with lastError preserved as the inner exception.
                var remainingAfter = totalTimeout - sw.Elapsed;

                if (remainingAfter <= TimeSpan.Zero) continue;

                // The server's own figure wins when it is longer: it is the one party that knows when it
                // will be ready, and backing off less than it asked is what earns the next refusal.
                var wantMs = honourFor is { } asked
                    ? Math.Max(delayMs, asked.TotalMilliseconds)
                    : delayMs;

                honourFor = null;

                var actualDelayMs = (int)Math.Min(wantMs, remainingAfter.TotalMilliseconds);
                await Task.Delay(actualDelayMs, ct);
                delayMs = Math.Min(delayMs * 2, (int)MaxDelay.TotalMilliseconds);
            }
        } finally {
            refused?.Dispose();
        }

        // Hands the held refusal over, so the finally above stops owning it.
        HttpResponseMessage? Answered() {
            var answer = refused;

            refused = null;

            return answer;
        }

        static HttpRequestException BudgetExhausted(TimeSpan totalTimeout, TimeSpan perAttemptTimeout, Exception? inner) =>
            new(
                $"Request did not complete within the {totalTimeout.TotalSeconds:F0}s retry budget "      +
                $"(per-attempt timeout {perAttemptTimeout.TotalSeconds:F0}s).",
                inner
            );
    }
}
