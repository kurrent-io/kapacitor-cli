using System.Net;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Cursor;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// a lifecycle failure (subagent-stop/session-end) or a no-new-
/// content retry AFTER a Cursor session's content has already been posted must not permanently
/// bypass <c>--private</c>. Drives the real <c>ImportCommand.HandleImport</c> orchestrator (not
/// just <see cref="CursorImportSource.ImportSessionAsync"/> in isolation) against a stub server,
/// because the bug lived in the routed-loop's privatize-set bookkeeping in
/// <c>ImportCommand.cs</c>, not in <see cref="CursorImportSource"/> itself.
///
/// <para>
/// Before the fix, a Cursor session only entered <c>importedSessionIds</c> — the sole input to
/// the end-of-run <c>--private</c> PUT /visibility pass — when its own
/// <see cref="ImportOutcome"/> was Loaded/Resumed AND it wasn't a lifecycle-only replay
/// (<see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/>). A lifecycle POST failing AFTER
/// content had already persisted made <see cref="CursorImportSource.ImportSessionAsync"/> return
/// <see cref="ImportOutcome.Failed"/> — excluded. A later retry that read the session as
/// AlreadyLoaded-with-nothing-new was excluded too (lifecycle-only replay). Either way the
/// session's already-public content was never privatized, even on a fresh <c>--private</c> run.
/// </para>
///
/// <para>
/// The fix adds a separate, outcome-independent tracker
/// (<c>privateScopeSessionIds</c> in <c>ImportCommand.cs</c>) that captures every Cursor routed
/// classification touched under <c>--private</c> regardless of outcome, and unions it into the
/// privatize set — without touching <c>importedSessionIds</c>/the Done-grid counting (that
/// stays deferred, untouched).
/// </para>
/// </summary>
public class CursorPrivatizeLifecycleFailureTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    string ProjectsDir         => _tmp.PathTo(".cursor", "projects");
    string WorkspaceStorageDir => _tmp.PathTo("workspaceStorage");

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    // On-disk Cursor session directories/files are named with dashes, but the CLI normalizes to
    // a dashless session id (CursorImportSource.NormalizeCursorSessionId) for every server call —
    // classification, hooks, and the visibility PUT all key on the dashless form.
    const string DirSessionId = "22222222-2222-2222-2222-222222222222";
    static readonly string SessionId = CursorImportSource.NormalizeCursorSessionId(DirSessionId);

    string WriteOneCursorSession() {
        Directory.CreateDirectory(ProjectsDir);

        var dir = Path.Combine(ProjectsDir, "no-workspace-match", "agent-transcripts", DirSessionId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DirSessionId + ".jsonl"), "{\"a\":1}\n{\"b\":2}\n");

        return ProjectsDir;
    }

    (string[] PutPaths, string[] PutBodies) VisibilityPuts() {
        var entries = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT" && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal))
            .ToArray();

        return (
            entries.Select(e => e.RequestMessage.Path).ToArray(),
            entries.Select(e => e.RequestMessage.Body!).ToArray()
        );
    }

    /// <summary>
    /// Case 1 (finding scenario 1): the child transcript POST succeeds (content persisted), but
    /// the subsequent session-end lifecycle POST FAILS. Old code: ImportSessionAsync returns
    /// Failed, the session never joins importedSessionIds, --private never privatizes it —
    /// content is left public forever. New code: the session is captured into
    /// privateScopeSessionIds unconditionally (before the outcome switch), so it's privatized
    /// regardless of the Failed outcome.
    /// </summary>
    [Test]
    public async Task private_run_privatizes_even_when_lifecycle_post_fails_after_content_persisted() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404)); // no watermark yet -> New
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)); // content persists
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500)); // lifecycle fails AFTER content persisted
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(Config.Root, WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, putBodies) = VisibilityPuts();
        await Assert.That(putPaths).Contains($"/api/sessions/{SessionId}/visibility");
        await Assert.That(putBodies.Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// Case 2 (finding scenario 2, the self-heal-on-retry contract): the session now classifies
    /// AlreadyLoaded (server already has every non-blank line) — the exact shape a retry sees
    /// after a prior run's content-then-lifecycle-failure. Old code: IsLifecycleOnlyRoutedReplay
    /// treats this as a no-op replay, excluded from importedSessionIds; a public session stays
    /// public even on repeated --private re-runs. New code: privatization no longer depends on
    /// that classification at all — the session is captured unconditionally.
    /// </summary>
    [Test]
    public async Task private_retry_run_privatizes_an_already_loaded_session_with_no_new_content() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}""")); // AlreadyLoaded
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(Config.Root, WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, putBodies) = VisibilityPuts();
        await Assert.That(putPaths).Contains($"/api/sessions/{SessionId}/visibility");
        await Assert.That(putBodies.Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// Case 3 (no-regression guard): a NON-private run must never call PUT /visibility, even
    /// though the session imports cleanly. Guards against the union-based fix accidentally
    /// widening privatization to plain imports.
    /// </summary>
    [Test]
    public async Task non_private_run_never_calls_set_visibility() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(Config.Root, WriteOneCursorSession(), WorkspaceStorageDir);

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: false
        );

        await Assert.That(exitCode).IsEqualTo(0);

        var (putPaths, _) = VisibilityPuts();
        await Assert.That(putPaths.Length).IsEqualTo(0);
    }

    // --- The 2-attempt cap on the child watermark probe must never regress the privacy fix
    // above, even though the cap changes WHEN the fail-open triggers (after 2 attempts instead
    // of 1). ---

    static string Line(object o) => JsonSerializer.Serialize(o);

    static string ParentTranscriptWithTask(string prompt) => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "do the thing" } } } }),
        Line(new {
            role    = "assistant",
            message = new {
                content = new object[] {
                    new { type = "tool_use", name = "Task", input = new { description = "explore", prompt, subagent_type = "generalPurpose" } }
                }
            }
        })
    );

    static string ChildTranscriptFromPrompt(string prompt) => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "<user_query>\n" + prompt + "\n</user_query>" } } } }),
        Line(new { role = "assistant", message = new { content = new object[] { new { type = "text", text = "exploring" } } } })
    );

    const string ParentDirSessionId = "33333333-3333-3333-3333-333333333333";
    const string ChildDirSessionId  = "44444444-4444-4444-4444-444444444444";
    static readonly string ParentSessionId = CursorImportSource.NormalizeCursorSessionId(ParentDirSessionId);
    static readonly string ChildSessionId  = CursorImportSource.NormalizeCursorSessionId(ChildDirSessionId);

    /// <summary>
    /// Writes a real parent+correlated-child pair (same sanitized workspace dir, so
    /// <see cref="CursorSubagentCorrelator"/> links them) — the only shape that actually
    /// exercises <c>SendSubagentLifecycleAsync</c>'s watermark probe end-to-end via
    /// <see cref="ImportCommand.HandleImport"/>.
    /// </summary>
    string WriteParentWithCorrelatedChild() {
        Directory.CreateDirectory(ProjectsDir);
        const string prompt = "You are exploring the LoanApplicationDemo project. Return an overview.";

        var parentDir = Path.Combine(ProjectsDir, "no-workspace-match", "agent-transcripts", ParentDirSessionId);
        Directory.CreateDirectory(parentDir);
        File.WriteAllText(Path.Combine(parentDir, ParentDirSessionId + ".jsonl"), ParentTranscriptWithTask(prompt));

        var childDir = Path.Combine(ProjectsDir, "no-workspace-match", "agent-transcripts", ChildDirSessionId);
        Directory.CreateDirectory(childDir);
        File.WriteAllText(Path.Combine(childDir, ChildDirSessionId + ".jsonl"), ChildTranscriptFromPrompt(prompt));

        return ProjectsDir;
    }

    [Test]
    public async Task private_run_privatizes_when_child_watermark_probe_fails_both_of_its_2_attempts() {
        // The child's own SUBSESSION watermark probe (/api/sessions/{parent}/last-line?agentId=
        // {child}) fails on EVERY call — D3's fixed 2-attempt cap exhausts both attempts, then
        // falls open (full resend). This must not regress the r6 privacy fix: the parent still
        // gets privatized, independent of whichever Done-grid bucket the run's outcome lands in.
        // The classify-time TOP-LEVEL probe (no agentId param) is a separate call shape and must
        // keep succeeding (404 = fresh) so both sessions actually reach the routed import phase
        // instead of failing classification outright.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", ChildSessionId).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));
        foreach (var route in new[] {
            "/hooks/session-start/cursor", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/cursor"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var source = new CursorImportSource(Config.Root, WriteParentWithCorrelatedChild(), WorkspaceStorageDir);

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [source],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        // The child's own subsession watermark probe was actually invoked twice (the fixed
        // 2-attempt cap engaging), not zero and not more — distinct from the classify-time
        // top-level probes (no agentId param), which succeed on the first attempt.
        var childProbeCalls = _server.LogEntries.Count(e =>
            e.RequestMessage.Method == "GET"
         && e.RequestMessage.Path.EndsWith("/last-line", StringComparison.Ordinal)
         && e.RequestMessage.Url!.Contains($"agentId={ChildSessionId}", StringComparison.Ordinal));
        await Assert.That(childProbeCalls).IsEqualTo(2);

        var (putPaths, putBodies) = VisibilityPuts();
        await Assert.That(putPaths).Contains($"/api/sessions/{ParentSessionId}/visibility");
        await Assert.That(putBodies.Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }
}
