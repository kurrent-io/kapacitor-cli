using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap feedback (--bug | --feedback) [-m|--message &lt;text&gt;]</c> — files a bug/feedback
/// report through the tenant's <c>POST /api/feedback</c>.
///
/// <para>Flag-validation and message-resolution tests drive
/// <see cref="FeedbackCommand.HandleAsync(string[], bool, Func{string?})"/> directly with an
/// injected TTY flag and line reader — no real stdin needed. Body-shape and response-mapping tests
/// drive <see cref="FeedbackCommand.HandleCore"/> with a <see cref="FeedbackApi"/> built against a
/// <see cref="FixedCapacitorHttpClient"/> pointed at a WireMock stub — no auth-discovery pipeline,
/// mirroring <c>ValidatePlanCommandTests</c>'s <c>HandleCore</c> seam.</para>
/// </summary>
public class FeedbackCommandTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    IFeedbackApi Api(string? url = null) =>
        new FeedbackApi(new FixedCapacitorHttpClient(), new CapacitorServer(url ?? _server.Urls[0], Config.Root, Resolutions.At(url ?? _server.Urls[0], Config.Root)));

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(Func<Task<int>> action) {
        using var capture = ConsoleOutput.StartFullCapture("\n");
        int exitCode;

        exitCode = await action();

        return (exitCode, capture.GetCapturedOutput(), capture.GetCapturedError());
    }

    // ── flag validation ────────────────────────────────────────────────────────────────────────

    [Test, NotInParallel]
    public async Task Neither_bug_nor_feedback_is_a_usage_error_naming_both_flags() {
        var (exitCode, _, stderr) = await RunAsync(() =>
            new FeedbackCommand(Api("http://unused.invalid")).HandleAsync(
                ["feedback", "-m", "hi"], true, () => null));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr).Contains("--bug");
        await Assert.That(stderr).Contains("--feedback");
    }

    [Test, NotInParallel]
    public async Task Both_bug_and_feedback_is_a_usage_error_naming_both_flags() {
        var (exitCode, _, stderr) = await RunAsync(() =>
            new FeedbackCommand(Api("http://unused.invalid")).HandleAsync(
                ["feedback", "--bug", "--feedback", "-m", "hi"], true, () => null));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr).Contains("--bug");
        await Assert.That(stderr).Contains("--feedback");
    }

    // ── message resolution ─────────────────────────────────────────────────────────────────────

    [Test, NotInParallel]
    public async Task Message_required_when_stdin_is_not_a_tty_and_dash_m_is_omitted() {
        var (exitCode, _, stderr) = await RunAsync(() =>
            new FeedbackCommand(Api("http://unused.invalid")).HandleAsync(
                ["feedback", "--bug"], stdinIsRedirected: true, readLine: () => null));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("A message is required.");
    }

    [Test, NotInParallel]
    public async Task Whitespace_only_message_is_rejected_after_trim() {
        var (exitCode, _, stderr) = await RunAsync(() =>
            new FeedbackCommand(Api("http://unused.invalid")).HandleAsync(
                ["feedback", "--bug", "-m", "   "], stdinIsRedirected: true, readLine: () => null));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("A message is required.");
    }

    [Test, NotInParallel]
    public async Task Interactive_prompt_collects_lines_until_empty_and_posts_the_joined_message() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"reporter_email":"someone@example.com"}"""));

        var lines = new Queue<string?>(["first line", "second line", ""]);

        var (exitCode, stdout, stderr) = await RunAsync(() =>
            new FeedbackCommand(Api()).HandleAsync(
                ["feedback", "--feedback"], stdinIsRedirected: false, readLine: () => lines.Dequeue()));

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stderr).Contains("What's going on? (end with an empty line)");
        await Assert.That(stdout.TrimEnd('\r', '\n')).IsEqualTo("✓ Sent to Kurrent support as someone@example.com — replies will reach you by email.");

        var hit = _server.LogEntries.Single(e => e.RequestMessage.Path == "/api/feedback");
        using var doc = JsonDocument.Parse(hit.RequestMessage.Body!);
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("first line\nsecond line");
        await Assert.That(doc.RootElement.GetProperty("category").GetString()).IsEqualTo("feedback");
    }

    [Test, NotInParallel]
    public async Task Dash_dash_message_is_accepted_as_an_alias_for_dash_m() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"reporter_email":"someone@example.com"}"""));

        var (exitCode, _, _) = await RunAsync(() =>
            new FeedbackCommand(Api()).HandleAsync(
                ["feedback", "--bug", "--message", "via long flag"], true, () => null));

        await Assert.That(exitCode).IsEqualTo(0);

        var hit = _server.LogEntries.Single(e => e.RequestMessage.Path == "/api/feedback");
        using var doc = JsonDocument.Parse(hit.RequestMessage.Body!);
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("via long flag");
        await Assert.That(doc.RootElement.GetProperty("category").GetString()).IsEqualTo("bug");
    }

    // ── body shape (via HandleCore — no auth path involved) ───────────────────────────────────

    [Test, NotInParallel]
    public async Task Body_carries_snake_case_keys_a_fresh_client_request_id_guid_and_cli_context() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"reporter_email":"someone@example.com"}"""));

        var exitCode = await FeedbackCommand.HandleCore(Api(), "bug", "the daemon crashed");

        await Assert.That(exitCode).IsEqualTo(0);

        var hit = _server.LogEntries.Single(e => e.RequestMessage.Path == "/api/feedback");
        using var doc = JsonDocument.Parse(hit.RequestMessage.Body!);
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("category").GetString()).IsEqualTo("bug");
        await Assert.That(root.GetProperty("message").GetString()).IsEqualTo("the daemon crashed");

        // client_request_id must be present, snake_case, and a real (non-empty, parseable) GUID —
        // it is the server's idempotency key, so a malformed or absent value would break dedup.
        var rawId = root.GetProperty("client_request_id").GetString();
        await Assert.That(Guid.TryParse(rawId, out var parsed)).IsTrue();
        await Assert.That(parsed).IsNotEqualTo(Guid.Empty);

        var context = root.GetProperty("context");
        await Assert.That(context.GetProperty("source").GetString()).IsEqualTo("cli");
        await Assert.That(context.GetProperty("client_version").GetString()).IsEqualTo(CapacitorVersion.CurrentDisplay());
        await Assert.That(string.IsNullOrEmpty(context.GetProperty("os").GetString())).IsFalse();

        // No camelCase leakage — the server binds snake_case only (Task 10's global JSON policy).
        await Assert.That(root.TryGetProperty("clientRequestId", out _)).IsFalse();
    }

    [Test, NotInParallel]
    public async Task Two_submissions_mint_two_distinct_client_request_ids() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"reporter_email":"someone@example.com"}"""));

        await FeedbackCommand.HandleCore(Api(), "bug", "first");
        await FeedbackCommand.HandleCore(Api(), "bug", "second");

        var hits = _server.LogEntries.Where(e => e.RequestMessage.Path == "/api/feedback").ToList();
        await Assert.That(hits.Count).IsEqualTo(2);

        var ids = hits.Select(h => {
            using var doc = JsonDocument.Parse(h.RequestMessage.Body!);
            return doc.RootElement.GetProperty("client_request_id").GetString();
        }).ToList();

        await Assert.That(ids[0]).IsNotEqualTo(ids[1]);
    }

    // ── response mapping (via HandleCore) ─────────────────────────────────────────────────────

    [Test, NotInParallel]
    public async Task Ok_prints_the_pinned_success_line_with_the_reporter_email() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"reporter_email":"alice@example.com"}"""));

        var (exitCode, stdout, _) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsEqualTo(0);
        // Byte-exact: the pinned string, including the checkmark glyph, with the reply promise.
        await Assert.That(stdout.TrimEnd('\r', '\n')).IsEqualTo("✓ Sent to Kurrent support as alice@example.com — replies will reach you by email.");
    }

    [Test, NotInParallel]
    public async Task Bare_404_with_no_body_reports_support_intake_not_enabled() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("This server doesn't have support intake enabled.");
    }

    [Test, NotInParallel]
    public async Task Bare_405_with_no_body_reports_support_intake_not_enabled() {
        // The real flag-off shape (see FeedbackCommand's doc comment): ASP.NET's routing layer
        // itself answers 405 for a POST to an unmapped route, carrying no JSON body at all.
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(405));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("This server doesn't have support intake enabled.");
    }

    [Test, NotInParallel]
    public async Task Coded_404_feedback_not_configured_reports_ask_your_admin() {
        // Same status code as the bare case above, but WITH a JSON error body — the route exists
        // (Features:Feedback is on) and the sink itself isn't configured. Must NOT be confused with
        // the bare 404 "feature is off" message.
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_not_configured","message":"Feedback submission is not configured for this server."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("Support intake isn't configured on this server — ask your admin.");
    }

    [Test, NotInParallel]
    public async Task Coded_503_feedback_misconfigured_reports_ask_your_admin() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_misconfigured","message":"Feedback submission is currently misconfigured on this server."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("Support intake isn't configured on this server — ask your admin.");
    }

    [Test, NotInParallel]
    public async Task Conflict_feedback_no_email_reports_sign_in_to_the_web_app() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_no_email","message":"We don't have an email address on file for your account."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo(
            "Your account has no email on file — sign in to the web app once, then retry.");
    }

    [Test, NotInParallel]
    public async Task TooManyRequests_reports_try_again_in_a_few_minutes() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_rate_limited","message":"Too many feedback submissions — please try again later."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("You've sent several reports recently — try again in a few minutes.");
    }

    [Test, NotInParallel]
    public async Task BadGateway_with_retry_after_reports_the_seconds() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502)
                .WithHeader("Retry-After", "30")
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_sink_error","message":"Feedback submission is temporarily unavailable — please try again shortly."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("Couldn't reach Kurrent support (temporary) — try again in 30s.");
    }

    [Test, NotInParallel]
    public async Task BadGateway_without_retry_after_reports_the_bare_sentence() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_sink_error","message":"Feedback submission is temporarily unavailable — please try again shortly."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("Couldn't reach Kurrent support (temporary) — try again.");
    }

    [Test, NotInParallel]
    public async Task BadRequest_echoes_the_servers_message_field_verbatim() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"feedback_invalid","message":"a custom validation message from the server"}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("a custom validation message from the server");
    }

    [Test, NotInParallel]
    public async Task Unauthorized_prints_the_servers_message_and_reuses_the_standard_handler() {
        // A 401 falls through to the shared CapacitorApiException path — this command must not
        // invent its own 401 handling.
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Your session has expired. Run 'kcap login' to re-authenticate."}"""));

        var (exitCode, _, stderr) = await RunAsync(() =>
            FeedbackCommand.HandleCore(Api(), "bug", "hi"));

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(stderr.Trim()).IsEqualTo("Your session has expired. Run 'kcap login' to re-authenticate.");
    }
}
