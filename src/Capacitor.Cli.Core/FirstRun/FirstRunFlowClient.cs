using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>One create attempt. <paramref name="StatusCode"/> 0 is a transport failure;
/// <paramref name="RetryAfter"/> is populated only on a 429, and only when the server sent one.</summary>
public sealed record FirstRunCreateOutcome(int StatusCode, FirstRunFlowResponse? Body, TimeSpan? RetryAfter = null);

/// <summary>One poll. The body is absent on every non-200, and on a 200 whose body was unreadable —
/// which <see cref="FirstRunFlowPoll.Classify"/> treats as a blip rather than as an answer.
/// <paramref name="RetryAfter"/> is populated only on a 429, and only when the server sent one.</summary>
public sealed record FirstRunPollOutcome(int StatusCode, FirstRunFlowResponse? Body, TimeSpan? RetryAfter = null);

/// <summary>One report of a performed action. <paramref name="StatusCode"/> 0 is a transport failure, and
/// the loop retries the report on the next tick — the action itself is never repeated.</summary>
public sealed record FirstRunActionReportOutcome(int StatusCode) {
    /// <summary>Whether the server recorded it. A non-2xx leaves the request outstanding, which is what
    /// makes the retry self-terminating: the browser stops listing it once this succeeds.</summary>
    public bool Recorded => StatusCode is >= 200 and < 300;
}

/// <summary>One report about the import — what was found on disk, or how the run ended. A non-2xx is
/// retried on a later tick; there is nothing to undo, because the server takes the first report for a
/// given decision and ignores the rest.</summary>
public sealed record FirstRunImportReportOutcome(int StatusCode) {
    public bool Recorded => StatusCode is >= 200 and < 300;
}

/// <summary>One attempt at saying this machine has gone. Never retried: it is sent as the leg ends, so
/// there is no later tick, and what a failure costs is a browser left waiting until the flow's own
/// lifetime ends it.</summary>
public sealed record FirstRunRelinquishOutcome(int StatusCode) {
    public bool Recorded => StatusCode is >= 200 and < 300;
}

/// <summary>One beat. Never retried, and success is not inspected: the next beat is already due, and a
/// run of them failing is precisely what the browser is meant to notice.
///
/// <para><paramref name="RetryAfter"/> is the exception, populated only on a 429 and only when the server
/// sent one. A throttle is an instruction rather than a failure, and beating through it would spend a
/// tenant's budget on liveness and leave the poll — the interactive half — in penalty.</para></summary>
public sealed record FirstRunHeartbeatOutcome(int StatusCode, TimeSpan? RetryAfter = null) {
    public bool Recorded => StatusCode is >= 200 and < 300;
}

/// <summary>The flow routes, as a seam: the loop, the backoff and the guards around them are the
/// part worth testing, and they should not need a socket to exercise.</summary>
public interface IFirstRunFlowChannel {
    /// <summary>Creates the flow, before the browser is opened, carrying what this machine found on
    /// itself — the Agents screen has no rows without it.</summary>
    Task<FirstRunCreateOutcome> CreateAsync(
        string serverUrl, string flowId, FirstRunMachineReport report, CancellationToken ct);

    /// <summary>Reads a flow this caller owns.</summary>
    Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct);

    /// <summary>Reports what performing one of the browser's requests produced, against the request's own
    /// timestamp — the server drops a report that answers a superseded request.</summary>
    Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
        string serverUrl, string flowId, ReportFirstRunMachineActionRequest report, CancellationToken ct);

    /// <summary>Reports what discovery found. The Import screen renders its waiting state until this
    /// lands, so a machine with no history still has to report — an empty repo list is an answer.</summary>
    Task<FirstRunImportReportOutcome> ReportImportAsync(
        string serverUrl, string flowId, ReportFirstRunImportRequest report, CancellationToken ct);

    /// <summary>Reports how the import ended, against the decision that ran. Also how the screen learns
    /// the run is over, so a refusal and an empty choice are both reported rather than left silent.</summary>
    Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(
        string serverUrl, string flowId, ReportFirstRunImportOutcomeRequest report, CancellationToken ct);

    /// <summary>Says this machine has stopped listening, so the browser stops offering decisions nothing
    /// will act on. Best effort by design: it is the last thing the leg does.</summary>
    Task<FirstRunRelinquishOutcome> RelinquishAsync(
        string serverUrl, string flowId, string reason, CancellationToken ct);

    /// <summary>Says this machine is still here. Sent on its own timer rather than from the poll, which
    /// stops for the whole of an import — see <see cref="FirstRunHeartbeat"/>.</summary>
    Task<FirstRunHeartbeatOutcome> HeartbeatAsync(string serverUrl, string flowId, CancellationToken ct);
}

