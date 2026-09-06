using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// a reconnect that discovers the server's acknowledged line frontier is
/// behind the watcher's own <see cref="WatchState.LinesProcessed"/> must rewind the Cursor byte
/// guard's checkpoint ATOMICALLY with the line cursor. Before the fix, only
/// <see cref="WatchState.LinesProcessed"/> rewound — <see cref="WatchState.CursorByteOffset"/> and
/// the guard's own checkpoint stayed at the LATER acked offset, so the next drain resent the
/// replayed line gap but started new-range verification past the bytes that gap actually
/// occupies. <see cref="WatchCommand.ApplyReconnectRewindAsync"/> is the extracted, directly
/// testable version of the fix (pulled out of <c>RunWatch</c>'s <c>Reconnected</c> handler, which
/// itself can't be driven without a live SignalR reconnect).
/// </summary>
public class CursorReconnectRewindTests {
    [TempHome] public required TempHome Home { get; init; }

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    // ---- WatchCommand.ResolveByteOffsetForLineAsync ----

    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_zero_for_line_zero_or_negative() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3\n");

        // the method now returns a (ByteOffset, LineNumber) pair —
        // trivial for a non-positive request, LineNumber just carries the input through.
        var zero = await WatchCommand.ResolveByteOffsetForLineAsync(path, 0, CancellationToken.None);
        await Assert.That(zero!.Value.ByteOffset).IsEqualTo(0L);
        await Assert.That(zero!.Value.LineNumber).IsEqualTo(0);

