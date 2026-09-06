using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Commands;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Tests.Unit.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The Antigravity PreInvocation memory-injection contract. Antigravity reads hook stdout as a JSON
/// envelope (<c>injectSteps</c>), so these tests pin the two things that must never break: a fragment
/// renders as a durable <c>userMessage</c> step, and every no-memory path writes NOTHING AT ALL.
///
/// The zero-bytes rule is the regression that matters most. This hook emitted nothing before the
/// memory index existed, so rendering the adapter's <c>{}</c> on the no-fragment path would change
/// the wire behaviour of EVERY invocation for EVERY user — including the IDE-only majority, whose
/// product was never probed — to buy nothing. Copilot and Kiro set the precedent.
///
/// The once-per-conversation dedupe that makes this safe under PreInvocation's per-INVOCATION firing
/// lives in SessionStartMemoryFoundationTests (the Antigravity_* tests), next to the lease fixtures.
/// </summary>
public class AntigravitySessionStartMemoryTests {

    [TempHome] public required TempHome Home { get; init; }

    // Instance, not static: the hook writes under the config dir (the repo-detection cache,
    // the lease store), so it must be handed this test's own root — which a static helper
    // cannot see, TUnit injecting it after construction.
    // The server URL is the resolution's, so a test proving the url guard fires hands in the bad one
    // here rather than as an argument.
    AntigravityHookCommand Hook(string serverUrl = "https://example.test") =>
        new(Config.Root, Resolutions.At(serverUrl, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient());
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string Write(string? fragment) {
        var sw = new StringWriter();
        AntigravityHookCommand.WritePreInvocationOutput(sw, fragment);

        return sw.ToString();
    }

    // Byte-identical to the pre-feature behaviour on every path a user gets no index: opt-out,
    // exclusion, provider failure, budget exhaustion, and (the common case) a repeat PreInvocation
    // whose lease is already spent. `{}` here would be a wire change on every invocation.
    [Test]
    public async Task no_fragment_writes_zero_bytes() {
        await Assert.That(Write(null)).IsEqualTo("");
    }

    // The vendor's own documented shape, verified against the agy binary's embedded hook contract:
    // injectSteps carries userMessage (durable) rather than ephemeralMessage (documented transient).
    [Test]
    public async Task a_fragment_renders_the_injectSteps_envelope() {
        await Assert.That(Write("F")).IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"F\"}]}\n");
    }

    // Markdown carries quotes, backslashes and newlines; the envelope is JSON, so the fragment must
    // survive a round trip byte-for-byte. Asserted by PARSING rather than by matching escape
    // sequences: the serializer escapes `"` as " rather than \" (System.Text.Json's default
    // encoder), which is equally valid JSON, so pinning specific escapes would fail on a correct
    // implementation. What actually matters to the model is that it decodes back to the input.
    //
    // This is the inverse of Kiro's raw-stdout contract, so it is pinned rather than assumed.
    [Test]
    [Arguments("quote \" backslash \\ newline \n")]
    [Arguments("non-BMP \U0001F600 emoji")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    public async Task a_fragment_round_trips_through_the_envelope(string fragment) {
        var output = Write(fragment);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var decoded = doc.RootElement
            .GetProperty("injectSteps")[0]
            .GetProperty("userMessage")
            .GetString();

        await Assert.That(decoded).IsEqualTo(fragment);
    }

    // Exactly one trailing terminator, and the envelope occupies a single line — a raw newline inside
    // the JSON would break any line-oriented reader on the vendor side.
    [Test]
    public async Task the_envelope_is_one_line_with_a_single_terminator() {
        var output = Write("- item one\n- item two");

        await Assert.That(output).EndsWith("\n");
        await Assert.That(output.TrimEnd('\n')).DoesNotContain("\n");
        await Assert.That(output.Split('\n').Length).IsEqualTo(2);   // content + the terminator
    }

    // An empty (not null) fragment is a real, if degenerate, Ready payload — it still renders the
    // envelope, because the null case is the ONLY zero-bytes case.
    [Test]
    public async Task an_empty_fragment_still_renders_the_envelope() {
        await Assert.That(Write("")).IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"\"}]}\n");
    }

