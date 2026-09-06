using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// Pins the DECIDED behaviour for durable state that can outlive a Cursor session: a
/// well-formed marker without an ack fails CLOSED (and silently), while a malformed marker
/// fails OPEN to top-level. The asymmetry is deliberate and is only defensible because the
/// offline recovery path (<c>kcap import --cursor</c> plus the server-side adoption sweep)
/// exists.
///
/// <para>
/// None of these states has a producer on the measured cursor-agent contract — the arm that
/// would write a marker never runs there — but consumption is NOT gated: TryLoadLink runs on
/// every event, so a marker persisted by another surface or an older build is still read.
/// See docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md (D2a).
/// </para>
/// <para>
/// [NotInParallel] — and deliberately UNKEYED, matching CursorWatcherSpawnTests. The marker
/// paths here are per-test GUIDs and would be safe on their own, but the tests that drive the
/// dispatcher install <see cref="WatcherManager.SpawnOverrideForTesting"/>, which is
/// process-wide and is also mutated from other classes. Observed, not assumed: without this the
/// two known-risk tests pass individually and fail when the class runs together.
/// </para>
/// </summary>
[NotInParallel]
public class CursorSubagentStaleStateTests {
    [TempHome] public required TempHome Home { get; init; }

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string MarkerPath(string child) =>
        Path.Combine(Config.Root.Path("cursor-subagent-links"), child);

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ---------------------------------------------------------------------------------
    // Contract pins. Each must fail when the behaviour it guards is removed.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task A_well_formed_marker_is_loaded_and_would_activate_the_divert() {
        var child = NewSessionId();
        try {
            CursorLiveSubagentLinker.SaveLink(Config.Root, child, "parent-sid", "researcher");

            var marker = CursorLiveSubagentLinker.TryLoadLink(Config.Root, child);
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.Value.ParentSessionId).IsEqualTo("parent-sid");
            await Assert.That(marker.Value.SubagentType).IsEqualTo("researcher");
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_truncated_marker_fails_open_to_top_level() {
        var child = NewSessionId();
        try {
            Directory.CreateDirectory(Config.Root.Path("cursor-subagent-links"));
            // One line only: TryLoadLink requires >= 2 with a non-empty first.
            File.WriteAllText(MarkerPath(child), "only-one-line\n");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task A_marker_with_an_empty_parent_id_also_fails_open() {
        var child = NewSessionId();
        try {
            Directory.CreateDirectory(Config.Root.Path("cursor-subagent-links"));
            File.WriteAllText(MarkerPath(child), "\ntask\n");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    /// <summary>
    /// The fail-closed half of the asymmetry, driven through the real dispatcher: a marker
    /// activates the divert, but with no ack every non-start hook must return at the
    /// HasSubagentStartAck gate — suppressing BOTH the raw event and the transcript backfill.
    /// Deleting that gate makes the transcript POST reappear and fails this test.
    /// </summary>
    [Test]
    public async Task A_marker_without_an_ack_suppresses_the_raw_event_and_the_transcript_backfill() {
        using var tmp = new TempDir();
        var child  = NewSessionId();
        var parent = NewSessionId();
        var childFile = tmp.PathTo($"{child}.jsonl");
        await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            CursorLiveSubagentLinker.SaveLink(Config.Root, child, parent, "task");
            // Deliberately NO MarkSubagentStartAcked.

            var routes = new List<string>();
            using var handler = new StubHandler((req, _) => {
                routes.Add(req.RequestUri!.AbsolutePath);
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"afterAgentThought","session_id":"{{child}}","generation_id":"g","text":"t","transcript_path":"{{childFile.Replace(@"\", @"\\")}}"}"""),
                spool);

            // Raw event suppressed...
            await Assert.That(routes).DoesNotContain("/hooks/agent-thought/cursor");
            // ...and so is the agent-routed transcript backfill. This is the assertion the gate
            // owns: without it the backfill runs even though SubagentStarted was never appended.
            await Assert.That(routes.Any(r => r.StartsWith("/hooks/transcript", StringComparison.Ordinal))).IsFalse();
            await Assert.That(spawned).IsEmpty();

            // Fail-closed AND SILENT: nothing is logged, surfaced or marked at the moment of the
            // loss. Recovery is `kcap import --cursor` plus the adoption sweep.
            await Assert.That(Markers.HasSubagentStartAck(child)).IsFalse();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            TryDeleteMarker(child);
        }
    }

    // ---------------------------------------------------------------------------------
    // KNOWN-BUG CHARACTERIZATION TESTS — NOT a contract.
    //
    // SaveLink swallows a write failure, and the caller has ALREADY assigned subagentParentId
    // before calling it, so the divert still runs. Both tests below drive the divert with the
    // marker write blocked, which is what the caller does in that state, and record the
    // resulting corrupt state: side effects land with NO marker on disk, so later invocations
    // miss TryLoadLink and route the same child top-level as well.
    //
    // The design spec (D2a) labels these states UNSUPPORTED and lists remedies; the leading one
    // is to have SaveLink report success and fail open BEFORE the start is posted. These two are
    // therefore EXCLUDED from the mutation rule that governs the pins above. When a remedy lands
    // they are EXPECTED to fail — rewrite or delete them then. Do not "fix" them to keep passing.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task Successful_start_with_a_failed_marker_write_leaves_ack_and_watcher_without_a_marker_known_risk() {
        using var tmp = new TempDir();
        var (parent, child, childPath) = SeedLinkedPair(tmp, "characterize the successful start");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        var blocker = MarkerPath(child);
        try {
            BlockMarkerWrite(blocker);

            var routes = new List<string>();
            using var handler = new StubHandler((req, _) => {
                routes.Add(req.RequestUri!.AbsolutePath);
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            // Drive the REAL CALLER, not the divert directly. That matters: the leading remedy
            // changes the caller (make SaveLink report success and fail open before the start is
            // posted), so a test that bypassed it by calling HandleSubagentChildEventAsync would
            // keep passing after the remedy landed — defeating the whole point of a
            // characterization test.
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"sessionStart","session_id":"{{child}}","transcript_path":"{{childPath.Replace(@"\", @"\\")}}"}"""),
                spool);

            // THE FINDING: the marker write failed, yet the start still went out, the ack was
            // persisted and the {parent}-{child} watcher spawned — with no marker tying them to
            // anything. Every later invocation misses TryLoadLink and routes this child
            // top-level while that watcher keeps feeding it under the parent.
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, child)).IsNull();
            await Assert.That(routes).Contains("/hooks/subagent-start");
            await Assert.That(Markers.HasSubagentStartAck(child)).IsTrue();
            await Assert.That(spawned).Contains($"{parent}-{child}");
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            UnblockMarkerWrite(blocker);
            TryDeleteMarker(child);
        }
    }

