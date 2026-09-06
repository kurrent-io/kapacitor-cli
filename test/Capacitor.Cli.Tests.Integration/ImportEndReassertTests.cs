using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Task A2.1: the Partial-resume branch must post exactly one
/// <c>session-end/{vendor}</c> AFTER a successful fail-closed transcript tail — end-only
/// reassert, since SessionStarted uses random ids (re-asserting start would duplicate it)
/// while session-end has server-side idempotency — and must NEVER finalize when the tail
/// send is rejected (fail-closed: a resumed-but-not-fully-sent session must not be marked
/// ended with a hole). Drives the real file-based path: <see cref="TranscriptFileClassification"/>
/// probes <c>/api/sessions/*/last-line</c> to classify the transcript Partial, then
/// <see cref="ImportCommand.ImportChainsAsync"/> runs the actual Partial branch of
/// <c>ImportSingleSessionAsync</c> — the same internal entry point
/// <c>ImportChainsTests</c> (Tests.Unit) drives for the New branch.
/// </summary>
public class ImportEndReassertTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // These tests exercise chaining and repo resolution, not profile selection.
    ImportCommand Import() =>
        new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient());
    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public ImportEndReassertTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    // 3-line Claude-shape transcript. Root-level "timestamp" matches what both
    // ExtractSessionMetadata and ExtractLastTimestamp scan for.
    static string WriteTranscript(string dir, string sessionId) {
        var path = Path.Combine(dir, $"{sessionId}.jsonl");

        File.WriteAllLines(
            path,
            [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-0"}}""",
                """{"type":"assistant","timestamp":"2026-03-15T10:00:01Z","cwd":"/tmp/proj","message":{"content":"line-1"}}""",
                """{"type":"user","timestamp":"2026-03-15T10:00:02Z","cwd":"/tmp/proj","message":{"content":"line-2"}}""",
            ]
        );

        return path;
    }

    static ImportCommand.ChainWorkerEvents NoOpEvents() => new() {
        OnSessionStarted      = (_, _) => { },
        OnSubagentStarted     = (_, _, _) => { },
        OnSubagentFinished    = (_, _, _, _) => { },
        OnSessionProgress     = (_, _, _) => { },
        OnSessionErrored      = (_, _, _) => { },
        OnSessionEnded        = (_, _, _, _) => { },
        OnTitleTaskReady      = _ => { },
        OnBackgroundWorkReady = _ => { },
    };

    // Real classification: probes the stubbed /api/sessions/*/last-line endpoint via
    // TranscriptFileClassification.ClassifyAsync (the same classifier ClaudeImportSource /
    // CodexImportSource use), rather than hand-constructing a Partial SessionClassification.
    async Task<ImportCommand.SessionClassification> ClassifyPartialAsync(HttpClient client, string sessionId) {
        var path = WriteTranscript(_tempDir, sessionId);

        var transcripts = new List<(string SessionId, string FilePath, string EncodedCwd)> {
            (sessionId, path, "-tmp-proj"),
        };

        var classified = await TranscriptFileClassification.ClassifyAsync(
            Config.Root,
            Home,
            client,
            _server.Url!,
            transcripts,
            minLines: 0,
            excludedRepos: null,
            CancellationToken.None
        );

        return classified[0];
    }

    [Test]
    public async Task Partial_resume_posts_session_end_after_fail_closed_tail_and_no_start() {
        // Stub: last-line probe returns a partial count => classification Partial
        // (last_line_number=1 → ResumeFromLine=2, so the 3rd transcript line is resent).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { generate_whats_done = false }));

        using var client = new HttpClient();

        var session = await ClassifyPartialAsync(client, "resume-ok");
        await Assert.That(session.Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

        var chains = new List<List<ImportCommand.SessionClassification>> { new() { session } };
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, NoOpEvents(), CancellationToken.None);

        await Assert.That(result.Resumed).IsEqualTo(1);
        await Assert.That(result.Errored).IsEqualTo(0);

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path!)
            .ToArray();

        await Assert.That(posts.Count(p => p.StartsWith("/hooks/session-start", StringComparison.Ordinal))).IsEqualTo(0);
        await Assert.That(posts.Count(p => p.StartsWith("/hooks/session-end", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(posts.Last()).StartsWith("/hooks/session-end");
    }

    [Test]
    public async Task Partial_does_not_finalize_when_tail_send_is_rejected() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500)); // tail rejected
        _server.Given(Request.Create().WithPath("/hooks/session-end/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        var session = await ClassifyPartialAsync(client, "resume-rejected");
        await Assert.That(session.Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

        var chains = new List<List<ImportCommand.SessionClassification>> { new() { session } };
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, NoOpEvents(), CancellationToken.None);

        await Assert.That(result.Errored).IsEqualTo(1);
        await Assert.That(result.Resumed).IsEqualTo(0);

        var endPosts = _server.LogEntries.Count(e => e.RequestMessage.Path!.StartsWith("/hooks/session-end", StringComparison.Ordinal));
        await Assert.That(endPosts).IsEqualTo(0); // never finalize on a gap
    }
}