    [Test]
    public async Task Lifecycle_is_a_repeating_top_level_callback() {
        var lifecycle = AntigravityHookCommand.LifecycleFor("e80c33bfc10f4d2fb626b0043f488fc0");

        await Assert.That(lifecycle.Harness).IsEqualTo(HarnessId.Antigravity);
        await Assert.That(lifecycle.IsTopLevel).IsTrue();
        await Assert.That(lifecycle.ClassificationAuthoritative).IsTrue();
        await Assert.That(lifecycle.Reason).IsEqualTo(SessionLifecycleReason.RepeatedTurnCallback);
        await Assert.That(lifecycle.CallbackMayRepeat).IsTrue();
        await Assert.That(lifecycle.LifecycleInstanceId).IsNull();

        // PreInvocation repeats, so the policy MUST hand back a lease-guarded decision.
        // EligibleOneShot here would re-inject on every turn.
        await Assert.That(SessionStartMemoryLifecyclePolicy.Decide(lifecycle))
            .IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    [Test]
    public async Task Fetch_is_skipped_when_disabled_or_unscoped_or_out_of_budget() {
        // Each guard alone must suppress the fetch. A non-postable base url is checked BEFORE
        // any client is built: an unusable URL should spend no budget, no lease, and start no task.
        // Both lanes off ⇒ suppressed; a single lane off would still fetch.
        await Assert.That(await Hook().StartMemoryIndexTask("e80c33bfc10f4d2fb626b0043f488fc0", "/repo",
            disabled: true, guidelinesDisabled: true, TimeSpan.FromSeconds(5))).IsNull();

        // The scope / budget / url guards suppress even with guidelines ENABLED.
        await Assert.That(await Hook().StartMemoryIndexTask("e80c33bfc10f4d2fb626b0043f488fc0", scopeRoot: null,
            disabled: false, guidelinesDisabled: false, TimeSpan.FromSeconds(5))).IsNull();

        await Assert.That(await Hook().StartMemoryIndexTask("e80c33bfc10f4d2fb626b0043f488fc0", "/repo",
            disabled: false, guidelinesDisabled: false, TimeSpan.Zero)).IsNull();

        await Assert.That(await Hook("").StartMemoryIndexTask(
            "e80c33bfc10f4d2fb626b0043f488fc0", "/repo",
            disabled: false, guidelinesDisabled: false, TimeSpan.FromSeconds(5))).IsNull();
    }

    /// <summary>The memory subsystem is optional, so a store that cannot even be constructed must
    /// resolve to "no fragment" rather than fault the hook. A file where the store root's directory
    /// has to go makes construction throw synchronously, inside the try — the one failure mode that
    /// would otherwise escape before any await.</summary>
    [Test]
    public async Task A_store_that_cannot_be_constructed_resolves_to_null_rather_than_faulting() {
        MemoryStoreProbe.Poison(Config.Root);

        var task = Hook().StartMemoryIndexTask("e80c33bfc10f4d2fb626b0043f488fc0", "/repo",
            disabled: false, guidelinesDisabled: true, TimeSpan.FromSeconds(5));

        await Assert.That(await task).IsNull();
    }

    [Test]
    public async Task A_non_PreInvocation_event_writes_nothing_and_exits_zero() {
        var sw   = new StringWriter();
        var code = await new AntigravityHookCommand(Config.Root, Resolutions.At("https://example.test", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["--antigravity", "Stop"], new StringReader("{}"), sw);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    [Test]
    public async Task A_malformed_payload_writes_nothing_and_exits_zero() {
        var sw   = new StringWriter();
        var code = await new AntigravityHookCommand(Config.Root, Resolutions.At("https://example.test", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["--antigravity", "PreInvocation"], new StringReader("{not json"), sw);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(sw.ToString()).IsEqualTo("");
    }

    // ---- workspace resolution: the print-mode empty-workspacePaths gap (agy 1.1.11, measured) ----
    //
    // Print mode sends `"workspacePaths": []` where interactive sends the launch dir. The fallback
    // recovers the workspace from the agent process itself; these pin the three properties that
    // make it safe: payload wins, empty payload consults the fallback, and a null fallback is the
    // exact pre-fallback behaviour.

    static System.Text.Json.Nodes.JsonObject Payload(string json) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

    // Ordering is the safety property: a vendor release that starts populating workspacePaths must
    // silently win. Asserted by a THROWING fallback, so "not invoked" cannot pass vacuously.
    [Test]
    public async Task A_populated_payload_never_consults_the_fallback() {
        var resolved = AntigravityHookCommand.ResolveWorkspace(
            Payload("""{"workspacePaths":["/real/workspace"]}"""),
            () => throw new InvalidOperationException("fallback must not run"));

        await Assert.That(resolved).IsEqualTo("/real/workspace");
    }

    [Test]
    public async Task An_empty_workspacePaths_array_resolves_via_the_fallback() {
        var resolved = AntigravityHookCommand.ResolveWorkspace(
            Payload("""{"workspacePaths":[]}"""), () => "/agent/cwd");

        await Assert.That(resolved).IsEqualTo("/agent/cwd");
    }

    [Test]
    public async Task A_missing_workspacePaths_field_resolves_via_the_fallback() {
        var resolved = AntigravityHookCommand.ResolveWorkspace(Payload("{}"), () => "/agent/cwd");

        await Assert.That(resolved).IsEqualTo("/agent/cwd");
    }

    // The singular `cwd` form still outranks the fallback — it is payload data too.
    [Test]
    public async Task A_payload_cwd_field_outranks_the_fallback() {
        var resolved = AntigravityHookCommand.ResolveWorkspace(
            Payload("""{"workspacePaths":[],"cwd":"/payload/cwd"}"""),
            () => throw new InvalidOperationException("fallback must not run"));

        await Assert.That(resolved).IsEqualTo("/payload/cwd");
    }

    [Test]
    public async Task A_null_fallback_result_leaves_the_workspace_unresolved() {
        await Assert.That(AntigravityHookCommand.ResolveWorkspace(Payload("{}"), () => null)).IsNull();
    }

    // Environment-dependent by nature (there is no agy ancestor under the test runner), so the
    // pinnable property is the fail-open contract: never throws, and never fabricates a path when
    // no agent is on the ancestry chain.
    [Test]
    public async Task The_production_fallback_fails_open_without_an_agy_ancestor() {
        await Assert.That(AntigravityHookCommand.AgentWorkspaceCwd()).IsNull();
    }

    // The WIRING pin: ResolveWorkspace existing and HandleCore calling it are two separate facts,
    // and only this test makes them agree. Routed through the disabled-session fast path — which
    // sits immediately AFTER workspace resolution — so the run stays hermetic (no POST, no watcher)
    // while still proving the injected fallback was consulted for an empty-workspacePaths payload.
    [Test, NotInParallel]
    public async Task HandleCore_consults_the_fallback_for_an_empty_workspacePaths_payload() {
        const string conversationId = "e80c33bf-c10f-4d2f-b626-b0043f488fc0";
        DisabledSessions.Mark(conversationId.Replace("-", ""), Config.Root);

        var consulted = false;
        var sw        = new StringWriter();
     var code = await new AntigravityHookCommand(Config.Root, Resolutions.At("https://example.test", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["--antigravity", "PreInvocation"],
            new StringReader($$"""
                {"conversationId":"{{conversationId}}","transcriptPath":"/tmp/t.jsonl","workspacePaths":[]}
                """),
            sw,
            workspaceFallback: () => { consulted = true; return null; });

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(sw.ToString()).IsEqualTo("");
        await Assert.That(consulted).IsTrue();
    }

    // The nudges carry no lease, so on this repeating callback only the invocation counter keeps
    // them from re-injecting a persistent userMessage step every turn.

    [Test]
    public async Task The_first_invocation_emits_nudges() {
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("""{"invocationNum":1}"""))).IsTrue();
    }

    [Test]
    public async Task A_later_invocation_suppresses_nudges() {
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("""{"invocationNum":2}"""))).IsFalse();
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("""{"invocationNum":97}"""))).IsFalse();
    }

    // Fail-open: a payload whose counter cannot mark a later turn — missing, non-numeric, or below
    // the genuine first value of one — must still emit.
    [Test]
    public async Task A_missing_or_unusable_counter_reads_as_the_first_invocation() {
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("{}"))).IsTrue();
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("""{"invocationNum":"2"}"""))).IsTrue();
        await Assert.That(AntigravityHookCommand.IsFirstInvocation(Payload("""{"invocationNum":0}"""))).IsTrue();
    }
}
