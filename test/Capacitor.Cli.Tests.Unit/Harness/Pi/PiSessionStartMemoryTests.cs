using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Commands;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Harness.Pi;

/// <summary>
/// The Pi memory-injection contract, two halves with no compiler between them: the CLI writes a RAW
/// fragment to stdout on session-start, and the generated kcap.ts extension captures it and appends it
/// to each turn's chained system prompt in before_agent_start. The extension half is asserted as TEXT
/// (PiExtensionInstallerTests / Task 2); only the gated live cert (PiMemoryIndexLiveCertTests, Task 3)
/// proves model receipt.
/// </summary>
public class PiSessionStartMemoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }


    // Instance, not static: the hook writes under the config dir (the repo-detection cache,
    // the lease store), so it must be handed this test's own root — which a static helper
    // cannot see, TUnit injecting it after construction.
    // The server URL is the resolution's, so a test proving the url guard fires hands in the bad one
    // here rather than as an argument.
    PiHookCommand Hook(string serverUrl = "http://localhost:5100") =>
        new(Config.Root, Resolutions.At(serverUrl, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient());
    static string Render(string? fragment) => PiHookCommand.RenderMemoryOutput(fragment);

    // Byte-identical to pre-feature behaviour on every no-index path (opt-out, failure, spent lease):
    // the extension treats marker-prefixed stdout as a fragment, so a placeholder would be appended
    // to every turn's system prompt for the rest of the session.
    [Test]
    public async Task no_fragment_writes_zero_bytes() {
        await Assert.That(Render(null)).IsEqualTo("");
    }

    [Test]
    public async Task a_fragment_is_written_raw_with_a_single_terminator() {
        await Assert.That(Render("F")).IsEqualTo("F\n");
    }

    [Test]
    [Arguments("quote \" backslash \\ text")]
    [Arguments("- item one\n- item two\n\n### Heading")]
    [Arguments("non-BMP \U0001F600 emoji")]
    public async Task a_fragment_round_trips_verbatim(string fragment) {
        await Assert.That(Render(fragment)).IsEqualTo(fragment + "\n");
    }

    // Pi's stable identity is the SESSION FILE PATH (SessionStartMemoryIdentity hashes it via
    // PiSessionPathCanonicalizer) — the uuid is deliberately NOT the lease key, because the file is
    // the one thing every lifecycle callback observably shares, resume reuses it, and fork mints a
    // new one (new file ⇒ new identity ⇒ fresh eligibility, exactly the design's fork semantics).
    [Test]
    public async Task Lifecycle_keys_on_the_rooted_session_file_path() {
        using var tmp = new TempDir();
        var file = tmp.PathTo("sessions", "20260807T101010_0a1b2c3d-1111-2222-3333-444455556666.jsonl");
        var lifecycle = PiHookCommand.LifecycleFor(file, "startup");

        await Assert.That(lifecycle.Harness).IsEqualTo(HarnessId.Pi);
        await Assert.That(lifecycle.SessionId).IsEqualTo(file);
        await Assert.That(lifecycle.IsTopLevel).IsTrue();
        await Assert.That(lifecycle.ClassificationAuthoritative).IsTrue();
        // Durable-lease dedupe, not one-shot: Pi restarts and resumes re-fire session_start for the
        // same file, and only the lease makes the repeat a no-op.
        await Assert.That(lifecycle.CallbackMayRepeat).IsTrue();

        await Assert.That(SessionStartMemoryLifecyclePolicy.Decide(lifecycle))
            .IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    // A relative/garbage path cannot canonicalize ⇒ identity null ⇒ policy answers RetryLaterNoCommit
    // ⇒ no lease is spent and no fetch runs. Fail-open in the direction that matters.
    [Test]
    public async Task An_unrooted_file_path_is_not_a_stable_identity() {
        var lifecycle = PiHookCommand.LifecycleFor("relative/sessions/x.jsonl", "startup");
        await Assert.That(SessionStartMemoryLifecyclePolicy.Decide(lifecycle))
            .IsEqualTo(SessionMemoryLifecycleDecision.RetryLaterNoCommit);
    }

    // Pi reasons (pinned upstream: startup|reload|new|resume|fork) map onto the shared vocabulary;
    // anything unrecognized degrades to RepeatedTurnCallback — NEVER Unknown, which the policy treats
    // as retry-later and would re-run the fetch decision on every callback forever.
    [Test]
    [Arguments("new", SessionLifecycleReason.New)]
    [Arguments("startup", SessionLifecycleReason.New)]
    [Arguments("resume", SessionLifecycleReason.Resume)]
    [Arguments("fork", SessionLifecycleReason.Fork)]
    [Arguments("reload", SessionLifecycleReason.Reopen)]
    [Arguments(null, SessionLifecycleReason.RepeatedTurnCallback)]
    [Arguments("someday-a-new-reason", SessionLifecycleReason.RepeatedTurnCallback)]
    internal async Task Pi_reasons_map_onto_the_shared_vocabulary(string? reason, SessionLifecycleReason expected) {
        using var tmp = new TempDir();
        var file = tmp.PathTo("s", "20260807T101010_0a1b2c3d-1111-2222-3333-444455556666.jsonl");
        await Assert.That(PiHookCommand.LifecycleFor(file, reason).Reason).IsEqualTo(expected);
    }

    // The cross-runtime negotiation: an older installed kcap.ts (which discards stdout) sends no flag,
    // so no fetch happens and no lease is spent for output nobody delivers.
    [Test]
    [Arguments(new[] { "--event", "session-start" }, 0)]
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "1" }, 1)]
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "2" }, 2)]
    [Arguments(new[] { "--event", "session-start", "--memory-contract", "abc" }, 0)]
    [Arguments(new[] { "--event", "session-start", "--memory-contract" }, 0)]
    public async Task Memory_contract_defaults_to_zero(string[] args, int expected) {
        await Assert.That(PiHookCommand.MemoryContractOf(args)).IsEqualTo(expected);
    }

    // Guard clauses that must short-circuit BEFORE any client is built: an unusable URL should
    // spend no budget, no lease, and start no task — the hook still owes its harness an output contract.
    [Test]
    public async Task Memory_task_short_circuits_without_prerequisites() {
        // The url / scope / budget guards suppress even with guidelines ENABLED; disabled alone
        // does not (a single lane off still fetches the other) — both off is required.
        await Assert.That(await Hook("not a url").StartMemoryIndexTask(
            "/abs/file.jsonl", "/scope", disabled: false, guidelinesDisabled: false,
            TimeSpan.FromSeconds(2), null)).IsNull();
        await Assert.That(await Hook().StartMemoryIndexTask("/abs/file.jsonl", scopeRoot: null, disabled: false, guidelinesDisabled: false,
            TimeSpan.FromSeconds(2), null)).IsNull();
        await Assert.That(await Hook().StartMemoryIndexTask("/abs/file.jsonl", "/scope", disabled: true, guidelinesDisabled: true,
            TimeSpan.FromSeconds(2), null)).IsNull();
        await Assert.That(await Hook().StartMemoryIndexTask("/abs/file.jsonl", "/scope", disabled: false, guidelinesDisabled: false,
            TimeSpan.Zero, null)).IsNull();
    }
}
