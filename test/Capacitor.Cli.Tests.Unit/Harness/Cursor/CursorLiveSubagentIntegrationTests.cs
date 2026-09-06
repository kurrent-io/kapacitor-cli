using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// Coverage for the marker-driven Cursor subagent divert. CursorLiveSubagentLinkerTests covers
/// the pure ResolveParent/marker/discovery pieces in isolation; these exercise the hook
/// dispatcher wiring against a realistic on-disk
/// <c>agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl</c> layout.
///
/// <para>
/// Scenarios here seed the link marker (and ack) DIRECTLY. A test may depend on that
/// persistent state, but must never assert that a CHILD LIFECYCLE HOOK produces it: a real
/// Cursor subagent child fires neither sessionStart nor sessionEnd, so four tests that
/// asserted those triggers (child sessionStart → subagent-start, child sessionEnd →
/// subagent-stop, the transcript-routing variant, and the stop-ordering guard) were removed
/// rather than relabelled — keeping them would have preserved an obsolete contract and the
/// false confidence that came with it. A native revival must be driven by the PARENT's
/// subagentStart/subagentStop, so the stop-ordering invariant is deferred until that design
/// defines how subagentStop is keyed and spooled. See
/// docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
/// </para>
/// </summary>
// The hook resolves the harness nudge through HarnessPaths, so it reads every vendor override
// variable a peer suite clears mid-test.
[NotInParallel("VendorEnvOverrides")]
public class CursorLiveSubagentIntegrationTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task linked_child_mid_lifecycle_hook_is_suppressed_but_transcript_still_backfills() {
        using var fx = new Fixture(Config.Root);
        var (parentId, childId, childPath) = fx.SetupLinkedPair("write the report");

        // Seed the link and the ack DIRECTLY. Establishing them via a child sessionStart would
        // assert a trigger a real child never fires (see the class doc). The ack seed is
        // required: without it the mid-lifecycle hook returns at the no-ack gate and never
        // reaches the backfill this test is about.
        CursorLiveSubagentLinker.SaveLink(Config.Root, childId, parentId, "task");
        new CursorMarkers(Config.Root).MarkSubagentStartAcked(childId);
        fx.Sent.Clear();
        fx.RouteOrder.Clear();

        // A later mid-lifecycle hook for the SAME child must not forward the raw event...
        await fx.HandleAsync(childId, "afterAgentThought", childPath, extraFields: ",\"generation_id\":\"g1\",\"text\":\"thinking\"");

