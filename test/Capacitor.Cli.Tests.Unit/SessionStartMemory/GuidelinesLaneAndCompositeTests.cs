using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>
/// Cross-vendor guideline injection — the guidelines lane, the composite provider's disposition/compose
/// rules, and the SessionGuidelinesEmitter row overload. The memory lane and the
/// orchestrator/lease/policy are covered by SessionStartMemoryFoundationTests.
/// </summary>
public class GuidelinesLaneAndCompositeTests {
    /// The lanes resolve their client inside the fetch, so a test hands them one already built.
    static Func<CancellationToken, Task<HttpClient>> Lazy(HttpClient client) => _ => Task.FromResult(client);

    const string Marker = "<!-- kcap-memory-index:v1 -->";

    const string MemoryBody =
        "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]";
    const string GuidelinesBody =
        "{\"repo_hash\":\"repo\",\"guidelines\":[{\"category\":\"safety\",\"text\":\"t1\"},{\"category\":\"agent_guidance\",\"text\":\"t2\"}]}";

    static SessionStartMemoryContextRequest Req(bool disabled = false, bool guidelinesDisabled = false) =>
        new("https://example.test", "/repo", disabled, TimeSpan.FromSeconds(1), CancellationToken.None,
            GuidelinesDisabled: guidelinesDisabled);

    static SessionStartGuidelinesLane Lane(HttpStatusCode status, string body, TimeSpan? retryAfter = null) =>
        new(Lazy(new HttpClient(new Handler(status, body, retryAfter))));

    // ---- Guidelines lane ----

