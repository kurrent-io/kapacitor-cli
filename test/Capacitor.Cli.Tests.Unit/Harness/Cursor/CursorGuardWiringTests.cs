using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// regression tests for the Cursor runtime rewrite-guard WIRING inside
/// <see cref="WatchCommand.DrainNewLines"/> (as distinct from <see cref="CursorRewriteGuardTests"/>,
/// which pins the guard's own pure hash-comparison logic in isolation). Both scenarios here trip
/// the guard and return BEFORE <c>DrainNewLines</c> ever touches its <see cref="HubConnection"/>
/// argument, so an unconnected/never-started connection is sufficient — no live SignalR server
/// needed (mirrors the constraint <c>Integration.CursorTailingWatcherTests</c>'s class doc
/// already states for this project).
/// </summary>
public class CursorGuardWiringTests {
    [TempHome] public required TempHome Home { get; init; }

    WatchCommand? _watch;
    WatchCommand Watch => _watch ??= new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() =>
        new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    [Test]
    public async Task DrainNewLines_trips_on_a_shrink_even_though_the_previous_code_only_checked_growth() {
        // Before the fix, the guard block was gated entirely behind
        // `cursorGuardNewLength > cursorGuardOldOffset` — a file that SHRINKS below the last
        // checkpoint skipped the whole zone check (including the prior-zone re-hash) and sailed
        // through undetected.
        var sid  = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "abc\n"); // 4 bytes — far shorter than the checkpoint below

        var guard = new CursorRewriteGuard(Config.Root, sid);
        var state = new WatchState { CursorByteOffset = 100 }; // a checkpoint from a much longer prior file

