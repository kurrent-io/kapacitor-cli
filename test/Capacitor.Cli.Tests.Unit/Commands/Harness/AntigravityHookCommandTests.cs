using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// <summary>
/// Unit tests for <see cref="AntigravityHookCommand"/>: event-arg parsing and
/// the fail-open routing paths that must NOT touch the network (non-PreInvocation events,
/// malformed / incomplete payloads). The session-start POST path is exercised by the
/// WireMock integration suite.
/// </summary>
public class AntigravityHookCommandTests {

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task EventArg_reads_the_positional_token_after_the_flag() {
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "PreInvocation"]))
            .IsEqualTo("PreInvocation");
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "Stop"]))
            .IsEqualTo("Stop");
    }

    [Test]
    public async Task EventArg_is_null_when_missing_or_a_flag_follows() {
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity"])).IsNull();
        await Assert.That(AntigravityHookCommand.EventArg(["hook", "--antigravity", "--debug"])).IsNull();
        await Assert.That(AntigravityHookCommand.EventArg(["hook"])).IsNull();
    }

    [Test]
    public async Task Missing_event_exits_zero_without_touching_network() {
        // Control hooks must always exit 0 so Antigravity doesn't treat the hook as failed.
        var rc = await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity"], new StringReader(""), new StringWriter());
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    public async Task PreInvocation_with_non_string_fields_fails_open() {
        // conversationId as a non-string shape must not throw (GetValue<string> would);
        // it fails open to a no-op.
        var rc = await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"],
            new StringReader("""{"conversationId":123,"transcriptPath":{"nested":true}}"""),
            new StringWriter());
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    [Arguments("Stop")]
    [Arguments("PostInvocation")]
    [Arguments("PreToolUse")]
    [Arguments("PostToolUse")]
    public async Task Non_PreInvocation_events_are_no_ops(string ev) {
        // These must return 0 and never read stdin / hit the network.
        var rc = await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", ev],
            new ThrowingReader(), new StringWriter());
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    public async Task PreInvocation_with_malformed_payload_fails_open() {
        var rc = await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"],
            new StringReader("{ not json"), new StringWriter());
        await Assert.That(rc).IsEqualTo(0);
    }

    [Test]
    public async Task PreInvocation_without_conversation_or_transcript_is_a_no_op() {
        // No conversationId → nothing to key on.
        await Assert.That(await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"],
            new StringReader("""{"transcriptPath":"/t.jsonl"}"""), new StringWriter())).IsEqualTo(0);

        // conversationId but no transcriptPath → nothing to tail.
        await Assert.That(await new AntigravityHookCommand(Config.Root, Resolutions.At("http://127.0.0.1:0", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"],
            new StringReader("""{"conversationId":"abc"}"""), new StringWriter())).IsEqualTo(0);
    }

    // A reader that throws if read — proves the non-PreInvocation path short-circuits
    // before consuming stdin.
    sealed class ThrowingReader : TextReader {
        public override Task<string> ReadToEndAsync() =>
            throw new InvalidOperationException("stdin must not be read for non-PreInvocation events");
    }
}
