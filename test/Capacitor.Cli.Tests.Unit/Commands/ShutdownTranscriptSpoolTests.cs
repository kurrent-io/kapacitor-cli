using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Task 8: at shutdown (idle-timeout / parent-exit) with the hub down, the final drain's
/// undelivered transcript tail (state.LinesProcessed → EOF) must not be silently dropped — it is
/// spooled into the dedicated <see cref="TranscriptSpool"/> so the global drain (task 3) replays it
/// after recovery, without a manual `kcap import`.
/// </summary>
public class ShutdownTranscriptSpoolTests {
    [TempHome] public required TempHome Home { get; init; }

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Test]
    public async Task build_batch_serializes_lines_and_vendor() {
        var json = WatchCommand.BuildTranscriptSpoolBatch(Sid, null, "kiro", ["{\"k\":1}"], [7]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(node["vendor"]!.GetValue<string>()).IsEqualTo("kiro");
        await Assert.That(node["line_numbers"]![0]!.GetValue<int>()).IsEqualTo(7);
        await Assert.That(node["lines"]![0]!.GetValue<string>()).IsEqualTo("{\"k\":1}");
    }

    [Test]
    public async Task build_batch_includes_agent_id_when_present() {
        var json = WatchCommand.BuildTranscriptSpoolBatch(Sid, "agent-1", "codex", ["{\"k\":1}"], [0]);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        await Assert.That(node["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
    }

    [Test]
    public async Task tail_spooled_when_hub_down_at_shutdown() {
        using var tmp = new TempDir();
        var dir = tmp.PathTo("shut");
        var tx    = new TranscriptSpool(dir);
        var batch = WatchCommand.BuildTranscriptSpoolBatch(Sid, null, "kiro", ["{\"k\":1}"], [0]);
        var r     = tx.Append(Sid, batch);
        await Assert.That(r).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(tx.HasBacklog(Sid)).IsTrue();
    }

    /// <summary>
    /// Full shutdown-during-outage path: a transcript file has lines beyond
    /// <c>state.LinesProcessed</c> (never confirmed sent because the hub was down), and
    /// <see cref="WatchCommand.SpoolUndeliveredTranscriptTailAsync"/> reads exactly that tail and
    /// spools it — never touching lines already confirmed delivered.
    /// </summary>
    [Test]
    public async Task shutdown_spools_only_the_undelivered_tail() {
        using var tmp = new TempDir();
        var spoolDir     = tmp.PathTo("shut-tail-spool");
        var transcriptPath = tmp.CreateDir("shut-tail").PathTo("transcript.jsonl");

        // Lines 0-1 already confirmed sent (LinesProcessed = 2); lines 2-3 are the undelivered
        // tail that the outage left behind at shutdown.
        await File.WriteAllTextAsync(transcriptPath,
            "{\"line\":0}\n{\"line\":1}\n{\"line\":2}\n{\"line\":3}\n");

        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 2, CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();

        // Replay via the same DrainAsync path the global drain (task 3) uses — only the
        // undelivered tail (lines 2 and 3) is replayed, in order, nothing from before LinesProcessed.
        var replayed = new List<string>();
        await spool.DrainAsync(Sid, body => {
            replayed.Add(body);
            return Task.FromResult(DrainOutcome.Delivered);
        }, () => false, CancellationToken.None);

        await Assert.That(replayed).Count().IsEqualTo(1);
        var node  = System.Text.Json.Nodes.JsonNode.Parse(replayed[0])!;
        var lines = node["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
        await Assert.That(lines).IsEquivalentTo(["{\"line\":2}", "{\"line\":3}"]);
        await Assert.That(spool.HasBacklog(Sid)).IsFalse();
    }

    [Test]
    public async Task shutdown_with_nothing_undelivered_is_a_noop() {
        using var tmp = new TempDir();
        var spoolDir        = tmp.PathTo("shut-empty-spool");
        var transcriptPath  = tmp.CreateDir("shut-empty").PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(transcriptPath, "{\"line\":0}\n{\"line\":1}\n");

        var spool  = new TranscriptSpool(spoolDir);
        // LinesProcessed already at EOF — the final drain sent everything before the hub went down.
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 2, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(spool.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>Cap exhaustion at shutdown must never silently drop the tail — it marks needs-import
    /// (surfaced by the global drain as a `kcap import` recovery hint) instead.</summary>
    [Test]
    public async Task shutdown_tail_exceeding_cap_marks_needs_import() {
        using var tmp = new TempDir();
        var spoolDir       = tmp.PathTo("shut-cap-spool");
        var transcriptPath = tmp.CreateDir("shut-cap").PathTo("transcript.jsonl");

        var bigLine = "{\"line\":0,\"pad\":\"" + new string('x', 200) + "\"}";
        await File.WriteAllTextAsync(transcriptPath, bigLine + "\n");

        var spool  = new TranscriptSpool(spoolDir, capBytes: 64); // tiny cap — the batch can't fit
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
        await Assert.That(spool.NeedsImport(Sid)).IsTrue();
    }

    /// <summary>
    /// a Cursor session already quarantined by the runtime rewrite guard
    /// must never have its tail re-read and spooled here — the bytes past `linesProcessed` ARE
    /// the exact corrupted batch the guard just discarded (the discard never advances
    /// LinesProcessed), so without this check the shutdown path would re-read and spool
    /// precisely what the guard existed to block, for a later drain to replay.
    /// </summary>
    [Test]
    public async Task shutdown_skips_spooling_when_the_cursor_session_is_quarantined() {
        var sid            = Guid.NewGuid().ToString("N");
        using var tmp = new TempDir();
        var spoolDir       = tmp.PathTo("shut-quarantine-spool");
        var transcriptPath = tmp.CreateDir("shut-quarantine").PathTo("transcript.jsonl");

        // Lines beyond `linesProcessed` exist on disk — exactly what a rejected/discarded
        // batch left behind when the guard tripped mid-poll.
        await File.WriteAllTextAsync(transcriptPath, "{\"line\":0}\n{\"line\":1}\n{\"line\":2}\n");
        Markers.Quarantine(sid, "rewrite detected");

        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, sid, agentId: null, vendor: "cursor", linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(spool.HasBacklog(sid)).IsFalse();
        await Assert.That(spool.NeedsImport(sid)).IsFalse();
    }

    /// <summary>Regression guard: the quarantine check is Cursor-scoped only — every other vendor's
    /// shutdown tail must still spool normally.</summary>
    [Test]
    public async Task shutdown_still_spools_for_non_cursor_vendors_when_a_cursor_marker_exists_for_a_different_session() {
        var quarantinedSid = Guid.NewGuid().ToString("N");
        Markers.Quarantine(quarantinedSid, "unrelated");

        using var tmp = new TempDir();
        var spoolDir       = tmp.PathTo("shut-nonCursor-spool");
        var transcriptPath = tmp.CreateDir("shut-nonCursor").PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(transcriptPath, "{\"line\":0}\n");

        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();
    }

    [Test]
    public async Task shutdown_missing_transcript_file_is_a_noop() {
        using var tmp = new TempDir();
        var spoolDir = tmp.PathTo("shut-missing-spool");
        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, "/tmp/kcap-nonexistent-" + Guid.NewGuid(), Sid, agentId: null, vendor: "kiro",
            linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    /// <summary>
    /// Review fix 1: the spool must fire whenever the tail is undelivered, NOT only when the hub is
    /// disconnected. A HubException thrown by SendTranscriptBatch2 leaves the connection Connected
    /// but the batch undelivered — so DrainNewLines does not advance state.LinesProcessed. The helper
    /// itself never consults connection state: it spools purely on "LinesProcessed &lt; EOF", which is
    /// exactly what that scenario produces. This test pins that state-agnostic behaviour so the
    /// unconditional call site can never regress back to a connection-state gate that drops the tail.
    /// </summary>
    [Test]
    public async Task undelivered_tail_spooled_even_though_connection_stayed_up() {
        using var tmp = new TempDir();
        var spoolDir       = tmp.PathTo("shut-hubex-spool");
        var transcriptPath = tmp.CreateDir("shut-hubex").PathTo("transcript.jsonl");

        // Send of line 0 threw a HubException (connection stayed up) → LinesProcessed still 0,
        // line 0 is the undelivered tail.
        await File.WriteAllTextAsync(transcriptPath, "{\"line\":0}\n");

        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();
    }

    /// <summary>
    /// Review fix 2 (SECURITY): the spooled tail must be secret-redacted exactly like the live drain
    /// (DrainNewLines → SecretRedactor.RedactLine). Otherwise a secret in an undelivered line lands
    /// on disk raw and is POSTed unredacted on replay. A GitHub `ghp_` token must be replaced with
    /// [REDACTED] in the spooled batch, and the raw token must appear nowhere on disk.
    /// </summary>
    [Test]
    public async Task spooled_tail_is_secret_redacted() {
        using var tmp = new TempDir();
        var spoolDir       = tmp.PathTo("shut-redact-spool");
        var transcriptPath = tmp.CreateDir("shut-redact").PathTo("transcript.jsonl");
        const string secret = "ghp_0123456789abcdefABCDEF";

        await File.WriteAllTextAsync(transcriptPath, "{\"token\":\"" + secret + "\"}\n");

        var spool  = new TranscriptSpool(spoolDir);
        var result = await Watch.SpoolUndeliveredTranscriptTailAsync(
            spool, transcriptPath, Sid, agentId: null, vendor: "kiro", linesProcessed: 0, CancellationToken.None);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.Appended);

        // The raw secret must appear nowhere in the spool directory; the placeholder must.
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var content = await File.ReadAllTextAsync(f);
            await Assert.That(content).DoesNotContain(secret);
        }

        var replayed = new List<string>();
        await spool.DrainAsync(Sid, body => { replayed.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
                               () => false, CancellationToken.None);
        var node  = System.Text.Json.Nodes.JsonNode.Parse(replayed[0])!;
        var lines = node["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToList();
        await Assert.That(lines[0]).Contains("[REDACTED]");
        await Assert.That(lines[0]).DoesNotContain(secret);
    }
}
