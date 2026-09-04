using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.SessionStartMemory;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// The Copilot SessionStart memory-injection contract. Copilot parses this hook's stdout as an
/// optional SINGLE JSON document, and today the hook writes nothing at all — so the tests pin both
/// halves of the decision: a fragment produces exactly one `additionalContext` object, and no
/// fragment produces byte-identical silence rather than a newly-emitted `{}`.
/// </summary>
public class CopilotSessionStartMemoryTests {
    static string Write(string? fragment) {
        var sw = new StringWriter();
        CopilotHookCommand.WriteSessionStartOutput(sw, fragment);

        return sw.ToString();
    }

    // The regression that matters: every no-memory path (opt-out, exclusion, provider failure,
    // budget exhaustion, ineligible source) funnels a null fragment through the writer. Copilot's
    // sessionStart hook emitted nothing before this feature, and must still emit nothing — not `{}`.
    [Test]
    public async Task no_fragment_writes_nothing_at_all() {
        await Assert.That(Write(null)).IsEqualTo("");
    }

    [Test]
    public async Task a_fragment_writes_one_top_level_additional_context_object() {
        var output = Write("## Team memory\n- prefer the integration suite");
        var parsed = System.Text.Json.JsonDocument.Parse(output);

        await Assert.That(parsed.RootElement.GetProperty("additionalContext").GetString())
            .IsEqualTo("## Team memory\n- prefer the integration suite");

        // Top-level only — Copilot's sessionStart contract has no hookSpecificOutput wrapper.
        await Assert.That(output).DoesNotContain("hookSpecificOutput");
    }

    // Copilot accepts one document; guard against a renderer that emits two.
    [Test]
    public async Task output_is_a_single_json_value_with_no_trailing_document() {
        var reader = new System.Text.Json.Utf8JsonReader(
            System.Text.Encoding.UTF8.GetBytes(Write("## Team memory")));
        reader.Read();
        reader.Skip();

        await Assert.That(reader.Read()).IsFalse();
    }

    [Test]
    [Arguments("quote \" backslash \\ newline \n tab \t")]
    [Arguments("non-BMP \U0001F600 and CR \r")]
    [Arguments("")]
    public async Task fragment_content_round_trips_through_escaping(string fragment) {
        var parsed = System.Text.Json.JsonDocument.Parse(Write(fragment));

        await Assert.That(parsed.RootElement.GetProperty("additionalContext").GetString())
            .IsEqualTo(fragment);
    }

    // Copilot DOES report a lifecycle source, unlike Codex — so the reason is mapped, not assumed.
    // `startup` is the value Copilot's own payload defaults to, and it must be eligible: treating it
    // as unknown would silently deny memory to the ordinary first-start case.
    // Compared by name: SessionLifecycleReason is internal, so it cannot appear in a public
    // signature (CS0051) — the mapping is what matters, not the parameter's static type.
    [Test]
    [Arguments("startup", "New")]
    [Arguments("new", "New")]
    [Arguments("resume", "Resume")]
    [Arguments("STARTUP", "New")]
    [Arguments(null, "New")]
    [Arguments("", "New")]
    public async Task a_reported_source_maps_to_its_lifecycle_reason(string? source, string expected) {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor(source).ToString()).IsEqualTo(expected);
    }

    // An unrecognised source must NOT be guessed as New: the lifecycle policy derives the injection
    // decision from this, so inventing a reason would invent a decision.
    [Test]
    public async Task an_unrecognised_source_maps_to_unknown_rather_than_being_guessed() {
        await Assert.That(SessionStartMemoryHookSupport.ReasonFor("teleported").ToString())
            .IsEqualTo("Unknown");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative")]
    public async Task an_unusable_base_url_is_refused_before_auth_discovery(string? baseUrl) {
        await Assert.That(HookHttp.IsPostable(baseUrl)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task an_absolute_base_url_is_permitted(string baseUrl) {
        await Assert.That(HookHttp.IsPostable(baseUrl)).IsTrue();
    }

    /// <summary>The budget of a Copilot hook that has been running for <paramref name="elapsed"/>.
    /// The wait itself still runs on the real timer — only the budget arithmetic is taken off the wall
    /// clock. The adapter's own ceiling, so the numbers below stay tied to what it really allows.</summary>
    static HookBudget Aged(TimeSpan elapsed) {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);
        time.Advance(elapsed);
        return clock.Budget(CopilotHookCommand.Ceiling);
    }

    // HookBudget.Remaining ALREADY reserves Safety. Reserving it a second time (as the first cut of
    // both this adapter and the Codex one did) collapses the usable window and silently discards a
    // healthy response. Pinned at the elapsed time where the two differ decisively: with a 5s ceiling
    // and a 1.5s safety, 2s elapsed leaves Remaining = 1.5s, whereas the double-reserved budget is
    // exactly zero — so a fragment arriving 300ms from now is returned only if Safety is reserved once.
    [Test]
    public async Task a_fragment_arriving_inside_the_remaining_budget_is_not_discarded() {
        var slow = Task.Run(async () => { await Task.Delay(300); return (string?)"## Team memory"; });

        await Assert.That(await SessionStartMemoryHookSupport.AwaitBounded(
                slow, Aged(TimeSpan.FromSeconds(2))))
            .IsEqualTo("## Team memory");
    }

    // An exhausted budget must degrade to null rather than wait: 4.9s elapsed against a 5s ceiling
    // leaves nothing once Safety is reserved.
    [Test]
    public async Task an_exhausted_budget_degrades_to_no_memory_without_waiting() {
        var slow = Task.Run(async () => { await Task.Delay(5_000); return (string?)"## Team memory"; });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fragment = await SessionStartMemoryHookSupport.AwaitBounded(
            slow, Aged(TimeSpan.FromSeconds(4.9)));
        sw.Stop();

        await Assert.That(fragment).IsNull();
        // Not just "null" — null WITHOUT waiting. A budget that ignored the elapsed time would
        // still yield null here, by timing out the 5s task against a full ceiling.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>Walks up from this file's compile-time path to the repo root.</summary>
    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);

        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"repo root not found from {here}");
    }

    // `disable_memory_index` must be read from the EFFECTIVE profile. ProfileResolver returns a null
    // Profile whenever --server-url or KCAP_URL wins, so reading the resolution's own profile
    // silently ignored the user's opt-out on every KCAP_URL deployment — the configuration most
    // hosted users run. Asserted at the source level because reaching this branch through Handle
    // needs a resolution whose profile is null while the effective one is not, which the hook's own
    // entry point will not produce.
    [Test]
    public async Task the_memory_opt_out_is_read_from_the_effective_profile_not_the_resolved_one() {
        var source = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Capacitor.Cli", "Commands", "Harness", "CopilotHookCommand.cs"));

        var start = source.IndexOf("var memoryTask = StartMemoryIndexTask(", StringComparison.Ordinal);
        await Assert.That(start).IsGreaterThan(-1);

        var callSite = source.Substring(start, Math.Min(900, source.Length - start));

        await Assert.That(callSite).Contains("activeProfile?.DisableMemoryIndex is true");

        // Comment lines stripped before the ban: the call site's own comment names the rejected
        // ResolvedProfile to explain WHY it is wrong, and must not be read as a use of it.
        var code = string.Join('\n', callSite.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        await Assert.That(code).DoesNotContain("ResolvedProfile");
    }
}
