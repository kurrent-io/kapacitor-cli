using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Gemini;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// <c>--private</c> must privatize a routed session whose replay attached brand-new nested-child
/// content, for EVERY vendor that can attach it — not just Cursor. Mirrors
/// <see cref="CursorPrivatizeLifecycleFailureTests"/>, which pins the same contract for Cursor.
///
/// <para>
/// The end-of-run privatize pass unions two sets: <c>importedSessionIds</c> (membership keyed on
/// the call's RAW <see cref="ImportOutcome"/> being Loaded/Resumed) and the outcome-independent
/// <c>privateScopeSessionIds</c>. Before the fix the latter was gated to <c>vendor == "cursor"</c>,
/// so a non-Cursor replay that attached child content fell through both:
/// </para>
/// <list type="bullet">
/// <item><b>Antigravity</b> — its AlreadyLoaded repair branch returns a hardcoded
/// <see cref="ImportOutcome.Skipped"/> however much child content it attached, so it never reaches
/// the Loaded/Resumed membership arm.</item>
/// <item><b>Gemini</b> — its AlreadyLoaded replay returns Resumed (so the SUCCESS path was already
/// covered), but a session-end POST failing AFTER the child content posted returns
/// <see cref="ImportOutcome.Failed"/>, which is excluded just the same.</item>
/// </list>
///
/// <para>
/// Either way the session keeps whatever visibility it already had — public, if an earlier
/// non-private import stamped an org default — while carrying content this <c>--private</c> run
/// just added. The replayed <c>session-start</c>'s <c>default_visibility</c> can't save it: that
/// is a CREATE-time hint, and by definition an already-ingested session already exists.
/// </para>
/// </summary>
public class RoutedReplayPrivatizeTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server     = WireMockServer.Start();
    readonly TempDir        _tmp        = new();
    readonly string         _agHome;
    readonly string         _geminiHome;

    public RoutedReplayPrivatizeTests() {
        _agHome     = _tmp.CreateDir("ag");
        _geminiHome = _tmp.CreateDir("gemini");
    }

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    static string Dashless(string id) => Guid.Parse(id).ToString("N");

    string[] VisibilityPutPaths() => _server.LogEntries
        .Where(e => e.RequestMessage.Method == "PUT"
                 && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal))
        .Select(e => e.RequestMessage.Path)
        .ToArray();

    string[] VisibilityPutBodies() => _server.LogEntries
        .Where(e => e.RequestMessage.Method == "PUT"
                 && e.RequestMessage.Path.EndsWith("/visibility", StringComparison.Ordinal))
        .Select(e => e.RequestMessage.Body!)
        .ToArray();

    void StubVisibilityPut() =>
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

    // =====================================================================
    // Antigravity — brain/<conv>/.system_generated/logs/transcript_full.jsonl,
    // with the child linked by an INVOKE_SUBAGENT step on the parent.
    // =====================================================================

    const string AgRootConvId  = "5aaa0000-0000-4000-8000-00000000000a";
    const string AgChildConvId = "6bbb0000-0000-4000-8000-00000000000b";
    static readonly string AgRoot  = Dashless(AgRootConvId);
    static readonly string AgChild = Dashless(AgChildConvId);

    string AgBrainDir(string convId) => Path.Combine(_agHome, ".gemini", "antigravity", "brain", convId);

    void WriteAntigravityTranscript(string convId, string firstUserText) {
        var dir = Path.Combine(AgBrainDir(convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>{{firstUserText}}</USER_REQUEST>"}""",
            """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-07-02T19:00:05Z","content":"done"}"""
        });
    }

    void WriteAntigravityLinkage() {
        var dir = Path.Combine(AgBrainDir(AgRootConvId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"{{AgChildConvId}}\"}"}"""
        });
    }

    /// <summary>
    /// Root is fully ingested (AlreadyLoaded); its child has never been ingested, so the repair
    /// branch attaches the child's content inline. Every POST succeeds, so the ONLY thing keeping
    /// the root out of the privatize set is that its raw outcome is Skipped.
    /// </summary>
    void StubAntigravityAlreadyLoadedWithNewChild(int sessionEndStatus = 200) {
        // Child's own subsession probe: never ingested -> the repair sends its content inline.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", AgChild).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));
        // Root's own top-level probe: server line 1 == the last import-relevant line -> AlreadyLoaded.
        _server.Given(Request.Create().WithPath($"/api/sessions/{AgRoot}/last-line").UsingGet())
            .AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));

        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        _server.Given(Request.Create().WithPath("/hooks/session-end/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(sessionEndStatus));

        StubVisibilityPut();
    }

    Task<int> RunAntigravityImport(bool forcePrivate) => new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
        filterCwd: null,
        minLines: 0,
        sources: [new AntigravityImportSource(new(new(_agHome), ""))],
        scope: new ImportScope.All(),
        skipConfirmation: true,
        forcePrivate: forcePrivate
    );

    /// <summary>
    /// The scenario named in the bug report: an AlreadyLoaded Antigravity root that attaches a
    /// brand-new child during the replay, with every POST succeeding. Its raw outcome is the
    /// hardcoded Skipped, so it never joins <c>importedSessionIds</c> — before the fix that meant
    /// ZERO visibility PUTs and the newly-attached child content stayed publicly visible.
    /// </summary>
    [Test, NotInParallel]
    public async Task private_run_privatizes_an_already_loaded_antigravity_root_that_attached_new_child_content() {
        WriteAntigravityTranscript(AgRootConvId, "build it");
        WriteAntigravityTranscript(AgChildConvId, "sub task");
        WriteAntigravityLinkage();
        StubAntigravityAlreadyLoadedWithNewChild();

        await Assert.That(await RunAntigravityImport(forcePrivate: true)).IsEqualTo(0);

        await Assert.That(VisibilityPutPaths()).Contains($"/api/sessions/{AgRoot}/visibility");
        await Assert.That(VisibilityPutBodies().Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// Same replay, but session-end fails AFTER the child content has already persisted — the
    /// outcome degrades from Skipped to Failed, which is excluded from the privatize set by a
    /// different arm. Privatization must not depend on the outcome classification at all.
    /// </summary>
    [Test, NotInParallel]
    public async Task private_run_privatizes_an_antigravity_replay_whose_session_end_failed_after_child_content() {
        WriteAntigravityTranscript(AgRootConvId, "build it");
        WriteAntigravityTranscript(AgChildConvId, "sub task");
        WriteAntigravityLinkage();
        StubAntigravityAlreadyLoadedWithNewChild(sessionEndStatus: 500);

        await Assert.That(await RunAntigravityImport(forcePrivate: true)).IsEqualTo(0);

        await Assert.That(VisibilityPutPaths()).Contains($"/api/sessions/{AgRoot}/visibility");
        await Assert.That(VisibilityPutBodies().Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }

    /// <summary>
    /// No-regression guard: widening the outcome-independent capture must never make a plain
    /// (non-private) import start touching visibility.
    /// </summary>
    [Test, NotInParallel]
    public async Task non_private_antigravity_run_never_calls_set_visibility() {
        WriteAntigravityTranscript(AgRootConvId, "build it");
        WriteAntigravityTranscript(AgChildConvId, "sub task");
        WriteAntigravityLinkage();
        StubAntigravityAlreadyLoadedWithNewChild();

        await Assert.That(await RunAntigravityImport(forcePrivate: false)).IsEqualTo(0);

        await Assert.That(VisibilityPutPaths().Length).IsEqualTo(0);
    }

    // =====================================================================
    // Gemini — chats/<parent>.jsonl with an invoke_agent tool call, plus the
    // nested chats/<dashedParent>/<dashedSub>.jsonl subagent transcript.
    // =====================================================================

    const string GemDashedParent   = "0a900000-0000-4000-8000-000000000903";
    const string GemDashedSub      = "57d9b498-2705-4af5-b060-ebaba4878c96";
    static readonly string GemParent = Dashless(GemDashedParent);

    void WriteGeminiFixture() {
        var chats = Path.Combine(_geminiHome, "proj", "chats");
        Directory.CreateDirectory(chats);

        File.WriteAllLines(Path.Combine(chats, "session-2026-06-22T14-31-0a900000.jsonl"), new[] {
            $$"""{"sessionId":"{{GemDashedParent}}","projectHash":"h","startTime":"2026-06-22T14:31:00.000Z","lastUpdated":"2026-06-22T14:31:00.000Z","kind":"main"}""",
            """{"id":"u1","timestamp":"2026-06-22T14:31:01.000Z","type":"user","content":[{"text":"delegate it"}]}""",
            $$"""{"id":"m1","timestamp":"2026-06-22T14:31:05.000Z","type":"gemini","content":"","tokens":{"input":5,"output":2,"total":7},"model":"gemini-3-flash-preview","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"codebase_investigator","prompt":"list files"},"agentId":"{{GemDashedSub}}","status":"success"}]}"""
        });

        var subDir = Path.Combine(chats, GemDashedParent);
        Directory.CreateDirectory(subDir);
        File.WriteAllLines(Path.Combine(subDir, GemDashedSub + ".jsonl"), new[] {
            $$"""{"sessionId":"{{GemDashedSub}}","projectHash":"h","startTime":"2026-06-22T14:31:06.000Z","lastUpdated":"2026-06-22T14:31:06.000Z","kind":"subagent","directories":[]}""",
            """{"id":"s1","timestamp":"2026-06-22T14:31:07.000Z","type":"gemini","content":"calc.py, README.md","tokens":{"input":3,"output":4,"total":7},"model":"gemini-3-flash-preview"}"""
        });
    }

    /// <summary>
    /// Gemini's AlreadyLoaded replay returns Resumed when every POST succeeds, so it already
    /// joined the privatize set on the success path. The gap is the failure path: its subagent
    /// import always resends the whole child transcript (no per-child watermark), so the child
    /// content lands BEFORE session-end — and a failing session-end turns the outcome into Failed,
    /// dropping the parent from the set with the child's content already public.
    /// </summary>
    [Test, NotInParallel]
    public async Task private_run_privatizes_a_gemini_replay_whose_session_end_failed_after_child_content() {
        WriteGeminiFixture();

        // Watermark far past the last import-relevant line -> AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":999}"""));
        foreach (var route in new[] {
            "/hooks/session-start/gemini", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        // Fails AFTER ImportSubagentsAsync has already posted the child's content.
        _server.Given(Request.Create().WithPath("/hooks/session-end/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));
        StubVisibilityPut();

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
            filterCwd: null,
            minLines: 0,
            sources: [new GeminiImportSource(_geminiHome)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            forcePrivate: true
        );

        await Assert.That(exitCode).IsEqualTo(0);

        await Assert.That(VisibilityPutPaths()).Contains($"/api/sessions/{GemParent}/visibility");
        await Assert.That(VisibilityPutBodies().Any(b => b == """{"visibility":"none"}""")).IsTrue();
    }
}
