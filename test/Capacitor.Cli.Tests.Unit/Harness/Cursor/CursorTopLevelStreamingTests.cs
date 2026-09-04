using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Cursor;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// a top-level Cursor watcher must stream new transcript lines
/// immediately rather than accumulating them in <see cref="WatchState.BufferedLines"/> (the
/// generic below-threshold buffer every other vendor without a pre-spawn commit still uses).
/// <see cref="WatchCommand.SkipsThresholdBuffering"/> is the pure decision (see
/// <c>WatchCommandTests</c>); these tests pin the actual <see cref="WatchCommand.DrainNewLines"/>
/// wiring consequence — unlike <see cref="CursorGuardWiringTests"/>, these calls DO reach (and
/// fail against) an unconnected <see cref="HubConnection"/>, which is caught internally and
/// returns gracefully — still no live SignalR server needed.
/// </summary>
public class CursorTopLevelStreamingTests {
    [TempHome] public required TempHome Home { get; init; }

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() =>
        new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    [Test]
    public async Task DrainNewLines_cursor_session_watcher_never_buffers_when_thresholdReached_is_preset_even_below_the_numeric_threshold() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        // 3 lines — well below WatchState.TranscriptThreshold (10).
        await File.WriteAllTextAsync(transcriptPath, "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");

        // What RunWatch's startup now does for Cursor (review fix #4): ThresholdReached is
        // already true before the main loop's very first poll, regardless of line count.
        var state = new WatchState { ThresholdReached = true };
        var guard = new CursorRewriteGuard(Config.Root, sid);
        await using var hub = UnconnectedHub();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard);

        // Neither switch(agentId) case in DrainNewLines can match (both require
        // `!state.ThresholdReached`), so the 3 lines are treated as flowing content, never
        // accumulated into the below-threshold buffer — even though the actual send then
        // fails (unconnected hub) and is gracefully retried next poll (position unchanged).
        await Assert.That(state.BufferedLines).IsEmpty();
        await Assert.That(result).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" });
        await Assert.That(state.LinesProcessed).IsEqualTo(0); // send failed — position not advanced
    }

    // Contrast: BEFORE RunWatch's startup wiring runs (thresholdReached left false), Cursor used
    // the generic vendor-agnostic buffering path exactly like every other vendor — pins that the
    // fix is specifically about the STARTUP flag, not a change to DrainNewLines' own buffering.
    [Test]
    public async Task DrainNewLines_cursor_session_watcher_still_buffers_when_thresholdReached_is_false_and_below_numeric_threshold() {
        var sid = NewSessionId();
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(transcriptPath, "{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n");

        var state = new WatchState(); // ThresholdReached defaults false
        var guard = new CursorRewriteGuard(Config.Root, sid);
        await using var hub = UnconnectedHub();

        var result = await Watch.DrainNewLines(
            hub, sid, transcriptPath, agentId: null, state, vendor: "cursor", CancellationToken.None,
            cursorGuard: guard);

        await Assert.That(state.BufferedLines).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" });
        await Assert.That(state.ThresholdReached).IsFalse();
        await Assert.That(result).IsEquivalentTo(new[] { "{\"a\":1}", "{\"b\":2}", "{\"c\":3}" });
    }
}
