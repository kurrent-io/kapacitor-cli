using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Pi;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;
using Capacitor.Cli.Tests.Unit.Harness.OpenCode;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Topology-specific coverage for the <c>default_visibility</c> stamp on historical import and
/// the <c>autoSkipExclusions</c> non-interactive guarantee.
///
/// <para>Pooled: the orchestrator-level rows run a real <c>HandleImport</c>, whose repo resolution
/// spawns git, and at TUnit's default width those hold slots long enough to starve this assembly's
/// timing-sensitive tests.</para>
///
/// <para>The rule the rows below pin, one for all nine sources
/// (<c>ImportContext.VisibilityStampFor</c>): <c>ForcePrivate</c> stamps <c>private</c> on every
/// status, and the Step-3 default lands on <c>New</c> alone. Reasoning in
/// docs/superpowers/specs/2026-08-26-ai2222-private-stamp-design.md.</para>
/// Driven through each source's real <c>ImportSessionAsync</c> / <see cref="ImportCommand.ImportChainsAsync"/>
/// / <see cref="ImportCommand.HandleImport"/> entry point — never through the private
/// per-source <c>BuildSessionStartPayload</c> builders in isolation — per the spec's own
/// testing note ("driven through the source/orchestrator entry point, not builders in
/// isolation").
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportVisibilityTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // These tests exercise chaining and repo resolution, not profile selection.
    ImportCommand Import() =>
        new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient());
    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _tempDir;

    public ImportVisibilityTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    void StubAllHookEndpoints() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    JsonObject SessionStartBody(string vendor) {
        var entry = _server.LogEntries.Single(e => e.RequestMessage.Path == $"/hooks/session-start/{vendor}");
        return JsonNode.Parse(entry.RequestMessage.Body!)!.AsObject();
    }

    string WriteTranscript(string name, int lines = 5) {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$"""{"role":"user","content":"line-{{i}}"}"""
        ));
        return path;
    }

    // =====================================================================
    // Section A — chain path (ImportCommand.ImportChainsAsync), direct.
    // =====================================================================

    ImportCommand.SessionClassification MakeChainSession(
            string                              id,
            ImportCommand.ClassificationStatus  status,
            int                                 resumeFromLine = 0,
            int                                 lines          = 5
        ) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new() {
            SessionId      = id,
            FilePath       = path,
            EncodedCwd     = "-tmp-proj",
            Meta           = new() { Cwd = "/tmp/proj" },
            Status         = status,
            ResumeFromLine = resumeFromLine,
            TotalLines     = lines,
        };
    }

    static ImportCommand.ChainWorkerEvents NoOpChainEvents() => new() {
        OnSessionStarted      = (_, _) => { },
        OnSubagentStarted     = (_, _, _) => { },
        OnSubagentFinished    = (_, _, _, _) => { },
        OnSessionProgress     = (_, _, _) => { },
        OnSessionErrored      = (_, _, _) => { },
        OnSessionEnded        = (_, _, _, _) => { },
        OnTitleTaskReady      = _ => { },
        OnBackgroundWorkReady = _ => { },
    };

    [Test]
    public async Task ImportChainsAsync_new_session_stamps_provided_default_visibility() {
        StubAllHookEndpoints();
        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeChainSession("vis-chain-new-1", ImportCommand.ClassificationStatus.New) },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(
            client, _server.Url!, chains, NoOpChainEvents(), CancellationToken.None,
            sessionCwds: null, defaultVisibility: "org_public");

        var body = SessionStartBody("claude");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task ImportChainsAsync_new_session_omits_default_visibility_when_null() {
        StubAllHookEndpoints();
        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeChainSession("vis-chain-new-2", ImportCommand.ClassificationStatus.New) },
        };

        using var client = new HttpClient();
        // defaultVisibility omitted -> defaults null.
        await Import().ImportChainsAsync(client, _server.Url!, chains, NoOpChainEvents(), CancellationToken.None);

        var body = SessionStartBody("claude");
        await Assert.That(body.ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task ImportChainsAsync_partial_session_never_posts_session_start() {
        StubAllHookEndpoints();
        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeChainSession("vis-chain-partial-1", ImportCommand.ClassificationStatus.Partial, resumeFromLine: 2) },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(
            client, _server.Url!, chains, NoOpChainEvents(), CancellationToken.None,
            sessionCwds: null, defaultVisibility: "org_public");

        var startHits = _server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/session-start/claude");
        await Assert.That(startHits).IsEqualTo(0);
    }

    // =====================================================================
    // Section B — orchestrator-level (ImportCommand.HandleImport): the
    // chainDefaultVisibility = forcePrivate ? "private" : defaultVisibility
    // resolution made in HandleImport itself, not just forwarded by
    // ImportChainsAsync.
    // =====================================================================

    static string WriteClaudeSession(string projectsDir, string sessionId, int lines = 20) {
        var cwdDir = Path.Combine(projectsDir, "-tmp-vis-proj");
        Directory.CreateDirectory(cwdDir);
        var path = Path.Combine(cwdDir, $"{sessionId}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/vis-proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return path;
    }

    [Test]
    public async Task HandleImport_chain_new_session_stamps_default_visibility_when_not_forced_private() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();

        var projectsDir = Path.Combine(_tempDir, "claude-projects-pos");
        WriteClaudeSession(projectsDir, "vis-chain-handle-pos");

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: false,
            defaultVisibility: "org_public"
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var body = SessionStartBody("claude");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task HandleImport_chain_forcePrivate_stamps_private_even_when_transcript_batch_fails_after_session_start() {
        // The scenario the stamp exists for: forcePrivate:true with a non-null defaultVisibility,
        // failing AFTER session-start succeeds (transcript batch 500s) and so before session-end /
        // importedSessionIds — which is what SetVisibilityNoneForAll reads, so this session is
        // never privatized post-hoc. An omitted field would leave it org-visible for good.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500)); // fails AFTER session-start succeeded
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-neg");
        WriteClaudeSession(projectsDir, "vis-chain-handle-neg");

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true,
            defaultVisibility: "org_public"
        );

        // Import is best-effort: HandleImport returns 0 even though this session's own
        // import failed mid-stream (see the Done-grid accounting) — matches the existing
        // CursorPrivatizeLifecycleFailureTests convention for a similar mid-run failure.
        await Assert.That(exitCode).IsEqualTo(0);

        var body = SessionStartBody("claude");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task HandleImport_chain_forcePrivate_privatizes_a_resume_whose_session_end_fails() {
        // A resume posts no session-start, so the stamp cannot reach it, and an errored one never
        // joins importedSessionIds — leaving the closing pass as the only route and nothing to put
        // it on that route.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-resume");
        WriteClaudeSession(projectsDir, "vis-chain-resume-fail");

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true,
            defaultVisibility: "org_public"
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var privatized = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT"
                     && e.RequestMessage.Path == "/api/sessions/vis-chain-resume-fail/visibility")
            .ToList();

        await Assert.That(privatized.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(JsonNode.Parse(privatized[0].RequestMessage.Body!)!["visibility"]?.GetValue<string>())
            .IsEqualTo("none");
    }

    [Test]
    public async Task HandleImport_forcePrivate_leaves_alone_a_session_the_run_never_sent() {
        // The in-scope capture is bounded by classification status, which is what keeps it to sessions
        // the server actually has — a too-short session was never uploaded, and the same gate is what
        // keeps an excluded source out.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-short-private");
        WriteClaudeSession(projectsDir, "vis-private-too-short", lines: 3);

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 500,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(_server.LogEntries.Where(e => e.RequestMessage.Method == "PUT")).IsEmpty();
    }

    [Test]
    public async Task HandleImport_forcePrivate_privatizes_an_existing_session_before_uploading_into_it() {
        // The window itself, not just its eventual closure: content replayed into a session the user
        // asked to keep private must not be publishable while it uploads. Ordering is the assertion —
        // a closing-pass-only implementation passes every count and still leaks for the run's duration.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-window");
        WriteClaudeSession(projectsDir, "vis-window");

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        var ordered = _server.LogEntries
            .OrderBy(e => e.RequestMessage.DateTime)
            .Select(e => e.RequestMessage.Method == "PUT" ? "privatize" : e.RequestMessage.Path)
            .ToList();

        var firstPrivatize = ordered.IndexOf("privatize");
        var firstTranscript = ordered.IndexOf("/hooks/transcript");

        await Assert.That(firstPrivatize).IsGreaterThanOrEqualTo(0)
                    .Because("a revisited session has to be narrowed by a write, not by a stamp");
        await Assert.That(firstTranscript).IsGreaterThanOrEqualTo(0)
                    .Because("otherwise nothing was uploaded and the ordering proves nothing");
        await Assert.That(firstPrivatize).IsLessThan(firstTranscript);
    }

    [Test]
    public async Task HandleImport_forcePrivate_does_not_pre_privatize_a_session_that_does_not_exist_yet() {
        // A New session has nothing to narrow, and naming it would PUT at a session id the server has
        // never seen. Its creation stamp is the mechanism that works.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-newonly");
        WriteClaudeSession(projectsDir, "vis-new-only");

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        var ordered = _server.LogEntries
            .OrderBy(e => e.RequestMessage.DateTime)
            .Select(e => e.RequestMessage.Method == "PUT" ? "privatize" : e.RequestMessage.Path)
            .ToList();

        // The closing pass still runs; what must not happen is a write before the session exists.
        await Assert.That(ordered.IndexOf("privatize"))
                    .IsGreaterThan(ordered.IndexOf("/hooks/transcript"));
    }

    [Test]
    public async Task HandleImport_shareWithOrg_writes_an_explicit_org_visibility() {
        // The whole reason the shared stop can be offered honestly: the profile default produces
        // `default:org`, which is admitted only where the repo owner matches the configured org.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-shared");
        WriteClaudeSession(projectsDir, "vis-chain-shared");

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: false,
            shareWithOrg: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var put = _server.LogEntries.Single(e =>
            e.RequestMessage.Method == "PUT"
         && e.RequestMessage.Path == "/api/sessions/vis-chain-shared/visibility");

        await Assert.That(JsonNode.Parse(put.RequestMessage.Body!)!["visibility"]?.GetValue<string>())
                    .IsEqualTo("org");
    }

    [Test]
    public async Task HandleImport_writes_no_explicit_visibility_when_neither_stop_was_asked_for() {
        // A plain `kcap import` leaves each session on whatever its default_visibility resolved to.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-plain");
        WriteClaudeSession(projectsDir, "vis-chain-plain");

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true
        );

        await Assert.That(_server.LogEntries.Where(e => e.RequestMessage.Method == "PUT")).IsEmpty();
    }

    [Test]
    public async Task HandleImport_shareWithOrg_reaches_a_session_this_run_only_revisited() {
        // The re-run case, and the one the screen offers: a session already fully loaded is counted
        // in discovery and selectable as Shared, but does nothing new, so importedSessionIds never
        // gains it. Without the scoped capture it stays owner-only under a green summary.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":19}"""));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-already");
        WriteClaudeSession(projectsDir, "vis-already-shared");

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            shareWithOrg: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var put = _server.LogEntries.SingleOrDefault(e =>
            e.RequestMessage.Method == "PUT"
         && e.RequestMessage.Path == "/api/sessions/vis-already-shared/visibility");

        await Assert.That(put).IsNotNull().Because("the user chose Shared for a repo holding this session");
        await Assert.That(JsonNode.Parse(put!.RequestMessage.Body!)!["visibility"]?.GetValue<string>())
                    .IsEqualTo("org");
    }

    [Test]
    public async Task HandleImport_shareWithOrg_leaves_alone_a_session_the_run_never_sent() {
        // The capture is bounded by status, which is what keeps it to sessions the server actually
        // has. A too-short session was never uploaded, so sharing it would name something absent —
        // and the same status gate is what keeps an excluded source out.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-short");
        WriteClaudeSession(projectsDir, "vis-too-short", lines: 3);

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 500,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            shareWithOrg: true
        );

        await Assert.That(_server.LogEntries.Where(e => e.RequestMessage.Method == "PUT")).IsEmpty();
    }

    [Test]
    public async Task HandleImport_reports_a_lost_visibility_write_through_the_outcome() {
        // The exit code stays 0 — import is best-effort — so a caller that has to say whether the
        // user's choice landed needs this, and only this.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-lostwrite");
        WriteClaudeSession(projectsDir, "vis-lost-write");

        ImportCommand.ImportRunOutcome? outcome = null;

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            shareWithOrg: true,
            onFinished: o => outcome = o
        );

        await Assert.That(exitCode).IsEqualTo(0).Because("import is best-effort and says so in its grid");
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.VisibilityFailures).IsEqualTo(1);
        await Assert.That(outcome.AnythingFailed).IsTrue();
    }

    [Test]
    public async Task HandleImport_reports_a_measured_zero_when_nothing_matches_the_scope() {
        // The exit that costs the first-run flow its finished signal: a scope matching nothing returns 0
        // having decided there was nothing to do, and a caller told nothing at all cannot tell that from
        // a run that died before it got there.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();

        var projectsDir = _tmp.PathTo("claude-projects-nothing-in-scope");
        WriteClaudeSession(projectsDir, "vis-out-of-scope");

        ImportCommand.ImportRunOutcome? outcome = null;

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.Repo([("kurrent-io", "nothing-here-by-that-name")]),
            skipConfirmation: true,
            onFinished: o => outcome = o
        );

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(outcome).IsNotNull().Because("zero is an answer; silence is not");
        await Assert.That(outcome!.Counts.Imported).IsEqualTo(0);
        await Assert.That(outcome.Counts.Skipped).IsEqualTo(0);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
        await Assert.That(outcome.AnythingFailed).IsFalse();
    }

    [Test]
    public async Task HandleImport_reports_a_clean_run_as_nothing_failed() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-cleanrun");
        WriteClaudeSession(projectsDir, "vis-clean-run");

        ImportCommand.ImportRunOutcome? outcome = null;

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            shareWithOrg: true,
            onFinished: o => outcome = o
        );

        await Assert.That(outcome!.AnythingFailed).IsFalse();
    }

    [Test]
    public async Task ExplicitVisibility_refuses_both_stops_at_once() {
        // Opposite promises. Silently picking one would hide the caller's bug.
        await Assert.That(() => ImportCommand.ExplicitVisibility(forcePrivate: true, shareWithOrg: true))
                    .Throws<ArgumentException>();
    }

    [Test]
    public async Task ExplicitVisibility_maps_each_stop_to_the_value_that_reaches_its_class() {
        await Assert.That(ImportCommand.ExplicitVisibility(true, false)).IsEqualTo("none");
        await Assert.That(ImportCommand.ExplicitVisibility(false, true)).IsEqualTo("org");
        await Assert.That(ImportCommand.ExplicitVisibility(false, false)).IsNull();
    }

    [Test]
    public async Task HandleImport_forcePrivate_does_not_upload_into_a_session_it_could_not_make_private() {
        // The failure path the ordering exists for. The pre-import write is best-effort, so awaiting it
        // proves nothing; a session it could not narrow must be dropped rather than published into.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-floorfail");
        WriteClaudeSession(projectsDir, "vis-floor-failed");

        ImportCommand.ImportRunOutcome? outcome = null;

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true,
            onFinished: o => outcome = o
        );

        await Assert.That(exitCode).IsEqualTo(0);

        // Nothing was uploaded: the transcript endpoint was never reached for this session.
        await Assert.That(_server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript"))
                    .IsEmpty()
                    .Because("publishing into a session that is still public is worse than not importing it");

        // And the run says so, rather than reporting a clean import of nothing.
        await Assert.That(outcome!.VisibilityFailures).IsGreaterThanOrEqualTo(1);
        await Assert.That(outcome.AnythingFailed).IsTrue();
    }

    // Bare, not keyed: the error capture is process-global, so a concurrent writer would land in it.
    [Test, NotInParallel]
    public async Task HandleImport_forcePrivate_reports_a_session_it_had_to_skip() {
        // Silently dropping it would read as a smaller history rather than as a refusal.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        var projectsDir = Path.Combine(_tempDir, "claude-projects-floorsay");
        WriteClaudeSession(projectsDir, "vis-floor-said");

        using var errors = ConsoleOutput.StartErrorCapture();

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(errors.GetCapturedError()).Contains("skipping vis-floor-said");
    }

    // =====================================================================
    // Section C — routed sources, direct-logic (ImportSessionAsync + WireMock).
    // =====================================================================

    static ImportCommand.SessionClassification RoutedClassification(
            string                              sessionId,
            ImportCommand.ClassificationStatus  status,
            Dictionary<string, object?>         sourceMeta,
            int                                 resumeFromLine = 0,
            int                                 totalLines     = 0,
            HarnessId?                          vendor         = null
        ) => new() {
        SessionId      = sessionId,
        FilePath       = "",
        EncodedCwd     = "",
        Meta           = new SessionMetadata(),
        Status         = status,
        ResumeFromLine = resumeFromLine,
        TotalLines     = totalLines,
        SourceMeta     = sourceMeta,
        Vendor         = vendor ?? HarnessId.Claude,
    };

    // --- Copilot ---

    [Test]
    public async Task Copilot_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("copilot-new.jsonl");
        var c = RoutedClassification("copilot-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("copilot");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Copilot_partial_and_already_loaded_sessions_omit_default_visibility() {
        StubAllHookEndpoints();
        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");

        var partialPath = WriteTranscript("copilot-partial.jsonl");
        var partial = RoutedClassification("copilot-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = partialPath }, resumeFromLine: 2);
        await new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths).ImportSessionAsync(partial, ctx, CancellationToken.None);
        await Assert.That(SessionStartBody("copilot").ContainsKey("default_visibility")).IsFalse();

        var alreadyPath = WriteTranscript("copilot-already.jsonl");
        var already = RoutedClassification("copilot-already-1", ImportCommand.ClassificationStatus.AlreadyLoaded,
            new() { ["TranscriptPath"] = alreadyPath }, totalLines: 5);
        await new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths).ImportSessionAsync(already, ctx, CancellationToken.None);

        var alreadyBody = JsonNode.Parse(
            _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/session-start/copilot")
                .ElementAt(1).RequestMessage.Body!
        )!.AsObject();
        await Assert.That(alreadyBody.ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Copilot_forcePrivate_stamps_private_over_the_step3_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("copilot-fp.jsonl");
        var c = RoutedClassification("copilot-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("copilot")["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    // --- Gemini ---

    [Test]
    public async Task Gemini_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("gemini-new.jsonl");
        var c = RoutedClassification("gemini-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("gemini");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Gemini_partial_session_omits_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("gemini-partial.jsonl");
        var c = RoutedClassification("gemini-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = path }, resumeFromLine: 2);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("gemini").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Gemini_forcePrivate_stamps_private_over_the_step3_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("gemini-fp.jsonl");
        var c = RoutedClassification("gemini-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("gemini")["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    // --- Kiro ---

    [Test]
    public async Task Kiro_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("kiro-new.jsonl");
        var c = RoutedClassification("kiro-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("kiro");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Kiro_partial_session_omits_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("kiro-partial.jsonl");
        var c = RoutedClassification("kiro-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = path }, resumeFromLine: 2);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("kiro").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Kiro_forcePrivate_stamps_private_over_the_step3_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("kiro-fp.jsonl");
        var c = RoutedClassification("kiro-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("kiro")["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    // --- Pi ---

    [Test]
    public async Task Pi_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("pi-new.jsonl");
        var c = RoutedClassification("pi-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("pi");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Pi_partial_session_omits_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("pi-partial.jsonl");
        var c = RoutedClassification("pi-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = path }, resumeFromLine: 2);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("pi").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Pi_forcePrivate_keeps_existing_private_stamp_and_never_the_org_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("pi-fp.jsonl");
        var c = RoutedClassification("pi-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir).ImportSessionAsync(c, ctx, CancellationToken.None);

        // Pi's existing forcePrivate behavior (stamping the literal "private") is untouched —
        // the new guard must never override it with the org-level default.
        var body = SessionStartBody("pi");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    // --- OpenCode: ImportSessionAsync needs a real sqlite db (SourceMeta unused), so this goes
    //     through Discover+Classify+Import like the existing OpenCodeImportSourceTests do, rather
    //     than a hand-built classification. ---

    [Test]
    public async Task OpenCode_new_session_stamps_default_visibility() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_new", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_new", "m1", "hello", 100);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await source.ImportSessionAsync(classified[0], ctx, CancellationToken.None);

        var body = SessionStartBody("opencode");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task OpenCode_partial_session_omits_default_visibility() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_partial", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_partial", "m1", "hello", 100);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        StubAllHookEndpoints();
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await source.ImportSessionAsync(classified[0], ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("opencode").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task OpenCode_already_loaded_session_is_skipped_before_any_session_start() {
        // OpenCodeImportSource.ImportSessionAsync early-returns Skipped for AlreadyLoaded
        // without posting anything — the guard's status==New check is unreachable for this
        // status, but this pins the "no stamp, no call at all" contract explicitly.
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_already", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_already", "m1", "hello", 100);

        StubAllHookEndpoints();
        using var client = new HttpClient();

        var c = new ImportCommand.SessionClassification {
            SessionId  = "ses_vis_already",
            FilePath   = "",
            EncodedCwd = "",
            Meta       = new SessionMetadata(),
            Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
        };

        var source = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var ctx    = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        var result = await source.ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Skipped);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/hooks/session-start/opencode")).IsFalse();
    }

    [Test]
    public async Task OpenCode_forcePrivate_keeps_existing_private_stamp_and_never_the_org_default() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_fp", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_fp", "m1", "hello", 100);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);

        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await source.ImportSessionAsync(classified[0], ctx, CancellationToken.None);

        var body = SessionStartBody("opencode");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    // --- Antigravity: needs SourceMeta["TranscriptPath"] (a real file) and tolerates a missing
    //     "Children" key (no subagents). ---

    [Test]
    public async Task Antigravity_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("ag-new.jsonl");
        var c = RoutedClassification("ag-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("antigravity");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Antigravity_partial_session_omits_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("ag-partial.jsonl");
        var c = RoutedClassification("ag-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = path }, resumeFromLine: 2);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("antigravity").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Antigravity_already_loaded_session_omits_default_visibility() {
        // AlreadyLoaded has its own dedicated repair branch (a separate BuildSessionStartPayload
        // call) — pinned separately from the New/Partial branch above.
        StubAllHookEndpoints();
        var path = WriteTranscript("ag-already.jsonl");
        var c = RoutedClassification("ag-already-1", ImportCommand.ClassificationStatus.AlreadyLoaded,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("antigravity").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Antigravity_forcePrivate_keeps_existing_private_stamp_and_never_the_org_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("ag-fp.jsonl");
        var c = RoutedClassification("ag-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path });

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths).ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("antigravity");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    // --- Cursor: the source the 2026-07-20 spec called out, because its live hook has no
    //     default_visibility injection at all, so the import path is the only one that stamps.
    //     Direct-logic coverage plus one full WireMock round-trip through HandleImport. ---

    [Test]
    public async Task Cursor_new_session_stamps_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("cursor-new.jsonl");
        var c = RoutedClassification("cursor-new-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path, ["WorkspaceFolder"] = "/Users/me/proj" }, vendor: HarnessId.Cursor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new CursorImportSource(Config.Root, 
                Path.Combine(_tempDir, "unused-cursor-projects"),
                Path.Combine(_tempDir, "unused-cursor-workspace-storage")
            )
            .ImportSessionAsync(c, ctx, CancellationToken.None);

        var body = SessionStartBody("cursor");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    [Test]
    public async Task Cursor_partial_session_omits_default_visibility() {
        StubAllHookEndpoints();
        var path = WriteTranscript("cursor-partial.jsonl");
        var c = RoutedClassification("cursor-partial-1", ImportCommand.ClassificationStatus.Partial,
            new() { ["TranscriptPath"] = path, ["WorkspaceFolder"] = "/Users/me/proj" },
            resumeFromLine: 2, vendor: HarnessId.Cursor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await new CursorImportSource(Config.Root, 
                Path.Combine(_tempDir, "unused-cursor-projects-2"),
                Path.Combine(_tempDir, "unused-cursor-workspace-storage-2")
            )
            .ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("cursor").ContainsKey("default_visibility")).IsFalse();
    }

    [Test]
    public async Task Cursor_forcePrivate_stamps_private_over_the_step3_default() {
        StubAllHookEndpoints();
        var path = WriteTranscript("cursor-fp.jsonl");
        var c = RoutedClassification("cursor-fp-1", ImportCommand.ClassificationStatus.New,
            new() { ["TranscriptPath"] = path, ["WorkspaceFolder"] = "/Users/me/proj" }, vendor: HarnessId.Cursor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await new CursorImportSource(Config.Root, 
                Path.Combine(_tempDir, "unused-cursor-projects-3"),
                Path.Combine(_tempDir, "unused-cursor-workspace-storage-3")
            )
            .ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("cursor")["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    [Test]
    public async Task Cursor_full_round_trip_through_HandleImport_stamps_default_visibility() {
        // Full WireMock round-trip via the real orchestrator entry point, mirroring
        // CursorPrivatizeLifecycleFailureTests' pattern — explicitly requested by the spec
        // since Cursor's live hook has no default_visibility injection today (this is new,
        // import-only behavior for Cursor).
        const string dirSessionId = "55555555-5555-5555-5555-555555555555";
        var sessionId = CursorImportSource.NormalizeCursorSessionId(dirSessionId);

        var projectsDir = Path.Combine(_tempDir, "cursor-projects-rt");
        var dir = Path.Combine(projectsDir, "no-workspace-match", "agent-transcripts", dirSessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, dirSessionId + ".jsonl"), "{\"a\":1}\n{\"b\":2}\n");

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();

        var source = new CursorImportSource(Config.Root, projectsDir, Path.Combine(_tempDir, "cursor-workspace-storage-rt"));

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: false,
            defaultVisibility: "org_public"
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var entry = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/cursor");
        var body  = JsonNode.Parse(entry.RequestMessage.Body!)!.AsObject();
        await Assert.That(body["session_id"]?.GetValue<string>()).IsEqualTo(sessionId);
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    // =====================================================================
    // Section E — matrix completion (review finding): AlreadyLoaded gaps,
    // forcePrivate × replay-status (Partial), and defaultVisibility:null,
    // for EACH of the 7 routed sources. DRY via RoutedSourceCase + shared
    // assertion helpers across the 6 "uniform-shaped" sources (Copilot,
    // Gemini, Kiro, Pi, Antigravity, Cursor) — all driven the same way as
    // Section C above (RoutedClassification + ImportSessionAsync directly).
    // OpenCode needs a real sqlite fixture (see OpenCodeImportSourceTests /
    // Section C above) so it's covered separately, right after this table.
    //
    // New + defaultVisibility set, and Partial ⇒ omit, are already fully
    // covered for all 7 sources in Section C — not repeated here.
    // =====================================================================

    /// <summary>One routed source's shape for the generic matrix helpers below.</summary>
    sealed record RoutedSourceCase(
        HarnessId                                 Vendor,
        Func<IImportSource>                       MakeSource,
        Func<string, Dictionary<string, object?>> MakeSourceMeta);

    RoutedSourceCase CopilotCase() =>
        new(HarnessId.Copilot, () => new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths), p => new() { ["TranscriptPath"] = p });

    RoutedSourceCase GeminiCase() =>
        new(HarnessId.Gemini, () => new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir), p => new() { ["TranscriptPath"] = p });

    RoutedSourceCase KiroCase() =>
        new(HarnessId.Kiro, () => new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir), p => new() { ["TranscriptPath"] = p });

    RoutedSourceCase PiCase() =>
        new(HarnessId.Pi, () => new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir), p => new() { ["TranscriptPath"] = p });

    RoutedSourceCase AntigravityCase() =>
        new(HarnessId.Antigravity, () => new AntigravityImportSource(AntigravityHarness.Over(GeminiHarness.FromEnvironment(Home)).Paths), p => new() { ["TranscriptPath"] = p });

    RoutedSourceCase CursorCase() =>
        new(HarnessId.Cursor,
            () => new CursorImportSource(Config.Root, 
                Path.Combine(_tempDir, $"unused-cursor-projects-{Guid.NewGuid():N}"),
                Path.Combine(_tempDir, $"unused-cursor-workspace-storage-{Guid.NewGuid():N}")),
            p => new() { ["TranscriptPath"] = p, ["WorkspaceFolder"] = "/Users/me/proj" });

    async Task AssertAlreadyLoadedOmitsDefaultVisibility(RoutedSourceCase rc) {
        StubAllHookEndpoints();
        var path = WriteTranscript($"{rc.Vendor.VendorId}-already-matrix.jsonl");
        var c = RoutedClassification($"{rc.Vendor.VendorId}-already-matrix-1", ImportCommand.ClassificationStatus.AlreadyLoaded,
            rc.MakeSourceMeta(path), totalLines: 5, vendor: rc.Vendor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: "org_public");
        await rc.MakeSource().ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody(rc.Vendor.VendorId).ContainsKey("default_visibility")).IsFalse();
    }

    async Task AssertForcePrivateStampsPrivateOnAlreadyLoaded(RoutedSourceCase rc) {
        StubAllHookEndpoints();
        var path = WriteTranscript($"{rc.Vendor.VendorId}-fp-already-matrix.jsonl");
        var c = RoutedClassification($"{rc.Vendor.VendorId}-fp-already-matrix-1", ImportCommand.ClassificationStatus.AlreadyLoaded,
            rc.MakeSourceMeta(path), totalLines: 5, vendor: rc.Vendor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await rc.MakeSource().ImportSessionAsync(c, ctx, CancellationToken.None);

        // The lifecycle-repair path, which several sources reach through a branch of their own
        // rather than the one the New/Partial rows exercise.
        await Assert.That(SessionStartBody(rc.Vendor.VendorId)["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    async Task AssertForcePrivateStampsPrivateOnReplay(RoutedSourceCase rc) {
        StubAllHookEndpoints();
        var path = WriteTranscript($"{rc.Vendor.VendorId}-fp-replay-matrix.jsonl");
        var c = RoutedClassification($"{rc.Vendor.VendorId}-fp-replay-matrix-1", ImportCommand.ClassificationStatus.Partial,
            rc.MakeSourceMeta(path), resumeFromLine: 2, vendor: rc.Vendor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await rc.MakeSource().ImportSessionAsync(c, ctx, CancellationToken.None);

        // Private does not gate on status, unlike the Step-3 default: it is a floor, and
        // re-asserting it on a replay can only narrow what is already there.
        await Assert.That(SessionStartBody(rc.Vendor.VendorId)["default_visibility"]?.GetValue<string>())
            .IsEqualTo("private");
    }

    async Task AssertNullDefaultVisibilityOmitsField(RoutedSourceCase rc) {
        StubAllHookEndpoints();
        var path = WriteTranscript($"{rc.Vendor.VendorId}-null-default-matrix.jsonl");
        var c = RoutedClassification($"{rc.Vendor.VendorId}-null-default-matrix-1", ImportCommand.ClassificationStatus.New,
            rc.MakeSourceMeta(path), vendor: rc.Vendor);

        using var client = new HttpClient();
        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: null);
        await rc.MakeSource().ImportSessionAsync(c, ctx, CancellationToken.None);

        await Assert.That(SessionStartBody(rc.Vendor.VendorId).ContainsKey("default_visibility")).IsFalse();
    }

    // --- AlreadyLoaded: missing for Gemini, Kiro, Pi, Cursor (Copilot/Antigravity/OpenCode
    //     already have one in Section C). ---

    [Test]
    public async Task Gemini_already_loaded_session_omits_default_visibility() => await AssertAlreadyLoadedOmitsDefaultVisibility(GeminiCase());

    [Test]
    public async Task Kiro_already_loaded_session_omits_default_visibility() => await AssertAlreadyLoadedOmitsDefaultVisibility(KiroCase());

    [Test]
    public async Task Pi_already_loaded_session_omits_default_visibility() => await AssertAlreadyLoadedOmitsDefaultVisibility(PiCase());

    [Test]
    public async Task Cursor_already_loaded_session_omits_default_visibility() => await AssertAlreadyLoadedOmitsDefaultVisibility(CursorCase());

    // --- forcePrivate × replay status (Partial): all 7 sources (Section C only exercised
    //     forcePrivate with New). OpenCode's own version follows after this table. ---

    [Test]
    public async Task Copilot_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(CopilotCase());

    [Test]
    public async Task Gemini_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(GeminiCase());

    [Test]
    public async Task Kiro_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(KiroCase());

    [Test]
    public async Task Pi_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(PiCase());

    [Test]
    public async Task Antigravity_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(AntigravityCase());

    [Test]
    public async Task Cursor_forcePrivate_stamps_private_on_a_replay() => await AssertForcePrivateStampsPrivateOnReplay(CursorCase());

    // --- forcePrivate × AlreadyLoaded: several sources answer this status from a branch of their
    //     own, so neither the New nor the Partial row reaches it. ---

    [Test]
    public async Task Copilot_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(CopilotCase());

    [Test]
    public async Task Gemini_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(GeminiCase());

    [Test]
    public async Task Kiro_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(KiroCase());

    [Test]
    public async Task Pi_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(PiCase());

    [Test]
    public async Task Antigravity_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(AntigravityCase());

    [Test]
    public async Task Cursor_forcePrivate_stamps_private_on_an_already_loaded_repair() => await AssertForcePrivateStampsPrivateOnAlreadyLoaded(CursorCase());

    // --- defaultVisibility:null (forcePrivate:false), New: all 7 sources. OpenCode's own
    //     version follows after this table. ---

    [Test]
    public async Task Copilot_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(CopilotCase());

    [Test]
    public async Task Gemini_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(GeminiCase());

    [Test]
    public async Task Kiro_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(KiroCase());

    [Test]
    public async Task Pi_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(PiCase());

    [Test]
    public async Task Antigravity_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(AntigravityCase());

    [Test]
    public async Task Cursor_new_session_omits_default_visibility_when_null() => await AssertNullDefaultVisibilityOmitsField(CursorCase());

    // --- OpenCode: needs a real sqlite fixture (own ImportSessionAsync reads the db
    //     directly), so it's not part of the RoutedSourceCase table above. ---

    [Test]
    public async Task OpenCode_forcePrivate_with_partial_status_keeps_existing_private_stamp() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_fp_partial", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_fp_partial", "m1", "hello", 100);

        // last_line_number:0 with the fixture's message present means one importable line
        // already landed on the server (repair-replay, not New) → Partial.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        StubAllHookEndpoints();
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: true, DefaultVisibility: "org_public");
        await source.ImportSessionAsync(classified[0], ctx, CancellationToken.None);

        // Same "private" stamp as the New case (OpenCode_forcePrivate_keeps_existing_private_stamp
        // in Section C) — BuildSessionStartPayload's forcePrivate branch doesn't gate on status.
        var body = SessionStartBody("opencode");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task OpenCode_new_session_omits_default_visibility_when_null() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_vis_null_default", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_vis_null_default", "m1", "hello", 100);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var ctx = new ImportContext(client, _server.Url!, ForcePrivate: false, DefaultVisibility: null);
        await source.ImportSessionAsync(classified[0], ctx, CancellationToken.None);

        await Assert.That(SessionStartBody("opencode").ContainsKey("default_visibility")).IsFalse();
    }

    // =====================================================================
    // Section D — autoSkipExclusions never blocks on stdin.
    // =====================================================================

    // Globally sequential (NO group key), like every other Console-redirecting test in this suite.
    // This test captures stderr by swapping the process-global Console.Error. A group key is not
    // enough: another capturing test in a DIFFERENT group runs concurrently, saves our writer as its
    // "original", and restores it when it finishes — after which our own "Auto-skipping" line is
    // written to that test's writer instead of ours, and our buffer holds only whatever some other
    // test wrote to stderr in the meantime (an unrelated login-flow error was the observed symptom).
    [Test, NotInParallel]
    [Arguments(false, "  Auto-skipping")]
    [Arguments(true, "    Auto-skipping")]
    public async Task HandleImport_autoSkipExclusions_completes_without_prompting_and_logs_auto_skip(
            bool nested, string expected) {
        // Excluded PATH (not repo) so no real git repo needs to be spun up — PathExclusion.IsExcluded
        // is a plain prefix check. The profile carrying the exclusion arrives as the import's own
        // resolution, so nothing process-global has to be steered.
        var excludedDir = Path.Combine(_tempDir, $"excluded-proj-{nested}");
        Directory.CreateDirectory(excludedDir);

        var projectsDir = Path.Combine(_tempDir, $"claude-projects-autoskip-{nested}");
        var cwdDir      = Path.Combine(projectsDir, "-excluded-proj");
        Directory.CreateDirectory(cwdDir);
        // Serialize the cwd so a Windows path's backslashes are JSON-escaped — a raw interpolation
        // produces invalid JSON on Windows (\a, \e … aren't valid escapes), which would leave cwd
        // unparsed, the session un-excluded, and the "Auto-skipping" branch never reached.
        var cwdJson = System.Text.Json.JsonSerializer.Serialize(excludedDir);
        File.WriteAllLines(Path.Combine(cwdDir, "autoskip-sess.jsonl"), Enumerable.Range(0, 20).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":{{{cwdJson}}},"message":{"content":"line-{{{i}}}"}}"""
        ));

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        StubAllHookEndpoints();

        using var capture = ConsoleOutput.StartErrorCapture();

        int exitCode;

        // If autoSkipExclusions didn't force the non-interactive branch, and this process
        // happened to look like an interactive TTY, this call could block forever on
        // Console.ReadLine(). It must not, regardless of ambient TTY state.
        var import = new ImportCommand(Config.Root,
            Resolutions.Of(new Profile { ExcludedPaths = [excludedDir] }, "autoskip-test", _server.Url!), Home, new FixedCapacitorHttpClient());

        var task = import.HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projectsDir)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            autoSkipExclusions: true,
            nested: nested
        );

        var winner    = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15)));
        var timedOut  = !ReferenceEquals(winner, task);
        await Assert.That(timedOut).IsFalse(); // did not time out / hang on stdin

        exitCode = await task;

        await Assert.That(exitCode).IsEqualTo(0);
        var notice = capture.GetCapturedError()
                            .Split('\n')
                            .First(l => l.Contains("Auto-skipping", StringComparison.Ordinal));

        await Assert.That(notice).StartsWith(expected);

        // Never actually asked the user to include the excluded path.
        await Assert.That(capture.GetCapturedError()).DoesNotContain("Include");
    }
}