        var tripped = false;
        await using var hub = UnconnectedHub();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        await Assert.That(result).IsEmpty();
        // The batch was discarded, not delivered — the byte checkpoint must be untouched.
        await Assert.That(state.CursorByteOffset).IsEqualTo(100L);
    }

    [Test]
    public async Task DrainNewLines_wires_the_periodic_full_prefix_rehash_and_trips_on_a_stale_seed_mismatch() {
        // CursorRewriteGuard.VerifyFullPrefix existed (D0) but nothing ever called it. Drive
        // DrainNewLines's poll counter to exactly the Nth poll (CursorFullPrefixVerifyEveryNPolls)
        // with a pre-seeded (deliberately wrong) prefix sample, so the wired-in periodic check —
        // not the two-zone checks, which pass trivially on a first/no-checkpoint poll — is what
        // must catch the mismatch.
        var sid  = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        // Seed with a WRONG hash for the first 6 bytes — the seed call never validates
        // against `laterBytes` (nothing to compare against yet), so this is accepted as-is.
        var wrongSeed = new CursorAppendOnlyProbe.Sample(6, CursorAppendOnlyProbe.Sha256Hex("WRONGX"u8));
        await Assert.That(guard.VerifyFullPrefix(wrongSeed, "WRONGX"u8)).IsTrue(); // seeds, doesn't validate

        var state = new WatchState {
            CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 1, // this poll lands on N
        };

        var tripped = false;
        await using var hub = UnconnectedHub();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task DrainNewLines_does_not_run_the_periodic_full_prefix_check_before_the_nth_poll() {
        // Regression guard for the cadence itself: one poll short of N must NOT trigger the
        // (expensive) full re-read/re-hash, even with a seeded mismatch waiting — otherwise the
        // cadence constant is meaningless and every poll pays the full-file cost.
        var sid  = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        var wrongSeed = new CursorAppendOnlyProbe.Sample(6, CursorAppendOnlyProbe.Sha256Hex("WRONGX"u8));
        await Assert.That(guard.VerifyFullPrefix(wrongSeed, "WRONGX"u8)).IsTrue();

        var state = new WatchState {
            CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 2, // one short of N
        };

        var tripped = false;
        await using var hub = UnconnectedHub();

        // Below threshold (only 3 lines, all buffered) — the call returns without ever
        // reaching the hub regardless, so this stays offline-safe.
        await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsFalse();
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // VerifyFullPrefix's only production call site (the periodic cadence
    // check) used to seed lazily on its OWN first invocation, which — before this fix — never
    // happened until poll N (CursorFullPrefixVerifyEveryNPolls). The two OTHER full-prefix tests
    // above manually pre-seed the guard via a direct guard.VerifyFullPrefix(...) call to isolate
    // the cadence itself, which is exactly why they do NOT cover this production seeding gap: a
    // same-length rewrite of an already-checkpointed MIDDLE region landing anywhere in polls
    // 1..N-1 had no real baseline to be caught against, and poll N would just seed the
    // ALREADY-rewritten file as if it were the original, valid one. This test drives a REAL
    // poll 1 (seeding from the file's actual, un-rewritten content via DrainNewLines itself,
    // never manually pre-seeding the guard), rewrites the file afterward, then fast-forwards to
    // poll N and confirms the rewrite is still caught.
    [Test]
    public async Task DrainNewLines_seeds_the_full_prefix_sample_on_the_real_first_poll_and_catches_a_mid_region_rewrite_at_the_cadence_poll() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        // Below WatchState.TranscriptThreshold (10) so every poll returns before ever
        // touching the unconnected hub.
        await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        var state = new WatchState();

        var trippedOnFirstPoll = false;
        await using var hub = UnconnectedHub();

        // Poll 1 — the REAL production seed (no manual guard.VerifyFullPrefix call anywhere).
        await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => trippedOnFirstPoll = true);

        await Assert.That(trippedOnFirstPoll).IsFalse();
        await Assert.That(state.CursorGuardPollCount).IsEqualTo(1);
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();

        // Rewrite an already-"seen" MIDDLE region — same total length, first line changed —
        // entirely outside the two-zone tail (nothing has ever been acked/checkpointed here;
        // the session stays below threshold the whole test, so the periodic full-prefix
        // re-hash is the ONLY check that can ever see this region).
        await File.WriteAllTextAsync(transcriptPath, "lineX\nline2\nline3\n");

        // Fast-forward to one poll before the cadence — mirrors the existing cadence tests'
        // technique of setting CursorGuardPollCount directly rather than looping 58 real polls.
        state.CursorGuardPollCount = WatchCommand.CursorFullPrefixVerifyEveryNPolls - 1;

        var tripped = false;
        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        await Assert.That(result).IsEmpty();
    }

    // the guard's checks (prior-zone hash, shrink, new-range
    // record) must still work correctly on a NON-cadence poll, where DrainNewLines now captures
    // only a BOUNDED window (the guard's own trailing-tail zone plus the new range) instead of
    // materializing the whole file. Before this fix, this poll would have re-allocated/re-read the
    // ~50KB padding line below every single second; this test proves correctness survives making
    // that incremental — a same-length in-place rewrite of already-checkpointed history (well
    // outside the small new range) is still caught via the bounded read's prior-zone hash.
    [Test]
    public async Task DrainNewLines_still_catches_a_prior_zone_rewrite_via_the_bounded_non_cadence_read_path() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        var padding = string.Concat(Enumerable.Repeat("p", 50_000)); // one huge already-checkpointed line
        await File.WriteAllTextAsync(transcriptPath, padding + "\nkept\nnew\n");

        var paddingLineByteLength = padding.Length + 1; // "ppp...p\n"
        var keptLineByteLength    = "kept\n".Length;
        var checkpointOffset      = (long)(paddingLineByteLength + keptLineByteLength); // right before "new\n"

        var guard = new CursorRewriteGuard(Config.Root, sid) { TrailingBytes = keptLineByteLength }; // exactly "kept\n"
        guard.Checkpoint(checkpointOffset, CursorAppendOnlyProbe.Sha256Hex("kept\n"u8));

        var state = new WatchState {
            LinesProcessed       = 2, // padding + "kept" already sent/acked
            CursorByteOffset     = checkpointOffset,
            ThresholdReached     = true, // skip the below-threshold buffering short-circuit
            // Neither poll 1 nor a multiple of the cadence — this poll MUST take the bounded
            // (non-full-file) read path, not the periodic whole-file re-hash.
            CursorGuardPollCount = 5,
        };

        // Rewrite already-checkpointed history (same length: "kept" → "XEPT") — entirely
        // OUTSIDE the tiny new range ("new\n") this poll will actually read, and nowhere near
        // the padding line. Only the guard's prior-zone check (which the bounded read still
        // captures, since it starts at the checkpoint offset minus TrailingBytes) can catch it.
        await File.WriteAllTextAsync(transcriptPath, padding + "\nXEPT\nnew\n");

        var tripped = false;
        await using var hub = UnconnectedHub();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => tripped = true);

        await Assert.That(tripped).IsTrue();
        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();
        await Assert.That(result).IsEmpty();
        // The batch was discarded, not delivered.
        await Assert.That(state.CursorByteOffset).IsEqualTo(checkpointOffset);
    }

    [Test]
    public async Task DrainNewLines_bounded_non_cadence_read_still_delivers_correct_new_lines_on_a_large_file() {
        // The "happy path" counterpart to the rewrite test above: no tampering, just proving a
        // large, mostly-unchanged file still correctly decodes and sends only the genuinely new
        // line via the bounded (non-cadence) read.
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        var padding = string.Concat(Enumerable.Repeat("p", 50_000));
        await File.WriteAllTextAsync(transcriptPath, padding + "\nkept\nnew\n");

        var paddingLineByteLength = padding.Length + 1;
        var checkpointOffset      = (long)(paddingLineByteLength + "kept\n".Length);

        var guard = new CursorRewriteGuard(Config.Root, sid) { TrailingBytes = "kept\n".Length };
        guard.Checkpoint(checkpointOffset, CursorAppendOnlyProbe.Sha256Hex("kept\n"u8));

        var state = new WatchState {
            LinesProcessed       = 2,
            CursorByteOffset     = checkpointOffset,
            ThresholdReached     = true,
            CursorGuardPollCount = 5, // non-cadence poll, same as above
        };

        await using var hub = UnconnectedHub();

        // agentId is null and ThresholdReached is true, so the send path is reached — the
        // unconnected hub makes SendTranscriptBatchAcked fail, which DrainNewLines treats as a
        // retryable send failure (logs, doesn't advance state) rather than a crash. The lines
        // returned are what matters here: correctness of the bounded decode.
        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(result).IsEquivalentTo(new[] { "new" });
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // a poll that reads ONLY blank/whitespace lines
    // (newLines.Count == 0, but the line frontier still advanced past them) must move
    // CursorByteOffset in LOCKSTEP with LinesProcessed. Before the fix, only LinesProcessed
    // advanced here, leaving CursorByteOffset (and the guard's own checkpoint) pointed at the OLD
    // byte boundary — the NEXT poll's bounded decoder then re-read those SAME already-"processed"
    // blank bytes (its read starts from CursorByteOffset) but seeded its line numbering from the
    // ALREADY-ADVANCED LinesProcessed, so a truly idle poll (nothing new on disk at all) kept
    // re-"discovering" the same blank lines and re-inflating LinesProcessed forever.
    [Test]
    public async Task DrainNewLines_keeps_byte_and_line_frontier_in_lockstep_across_a_blank_only_poll_so_the_next_idle_poll_re_decodes_nothing() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        // Two real (non-blank) lines already "delivered" by a prior poll — seeded directly
        // (mirrors the existing bounded-read tests' pattern) to establish a clean baseline
        // without needing a live hub round trip.
        await File.WriteAllTextAsync(transcriptPath, "a\nb\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        guard.Checkpoint(offset: 4, trailingSha: CursorAppendOnlyProbe.Sha256Hex("a\nb\n"u8));
        var state = new WatchState {
            LinesProcessed   = 2,
            CursorByteOffset = 4,
            ThresholdReached = true,
        };

        await using var hub = UnconnectedHub();

        // Append TWO blank/whitespace-only lines — complete (newline-terminated), so the
        // decoder must consume them, but nothing a real agent would ever emit as content.
        await File.AppendAllTextAsync(transcriptPath, "\n   \n"); // 5 bytes: "\n" then "   \n"

        var pollA = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(pollA).IsEmpty(); // both new lines are blank — nothing to send
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();

        // THE FIX: the byte frontier must land at the TRUE end of the two blank lines (offset
        // 9), in lockstep with the line frontier (4) — not stuck at the old offset (4) while
        // LinesProcessed alone raced ahead.
        await Assert.That(state.LinesProcessed).IsEqualTo(4);
        await Assert.That(state.CursorByteOffset).IsEqualTo(9L);

        // Poll B: a genuinely idle poll — NOTHING new appended since poll A. Before the fix,
        // CursorByteOffset would have stayed at 4 (the OLD offset), so this poll would re-read
        // and re-"discover" the SAME two blank lines all over again, numbered starting from the
        // now-inflated LinesProcessed (4) and pushing it to 6 — repeating forever on every
        // subsequent idle poll. With the fix, CursorByteOffset already sits at the TRUE EOF (9),
        // so there is nothing left to decode — neither frontier moves again.
        var pollB = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(pollB).IsEmpty();
        await Assert.That(state.LinesProcessed).IsEqualTo(4);    // unchanged — nothing new to find
        await Assert.That(state.CursorByteOffset).IsEqualTo(9L); // unchanged — already at true EOF
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // CursorGuardPollCount (the full-prefix cadence counter)
    // must only be committed once the guarded read it fed actually completes. Before the fix, the
    // counter incremented UNCONDITIONALLY before opening the file — a transient IOException on that
    // open/read (e.g. a concurrent writer briefly holding an exclusive lock) still "used up" the
    // cadence slot even though VerifyFullPrefix was never reached, so the NEXT successful poll saw a
    // cadence value one past due and silently skipped the full-prefix baseline it was supposed to
    // perform, recreating the polls-1..N-1 history-rewrite blind window the cadence exists to close.
    [Test]
    public async Task DrainNewLines_does_not_consume_the_full_prefix_cadence_when_the_guarded_read_fails() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "line1\nline2\nline3\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        var state = new WatchState(); // CursorGuardPollCount starts at 0 — this poll is poll 1 (full-prefix-due)

        await using var hub = UnconnectedHub();

        // Force the guarded read to throw IOException for the duration of this one poll only,
        // by holding an exclusive lock (FileShare.None denies DrainNewLines's own
        // FileShare.ReadWrite open — verified to throw System.IO.IOException on this platform).
        await using (new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.None)) {
            var failedResult = await Watch.DrainNewLines(
                hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
                cursorGuard: guard, onCursorRewriteDetected: () => { });

            await Assert.That(failedResult).IsEmpty();
        }

        // The failed poll must NOT have consumed the cadence counter.
        await Assert.That(state.CursorGuardPollCount).IsEqualTo(0);

        // The NEXT (successful) poll must still be treated as poll 1 — i.e. it must still be
        // the one that performs the full-prefix baseline seed, not skip it as if some earlier
        // poll (the failed one) had already used up that slot.
        await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(state.CursorGuardPollCount).IsEqualTo(1);
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    // the SAME byte/line-lockstep the
    // finding-#1 blank-only early-return got must also hold on the OTHER path that advances
    // LinesProcessed past blank lines without an acked send: the "repo-only batch failed" catch
    // branch. That branch is only reached when a repo change was pending (repoToSend != null) AND
    // newLines.Count == 0 (any lines read this poll were blank/whitespace), and the repo-only RPC
    // threw. Before the fix it advanced LinesProcessed alone, so — exactly like the pre-#1-fix
    // early return — CursorByteOffset/checkpoint lagged and the next poll re-decoded the same blank
    // bytes under an inflated line number. This mirrors the finding-#1 test with a pending repo
    // change and an RPC that fails (the unconnected hub).
    [Test]
    public async Task DrainNewLines_keeps_frontiers_in_lockstep_across_a_blank_only_poll_whose_repo_only_send_fails() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        // Two real (non-blank) lines already "delivered" — seeded directly (same baseline
        // technique as the finding-#1 test).
        await File.WriteAllTextAsync(transcriptPath, "a\nb\n");

        var guard = new CursorRewriteGuard(Config.Root, sid);
        guard.Checkpoint(offset: 4, trailingSha: CursorAppendOnlyProbe.Sha256Hex("a\nb\n"u8));
        var state = new WatchState {
            LinesProcessed   = 2,
            CursorByteOffset = 4,
            ThresholdReached = true,
            // A pending repo change (Repository set, nothing sent yet) is what forces this poll
            // into the send path even though it read only blank lines — and, once the send
            // fails, into the repo-only-failed catch branch under test.
            Repository       = new RepositoryPayload { Owner = "acme", RepoName = "widgets" },
        };

        await using var hub = UnconnectedHub(); // every send RPC throws (never connected)

        // Append TWO blank/whitespace-only lines.
        await File.AppendAllTextAsync(transcriptPath, "\n   \n"); // 5 bytes → EOF at 9

        var pollA = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(pollA).IsEmpty(); // both new lines blank — nothing sent; the repo-only RPC threw
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();

        // THE FIX: even though the send failed, the byte frontier must land at the true end of
        // the two blank lines (9), in lockstep with the line frontier (4) — not stay stuck at 4.
        await Assert.That(state.LinesProcessed).IsEqualTo(4);
        await Assert.That(state.CursorByteOffset).IsEqualTo(9L);
        // The failed repo-only send left LastSentRepository unset, so the change is still pending
        // (it retries on a later cycle) — this must NOT re-inflate the line frontier.
        await Assert.That(state.LastSentRepository).IsNull();

        // Poll B: genuinely idle — nothing appended since poll A. The repo change is still
        // pending, so this poll again reaches (and fails) the repo-only send; but with the byte
        // frontier already at the true EOF (9), there is nothing left to decode, so neither
        // frontier moves again. Before the fix, CursorByteOffset stuck at 4 would have re-read
        // the same two blank lines and pushed LinesProcessed to 6.
        var pollB = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard, onCursorRewriteDetected: () => { });

        await Assert.That(pollB).IsEmpty();
        await Assert.That(state.LinesProcessed).IsEqualTo(4);    // unchanged — nothing new to find
        await Assert.That(state.CursorByteOffset).IsEqualTo(9L); // unchanged — already at true EOF
        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }
}