    [Test]
    public async Task Rows_render_to_known_patterns_and_guidance_marker_less() {
        var result = await Lane(HttpStatusCode.OK, GuidelinesBody)
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!).Contains("## Known patterns");
        await Assert.That(result.Fragment!).Contains("- t1");
        await Assert.That(result.Fragment!).Contains("## Guidance from past sessions");
        await Assert.That(result.Fragment!).Contains("- t2");
        // The lane is marker-less; the composite owns marker placement.
        await Assert.That(result.Fragment!).DoesNotContain(Marker);
    }

    [Test]
    public async Task NotFound_maps_to_retryable_not_empty() {
        // The visibility-race rule: 404 means "not visible yet", not "no facts".
        var result = await Lane(HttpStatusCode.NotFound, "")
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task NoContent_and_BadRequest_map_to_empty() {
        var noContent = await Lane(HttpStatusCode.NoContent, "")
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);
        var badRequest = await Lane(HttpStatusCode.BadRequest, "")
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);

        await Assert.That(noContent.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        await Assert.That(badRequest.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    public async Task ServerError_maps_to_retryable() {
        var result = await Lane(HttpStatusCode.InternalServerError, "")
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task Empty_rows_map_to_empty() {
        var result = await Lane(HttpStatusCode.OK, "{\"repo_hash\":\"repo\",\"guidelines\":[]}")
            .FetchWithScopeAsync(new SessionStartMemoryScope("repo", null), Req(), CancellationToken.None);
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    public async Task Null_repo_scope_skips_fetch() {
        var handler = new Handler(HttpStatusCode.OK, GuidelinesBody);
        var lane    = new SessionStartGuidelinesLane(Lazy(new HttpClient(handler)));

        var result = await lane.FetchWithScopeAsync(
            new SessionStartMemoryScope(null, "machine"), Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Builds_repo_scoped_url() {
        var handler = new Handler(HttpStatusCode.NoContent, "");
        var lane    = new SessionStartGuidelinesLane(Lazy(new HttpClient(handler)));

        await lane.FetchWithScopeAsync(new SessionStartMemoryScope("deadbeef01", null), Req(), CancellationToken.None);

        await Assert.That(handler.Uri).IsEqualTo("https://example.test/api/repositories/deadbeef01/guidelines");
    }

    // ---- Composite ----

    static SessionStartCompositeContextProvider Composite(
            (HttpStatusCode status, string body, TimeSpan? retryAfter) memory,
            (HttpStatusCode status, string body, TimeSpan? retryAfter) guidelines,
            Handler? memoryHandler = null, Handler? guidelinesHandler = null) {
        var scope    = new FixedScope("repo", "machine");
        var memH     = memoryHandler ?? new Handler(memory.status, memory.body, memory.retryAfter);
        var guideH   = guidelinesHandler ?? new Handler(guidelines.status, guidelines.body, guidelines.retryAfter);
        var memory2  = new SessionStartMemoryContextProvider(scope, Lazy(new HttpClient(memH)));
        var guide2   = new SessionStartGuidelinesLane(Lazy(new HttpClient(guideH)));
        return new SessionStartCompositeContextProvider(scope, memory2, guide2);
    }

    [Test]
    public async Task Both_content_compose_marker_first_then_memory_then_guidelines() {
        var result = await Composite((HttpStatusCode.OK, MemoryBody, null), (HttpStatusCode.OK, GuidelinesBody, null))
            .GetAsync(Req());

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!.StartsWith(Marker, StringComparison.Ordinal)).IsTrue();
        var teamIdx     = result.Fragment!.IndexOf("## Team memory", StringComparison.Ordinal);
        var patternsIdx = result.Fragment!.IndexOf("## Known patterns", StringComparison.Ordinal);
        await Assert.That(teamIdx).IsGreaterThan(-1);
        await Assert.That(patternsIdx).IsGreaterThan(teamIdx);
    }

    [Test]
    public async Task Guidelines_only_prepends_marker() {
        var result = await Composite((HttpStatusCode.NoContent, "", null), (HttpStatusCode.OK, GuidelinesBody, null))
            .GetAsync(Req());

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!.StartsWith(Marker, StringComparison.Ordinal)).IsTrue();
        await Assert.That(result.Fragment!).Contains("## Known patterns");
        await Assert.That(result.Fragment!).DoesNotContain("## Team memory");
    }

    [Test]
    public async Task Both_empty_completes_without_context() {
        var result = await Composite((HttpStatusCode.NoContent, "", null), (HttpStatusCode.NoContent, "", null))
            .GetAsync(Req());
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    public async Task Partial_success_commits_memory_when_guidelines_retryably_fail() {
        var result = await Composite((HttpStatusCode.OK, MemoryBody, null), (HttpStatusCode.InternalServerError, "", null))
            .GetAsync(Req());

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!).Contains("## Team memory");
        await Assert.That(result.Fragment!).DoesNotContain("## Known patterns");
    }

    [Test]
    public async Task Both_retryable_returns_retryable() {
        var result = await Composite((HttpStatusCode.InternalServerError, "", null), (HttpStatusCode.NotFound, "", null))
            .GetAsync(Req());
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task RetryAfter_is_the_max_of_lane_hints() {
        var result = await Composite(
            (HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(10)),
            (HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(30))).GetAsync(Req());

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
        await Assert.That(result.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Disabled_memory_lane_runs_guidelines_only_and_never_calls_memory() {
        var memoryHandler = new Handler(HttpStatusCode.OK, MemoryBody);
        var result = await Composite(
            (HttpStatusCode.OK, MemoryBody, null), (HttpStatusCode.OK, GuidelinesBody, null),
            memoryHandler: memoryHandler).GetAsync(Req(disabled: true, guidelinesDisabled: false));

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!).DoesNotContain("## Team memory");
        await Assert.That(result.Fragment!).Contains("## Known patterns");
        await Assert.That(memoryHandler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Disabled_guidelines_lane_runs_memory_only_and_never_calls_guidelines() {
        var guidelinesHandler = new Handler(HttpStatusCode.OK, GuidelinesBody);
        var result = await Composite(
            (HttpStatusCode.OK, MemoryBody, null), (HttpStatusCode.OK, GuidelinesBody, null),
            guidelinesHandler: guidelinesHandler).GetAsync(Req(disabled: false, guidelinesDisabled: true));

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!).Contains("## Team memory");
        await Assert.That(result.Fragment!).DoesNotContain("## Known patterns");
        await Assert.That(guidelinesHandler.Calls).IsEqualTo(0);
    }

    // ---- Emitter row overload ----

    [Test]
    public async Task Emitter_groups_rows_by_category() {
        var fragment = SessionGuidelinesEmitter.BuildFragment(new List<GuidelineRow> {
            new("safety", "a"),
            new("agent_guidance", "b"),
            new(null, "c"),
            new("quality", "   ")  // whitespace text dropped
        });

        await Assert.That(fragment!).Contains("## Known patterns");
        await Assert.That(fragment!).Contains("- a");
        await Assert.That(fragment!).Contains("- c");           // null category → patterns
        await Assert.That(fragment!).Contains("## Guidance from past sessions");
        await Assert.That(fragment!).Contains("- b");
        await Assert.That(fragment!).DoesNotContain("-    ");    // whitespace row skipped
    }

    [Test]
    public async Task Emitter_null_or_empty_rows_returns_null() {
        await Assert.That(SessionGuidelinesEmitter.BuildFragment((IReadOnlyList<GuidelineRow>?)null)).IsNull();
        await Assert.That(SessionGuidelinesEmitter.BuildFragment(new List<GuidelineRow>())).IsNull();
        await Assert.That(SessionGuidelinesEmitter.BuildFragment(new List<GuidelineRow> { new("safety", " ") })).IsNull();
    }

    // ---- Stubs ----

    sealed class FixedScope(string? repo, string? machine) : ISessionStartMemoryScopeResolver {
        public Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(new SessionStartMemoryScope(repo, machine));
    }

    sealed class Handler(HttpStatusCode status, string body, TimeSpan? retryAfter = null) : HttpMessageHandler {
        public int Calls;
        public string? Uri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Calls++;
            Uri = request.RequestUri?.ToString();
            var response = new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            return Task.FromResult(response);
        }
    }
}
