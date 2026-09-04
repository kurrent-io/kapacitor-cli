namespace Capacitor.Cli.Core.Http;

/// <summary>
/// What filing a feedback/bug report can mean — one case per refusal shape the tenant's support
/// intake distinguishes (see <c>SupportEndpoints</c>). Any status outside this set is unexpected and
/// throws instead, per the shared API convention.
/// </summary>
public abstract record FeedbackResult {
    public sealed record Sent(string ReporterEmail) : FeedbackResult;

    /// <summary>A bare 404/405 with no body — the whole feature is off on this server.</summary>
    public sealed record NotConfigured : FeedbackResult;

    /// <summary>A coded 404/503 — the route exists but the sink isn't configured.</summary>
    public sealed record Unavailable : FeedbackResult;

    /// <summary>409 — the reporter's account carries no email the server can reply to.</summary>
    public sealed record NoEmailOnFile : FeedbackResult;

    /// <summary>429 — too many submissions recently.</summary>
    public sealed record RateLimited : FeedbackResult;

    /// <summary>502 — the sink is temporarily unreachable; carries the server's own retry hint.</summary>
    public sealed record TemporarilyUnavailable(TimeSpan? RetryAfter) : FeedbackResult;

    /// <summary>400 — the request itself was invalid, with the server's own explanation.</summary>
    public sealed record Invalid(string Message) : FeedbackResult;
}