/// <summary>
/// The CLI's client for the tenant's first-run flow routes.
///
/// <para>Degrades rather than throws, on <c>TenantProvisioningClient</c>'s convention: a transient
/// blip mid-poll must not crash an interactive <c>kcap setup</c>, and the loop is the right place to
/// decide what a blip means.</para>
///
/// <para><paramref name="http"/> must already carry the caller's bearer. Both routes are
/// authenticated — there is no anonymous overload of either, deliberately, because Capacitor is
/// single-tenant <i>multi-user</i> and it is the server's ownership check, not the token, that
/// decides whose flow this is.</para>
/// </summary>
public sealed class FirstRunFlowClient(HttpClient http) : IFirstRunFlowChannel {
    /// <inheritdoc/>
    public async Task<FirstRunCreateOutcome> CreateAsync(
            string serverUrl, string flowId, FirstRunMachineReport report, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new CreateFirstRunFlowRequest {
                FlowId             = flowId,
                Machine            = report.Machine,
                MachineId          = report.MachineId,
                Harnesses          = new Dictionary<string, FirstRunHarnessReport>(report.Harnesses, StringComparer.Ordinal),
                Declined           = [.. report.Declined],
                LoginShellFindsCli = report.LoginShellFindsCli,
                Platform           = report.Platform
            },
            CapacitorJsonContext.Default.CreateFirstRunFlowRequest);

        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(serverUrl)}/api/first-run/flows") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null, RetryAfter(resp));

            return new((int)resp.StatusCode, await ReadAsync(resp, ct));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0, null);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
        try {
            using var req  = new HttpRequestMessage(
                HttpMethod.Get, $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}");
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null, RetryAfter(resp));

            return new((int)resp.StatusCode, await ReadAsync(resp, ct));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0, null);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
            string serverUrl, string flowId, ReportFirstRunMachineActionRequest report, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            report, CapacitorJsonContext.Default.ReportFirstRunMachineActionRequest);

        try {
            using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}/actions") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            return new((int)resp.StatusCode);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunImportReportOutcome> ReportImportAsync(
            string serverUrl, string flowId, ReportFirstRunImportRequest report, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(report, CapacitorJsonContext.Default.ReportFirstRunImportRequest);

        try {
            using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}/import") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            return new((int)resp.StatusCode);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(
            string serverUrl, string flowId, ReportFirstRunImportOutcomeRequest report, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            report, CapacitorJsonContext.Default.ReportFirstRunImportOutcomeRequest);

        try {
            using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}/import-outcome") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            return new((int)resp.StatusCode);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunRelinquishOutcome> RelinquishAsync(
            string serverUrl, string flowId, string reason, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new RelinquishFirstRunFlowRequest { Reason = reason },
            CapacitorJsonContext.Default.RelinquishFirstRunFlowRequest);

        try {
            using var req = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}/relinquish") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            return new((int)resp.StatusCode);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunHeartbeatOutcome> HeartbeatAsync(
            string serverUrl, string flowId, CancellationToken ct) {
        try {
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}/heartbeat");

            using var resp = await http.SendAsync(req, ct);

            var status = (int)resp.StatusCode;

            return new(status, status is 429 ? RetryAfter(resp) : null);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0);
        }
    }

    /// <summary>How long the server asked us to wait, in either header form — a proxy may rewrite
    /// delta-seconds as an HTTP date, and reading only the delta would report that as no header at
    /// all. A date is measured against the response's own Date header, so server clock skew cannot
    /// turn the wait negative.</summary>
    static TimeSpan? RetryAfter(HttpResponseMessage resp) => resp.Headers.RetryAfter switch {
        { Delta: { } delta } => delta,
        { Date:  { } date  } => Max(date - (resp.Headers.Date ?? DateTimeOffset.UtcNow), TimeSpan.Zero),
        _                    => null
    };

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>Guarded separately from the send: an unreadable body must not collapse to status 0,
    /// which the loop reports as "could not reach the server" about a server that just answered.</summary>
    static async Task<FirstRunFlowResponse?> ReadAsync(HttpResponseMessage resp, CancellationToken ct) {
        try {
            return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.FirstRunFlowResponse, ct);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return null;
        }
    }

    static string Base(string serverUrl) => serverUrl.TrimEnd('/');

    /// <summary>A cancel from the caller is not a blip: reported as status 0 it reads as a transport
    /// failure, and the poll loop would run to its budget instead of stopping. HttpClient's own
    /// timeout is the same exception type with the token unsignalled, and that one genuinely is a blip.</summary>
    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException;
}
