using System.Net;
using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Gemini;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Integration-level regression pinning the routed-loop aggregation boundary in
/// <c>ImportCommand.HandleImport</c>'s "Importing N routed sessions" phase (the per-session
/// switch on <see cref="ImportCommand.ResolveRoutedOutcomeForCounting"/>).
///
/// <para>
/// The unit tests in <c>ImportDoneBreakdownTests</c> pin <c>ResolveRoutedOutcomeForCounting</c> /
/// <c>IsSkippedChildContentOverride</c> in isolation, and separately compute the
/// <c>importedSessionIds</c> membership expression (<c>resolved is not null &amp;&amp; outcome is
/// Loaded or Resumed</c>) LOCALLY rather than observing it emerge from the real loop. That leaves
/// a real regression window open: rewiring the loop's membership check to key off <c>resolved</c>
/// instead of the raw <c>outcome</c> — or wiring routedLoaded / the Done sub-grid / the printed
/// line back to the raw outcome instead of <c>resolved</c> — would leave every one of those unit
/// tests green, because none of them drive <c>HandleImport</c> itself.
/// </para>
///
/// <para>
/// This test drives the exact "Antigravity shape" the unit tests' comments call out — an
/// AlreadyLoaded root conversation whose own content is fully ingested, but which attaches a
/// brand-new correlated child conversation inline during the routed-phase lifecycle repair
/// (<c>ImportSessionResult(ImportOutcome.Skipped, SentChildContent: true)</c>) — through the REAL
/// <see cref="ImportCommand.HandleImport"/> orchestrator end-to-end, and asserts all four required
/// effects move together:
/// </para>
/// <list type="number">
/// <item>Counted as Loaded, not Excluded (<c>routedLoaded</c>, via the "== Done ==" totals).</item>
/// <item>The per-vendor Done sub-grid shows the "antigravity" row as Loaded, not Excluded (a
/// second vendor — a Gemini session that hits a probe error — is added so the by-source grid
/// actually has &gt;1 vendor key and renders at all).</item>
/// <item>The printed per-session line is the "Loading …" line, not the "Already loaded … (no new
/// content)" no-op line.</item>
/// <item>Under <c>--private</c> the root gets exactly one <c>PUT .../visibility</c> — it is
/// captured outcome-independently because Antigravity declares
/// <see cref="IImportSource.AttachesChildContentOnReplay"/>. The nested child is not a routed
/// classification of its own and contributes no PUT of its own.</item>
/// </list>
///
/// <para>
/// Effect 4 is NOT a guard on <c>importedSessionIds</c> membership, and must not be read as one.
/// The privatize set is <c>HashSet(importedSessionIds) ∪ privateScopeSessionIds</c> and this root
/// is already in the private scope, so rewiring membership to the resolved <c>Loaded</c> outcome
/// would union the same single id and still emit exactly one PUT — verified by mutation. The
/// Done-grid assertions do not expose it either (<c>ComputePerSourceFinalCounts</c> takes the
/// routed early return and ignores its <c>imported</c> argument). That rule is pinned instead by
/// <see cref="RoutedPrivatizeMembershipTests"/>, which uses a source outside the private scope so
/// the union cannot absorb the difference.
/// </para>
/// </summary>
public class AntigravitySkippedChildOverrideRoutedLoopTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server         = WireMockServer.Start();
    readonly TempDir        _tmp            = new();
    readonly string         _home;
    readonly string         _geminiTmpDir;

    public AntigravitySkippedChildOverrideRoutedLoopTests() {
        _home         = _tmp.CreateDir("home");
        _geminiTmpDir = _tmp.CreateDir("gemini");
    }

    const string RootConvId    = "55550000-0000-4000-8000-000000000005";
    const string ChildConvId   = "66660000-0000-4000-8000-000000000006";
    const string GeminiConvId  = "77770000-0000-4000-8000-000000000007";

    static string Dashless(string id) => Guid.Parse(id).ToString("N");
    static readonly string Root  = Dashless(RootConvId);
    static readonly string Child = Dashless(ChildConvId);

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string BrainDir(string convId) => Path.Combine(_home, ".gemini", "antigravity", "brain", convId);

    void WriteAntigravityTranscript(string convId, string firstUserText) {
        var dir = Path.Combine(BrainDir(convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>{{firstUserText}}</USER_REQUEST>"}""",
            """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-07-02T19:00:05Z","content":"done"}"""
        });
    }

    // Appends an INVOKE_SUBAGENT step to Root's transcript naming Child as the spawned
    // conversation — the spawn-time linkage AntigravitySubagents.BuildParentMap reads.
    void WriteLinkage() {
        var dir = Path.Combine(BrainDir(RootConvId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.AppendAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"{{ChildConvId}}\"}"}"""
        });
    }

    // A second vendor with exactly one classification (ProbeError, from a failing watermark
    // probe) — just enough for ImportCommand's per-vendor `doneBySource` dictionary to carry 2
    // keys so the "By source" sub-grid actually renders. ProbeError classifications are excluded
    // from both the chain and routed import phases, so this session never touches a hook route.
    void WriteGeminiFixture() {
        var chats = Path.Combine(_geminiTmpDir, "proj", "chats");
        Directory.CreateDirectory(chats);
        File.WriteAllLines(Path.Combine(chats, "session-2026-07-02T19-00-00007777.jsonl"), new[] {
            $$"""{"sessionId":"{{GeminiConvId}}","projectHash":"h","startTime":"2026-07-02T19:00:00.000Z","lastUpdated":"2026-07-02T19:00:01.000Z","kind":"main"}""",
            """{"id":"u1","timestamp":"2026-07-02T19:00:01.000Z","type":"user","content":[{"text":"hi"}]}"""
        });
    }

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        using var capture = ConsoleOutput.StartCapture();
        await action();
        return capture.GetCapturedOutput();
    }

    static bool LineMatches(string text, string label, int value) =>
        Regex.IsMatch(text, $@"(?m)^\s*{Regex.Escape(label)}\s+{value}\s*$");

    [Test, NotInParallel]
    public async Task already_loaded_root_with_new_child_content_counts_loaded_and_is_privatized_via_replay_scope() {
        WriteAntigravityTranscript(RootConvId, "build it");
        WriteAntigravityTranscript(ChildConvId, "sub task");
        WriteLinkage();
        WriteGeminiFixture();

        // Child's own subsession watermark probe (agentId=Child): not yet ingested -> the
        // AlreadyLoaded-repair path sends its content inline (SentChildContent=true).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Child).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));
        // Root's own top-level probe (no agentId param): already fully ingested (server line 1 ==
        // the parent's last IMPORT-RELEVANT line, index 1 / PLANNER_RESPONSE) -> AlreadyLoaded.
        _server.Given(Request.Create().WithPath($"/api/sessions/{Root}/last-line").UsingGet())
            .AtPriority(5)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":1}"""));
        // Fallback for any other session's probe (the Gemini fixture's own session id) -> a hard
        // failure, so it classifies ProbeError rather than accidentally looking AlreadyLoaded too.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError));

        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var antigravity = new AntigravityImportSource(new(new(_home), ""));
        var gemini      = new GeminiImportSource(_geminiTmpDir);

        var exitCode = 0;
        var stdout = await CaptureStdoutAsync(async () => {
            exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
                filterCwd: null,
                minLines: 0,
                sources: [antigravity, gemini],
                scope: new ImportScope.All(),
                skipConfirmation: true,
                forcePrivate: true
            );
        });

        await Assert.That(exitCode).IsEqualTo(0);

        // --- Effect 3: the printed per-session line is the "Loading" line, not the grey
        // "Already loaded ... (no new content)" no-op line (which is what a raw Skipped outcome
        // with no override would print instead). ---
        await Assert.That(stdout).Contains($"Loading {Root} (antigravity)");
        await Assert.That(stdout).DoesNotContain($"Already loaded {Root}");
        await Assert.That(stdout).DoesNotContain($"Refreshed {Root}");

        // --- Effects 1 & 2: isolate the "== Done ==" section from the earlier "== Plan ==" one
        // (which unconditionally prints "Excluded" even at 0) and check both the totals and the
        // per-vendor "antigravity" sub-grid row show Loaded, not Excluded. ---
        var doneIdx = stdout.IndexOf("== Done ==", StringComparison.Ordinal);
        await Assert.That(doneIdx).IsGreaterThanOrEqualTo(0);
        var doneSection = stdout[doneIdx..];

        // Total row: routedLoaded folded in (chain phase never ran - both sources are routed).
        await Assert.That(LineMatches(doneSection, "Loaded", 1)).IsTrue();
        await Assert.That(doneSection).DoesNotContain("Excluded");
        await Assert.That(doneSection).DoesNotContain("Errored");

        var vendorIdx = doneSection.IndexOf("[antigravity]", StringComparison.Ordinal);
        await Assert.That(vendorIdx).IsGreaterThanOrEqualTo(0);
        var nextVendorIdx = doneSection.IndexOf('[', vendorIdx + "[antigravity]".Length);
        var vendorRow     = nextVendorIdx > 0 ? doneSection[vendorIdx..nextVendorIdx] : doneSection[vendorIdx..];

        await Assert.That(LineMatches(vendorRow, "Loaded", 1)).IsTrue();
        await Assert.That(LineMatches(vendorRow, "Already loaded", 1)).IsTrue();
        await Assert.That(vendorRow).DoesNotContain("Excluded");

        // --- Effect 4: under --private the root is privatized exactly once, via the
        // outcome-independent tracker (Antigravity's replay can attach child content). The nested
        // child is not a routed classification and contributes no PUT; the Gemini fixture never
        // reaches import (ProbeError) so it contributes none either.
        //
        // This does NOT pin importedSessionIds membership — see the class doc: the private scope
        // already covers this root, so the union makes raw-vs-resolved membership unobservable
        // here. RoutedPrivatizeMembershipTests pins that.
        var putPaths = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Select(e => e.RequestMessage.Path)
            .ToArray();
        // Distinct: an existing session is narrowed before the replay and again by the closing pass, so
        // which sessions were privatized is the claim here, not how many writes it took.
        await Assert.That(putPaths.Distinct()).IsEquivalentTo([$"/api/sessions/{Root}/visibility"]);
    }
}
