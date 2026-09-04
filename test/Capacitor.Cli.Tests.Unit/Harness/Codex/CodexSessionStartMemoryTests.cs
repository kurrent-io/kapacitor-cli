using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>
/// The Codex SessionStart memory-injection envelope contract. Codex blocks on this hook's stdout
/// and its parser accepts exactly ONE JSON value, so these tests pin three things the adapter must
/// never break: the byte-for-byte minimal handshake when no fragment exists, a single well-formed
/// combined object when one does, and that a fragment never leaks into Stop's output.
///
/// Drives <see cref="CodexHookCommand.WriteSessionStartOutput"/> (the SessionStart writer) directly
/// rather than the full hook, so no server, git repo, or config root is required.
/// </summary>
public class CodexSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionStartOutput(sw, fragment);

        return sw.ToString();
    }

    // The regression that matters most: every no-memory path (opt-out, exclusion, provider
    // failure, budget exhaustion) funnels a null fragment through the writer, and the bytes
    // must be indistinguishable from the pre-memory handshake. If the shared adapter ever
    // renders `{"continue":true,"hookSpecificOutput":null}` or reorders keys, Codex's parser
    // contract changes silently — this test is the tripwire.
    [Test]
    public async Task no_fragment_emits_the_byte_identical_minimal_handshake() {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionScopedOutput(sw);

        await Assert.That(Write(null)).IsEqualTo(sw.ToString());
        await Assert.That(Write(null)).IsEqualTo("""{"continue":true}""");
    }

    // The fragment-bearing shape is rendered by the shared adapter, which appends a trailing
    // newline to every envelope (as Claude and Cursor already ship). Pinned so the asymmetry with
    // the null case above is a recorded decision rather than an accident.
    [Test]
    public async Task a_fragment_bearing_payload_carries_the_shared_adapters_trailing_newline() {
        await Assert.That(Write("## Team memory")).EndsWith("\n");
        await Assert.That(Write(null)).DoesNotContain("\n");
    }

    [Test]
    public async Task a_fragment_emits_one_combined_object_carrying_continue_and_additional_context() {
        var output = Write("## Team memory\n- always run the integration suite");

        // Exactly one JSON value, and it parses.
        var parsed = System.Text.Json.JsonDocument.Parse(output);

        await Assert.That(parsed.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        var hookOutput = parsed.RootElement.GetProperty("hookSpecificOutput");

        await Assert.That(hookOutput.GetProperty("hookEventName").GetString()).IsEqualTo("SessionStart");
        await Assert.That(hookOutput.GetProperty("additionalContext").GetString())
            .IsEqualTo("## Team memory\n- always run the integration suite");
    }

    // Codex's parser rejects a second JSON value on stdout. Guard against a future renderer that
    // emits the handshake and the memory object as two documents.
    [Test]
    public async Task output_is_a_single_json_value_with_no_trailing_document() {
        var output = Write("## Team memory\n- one");

        var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(output));
        reader.Read();
        reader.Skip();

        await Assert.That(reader.Read()).IsFalse();
    }

    // Control/quote/newline/non-BMP content must survive JSON escaping intact — the fragment is
    // server-rendered markdown and carries all of these.
    [Test]
    [Arguments("quote \" backslash \\ newline \n tab \t")]
    [Arguments("non-BMP \U0001F600 and CR \r")]
    [Arguments("")]
    public async Task fragment_content_round_trips_through_escaping(string fragment) {
        var parsed = System.Text.Json.JsonDocument.Parse(Write(fragment));

        await Assert.That(parsed.RootElement.GetProperty("hookSpecificOutput")
                               .GetProperty("additionalContext").GetString())
            .IsEqualTo(fragment);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative/path")]
    public async Task an_unusable_base_url_skips_memory_injection_instead_of_exiting(string? baseUrl) {
        await Assert.That(HookHttp.IsPostable(baseUrl)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task an_absolute_base_url_permits_memory_injection(string baseUrl) {
        await Assert.That(HookHttp.IsPostable(baseUrl)).IsTrue();
    }

    // Stop shares the handshake constant but must NEVER carry memory context: it is a
    // per-turn-ish event and injecting there would re-inject on every stop.
    [Test]
    public async Task stop_output_never_carries_memory_context() {
        var sw = new StringWriter();
        CodexHookCommand.WriteSessionScopedOutput(sw);

        await Assert.That(sw.ToString()).DoesNotContain("additionalContext");
        await Assert.That(sw.ToString()).IsEqualTo("""{"continue":true}""");
    }
}
