using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Task 18: locks in the daemon's V2 wire-format migration. After
/// migrating <see cref="EvalService.Aggregate"/> to return
/// <see cref="SessionEvalCompletedPayloadV2"/>, the persistence step must
/// POST to <c>/api/sessions/{id}/evals/v2</c> (not the legacy V1 route)
/// and the body must carry structured suggestions with both
/// <c>text</c> and <c>audience</c> fields.
///
/// <para>
/// Drives <see cref="EvalService.PersistAggregateV2Async"/> directly — the
/// minimal seam extracted from <see cref="EvalService.FinalizeAsync"/> for
/// testing. Going through FinalizeAsync would require shelling out to the
/// claude CLI for the retrospective synthesis step, which isn't available
/// in CI; the persistence step is the load-bearing wire contract here.
/// </para>
/// </summary>
public class EvalRunnerV2PostTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task Daemon_persists_aggregate_to_v2_route_with_structured_suggestions() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v2").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));

        // V1 route still wired but should NOT be hit. Respond 410 so any
        // accidental fallback would surface as a HTTP failure, not a silent
        // success.
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(410));

        var aggregate = new SessionEvalCompletedPayloadV2 {
            EvalRunId    = "run-1",
            JudgeModel   = "claude-sonnet-4-6",
            OverallScore = 4,
            Summary      = "ok",
            Categories   = [],
            Retrospective = new EvalRetrospectiveV2 {
                OverallSummary = "fine",
                Suggestions = [
                    new RetrospectiveSuggestion { Text = "Run tests before commit",  Audience = "agent" },
                    new RetrospectiveSuggestion { Text = "Discuss design with team", Audience = "human" }
                ]
            }
        };

        var observer = new RecordingObserver();
        using var httpClient = new HttpClient();

        var ok = await EvalService.PersistAggregateV2Async(
            httpClient:       httpClient,
            baseUrl:          _server.Url!,
            encodedSessionId: "sess-1",
            aggregate:        aggregate,
            observer:         observer,
            ct:               CancellationToken.None
        );

        await Assert.That(ok).IsTrue();

        var v2Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v2").UsingPost());
        var v1Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals").UsingPost());

        await Assert.That(v2Hits.Count).IsEqualTo(1);
        await Assert.That(v1Hits.Count).IsEqualTo(0);

        var body = v2Hits[0].RequestMessage.Body!;

        // V2 wire shape: snake_case top-level fields, structured suggestions
        // with text + audience keys.
        await Assert.That(body).Contains(@"""eval_run_id"":""run-1""");
        await Assert.That(body).Contains(@"""judge_model"":""claude-sonnet-4-6""");
        await Assert.That(body).Contains(@"""suggestions""");
        await Assert.That(body).Contains(@"""audience""");
        await Assert.That(body).Contains(@"""agent""");
        await Assert.That(body).Contains(@"""human""");
    }

    sealed class RecordingObserver : IEvalObserver {
        public void OnInfo(string message) { }
        public void OnStarted(string runId, string judgeModel, int totalQuestions) { }
        public void OnContextFetched(int e, int c, int t, int tr, long b) { }
        public void OnQuestionStarted(int i, int t, string c, string q) { }
        public void OnQuestionCompleted(int i, int t, EvalQuestionVerdict v, long it, long ot) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV3 a) { }
        public void OnFailed(string r) { }
    }
}
