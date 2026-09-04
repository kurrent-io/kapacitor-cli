using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Http;

internal sealed class FeedbackApi(ICapacitorHttpClient http, CapacitorServer server) : IFeedbackApi {
    public async Task<FeedbackResult> SubmitAsync(string category, string message, CancellationToken ct = default) {
        var request = new FeedbackSubmitRequest(
            Category:        category,
            Message:         message,
            ClientRequestId: Guid.NewGuid(),
            Context: new FeedbackSubmitContext(
                Source:        "cli",
                ClientVersion: CapacitorVersion.CurrentDisplay(),
                Os:            RuntimeInformation.OSDescription
            )
        );

        using var content = JsonContent.Create(request, CapacitorJsonContext.Default.FeedbackSubmitRequest);
        using var response = await CapacitorApiRequests.SendAsync(
            http, server, (c, token) => c.PostWithRetryAsync($"{server.Url}/api/feedback", content, ct: token), ct);

        if (response.StatusCode == HttpStatusCode.OK) {
            var success = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.FeedbackSubmitResponse, ct);

            return new FeedbackResult.Sent(success?.ReporterEmail ?? "");
        }

        // Every other outcome this command distinguishes; anything outside this set is unexpected and
        // throws below — before the body is read, since FailureAsync's 401 branch reads it itself.
        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.BadRequest)) {
            throw await CapacitorApiRequests.FailureAsync(response);
        }

        var body      = await response.Content.ReadAsStringAsync(ct);
        var errorCode = TryGetField(body, "error");

        return response.StatusCode switch {
            // A bare 404/405 (no JSON body — see the server's SupportEndpoints doc: a POST to an
            // unmapped route 405s via ASP.NET's own routing layer, a GET 404s via the Blazor
            // fallback route) means the whole feature is off. A CODED 404 (feedback_not_configured)
            // means the route exists but the sink isn't configured — same advice as the coded 503.
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed when errorCode is null => new FeedbackResult.NotConfigured(),
            HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable                      => new FeedbackResult.Unavailable(),
            HttpStatusCode.Conflict                                                           => new FeedbackResult.NoEmailOnFile(),
            HttpStatusCode.TooManyRequests                                                     => new FeedbackResult.RateLimited(),
            HttpStatusCode.BadGateway                                                          => new FeedbackResult.TemporarilyUnavailable(response.Headers.RetryAfter?.Delta),
            HttpStatusCode.BadRequest                                                          => new FeedbackResult.Invalid(TryGetField(body, "message") ?? "The feedback request was invalid."),
            _                                                                                   => throw new UnreachableException()
        };
    }

    /// <summary>Reads a named string field from a possibly-empty, possibly-non-JSON body. Absence
    /// (empty body, malformed JSON, or a missing/wrong-typed field) all read as <c>null</c> — the
    /// same defensive contract <see cref="JsonElementExtensions"/> uses everywhere else.</summary>
    static string? TryGetField(string body, string field) {
        if (string.IsNullOrEmpty(body)) return null;

        try {
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.Str(field);
        } catch (JsonException) {
            return null;
        }
    }
}