        await Assert.That(fx.RouteOrder).DoesNotContain("agent-thought/cursor");
        // ...but the transcript watermark is still (re)checked under the parent + agent_id.
        await Assert.That(fx.RouteOrder).Contains("transcript");
        var batch = JsonNode.Parse(fx.SentToHook("transcript"))!;
        await Assert.That(batch["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(batch["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
    }

    [Test]
    public async Task unlinked_session_still_posts_top_level_session_start() {
        // Regression guard: an ordinary (non-subagent) session must behave exactly as before —
        // no sibling transcript happens to match, so ResolveParent returns null and the normal
        // top-level flow runs unmodified.
        using var fx = new Fixture(Config.Root);
        var soloId  = Guid.NewGuid().ToString();
        var soloDir = Path.Combine(fx.TranscriptsRoot, soloId);
        Directory.CreateDirectory(soloDir);
        var soloPath = Path.Combine(soloDir, soloId + ".jsonl");
        File.WriteAllText(soloPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"hello, nothing to correlate"}]}}""" + "\n");

        await fx.HandleAsync(soloId, "sessionStart", soloPath);

        await Assert.That(fx.RouteOrder).Contains("session-start/cursor");
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-start");
    }

    /// <summary>
    /// the agent_id the LIVE path uses (child session id, dashless) must
    /// be byte-identical to what the IMPORT path (CursorImportSource.SendSubagentLifecycleAsync)
    /// would use for the same child — otherwise a live-then-import of the same session would
    /// duplicate the subagent's AgentSubsession stream instead of converging on it.
    /// </summary>
    [Test]
    public async Task live_agent_id_matches_the_dashless_id_the_import_path_would_use() {
        // Exercises the payload seam DIRECTLY. This previously drove a child sessionStart and
        // asserted a subagent-start came back — the exact child-lifecycle trigger this suite no
        // longer asserts (see the class doc). The parity property lives in the builder, not in
        // how the builder happens to be reached, so nothing is lost by testing it here.
        var childRaw = Guid.NewGuid().ToString();  // dashed, as Cursor emits
        var childId  = CursorImportSource.NormalizeCursorSessionId(childRaw);
        var parentId = CursorImportSource.NormalizeCursorSessionId(Guid.NewGuid().ToString());

        var body = CursorLiveSubagentLinker.BuildSubagentStartPayload(
            parentId, childId, "task", "/tmp/parity.jsonl");

        var liveAgentId = body["agent_id"]!.GetValue<string>();
        // Mirrors CursorImportSource.NormalizeCursorSessionId, the import path's own
        // dashless-id convention (CursorImportSource.cs:91,468).
        await Assert.That(liveAgentId).IsEqualTo(childId);
        await Assert.That(liveAgentId.Contains('-')).IsFalse();
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
    }

    sealed class Fixture : IDisposable {
        readonly TempHome _home = new();

        public string TranscriptsRoot { get; }
        public string SpoolDir        { get; }
        public List<string> Sent       { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool    Spool      { get; }
        public ConfigRoot   Config     { get; }
        public HttpClient   Client     { get; }
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;

        readonly List<string> _markersToClean = [];

        public Fixture(ConfigRoot config, HttpStatusCode postStatus = HttpStatusCode.OK) {
            PostStatus      = postStatus;
            TranscriptsRoot = _home.CreateDir("agent-transcripts");
            SpoolDir        = _home.PathTo("spool");
            Spool           = new HookSpool(SpoolDir);
            Config          = config;

            var handler = new StubHandler(async req => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/", StringComparison.Ordinal)) RouteOrder.Add(path.Replace("/hooks/", ""));

                // Watermark GET — always 404 so the backfill always resumes from 0 and posts
                // whatever's on disk right now.
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(PostStatus);
            });
            Client = new HttpClient(handler);
        }

        /// <summary>
        /// Builds a realistic Cursor `agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl` parent +
        /// child pair whose child's first user_query matches the parent's Task prompt — the
        /// exact shape CursorSubagentCorrelator/CursorLiveSubagentLinker key off of. Session ids
        /// are dashed (mirroring real Cursor ids); the returned ids are dashless (post-normalize,
        /// matching what CursorHookCommand's own NormalizeGuidField produces).
        /// </summary>
        public (string ParentId, string ChildId, string ChildPath) SetupLinkedPair(string prompt) {
            var parentRaw = Guid.NewGuid().ToString();
            var childRaw  = Guid.NewGuid().ToString();

            var parentDir = Path.Combine(TranscriptsRoot, parentRaw);
            Directory.CreateDirectory(parentDir);
            var parentLine1 = """{"role":"user","message":{"content":[{"type":"text","text":"kick it off"}]}}""";
            var parentLine2 = System.Text.Json.JsonSerializer.Serialize(new {
                role = "assistant",
                message = new { content = new object[] { new { type = "tool_use", name = "Task", input = new { prompt } } } },
            });
            File.WriteAllText(Path.Combine(parentDir, parentRaw + ".jsonl"), parentLine1 + "\n" + parentLine2 + "\n");

            var childDir = Path.Combine(TranscriptsRoot, childRaw);
            Directory.CreateDirectory(childDir);
            var childPath  = Path.Combine(childDir, childRaw + ".jsonl");
            var childLine1 = System.Text.Json.JsonSerializer.Serialize(new {
                role = "user",
                message = new { content = new object[] { new { type = "text", text = $"<user_query>\n{prompt}\n</user_query>" } } },
            });
            File.WriteAllText(childPath, childLine1 + "\n");

            _markersToClean.Add(childRaw.Replace("-", ""));

            return (parentRaw.Replace("-", ""), childRaw.Replace("-", ""), childPath);
        }

        public Task<int> HandleAsync(string sessionId, string eventName, string? transcriptPath, string extraFields = "") =>
            new CursorHookCommand(Config, Resolutions.At("http://localhost", Config), new HookClock(TimeProvider.System), _home, new FixedCapacitorHttpClient()).HandleCore(
                Client,
                stdin: new StringReader(
                    $$"""{"hook_event_name":"{{eventName}}","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath?.Replace(@"\", @"\\")}}"{{extraFields}}}"""
                ),
                spool: Spool
            );

        public string SentToHook(string segment) =>
            Sent.Last(s => s.StartsWith($"/hooks/{segment}", StringComparison.Ordinal)).Split('|', 2)[1];

        public void Dispose() {
            Client.Dispose();
            foreach (var m in _markersToClean) {
                try { File.Delete(Path.Combine(Config.Path("cursor-subagent-links"), m)); } catch { }
                // The subagent-start ACK marker lives in a different directory and is durable
                // too. A test that seeds one (see the mid-lifecycle scenario) would otherwise
                // leave it behind for the rest of the process, where a later
                // HasSubagentStartAck check could read it.
                try { File.Delete(new CursorMarkers(Config).SubagentStartAckPath(m)); } catch { }
            }
            _home.Dispose();
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }
}