        var negative = await WatchCommand.ResolveByteOffsetForLineAsync(path, -1, CancellationToken.None);
        await Assert.That(negative!.Value.ByteOffset).IsEqualTo(0L);
        await Assert.That(negative!.Value.LineNumber).IsEqualTo(-1);
    }

    // a missing file with a POSITIVE requested line number is
    // exactly "fewer lines on disk than requested" (zero, in this case) — the caller must quarantine
    // rather than clamp to a byte offset of 0, which used to look like a perfectly valid (if empty)
    // resume baseline.
    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_null_when_the_file_is_missing_and_a_line_is_requested() {
        using var missingDir = TempDir.WithPathTo("kcap-reconnect-missing.jsonl", out var missing);
        await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(missing, 2, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ResolveByteOffsetForLineAsync_maps_a_mid_file_line_number_to_its_true_byte_offset() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        // Deliberately uneven line lengths so a naive "count * avg-length" guess would be wrong.
        await File.WriteAllTextAsync(path, "a\nbbbb\ncc\n"); // offsets: line0 ends at 2, line1 ends at 7, line2 ends at 10

        // unchanged mid-file case — the returned pair's LineNumber is
        // always the requested lineNumber unchanged; only the r5 unterminated-final-record case
        // (covered below) rewinds it.
        var r1 = await WatchCommand.ResolveByteOffsetForLineAsync(path, 1, CancellationToken.None);
        await Assert.That(r1!.Value.ByteOffset).IsEqualTo(2L);
        await Assert.That(r1!.Value.LineNumber).IsEqualTo(1);

        var r2 = await WatchCommand.ResolveByteOffsetForLineAsync(path, 2, CancellationToken.None);
        await Assert.That(r2!.Value.ByteOffset).IsEqualTo(7L);
        await Assert.That(r2!.Value.LineNumber).IsEqualTo(2);

        var r3 = await WatchCommand.ResolveByteOffsetForLineAsync(path, 3, CancellationToken.None);
        await Assert.That(r3!.Value.ByteOffset).IsEqualTo(10L);
        await Assert.That(r3!.Value.LineNumber).IsEqualTo(3);
    }

    // the pre-fix behaviour silently clamped to EOF here; this
    // is exactly the truncated-transcript scenario the fix closes, so the method must now signal
    // "cannot resolve exactly" (null) instead of handing back a bogus baseline offset.
    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_null_when_the_file_has_fewer_lines_than_requested() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, "a\nb\n"); // only 2 lines

        await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 5, CancellationToken.None)).IsNull();
    }

    // a COMPLETE final record with no trailing newline yet must resolve,
    // not quarantine. This is exactly what the watcher's own shutdown final drain
    // (IncompleteFinalLinePolicy.ConsumeIfComplete) and historical import's StreamReader-based
    // splitting both send as the last line of a file. Before this fix, the round-4 guard treated
    // "fewer newline-terminated lines than requested" as an unconditional shortfall, so a later
    // watcher restart resuming at that same final line (server frontier == local line count) would
    // permanently quarantine an otherwise-healthy, append-only session.
    //
    // the r5 fix originally resolved this case at EOF, unchanged
    // LineNumber. That seeded CursorByteOffset exactly where Cursor's OWN later-arriving
    // terminator for this SAME already-acked record lands; when it arrived, the bounded reader
    // (seeding its own line index from LinesProcessed, left at the record's line number) misread
    // the terminator as closing a phantom empty line, permanently shifting every following line's
    // number by one. The fix REWINDS instead: the pair now points at the record's own START
    // (identical to line 1's END below) with LineNumber one less — the record is re-read/re-sent
    // next poll, harmlessly deduped by the server's source-ack frontier.
    [Test]
    public async Task ResolveByteOffsetForLineAsync_rewinds_a_complete_unterminated_final_line_to_its_start() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        const string content = "{\"a\":1}\n{\"b\":2}"; // 1 newline-terminated line + 1 complete, unterminated final record
        await File.WriteAllTextAsync(path, content);

        // Line 1 (newline-terminated) resolves exactly as before.
        var r1 = await WatchCommand.ResolveByteOffsetForLineAsync(path, 1, CancellationToken.None);
        await Assert.That(r1!.Value.ByteOffset).IsEqualTo(8L); // "{\"a\":1}\n".Length
        await Assert.That(r1!.Value.LineNumber).IsEqualTo(1);

        // Line 2 — the complete, unterminated final record — rewinds to the record's own start
        // (same byte offset as line 1's end, NOT EOF) paired with LineNumber - 1 (NOT counted
        // as processed yet).
        var r2 = await WatchCommand.ResolveByteOffsetForLineAsync(path, 2, CancellationToken.None);
        await Assert.That(r2!.Value.ByteOffset).IsEqualTo(8L);
        await Assert.That(r2!.Value.LineNumber).IsEqualTo(1);
    }

    // the completeness gate matters: an unterminated tail that ISN'T a
    // parseable JSON record (still being written, or genuinely truncated mid-record) must still
    // quarantine — only a demonstrably COMPLETE trailing record is allowed to resolve at EOF.
    [Test]
    public async Task ResolveByteOffsetForLineAsync_returns_null_when_the_trailing_unterminated_content_is_incomplete_json() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":2"); // final record missing its closing brace

        await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 2, CancellationToken.None)).IsNull();
    }

    // review fix (r4, finding #5), preserved under r5 — a request for a line further beyond
    // the local file than "the final unterminated record" is a genuine shortfall (truncation/rewrite)
    // and must still quarantine, never resolve. (No trailing content at all here — both requested
    // lines are already accounted for by the two newlines, and line 5 is nowhere close.)
    [Test]
    public async Task ResolveByteOffsetForLineAsync_still_returns_null_when_the_request_is_genuinely_beyond_eof() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, "a\nb\n"); // exactly 2 complete, terminated lines, no trailing content

        await Assert.That(await WatchCommand.ResolveByteOffsetForLineAsync(path, 5, CancellationToken.None)).IsNull();
    }

    // ---- CursorRewriteGuard.ResetCheckpoint ----

    [Test]
    public async Task ResetCheckpoint_clears_a_prior_checkpoint_so_the_next_prior_zone_check_passes_trivially() {
        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());
        guard.Checkpoint(offset: 100, trailingSha: "some-stale-hash");

        // Before reset: an unrelated hash mismatches the stale checkpoint.
        await Assert.That(guard.VerifyPriorZone("different-hash")).IsFalse();
    }

    [Test]
    public async Task ResetCheckpoint_after_reset_any_hash_passes_like_a_fresh_watcher() {
        var sid   = Guid.NewGuid().ToString("N");
        var guard = new CursorRewriteGuard(Config.Root, sid);
        guard.Checkpoint(offset: 100, trailingSha: "some-stale-hash");

        guard.ResetCheckpoint();

        // No checkpoint recorded → trivially true, exactly like a brand-new guard's first poll.
        await Assert.That(guard.VerifyPriorZone("anything-at-all")).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // ---- review fix (r3, finding #2) — WatchCommand.SeedCursorByteOffsetAsync ----
    //
    // Shared by ApplyReconnectRewindAsync (already covered above) and RunWatch's INITIAL
    // WatcherConnect registration, which — before this fix — left CursorByteOffset at its default
    // (0) after resuming at server line N: the ack-to-byte mapping then measured acked lines
    // relative to N but counted their bytes from 0, so a full ack of M resumed lines checkpointed
    // at file offset M instead of N's TRUE offset plus M.

    [Test]
    public async Task SeedCursorByteOffsetAsync_seeds_the_true_byte_offset_of_the_resumed_line() {
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        // Deliberately uneven line lengths: offsets 2, 7, 10, 15, 21.
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\neeeee\n");

        var sid   = NewSessionId();
        var guard = new CursorRewriteGuard(Config.Root, sid);
        // A fresh watcher process resuming at server line 2 — CursorByteOffset starts at its
        // default (0), exactly as WatchState leaves it before this fix's seeding runs.
        var state = new WatchState { LinesProcessed = 2 };
        await Assert.That(state.CursorByteOffset).IsEqualTo(0L);

        var ok = await Watch.SeedCursorByteOffsetAsync(
            state, lineNumber: 2, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

        await Assert.That(ok).IsTrue();
        // The TRUE byte offset of line 2 ("a\nbbbb\n" = 7 bytes) — not the default 0, and not
        // the resumed line COUNT (2) either.
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L);
        // The checkpoint is reset so the guard's two-zone checks start clean from here.
        await Assert.That(guard.VerifyPriorZone("anything-at-all")).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // reproduces the actual failure scenario: a final drain
    // (IncompleteFinalLinePolicy.ConsumeIfComplete) sends a complete but unterminated final record,
    // the watcher exits (idle), and a LATER hook restarts the watcher with the server resuming it at
    // that exact same final line (server frontier == local line count, file STILL lacks the trailing
    // newline). SeedCursorByteOffsetAsync must seed a valid byte offset and NOT quarantine.
    //
    // reproduces the P1 finding this seeds against: the r5 fix seeded
    // CursorByteOffset at EOF while leaving LinesProcessed at the (already-acked) final record's own
    // line number. That put the byte cursor exactly where Cursor's OWN later-arriving terminator
    // for this SAME record lands; when it arrived, the bounded reader — seeding its line index from
    // LinesProcessed — misread the terminator as a phantom empty line and shifted every subsequent
    // line's number by one, permanently. The fix REWINDS: CursorByteOffset seeds to the record's own
    // START (byte 8, right after line 1's newline) and LinesProcessed rewinds to 1 (the record is
    // NOT yet counted as processed — it will be re-read/re-sent next poll, harmlessly deduped by the
    // server's source-ack frontier).
    [Test]
    public async Task SeedCursorByteOffsetAsync_rewinds_a_final_drains_complete_unterminated_line_instead_of_seeding_at_eof() {
        using var tmp = new TempDir();
        var sid = NewSessionId();
        try {
            var transcriptPath = tmp.PathTo("t.jsonl");
            const string content = "{\"a\":1}\n{\"b\":2}"; // line 1 terminated, line 2 complete but no trailing '\n'
            await File.WriteAllTextAsync(transcriptPath, content);

            var guard = new CursorRewriteGuard(Config.Root, sid);
            // Resuming exactly at line 2 — the final, unterminated-but-complete record the prior
            // watcher's shutdown drain already sent and the server already acknowledged.
            var state = new WatchState { LinesProcessed = 2 };

            var ok = await Watch.SeedCursorByteOffsetAsync(
                state, lineNumber: 2, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            await Assert.That(ok).IsTrue();
            await Assert.That(state.CursorByteOffset).IsEqualTo(8L); // rewound to the record's own start, NOT EOF (15)
            await Assert.That(state.LinesProcessed).IsEqualTo(1);    // rewound — record not yet counted as processed
            await Assert.That(guard.VerifyPriorZone("anything-at-all")).IsTrue();
            await Assert.That(Markers.IsQuarantined(sid)).IsFalse(); // NOT quarantined
        } finally {
            try { File.Delete(Markers.QuarantinePath(sid)); } catch { }
        }
    }

    [Test]
    public async Task SeedCursorByteOffsetAsync_is_a_byte_only_no_op_for_non_cursor_vendors() {
        // SeedCursorByteOffsetAsync now sets state.LinesProcessed for
        // EVERY vendor (previously only its callers did) — the "no-op" is byte-side only.
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\n");

        var state = new WatchState { LinesProcessed = 0, CursorByteOffset = 999 };
        var ok = await Watch.SeedCursorByteOffsetAsync(
            state, lineNumber: 2, NewSessionId(), vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);

        await Assert.That(ok).IsTrue();
        await Assert.That(state.CursorByteOffset).IsEqualTo(999L); // untouched — no guard to keep in sync
        await Assert.That(state.LinesProcessed).IsEqualTo(2);      // still set to lineNumber
    }

    [Test]
    public async Task InitialResume_seeded_offset_composed_with_ByteOffsetForAckedLines_checkpoints_at_N_plus_M_not_M() {
        // End-to-end regression for the actual production consequence, composed from the two pure
        // functions DrainNewLines itself calls (no live hub needed — see the class doc on why a
        // real ack round trip isn't unit-testable here).
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\neeeee\n"); // 5 lines, offsets 2,7,10,15,21

        var guard = new CursorRewriteGuard(Config.Root, NewSessionId());
        // The server resumed this fresh watcher process at line N=2 (0-based frontier already
        // sent/acked by a PRIOR watcher instance).
        var state = new WatchState { LinesProcessed = 2 };

        await Watch.SeedCursorByteOffsetAsync(
            state, lineNumber: 2, NewSessionId(), vendor: "cursor", transcriptPath, guard, CancellationToken.None);
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L);

        // The watcher's next poll fully acks the M=3 remaining lines (2, 3, 4) — mirrors
        // DrainNewLines' own ByteOffsetForAckedLines(verifiedRange, cursorGuardOldOffset,
        // ackedLineCount) call, with rangeStartOffset seeded from the SAME offset above.
        var remainingRange  = System.Text.Encoding.UTF8.GetBytes("cc\ndddd\neeeee\n"); // lines 2,3,4 — 14 bytes
        var ackedByteOffset = WatchCommand.ByteOffsetForAckedLines(
            remainingRange, rangeStartOffset: state.CursorByteOffset, ackedLineCount: 3);

        // N's offset (7) + M's bytes (14) = 21 — the TRUE end-of-file offset. The pre-fix bug
        // (CursorByteOffset left at 0, so the ack maps M's bytes as if counted from byte 0)
        // would have produced 14 instead.
        await Assert.That(ackedByteOffset).IsEqualTo(21L);
        await Assert.That(ackedByteOffset).IsNotEqualTo(14L);
    }

    // the server-acknowledged resume frontier can legitimately
    // exceed the local transcript's line count (truncated/replaced while the watcher was offline).
    // SeedCursorByteOffsetAsync must quarantine and refuse to seed rather than clamp to EOF.
    [Test]
    public async Task SeedCursorByteOffsetAsync_quarantines_instead_of_seeding_when_the_server_frontier_exceeds_local_lines() {
        using var tmp = new TempDir();
        var sid = NewSessionId();
        try {
            var transcriptPath = tmp.PathTo("t.jsonl");
            await File.WriteAllTextAsync(transcriptPath, "a\nb\n"); // only 2 lines locally

            var guard = new CursorRewriteGuard(Config.Root, sid);
            var state = new WatchState { LinesProcessed = 0, CursorByteOffset = 0 };

            // Server claims line 5 was already acknowledged — beyond what this (truncated) local
            // file can produce.
            var ok = await Watch.SeedCursorByteOffsetAsync(
                state, lineNumber: 5, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

            await Assert.That(ok).IsFalse();
            await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
            // Neither the byte offset nor the guard's checkpoint were touched — no bogus baseline.
            await Assert.That(state.CursorByteOffset).IsEqualTo(0L);
        } finally {
            try { File.Delete(Markers.QuarantinePath(sid)); } catch { }
        }
    }

    // ---- WatchCommand.ApplyReconnectRewindAsync — the atomic composition ----

    [Test]
    public async Task ApplyReconnectRewindAsync_rewinds_the_byte_checkpoint_to_the_true_offset_of_the_server_frontier() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines

        var guard = new CursorRewriteGuard(Config.Root, sid);
        // Simulate: the watcher had sent/acked all 4 lines and checkpointed at the file's
        // full (stale, too-far-ahead) length.
        guard.Checkpoint(offset: 15, trailingSha: "later-acked-hash");
        var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

        // Reconnect discovers the server only actually has the first 2 lines (offset 7).
        var ok = await Watch.ApplyReconnectRewindAsync(state, serverPosition: 2, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

        await Assert.That(ok).IsTrue();
        await Assert.That(state.LinesProcessed).IsEqualTo(2);
        // The byte checkpoint must rewind to the TRUE byte offset of line 2 ("a\nbbbb\n" = 7
        // bytes) — not stay at the later, stale, too-far-ahead offset (15).
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L);
        // The guard's own checkpoint must be reset too — a stale "later-acked-hash" checkpoint
        // must never be compared against post-rewind content.
        await Assert.That(guard.VerifyPriorZone("literally-anything")).IsTrue();
    }

    [Test]
    public async Task ApplyReconnectRewindAsync_is_a_line_only_rewind_for_non_cursor_vendors() {
        // No CursorRewriteGuard exists for any other vendor — only the line cursor rewinds.
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n");

        var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 999 }; // never meaningful for non-cursor

        var ok = await Watch.ApplyReconnectRewindAsync(state, serverPosition: 1, NewSessionId(), vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);

        await Assert.That(ok).IsTrue();
        await Assert.That(state.LinesProcessed).IsEqualTo(1);
        await Assert.That(state.CursorByteOffset).IsEqualTo(999L); // untouched — no guard to keep in sync
    }

    [Test]
    public async Task ApplyReconnectRewindAsync_keeps_the_replayed_region_under_prior_zone_protection_after_a_later_checkpoint() {
        // End-to-end regression for the actual production consequence: BEFORE the fix, the guard's
        // checkpoint stayed at the LATER (stale, too-far-ahead) offset after a rewind, so a FUTURE
        // checkpoint re-established post-rewind would still only ever prior-zone-protect the
        // trailing bytes ending at whatever offset the LATEST send reached — the replayed gap
        // itself (bytes 7..10 below) would never again fall inside that trailing window once the
        // file grows past it, becoming a permanent blind spot. With the fix, the checkpoint
        // correctly rewinds to the TRUE (small) offset, so a checkpoint re-established from there
        // still covers the replayed region's own trailing bytes — and a same-length in-place
        // rewrite of it is caught on the very next poll.
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines: offsets 2,7,10,15

        // A small TrailingBytes makes the "which region does the checkpoint actually
        // protect" distinction concrete without needing a large synthetic file.
        var guard = new CursorRewriteGuard(Config.Root, sid) { TrailingBytes = 3 };
        guard.Checkpoint(offset: 15, trailingSha: "stale-hash-from-before-reconnect"); // the bug: too-far-ahead
        var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

        // Reconnect: the server only actually has the first 2 lines (byte offset 7).
        await Watch.ApplyReconnectRewindAsync(state, serverPosition: 2, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L);

        // The replayed line ("cc\n", offset 7..10) is resent and acked — a checkpoint is
        // re-established from the CORRECT rewound frontier (mirrors DrainNewLines's own
        // Checkpoint() call after a successful ack).
        state.LinesProcessed   = 3;
        state.CursorByteOffset = 10;
        guard.Checkpoint(offset: 10, trailingSha: CursorAppendOnlyProbe.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("cc\n")));

        // Now the replayed region itself is rewritten in place (same length: "cc" → "XX").
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\nXX\ndddd\n");

        var tripped = false;
        await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        await Assert.That(result).IsEmpty();
    }

    // ---- review fix (r3, finding #1) — GatedApplyReconnectRewindAsync/GatedDrainNewLinesAsync ----
    //
    // RunWatch's own cursorRewindGate/Reconnected handler can't be driven without a live SignalR
    // reconnect (same constraint CursorGuardWiringTests' class doc states), so these tests drive
    // the extracted gate-composition helpers directly — the SAME SemaphoreSlim(1, 1) instance
    // RunWatch constructs and passes to both call sites. A SemaphoreSlim(1, 1) enforces exclusivity
    // unconditionally, so rather than chase a timing-dependent race, these tests prove the
    // EXCLUSION property deterministically: while one gated operation holds the gate, the other
    // cannot even START running (its Task stays incomplete), so it is architecturally impossible
    // for a drain to observe a half-applied rewind (or vice versa).

    [Test]
    public async Task GatedDrainNewLinesAsync_cannot_run_while_the_gate_is_held_by_a_reconnect_rewind() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines

        var gate  = new SemaphoreSlim(1, 1);
        var guard = new CursorRewriteGuard(Config.Root, sid);
        var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

        // Simulate an in-flight reconnect rewind: acquire the gate ourselves (standing in for
        // GatedApplyReconnectRewindAsync mid-ResolveByteOffsetForLineAsync, BEFORE it has
        // applied any of its three writes).
        await gate.WaitAsync();

        await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

        var drainTask = Watch.GatedDrainNewLinesAsync(
            gate, hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        // Give the (incorrectly ungated) case every chance to complete — it must NOT, because
        // the gate is still held.
        await Task.Delay(50);
        await Assert.That(drainTask.IsCompleted).IsFalse();

        // A half-applied rewind is architecturally impossible here: DrainNewLines has not
        // even started reading cursorGuardOldOffset/priorLineCursorForGuard yet, let alone
        // written a checkpoint derived from stale pre-rewind numbers.
        await Assert.That(state.CursorByteOffset).IsEqualTo(15L);

        gate.Release(); // the "rewind" completes — now the queued drain may proceed
        await drainTask;
    }

    [Test]
    public async Task GatedApplyReconnectRewindAsync_cannot_run_while_the_gate_is_held_by_a_drain() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nbbbb\ncc\ndddd\n"); // 4 lines: offsets 2,7,10,15

        var gate  = new SemaphoreSlim(1, 1);
        var guard = new CursorRewriteGuard(Config.Root, sid);
        guard.Checkpoint(offset: 15, trailingSha: "acked-hash");
        var state = new WatchState { LinesProcessed = 4, CursorByteOffset = 15 };

        // Simulate an in-flight drain (standing in for GatedDrainNewLinesAsync mid-ack).
        await gate.WaitAsync();

        var rewindTask = Watch.GatedApplyReconnectRewindAsync(
            gate, state, serverPosition: 2, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

        await Task.Delay(50);
        await Assert.That(rewindTask.IsCompleted).IsFalse();

        // The rewind has not been allowed to touch state yet — no half-applied rewind for a
        // concurrently-finishing drain to observe (or clobber).
        await Assert.That(state.LinesProcessed).IsEqualTo(4);
        await Assert.That(state.CursorByteOffset).IsEqualTo(15L);

        gate.Release(); // the "drain" completes — now the queued rewind may proceed
        var rewound = await rewindTask;

        await Assert.That(rewound).IsTrue();
        await Assert.That(state.LinesProcessed).IsEqualTo(2);
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L); // true byte offset of line 2
    }

    [Test]
    public async Task GatedDrainNewLinesAsync_and_GatedApplyReconnectRewindAsync_are_no_ops_when_gate_is_null() {
        // Every non-Cursor vendor passes a null gate — both helpers must fall back to calling the
        // underlying method directly rather than deadlocking or throwing on a null semaphore.
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nb\n");

        var state = new WatchState { LinesProcessed = 2, CursorByteOffset = 999 };
        var rewound = await Watch.GatedApplyReconnectRewindAsync(
            gate: null, state, serverPosition: 1, NewSessionId(), vendor: "codex", transcriptPath, cursorGuard: null, CancellationToken.None);
        await Assert.That(rewound).IsTrue();
        await Assert.That(state.LinesProcessed).IsEqualTo(1);

        await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();
        var result = await Watch.GatedDrainNewLinesAsync(
            gate: null, hub, "sid", transcriptPath, agentId: null, new WatchState { ThresholdReached = true },
            vendor: "codex", CancellationToken.None);
        await Assert.That(result).IsNotNull();
    }

    // ---- review fix (r4, finding #5) — GatedApplyReconnectRewindAsync propagates a refused rewind ----

    [Test]
    public async Task GatedApplyReconnectRewindAsync_returns_false_and_leaves_state_untouched_when_the_server_frontier_exceeds_local_lines() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "a\nb\n"); // only 2 lines locally

        var guard = new CursorRewriteGuard(Config.Root, sid);
        var state = new WatchState { LinesProcessed = 2, CursorByteOffset = 7 };

        // The server claims line 9 — far beyond the local (truncated) file's 2 lines.
        var rewound = await Watch.GatedApplyReconnectRewindAsync(
            gate: null, state, serverPosition: 9, sid, vendor: "cursor", transcriptPath, guard, CancellationToken.None);

        await Assert.That(rewound).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        // Neither frontier moved — the caller (RunWatch) is responsible for exiting instead.
        await Assert.That(state.LinesProcessed).IsEqualTo(2);
        await Assert.That(state.CursorByteOffset).IsEqualTo(7L);
        try { File.Delete(Markers.QuarantinePath(sid)); } catch { }
    }
}
