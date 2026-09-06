using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportChainsTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // These tests exercise chaining and repo resolution, not profile selection.
    ImportCommand Import() =>
        new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient());
    readonly WireMockServer _server  = WireMockServer.Start();
    // TUnit creates a new class instance per test, so _tempDir is always unique.
    readonly TempDir _tmp     = new();
    readonly string  _tempDir;
    public ImportChainsTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    void StubAllHookEndpoints() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        // Match both the legacy un-vendored path and the new vendor-routed
        // path the importer uses now (/hooks/session-start/{vendor}) so codex
        // imports can't silently land as claude-tagged SessionStarted events.
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    ImportCommand.SessionClassification MakeNew(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new() {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "-tmp-proj",
            Meta       = new() { Cwd = "/tmp/proj" },
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    // No-git variant: both Meta.Cwd AND EncodedCwd must be empty so
    // ImportSingleSessionAsync resolves cwd to null and skips DetectRepositoryAsync.
    // A non-empty EncodedCwd would still decode to a valid cwd, triggering repo
    // detection and polluting parallelism-timing measurements.
    ImportCommand.SessionClassification MakeNewNoGit(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new() {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "",                      // DecodeCwdFromDirName returns null on empty input
            Meta       = new SessionMetadata(),   // Cwd = null
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    [Test]
    public async Task ImportChainsAsync_counts_loaded_sessions() {
        StubAllHookEndpoints();

        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("cnt-s1", 50) },
            new() { MakeNew("cnt-s2", 50) },
            new() { MakeNew("cnt-s3", 50) },
        };

        var completedLines = new ConcurrentBag<string>();
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => completedLines.Add($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        await Assert.That(result.Loaded).IsEqualTo(3);
        await Assert.That(result.Errored).IsEqualTo(0);
        await Assert.That(completedLines.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ImportChainsAsync_reports_per_session_progress() {
        // Regression: per-session slot rows always showed 0% because
        // BatchFlushed events were never wired to the slot bar. OnSessionProgress
        // must now fire once per flushed parent batch, carrying the batch size and
        // the session's full sendable-line total.
        StubAllHookEndpoints();

        // 250 non-blank lines → batches of 100 → flushes of 100, 100, 50.
        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("prog-s1", 250) },
        };

        var progressLines  = new ConcurrentBag<int>();
        var progressTotals = new ConcurrentBag<int>();

        var events = new ImportCommand.ChainWorkerEvents {
            // Opt into the sendable-line pre-count so OnSessionProgress carries a real total.
            TrackPerSessionProgress = true,
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, lines, total) => { progressLines.Add(lines); progressTotals.Add(total); },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Three batches were flushed and the line counts sum to the whole transcript.
        await Assert.That(progressLines.Count).IsEqualTo(3);
        await Assert.That(progressLines.Sum()).IsEqualTo(250);
        // Every report carries the same denominator: the session's sendable total.
        await Assert.That(progressTotals.All(t => t == 250)).IsTrue();
    }

    [Test]
    public async Task ImportChainsAsync_skips_line_precount_when_progress_disabled() {
        // Perf gate (PR #186 review): when no live bar consumes the denominator
        // (non-TTY, TrackPerSessionProgress defaults false), the per-session
        // pre-count is skipped — OnSessionProgress still fires per batch but with
        // total == 0, so we never read the whole transcript just to throw it away.
        StubAllHookEndpoints();

        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("noprecount-s1", 250) },
        };

        var progressTotals = new ConcurrentBag<int>();

        var events = new ImportCommand.ChainWorkerEvents {
            // TrackPerSessionProgress omitted → defaults false (the non-TTY default).
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, total) => progressTotals.Add(total),
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Batches still flowed, but the denominator was never computed.
        await Assert.That(progressTotals.Count).IsEqualTo(3);
        await Assert.That(progressTotals.All(t => t == 0)).IsTrue();
    }

    [Test]
    public async Task ImportChainsAsync_processes_within_chain_in_order() {
        StubAllHookEndpoints();

        // Single chain of 3 sessions. They must complete in order s1 → s2 → s3.
        var chain = new List<ImportCommand.SessionClassification> {
            MakeNew("ord-s1", 50), MakeNew("ord-s2", 50), MakeNew("ord-s3", 50),
        };
        var chains = new List<List<ImportCommand.SessionClassification>> { chain };

        var order = new ConcurrentQueue<string>();
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => order.Enqueue($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var lines = order.ToArray();
        await Assert.That(lines[0]).Contains("ord-s1");
        await Assert.That(lines[1]).Contains("ord-s2");
        await Assert.That(lines[2]).Contains("ord-s3");
    }

    [Test]
    public async Task ImportChainsAsync_routes_codex_session_to_vendor_specific_url() {
        // Regression: kcap import --codex used to POST to /hooks/session-start
        // (no vendor in path), which routes to the {vendor=claude} default and
        // wrote claude-tagged SessionStarted events for every codex rollout.
        // Asserting on the URL path here keeps that bug from re-appearing.
        StubAllHookEndpoints();

        var session = MakeNew("codex-routing", 10) with { Vendor = HarnessId.Codex };
        var chains  = new List<List<ImportCommand.SessionClassification>> { new() { session } };

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var startHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path == "/hooks/session-start/codex");
        var endHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path == "/hooks/session-end/codex");
        var legacyHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path is "/hooks/session-start" or "/hooks/session-end");

        await Assert.That(startHits).IsEqualTo(1);
        await Assert.That(endHits).IsEqualTo(1);
        await Assert.That(legacyHits).IsEqualTo(0);
    }

    [Test]
    public async Task ImportChainsAsync_dispatches_independent_chains_in_parallel() {
        // WireMock adds a 200ms server-side delay to every transcript POST.
        // If chains run serially: 4 × 200ms = 800ms minimum.
        // If chains run in parallel (4 workers): all 4 transcript POSTs arrive at the server
        // within a short window; the spread between first and last POST arrival should be
        // well under 200ms (they all start roughly simultaneously).
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromMilliseconds(200)));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // 4 independent chains, 1 session each.
        // MakeNewNoGit omits Meta.Cwd so DetectRepositoryAsync is never called —
        // git process startup would pollute the arrival-time spread.
        var chains = Enumerable.Range(0, 4)
            .Select(i => new List<ImportCommand.SessionClassification> { MakeNewNoGit($"par{i}", 50) })
            .ToList();

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Verify all 4 transcript POSTs arrived at the server.
        var transcriptEntries = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .OrderBy(e => e.RequestMessage.DateTime)
            .ToList();
        await Assert.That(transcriptEntries.Count).IsEqualTo(4);

        // If chains ran in parallel, all 4 transcript requests arrived close together.
        // The spread between first and last arrival should be under 160ms
        // (200ms serial gap × 0.8 margin — any overlap proves parallelism).
        var firstArrival = transcriptEntries.First().RequestMessage.DateTime;
        var lastArrival  = transcriptEntries.Last().RequestMessage.DateTime;
        var spreadMs     = (lastArrival - firstArrival).TotalMilliseconds;

        // If serial, spread ≥ 3 × 200ms = 600ms (each chain waits for the previous).
        // If parallel, spread < 100ms (all chains start simultaneously).
        await Assert.That(spreadMs).IsLessThan(160);
    }

    [Test]
    public async Task ImportChainsAsync_derives_workspace_root_from_the_remapped_cwd_not_the_raw_meta_cwd() {
        // Regression: workspace_root discovery used to run
        // GitRepository.FindRoot against the RAW transcript cwd (session.Meta.Cwd)
        // instead of the already remap-/worktree-resolved path the import flow
        // computes up front into sessionCwds. A historical transcript whose recorded
        // cwd no longer exists (moved/renamed repo) would silently omit
        // workspace_root even though the user had configured a cwd_remap that
        // resolves to a real git working tree.
        StubAllHookEndpoints();

        var resolvedRepoDir = Path.Combine(_tempDir, "resolved-repo");
        Directory.CreateDirectory(Path.Combine(resolvedRepoDir, ".git"));

        const string sid = "workspace-root-remap";
        var path = Path.Combine(_tempDir, $"{sid}.jsonl");
        File.WriteAllLines(path, new[] {
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-0"}}""",
        });

        var session = new ImportCommand.SessionClassification {
            SessionId  = sid,
            FilePath   = path,
            EncodedCwd = "-tmp-proj",
            // The raw recorded cwd no longer exists on disk / has no git repo — if this
            // were used directly, FindRoot would return null and workspace_root would
            // be omitted entirely.
            Meta       = new() { Cwd = "/definitely/does-not-exist/raw-cwd" },
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = 1,
        };
        var chains = new List<List<ImportCommand.SessionClassification>> { new() { session } };

        // Mirrors what ResolveTranscriptReposAsync populates: the remapped/worktree-resolved
        // cwd for this session, fed back to ImportSingleSessionAsync's workspace_root lookup.
        var sessionCwds = new Dictionary<string, string> { [sid] = resolvedRepoDir };

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None, sessionCwds);

        await Assert.That(result.Loaded).IsEqualTo(1);

        var startEntry = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/claude");
        var body       = JsonNode.Parse(startEntry.RequestMessage.Body!)!.AsObject();

        await Assert.That(body["workspace_root"]?.GetValue<string>()).IsEqualTo(resolvedRepoDir);
        await Assert.That(body["cwd"]?.GetValue<string>()).IsEqualTo(resolvedRepoDir);
    }
}