    [Test]
    public async Task Spooled_start_with_a_failed_marker_write_dual_routes_on_the_next_hook_known_risk() {
        using var tmp = new TempDir();
        var (parent, child, childPath) = SeedLinkedPair(tmp, "characterize the spooled start");

        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        var blocker = MarkerPath(child);
        try {
            BlockMarkerWrite(blocker);

            var startAttempts = 0;
            var routes = new List<string>();
            using var handler = new StubHandler((req, _) => {
                routes.Add(req.RequestUri!.AbsolutePath);
                if (req.RequestUri!.AbsolutePath == "/hooks/subagent-start") {
                    startAttempts++;
                    return startAttempts == 1
                        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) // spooled
                        : new HttpResponseMessage(HttpStatusCode.OK);                // drained
                }
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            // Again through the REAL CALLER — see the note in the test above.
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"sessionStart","session_id":"{{child}}","transcript_path":"{{childPath.Replace(@"\", @"\\")}}"}"""),
                spool);

            await Assert.That(spool.HasBacklog(child)).IsTrue();
            await Assert.That(spawned).IsEmpty();
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, child)).IsNull();

            // Next hook: the drain delivers the spooled start and spawns the agent-scoped
            // watcher, while this invocation — having missed TryLoadLink — also takes the
            // ordinary top-level route. THE FINDING: two watchers now tail the SAME transcript,
            // one under the parent and one as the child's own session.
            routes.Clear();
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"afterAgentResponse","session_id":"{{child}}","transcript_path":"{{childPath.Replace(@"\", @"\\")}}"}"""),
                spool);

            await Assert.That(spawned).Contains($"{parent}-{child}");   // under the parent
            await Assert.That(spawned).Contains(child);                 // ...and as its own session
            await Assert.That(routes).Contains("/hooks/agent-response/cursor");
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(Config.Root, child)).IsNull();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
            UnblockMarkerWrite(blocker);
            TryDeleteMarker(child);
        }
    }

    /// <summary>
    /// Builds a real Cursor <c>agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl</c> parent+child
    /// pair whose child's first user_query matches the parent's Task prompt, so the dispatcher's
    /// own classification arm resolves the link. Returns dashless ids (what the hook normalizes
    /// to) plus the child's transcript path.
    /// </summary>
    static (string Parent, string Child, string ChildPath) SeedLinkedPair(TempDir tmp, string prompt) {
        var root = tmp.CreateDir("agent-transcripts");

        var parentRaw = Guid.NewGuid().ToString();
        var childRaw  = Guid.NewGuid().ToString();

        var parentDir = root.PathTo(parentRaw);
        Directory.CreateDirectory(parentDir);
        var parentLine1 = """{"role":"user","message":{"content":[{"type":"text","text":"kick it off"}]}}""";
        var parentLine2 = System.Text.Json.JsonSerializer.Serialize(new {
            role = "assistant",
            message = new { content = new object[] { new { type = "tool_use", name = "Task", input = new { prompt } } } },
        });
        File.WriteAllText(Path.Combine(parentDir, parentRaw + ".jsonl"), parentLine1 + "\n" + parentLine2 + "\n");

        var childDir = root.PathTo(childRaw);
        Directory.CreateDirectory(childDir);
        var childPath = Path.Combine(childDir, childRaw + ".jsonl");
        var childLine = System.Text.Json.JsonSerializer.Serialize(new {
            role = "user",
            message = new { content = new object[] { new { type = "text", text = $"<user_query>\n{prompt}\n</user_query>" } } },
        });
        File.WriteAllText(childPath, childLine + "\n");

        return (parentRaw.Replace("-", ""), childRaw.Replace("-", ""), childPath);
    }

    /// <summary>
    /// Makes the marker write fail by putting a DIRECTORY where the marker FILE must go, so
    /// File.WriteAllLines throws and SaveLink swallows it — a failing write, not an absent root.
    /// </summary>
    static void BlockMarkerWrite(string markerPath) {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        Directory.CreateDirectory(markerPath);
    }

    static void UnblockMarkerWrite(string markerPath) {
        try { Directory.Delete(markerPath, true); } catch { /* best effort */ }
    }

    void TryDeleteMarker(string child) {
        try { File.Delete(MarkerPath(child)); } catch { /* best effort */ }
        try { File.Delete(Markers.SubagentStartAckPath(child)); } catch { /* best effort */ }
    }

    sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> impl) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return impl(request, body);
        }
    }
}
