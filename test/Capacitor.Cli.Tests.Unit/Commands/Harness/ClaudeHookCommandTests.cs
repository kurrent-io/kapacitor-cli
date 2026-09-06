using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Tests.Unit.SessionStartMemory;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Policy;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

// Shares the AuthProviderDiscoveryCache cohort with the rest of the assembly's /auth/config
// stubbers, so this class's own stub cannot race one of theirs.
[NotInParallel("AuthProviderDiscoveryCache")]
public class ClaudeHookCommandTests {

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    /// <summary>A hook clock that has notionally been running for <paramref name="elapsed"/>. The
    /// waits themselves still run on the real timer — only the budget arithmetic comes off the wall
    /// clock, which is what lets a near-exhausted ceiling be asserted without sleeping into it.</summary>
    static HookClock Aged(TimeSpan elapsed) {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);
        time.Advance(elapsed);
        return clock;
    }

    // The harness nudge fires unless an on-disk stamp throttles it, and a private root starts with
    // none — these tests assert on the memory fragment alone. Claim the window explicitly instead of
    // relying on whichever sibling test happened to claim it in the shared config dir first.
    static void ThrottleHarnessNudge(ConfigRoot root) =>
        new HarnessOfferStore(root).TryClaimCheck(HarnessNudgeEmitter.CheckThrottle);

    [Before(Test)]
    public void ThrottleNudgeForThisRoot() => ThrottleHarnessNudge(Config.Root);

    [Test]
    public async Task session_start_posts_to_session_start_route() {
        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    /// <summary>Pins the seam's position: <c>PreToolUse</c> is decided before a client is ever built,
    /// so an unreachable server or a slow auth probe — both of which return early from
    /// <c>HandleWithDeps</c> — cannot silently disable a deny. The injected factory throws, so any
    /// client construction ahead of the branch fails the test rather than passing quietly.</summary>
    [Test]
    public async Task pre_tool_use_is_decided_before_any_client_is_created() {
        using var fx = new Fixture(Config.Root);
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();

        var exit = await new ClaudeHookCommand(Config.Root, fx.Profiles, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(fx.Spool, new StringReader(
                $$"""{"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash","tool_input":{"command":"git push --force"},"cwd":"/tmp"}"""),
                () => throw new InvalidOperationException("the seam must decide before a client exists"),
                stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).Contains("\"permissionDecision\":\"deny\"");
        // Spool-first: the decision is a local line, and nothing reached the network.
        await Assert.That(new HookSpool(Config.Root).HasBacklog(Sid)).IsTrue();
        await Assert.That(fx.ServerRequestCount).IsEqualTo(0);
        await Assert.That(fx.RouteOrder).IsEmpty();
    }

    /// <summary>The seam decides ahead of every other fail-open boundary in this method, so an
    /// unforeseen throw inside its branch must still exit 0. A non-zero exit is Claude's opaque
    /// hook-error banner, and for a natively auto-allowed tool the deny that never got written
    /// lets the call run.</summary>
    [Test]
    public async Task pre_tool_use_fails_open_when_the_branch_throws() {
        using var fx = new Fixture(Config.Root);
        File.WriteAllText(Config.Root.Path("approvals.yaml"),
            "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new ClosedPipeWriter();

        var exit = await new ClaudeHookCommand(Config.Root, fx.Profiles, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(fx.Spool, new StringReader(
                $$"""{"hook_event_name":"PreToolUse","session_id":"{{Sid}}","tool_name":"Bash","tool_input":{"command":"git push --force"},"cwd":"/tmp"}"""),
                () => throw new InvalidOperationException("the seam must decide before a client exists"),
                stdout);

        await Assert.That(exit).IsEqualTo(0);
        // Non-vacuous: the exit came out of the catch, not from a branch that never reached the write.
        await Assert.That(stdout.Attempted).IsTrue();
    }

    // ── Approval-policy lifecycle: build at session-start, expire per turn, evict at session-end ──

    /// <summary>The snapshot is built eagerly at session-start and a loss surfaces on the hook's own
    /// stdout, which must stay a single JSON object.</summary>
    [Test]
    public async Task session_start_surfaces_a_degraded_policy_snapshot() {
        // A server-scope field in a user document — parsed, then refused, so the file is dropped
        // with a degradation rather than silently ignored.
        File.WriteAllText(Config.Root.Path("approvals.yaml"), "version: 1\nenforcement: strict\n");
        using var fx = new Fixture(Config.Root);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).Contains("approval policy degraded");
        // Built eagerly, not on the first tool call: the snapshot that governs the session is frozen here.
        await Assert.That(new PolicySnapshotStore(Config.Root).TryLoad(Sid)).IsNotNull();
    }

    /// <summary>A degraded snapshot and a server-rejected credential both want the session-start
    /// stdout. Claude reads that stdout as a single value, so the two notices share one object or
    /// one of them is lost — and a second object would cost the reader both.</summary>
    [Test, NotInParallel]
    public async Task session_start_merges_a_policy_degradation_into_the_401_notice() {
        File.WriteAllText(Config.Root.Path("approvals.yaml"), "version: 1\nenforcement: strict\n");
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        var written = JsonNode.Parse(lines[0]) as JsonObject;
        await Assert.That(written).IsNotNull();
        var message = written!["systemMessage"]!.GetValue<string>();
        await Assert.That(message).Contains(AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid));
        await Assert.That(message).Contains("approval policy degraded");
    }

    /// <summary>The degraded arm never reaches HandleCore, so without its own freeze the first
    /// PreToolUse would build the snapshot against files edited since the session began — the
    /// per-session freeze silently lost exactly when the server is unreachable.</summary>
    [Test]
    public async Task session_start_freezes_the_policy_snapshot_on_the_degraded_arm() {
        File.WriteAllText(Config.Root.Path("approvals.yaml"), "version: 1\nenforcement: strict\n");
        using var fx = new Fixture(Config.Root);
        var stdout = new StringWriter { NewLine = "\n" };

        // Unusable URL: no client is ever built, so HandleWithDeps returns from the degraded arm.
        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("not-a-url", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .HandleWithDeps(fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
                () => throw new InvalidOperationException("no client is buildable for an unusable URL"),
                stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(new PolicySnapshotStore(Config.Root).TryLoad(Sid)).IsNotNull();
        // The degradation still reaches the user, as one object — this arm writes no other.
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(lines[0])!["systemMessage"]!.GetValue<string>())
            .Contains("approval policy degraded");
    }

    [Test]
    public async Task stop_clears_the_turn_journal() {
        var journal = new PolicyDecisionJournal(Config.Root);
        journal.RecordAsk(Sid, null, "h1");
        await Assert.That(File.Exists(Config.Root.Path("policy", "journal", $"{Sid}.json"))).IsTrue();
        using var fx = new Fixture(Config.Root);

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(journal.Consume(Sid, null, "h1").PendingAsk).IsFalse();
    }

    [Test]
    public async Task session_end_stamps_the_policy_pass_through_count() {
        new PolicyDecisionJournal(Config.Root).IncrementPassThrough(Sid);
        using var fx = new Fixture(Config.Root);

        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","cwd":"/tmp"}""");

        await Assert.That(exit).IsEqualTo(0);
        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-end|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["policy_pass_through_count"]!.GetValue<long>()).IsEqualTo(1L);
    }

    /// <summary>Session-end is the only eviction the policy directories get, so what it leaves
    /// behind accumulates for the life of the config dir.</summary>
    [Test]
    public async Task session_end_evicts_the_sessions_policy_files() {
        var store = new PolicySnapshotStore(Config.Root);
        store.LoadOrBuild(Sid, null);
        var journal = new PolicyDecisionJournal(Config.Root);
        journal.RecordAsk(Sid, "call-1", "h1");
        var marker = Config.Root.Path("policy", "uploaded", $"{Sid}-0123456789abcdef");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "");
        await Assert.That(store.TryLoad(Sid)).IsNotNull();
        await Assert.That(File.Exists(Config.Root.Path("policy", "journal", $"{Sid}.json"))).IsTrue();

        using var fx = new Fixture(Config.Root);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","cwd":"/tmp"}""");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(store.TryLoad(Sid)).IsNull();
        await Assert.That(journal.Consume(Sid, "call-1", "h1").ExactOutcome).IsNull();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task memory_store_initialization_failure_does_not_suppress_session_start_capture() {
        using var fx = new Fixture(Config.Root);
        MemoryStoreProbe.Poison(Config.Root);

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    [Test, NotInParallel]
    public async Task disabled_memory_index_does_not_construct_the_lease_store() {
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]"""; // decoy — must never be fetched
        var hook = new ClaudeHookCommand(
            Config.Root, Resolutions.Of(new Profile { DisableMemoryIndex = true }, serverUrl: "http://localhost"), new HookClock(TimeProvider.System), Home,
            new FixedCapacitorHttpClient());

        var exit = await hook.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""));

        await Assert.That(exit).IsEqualTo(0);
        // The store creates its root on construction, so an absent directory is the guard having
        // returned before it was built — the provider would also decline, but only later.
        await Assert.That(MemoryStoreProbe.WasBuilt(Config.Root)).IsFalse();
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    // ── In-agent version nudge gated on update_check ────────────────────────────────────────

    [Test, NotInParallel]
    public async Task update_check_off_suppresses_the_in_agent_nudge_even_when_server_reports_a_newer_version() {
        using var fx = new Fixture(Config.Root) { RespondJson = """{"version": "999.0.0"}""" };
        var stdout = new StringWriter();

        var hook = new ClaudeHookCommand(
            Config.Root, Resolutions.Of(new Profile { UpdateCheck = false }, serverUrl: fx.MemoryServerUrl), new HookClock(TimeProvider.System), Home,
            new FixedCapacitorHttpClient());

        var exit = await hook.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),

            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var ctx = stdout.ToString();
        await Assert.That(ctx).DoesNotContain("999.0.0");
        await Assert.That(ctx).DoesNotContain("kcap update");
    }

    /// <summary>Non-vacuous control for the test above: with update_check on (the default), the
    /// same newer-version server response still produces the in-agent nudge fragment — proves the
    /// gate suppresses the fragment specifically because of the opt-out, not because the fixture
    /// never produces one.</summary>
    [Test, NotInParallel]
    public async Task update_check_on_still_emits_the_in_agent_nudge_for_a_newer_server_version() {
        using var fx = new Fixture(Config.Root) { RespondJson = """{"version": "999.0.0"}""" };
        var stdout = new StringWriter();

        var hook = new ClaudeHookCommand(
            Config.Root, Resolutions.Of(new Profile { UpdateCheck = true }, serverUrl: fx.MemoryServerUrl), new HookClock(TimeProvider.System), Home,
            new FixedCapacitorHttpClient());

        var exit = await hook.HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),

            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var ctx = stdout.ToString();
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("kcap update");
    }

    // ── SessionStart team-memory index: behavioral baseline ─────────────────────────────────
    // Characterizes today's byte-level SessionStart output on the shared
    // SessionStartMemoryOrchestrator/ContextProvider/LeaseStore foundation (StartMemoryIndexTask
    // below) — memory-index GET runs parallel with the session-start POST, joined within the hook
    // budget, composed with lessons/version-nudge into one hookSpecificOutput.additionalContext
    // envelope — so a future change to that wiring can't silently regress it.

    [Test, NotInParallel]
    public async Task session_start_joins_lessons_nudge_and_memory_fragments_in_order() {
        using var fx = new Fixture(Config.Root);
        const string responseJson =
            """{"top_clusters":[{"text":"seal secrets","category":"safety"},{"text":"run tests first","category":"agent_guidance"}],"version":"999.999.999"}""";
        fx.RespondJson = responseJson;
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        var responseNode     = JsonNode.Parse(responseJson);
        var expectedLessons  = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled: false);
        var expectedNudge    = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.CurrentDisplay());
        var expectedMemory   = MemoryIndexEmitter.BuildFragment(JsonNode.Parse(fx.MemoryIndexBody), disabled: false);
        var expectedEnvelope = SessionStartAdditionalContext.BuildEnvelope(expectedLessons, expectedNudge, expectedMemory);

        // Byte-exact: today's wiring order is lessons, then nudge, then memory — joined by
        // BuildEnvelope and written via a single Console.WriteLine (hence the trailing "\n").
        await Assert.That(stdout).IsEqualTo(expectedEnvelope + "\n");

        var ctx        = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        var lessonsIdx = ctx.IndexOf("## Known patterns", StringComparison.Ordinal);
        var nudgeIdx   = ctx.IndexOf("newer kcap version", StringComparison.Ordinal);
        var memoryIdx  = ctx.IndexOf("Team memory", StringComparison.Ordinal);
        await Assert.That(lessonsIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(nudgeIdx).IsGreaterThan(lessonsIdx);
        await Assert.That(memoryIdx).IsGreaterThan(nudgeIdx);
    }

    // Cross-vendor isolation: Claude's guidelines come from the hook POST response (top_clusters), NOT
    // the /api/repositories/{hash}/guidelines GET the eight non-Claude harnesses use. Claude keeps a
    // memory-only provider, so it must NEVER issue a guidelines GET — pinned here so a future refactor
    // can't silently route Claude through the composite and double-fetch/reorder its envelope.
    [Test, NotInParallel]
    public async Task session_start_never_issues_a_guidelines_get() {
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = """{"top_clusters":[{"text":"seal secrets","category":"safety"}],"version":"1.0.0"}""";
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, _) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        var paths = fx.Sent.Select(s => s.Split('|', 2)[0]).ToList();
        await Assert.That(paths.Any(p => p.Contains("/guidelines", StringComparison.Ordinal))).IsFalse();
        await Assert.That(paths.Any(p => p.Contains("/api/repositories/", StringComparison.Ordinal))).IsFalse();
    }

    [Test, NotInParallel]
    public async Task session_start_with_only_a_ready_memory_index_emits_just_the_memory_fragment() {
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}"; // no top_clusters/version — lessons and nudge fragments are both null
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        var expectedMemory   = MemoryIndexEmitter.BuildFragment(JsonNode.Parse(fx.MemoryIndexBody), disabled: false);
        var expectedEnvelope = SessionStartAdditionalContext.BuildEnvelope(null, null, expectedMemory);
        await Assert.That(stdout).IsEqualTo(expectedEnvelope + "\n");
        await Assert.That(stdout).Contains("Team memory");
    }

    [Test, NotInParallel]
    public async Task session_start_with_an_empty_memory_index_array_emits_nothing() {
        // CompleteWithoutContext disposition (a successful, empty fetch) — with no lessons/nudge
        // either, BuildEnvelope collapses to null and NOTHING is written to stdout at all.
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}";
        fx.MemoryIndexBody = "[]";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task session_start_carries_the_projects_lead_in_into_additional_context() {
        // End of the whole path: the CLI asks for projects, the server answers in the object shape,
        // and the lead-in reaches the harness envelope. The index is deliberately EMPTY, so the
        // projects are the only thing keeping the hook from writing nothing at all.
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}";
        fx.MemoryIndexBody = """{"entries":[],"projects":[{"slug":"capacitor","name":"Kurrent Capacitor"}]}""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        var query = fx.MemoryIndexQuery;
        await Assert.That(query).Contains("include=projects");
        var ctx = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains(
            "This repo belongs to project \"capacitor\" (Kurrent Capacitor). " +
            "Save learnings that span its repos with project: \"capacitor\".");
        await Assert.That(ctx).DoesNotContain("## Team memory");
    }

    [Test, NotInParallel]
    public async Task session_start_with_a_204_memory_index_response_emits_nothing() {
        // The provider special-cases 204 NoContent as CompleteWithoutContext without even
        // reading a body.
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}";
        fx.MemoryIndexStatus = HttpStatusCode.NoContent;
        fx.MemoryIndexBody = "";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task session_start_with_a_5xx_memory_index_response_emits_nothing_and_does_not_fail_the_hook() {
        // RetryableFailure disposition — fail-open: the hook still succeeds and nothing about
        // the memory fetch surfaces in the envelope (there is none, since lessons/nudge are
        // absent here too).
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}";
        fx.MemoryIndexStatus = HttpStatusCode.InternalServerError;
        fx.MemoryIndexBody = "";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsTrue();
        await Assert.That(stdout).IsEqualTo("");
    }

    [Test, NotInParallel]
    public async Task memory_fetch_is_not_repeated_on_a_second_session_start_for_the_same_session() {
        // Once the shared lease store commits a disposition — Ready OR CompleteWithoutContext —
        // for a session_id, a later SessionStart for that SAME session never re-fetches: a
        // resolved, non-repeating lifecycle is exactly-once, not "repeat until non-empty".
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = "{}";
        fx.MemoryIndexBody = "[]"; // CompleteWithoutContext on the first call
        var sid = Guid.NewGuid().ToString("N");
        var payload = $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""";

        var (exit1, stdout1) = await RunCapturingStdoutAsync(() => fx.HandleAsync(payload));
        await Assert.That(exit1).IsEqualTo(0);
        await Assert.That(stdout1).IsEqualTo("");
        await Assert.That(fx.MemoryIndexRequestCount).IsEqualTo(1);

        // A decoy non-empty index — if the second call re-fetched, this WOULD surface.
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var (exit2, stdout2) = await RunCapturingStdoutAsync(() => fx.HandleAsync(payload));
        await Assert.That(exit2).IsEqualTo(0);
        await Assert.That(stdout2).IsEqualTo("");
        await Assert.That(fx.MemoryIndexRequestCount).IsEqualTo(1); // NOT re-fetched
    }

    [Test, NotInParallel]
    public async Task memory_index_get_timing_out_does_not_suppress_lessons_or_nudge() {
        // The memory-index GET runs in parallel with the POST and is joined ONLY within the
        // remaining hook budget: a GET that outlives that budget yields a null fragment
        // (fail-open) without delaying — or breaking — the lessons fragment the same response
        // already carries.
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexDelay = TimeSpan.FromSeconds(30); // never resolves inside the session-start budget
        fx.RespondJson = """{"top_clusters":[{"text":"seal secrets","category":"safety"}]}""";

        // A fresh budget, exactly like this file's other hung-server tests
        // (e.g. subagent_stop_against_hung_server_is_spooled_within_budget) — the full ~3.5s of
        // session-start's usable budget (5s ceiling minus the 1.5s safety margin) is comfortably
        // enough for watcher-start + repo enrichment + the fast, undelayed POST, but far short of
        // the 30s memory-index delay above.
        var sid = Guid.NewGuid().ToString("N");
        var sw  = System.Diagnostics.Stopwatch.StartNew();
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5)); // did not wait out the 30s delay
        await Assert.That(stdout).Contains("## Known patterns"); // lessons fragment still injected
        await Assert.That(stdout).DoesNotContain("Team memory"); // memory fragment never joined in time
    }

    [Test, NotInParallel]
    public async Task exhausted_budget_before_the_memory_task_starts_never_touches_the_provider() {
        // When HookBudget.Remaining("session-start") is already <= 0 by the time
        // StartMemoryIndexTask is reached, the memory subsystem must never touch the network at
        // all (same short-circuit as the `disabled` guard) — and, at that point, neither can the
        // session-start POST itself, which the ordering/spool path below already covers.
        using var fx = new Fixture(Config.Root);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]"""; // decoy
        fx.RespondJson = """{"top_clusters":[{"text":"seal secrets","category":"safety"}]}""";

        var sid = Guid.NewGuid().ToString("N");
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}""",
            elapsed: TimeSpan.FromSeconds(4));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }

    [Test, NotInParallel]
    public async Task memory_index_ready_is_discarded_when_the_session_start_post_fails() {
        // GET-succeeds-but-POST-fails: the POST failure short-circuits BEFORE the response is
        // ever read, so no envelope is built at all — even a Ready memory fragment never
        // surfaces. The memory task may be left running in the background (abandoned).
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError);
        fx.MemoryIndexBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo("");
        await Assert.That(fx.SpoolFiles.Any()).IsTrue(); // still durably spooled for retry
    }

    // ── SessionStart coordination-notices lane: capability advertise + response render ───────

    [Test, NotInParallel]
    public async Task session_start_advertises_the_coordination_notices_capability_by_default() {
        using var fx = new Fixture(Config.Root);
        var sid = Guid.NewGuid().ToString("N");

        var (exit, _) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["coordination_notices"]?.GetValue<string>()).IsEqualTo("v1");
    }

    [Test, NotInParallel]
    public async Task disable_coordination_notices_omits_the_capability_from_the_post() {
        using var fx = new Fixture(Config.Root, profile: new Profile { DisableCoordinationNotices = true });
        var sid = Guid.NewGuid().ToString("N");

        var (exit, _) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["coordination_notices"]).IsNull();
    }

    [Test, NotInParallel]
    public async Task session_start_renders_coordination_notices_from_the_response() {
        using var fx = new Fixture(Config.Root) {
            RespondJson = """{"coordination_notices":[{"text":"Sam is also on AUTH-12"},{"text":"+2 more in the notification centre"}]}"""
        };
        var sid = Guid.NewGuid().ToString("N");

        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        var ctx = JsonNode.Parse(stdout)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("## Coordination notices");
        await Assert.That(ctx).Contains("- Sam is also on AUTH-12");
        await Assert.That(ctx).Contains("- +2 more in the notification centre");
    }

    /// <summary>Non-vacuous control for the opt-out: the SAME server response that renders above
    /// produces NOTHING when disable_coordination_notices is set — proving the opt-out suppresses
    /// the block (and the capability), not that the fixture never produced one.</summary>
    [Test, NotInParallel]
    public async Task disable_coordination_notices_suppresses_both_the_capability_and_the_render() {
        using var fx = new Fixture(Config.Root, profile: new Profile { DisableCoordinationNotices = true }) {
            RespondJson = """{"coordination_notices":[{"text":"Sam is also on AUTH-12"}]}"""
        };
        var sid = Guid.NewGuid().ToString("N");

        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        // Capability never sent.
        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["coordination_notices"]).IsNull();

        // Nothing rendered (no other fragments in this response either).
        await Assert.That(stdout).DoesNotContain("## Coordination notices");
        await Assert.That(stdout).DoesNotContain("Sam is also on AUTH-12");
    }

    [Test, NotInParallel]
    public async Task malformed_coordination_notices_field_does_not_fail_the_hook() {
        // Server echoes the capability token back as a bare string instead of the {text}[] array.
        using var fx = new Fixture(Config.Root) { RespondJson = """{"coordination_notices":"v1"}""" };
        var sid = Guid.NewGuid().ToString("N");

        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("## Coordination notices");
    }

    /// <summary>The capability rides only the POST (postBody); the SPOOLED body (for replay on the
    /// next hook) must stay capability-free, so a catch-up replay never makes the server mark
    /// coordination notices delivered that the replay can't render into a live agent.</summary>
    [Test, NotInParallel]
    public async Task transient_post_failure_spools_a_body_without_the_coordination_notices_capability() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError); // 5xx → transient → spooled
        var sid = Guid.NewGuid().ToString("N");

        var (exit, _) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"/tmp","source":"startup"}"""));
        await Assert.That(exit).IsEqualTo(0);

        // The live POST DID advertise the capability...
        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        await Assert.That(JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..])!["coordination_notices"]?.GetValue<string>())
            .IsEqualTo("v1");

        // ...but the spooled body (replayed later) did NOT.
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content     = await File.ReadAllTextAsync(files[0]);
        var spooledBody = JsonNode.Parse(JsonNode.Parse(content.Split('\n')[0])!["body"]!.GetValue<string>());
        await Assert.That(spooledBody!["coordination_notices"]).IsNull();
    }

    /// <summary>Redirects <see cref="Console.Out"/> to a buffer for the duration of
    /// <paramref name="action"/> (a fresh <see cref="StringWriter"/> with <c>NewLine = "\n"</c> so
    /// the captured bytes are platform-independent), restoring the original writer even if
    /// <paramref name="action"/> throws.</summary>
    static async Task<(int Exit, string Stdout)> RunCapturingStdoutAsync(Func<Task<int>> action) {
        using var capture = ConsoleOutput.StartCapture("\n");
        var exit = await action();
        return (exit, capture.GetCapturedOutput());
    }

    // the session-start payload gains a best-effort workspace_root (the git repo root
    // for cwd), used server-side by plan-artifact discovery. Fail-open: a cwd with no
    // discoverable .git entry (e.g. "/tmp") must omit the field entirely rather than send null.
    [Test]
    public async Task session_start_includes_workspace_root_when_cwd_is_inside_a_git_repo() {
        using var tmp = new TempDir();
        tmp.CreateDir(".git");
        var nested = tmp.CreateDir("nested", "dir");

        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"{{nested.Path.Replace("\\", "\\\\")}}"}""");

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["workspace_root"]?.GetValue<string>()).IsEqualTo(tmp.Path);
    }

    [Test]
    public async Task session_start_omits_workspace_root_when_cwd_has_no_git_repo() {
        using var fx = new Fixture(Config.Root);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|", StringComparison.Ordinal));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["workspace_root"]).IsNull();
    }

    // Covers the auth-hang case from the spec: the hard cap must beat an
    // uncancellable hang (e.g. TokenStore.RefreshAsync's untimed HttpClient.PostAsync).
    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        var inner = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); return 42; });
        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var exit  = await ClaudeHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var exit = await ClaudeHookCommand.WithHardCap(Task.FromResult(7), TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task session_end_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","reason":"other"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
        await Assert.That(content).Contains("ended_at");
    }

    [Test]
    public async Task session_end_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture(Config.Root);
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        // Bounded well clear of the hook's own 15s budget rather than at it: the claim is that the
        // attempt gave up instead of waiting the server's 30s hold, and a bound equal to the budget
        // it is measuring has no headroom for a loaded runner (16.2s on a Windows leg, 14.2s alone
        // on an idle machine).
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(20));
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task session_end_on_4xx_is_not_spooled() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task session_start_on_failure_is_spooled_with_minimal_body() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","source":"startup"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-start\"");
        await Assert.That(JsonNode.Parse(JsonNode.Parse(content.Split('\n')[0])!["body"]!.GetValue<string>())!["session_id"]!.GetValue<string>())
            .IsEqualTo(Sid);
    }

    [Test, NotInParallel]
    public async Task session_start_on_401_exits_zero_and_nudges_the_user_to_log_in() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),

            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);

        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid));
    }

    // ── A 401 spools ────────────────────────────────────────────────────────────────────────
    // A rejected credential is repaired by `kcap login`, so the lifecycle event that hit it is kept
    // for the drain that follows the login rather than lost with the turn. A dropped session-start
    // leaves the session with no server-side record; a dropped session-end leaves it stuck "Active".

    [Test]
    public async Task session_start_on_401_is_spooled_for_replay_after_login() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(files[0])).Contains("\"route\":\"session-start\"");
    }

    [Test]
    public async Task session_end_on_401_is_spooled_for_replay_after_login() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(files[0])).Contains("\"route\":\"session-end\"");
    }

    [Test]
    public async Task subagent_stop_on_401_is_spooled_for_replay_after_login() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(files[0])).Contains("\"route\":\"subagent-stop\"");
    }

    /// <summary>"Spooled" promises a replay, so the line may only say so when the append persisted
    /// something; a spool whose directory cannot be created must be reported as a drop. Redirects the
    /// process-global Console.Error, so it runs alone.</summary>
    [Test, NotInParallel]
    public async Task session_end_whose_spool_write_fails_reports_the_drop_not_a_spool() {
        using var fx  = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        using var tmp = new TempDir();
        tmp.CreateFile("blocker");
        var unwritable = new HookSpool(tmp.PathTo("blocker", "spool")); // a directory under a file
        using var capture = ConsoleOutput.StartErrorCapture("\n");

        await new ClaudeHookCommand(Config.Root, fx.Profiles, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, unwritable, new StringReader(
                $$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""));

        await Assert.That(capture.GetCapturedError()).Contains("session-end dropped");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("session-end spooled");
    }

    /// <summary>The drain must classify a 401 as the live post does: an entry that hits one on replay
    /// stays spooled and lands once the server accepts the credential again. Otherwise spooling at
    /// the live post only turns a visible loss into a delayed silent one.</summary>
    [Test]
    public async Task spooled_entry_that_hits_a_401_on_drain_is_replayed_once_the_server_accepts() {
        using var rejecting = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        using var accepting = new Fixture(Config.Root);
        rejecting.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        var stdout = new StringWriter { NewLine = "\n" };

        await new ClaudeHookCommand(Config.Root, rejecting.Profiles, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            rejecting.Client, AuthStatus.Ok, rejecting.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""),
            stdout: stdout);
        await Assert.That(rejecting.Spool.HasBacklog(Sid)).IsTrue();

        await new ClaudeHookCommand(Config.Root, accepting.Profiles, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            accepting.Client, AuthStatus.Ok, rejecting.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(accepting.RouteOrder).Contains("session-end");
        await Assert.That(rejecting.Spool.HasBacklog(Sid)).IsFalse();
    }

    [Test]
    public async Task pending_backlog_is_drained_on_next_hook_when_server_up() {
        using var fx = new Fixture(Config.Root); // 200 OK
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        // A fresh, unrelated stop hook with the server up flushes the backlog.
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-end"); // replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();          // delivered + cleaned
    }

    [Test]
    public async Task current_session_start_replays_before_its_session_end() {
        using var fx = new Fixture(Config.Root);
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var endIdx   = fx.RouteOrder.IndexOf("session-end");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(endIdx).IsGreaterThan(startIdx);
    }

    // CRITICAL 1: bound client creation. If the hook's client factory hangs (untimed
    // /auth/config GET or token refresh during an outage) past the hook budget, the lifecycle
    // event must still be spooled — spooling is a local disk write that needs no client.
    [Test]
    public async Task session_end_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture(Config.Root);
        // Slow factory: never completes within the cap (30s) so the budget elapses first.
        Func<Task<AuthAttempt>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => new AuthAttempt(new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);

        // 13.4s already elapsed → session-end remaining = 15 - 13.4 - 1.5 ≈ 0.1s cap.
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), Aged(TimeSpan.FromSeconds(13.4)), Home, new FixedCapacitorHttpClient()).HandleWithDeps(
            fx.Spool,
            new StringReader($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""),
            slowFactory);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10)); // well under the 15s ceiling, not the 30s factory
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
    }

    [Test]
    public async Task create_client_within_budget_returns_null_when_factory_slower_than_cap() {
        Func<Task<AuthAttempt>> slow = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => new AuthAttempt(new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(slow, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(result).IsNull();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task create_client_within_budget_returns_client_when_factory_fast() {
        var made   = new HttpClient();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(() => Task.FromResult(new AuthAttempt(made, AuthStatus.Ok)), TimeSpan.FromSeconds(2));
        await Assert.That(result).IsNotNull();
        await Assert.That(ReferenceEquals(result!.Value.Client, made)).IsTrue();
        result.Value.Client.Dispose();
    }

    const string AgentId = "a1b2c3d4";

    [Test]
    public async Task subagent_stop_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task subagent_stop_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture(Config.Root);
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        // Bounded well clear of the hook's own 5s budget rather than at it: the claim is that the
        // attempt gave up instead of waiting the server's 30s hold, and a bound equal to the budget
        // it is measuring has no headroom for a loaded runner (observed 5.24s on a Windows leg).
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task subagent_stop_on_4xx_is_not_spooled() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_without_agent_id_is_not_spooled() {
        // No agent_id → no SubagentCompleted to deliver → unchanged shared-path behavior (no spool).
        using var fx = new Fixture(Config.Root); // OK
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task spooled_subagent_stop_is_replayed_on_next_hook() {
        using var fx = new Fixture(Config.Root); // server up
        fx.Spool.Append(Sid, "subagent-stop", $$"""{"session_id":"{{Sid}}","agent_id":"{{AgentId}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("subagent-stop"); // drained + replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();           // delivered + cleaned
    }

    [Test]
    public async Task replayed_session_end_with_generate_whats_done_is_handled() {
        // Server returns generate_whats_done:false for the replayed session-end (set false to avoid process spawn).
        using var fx = new Fixture(Config.Root);
        fx.RespondJson = """{"generate_whats_done":false}""";
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture(Config.Root);
        Func<Task<AuthAttempt>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => new AuthAttempt(new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        // 3.4s already elapsed → subagent-stop remaining = 5 - 3.4 - 1.5 ≈ 0.1s cap.
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), Aged(TimeSpan.FromSeconds(3.4)), Home, new FixedCapacitorHttpClient()).HandleWithDeps(
            fx.Spool,
            new StringReader($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}"""),
            slowFactory);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task current_session_start_replays_before_subagent_stop() {
        using var fx = new Fixture(Config.Root); // server up
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var stopIdx  = fx.RouteOrder.IndexOf("subagent-stop");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(stopIdx).IsGreaterThan(startIdx);
    }

    [Test]
    public async Task subagent_stop_spooled_not_posted_when_current_session_backlog_remains() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError); // drain fails transiently → backlog remains
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");

        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");

        // The drain attempted the stranded session-start (and failed transiently, leaving backlog).
        await Assert.That(fx.RouteOrder).Contains("session-start");
        // Ordering guard fired: the fresh subagent-stop was spooled, NOT posted — so it never
        // appears in RouteOrder. (Without the guard it would be POSTed before this session's
        // stranded session-start is delivered.)
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");
        // ...and it is durably spooled.
        var all = string.Concat(fx.SpoolFiles.Select(File.ReadAllText));
        await Assert.That(all).Contains("\"route\":\"subagent-stop\"");
    }

    // Task 12 / BLOCKER-1+3 regression: the centralized ordered drain (now running on every
    // non-Codex hook, incl. --claude) can WITHHOLD a spooled session-end in the ".ordered-*" temp
    // namespace pending the transcript tail. ClaudeHookCommand.CurrentSessionHasBacklog must see that
    // withheld terminal (it now delegates to HookSpool.HasBacklog, which covers ".ordered-*") so a
    // later Claude subagent-stop for the SAME session spools BEHIND it rather than POSTing ahead of
    // the still-withheld session-end — the exact cross-spool ordering violation the blockers prevent.
    [Test]
    public async Task subagent_stop_spools_behind_a_session_end_withheld_in_the_ordered_namespace() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.OK); // server up — only the ordering guard can hold the post back
        // A withheld ordered-drain remainder, exactly as LifecycleSpoolDrain/DrainRoutesAsync leaves it.
        fx.WriteOrderedTemp(Sid, """{"route":"session-end","body":"{\"session_id\":\"withheld\"}"}""");

        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");

        // The guard saw the .ordered-* backlog: the fresh subagent-stop was spooled, NOT posted.
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");
        var all = string.Concat(fx.SpoolFiles.Select(File.ReadAllText));
        await Assert.That(all).Contains("\"route\":\"subagent-stop\"");
    }

    // ── Pre-flight auth lapse (token store already knows the credential is dead) ───────────
    // Distinct from the server-rejected (HTTP 401) arm below: HandleCore short-circuits at
    // AuthStatus.Expired/NotAuthenticated/WrongServer BEFORE any POST, so no HTTP status is
    // involved. Only session-start nudges (once per session), and WrongServer maps to the
    // NotAuthenticated wording, same as any other non-Expired lapse status.

    [Test]
    public async Task session_start_with_expired_auth_exits_zero_and_emits_the_expired_notice() {
        using var fx = new Fixture(Config.Root);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Expired, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),

            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthRejectionNotice.RecordingNotice(StoredCredentialState.Expired));
    }

    [Test]
    public async Task session_start_with_wrong_server_auth_exits_zero_and_emits_the_not_authenticated_notice() {
        using var fx = new Fixture(Config.Root);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.WrongServer, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}"""),

            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthRejectionNotice.RecordingNotice(StoredCredentialState.Missing));
    }

    [Test]
    public async Task stop_with_expired_auth_exits_zero_without_a_notice() {
        using var fx = new Fixture(Config.Root);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Expired, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    // ── Server-rejected credential (HTTP 401) ───────────────────────────────────────────────
    // A 401 is not a transient failure the user can wait out. Exiting non-zero makes Claude
    // render its opaque "non-blocking status code" banner, which says nothing about recording
    // being paused; exit 0 plus a systemMessage says exactly what to do. Only `stop` nudges on
    // this path — `notification` fires on every permission prompt, so nudging there would stack
    // duplicate notices within one turn.

    [Test]
    public async Task stop_on_401_exits_zero_and_nudges_the_user_to_log_in() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);

        var notice = JsonNode.Parse(stdout.ToString().Trim());
        await Assert.That(notice!["systemMessage"]!.GetValue<string>()).IsEqualTo(AuthRejectionNotice.RecordingNotice(StoredCredentialState.LooksValid));
    }

    [Test]
    public async Task notification_on_401_exits_zero_without_a_notice() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.Unauthorized);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"Notification","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    /// <summary>Regression guard on the arm this change must NOT touch: a real server fault keeps
    /// its bare-status stderr line and its non-zero exit, so a 500 still reads as a failure.</summary>
    [Test]
    public async Task stop_on_500_still_exits_non_zero_without_a_notice() {
        using var fx = new Fixture(Config.Root, HttpStatusCode.InternalServerError);
        var stdout = new StringWriter { NewLine = "\n" };

        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://localhost", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
            fx.Client, AuthStatus.Ok, fx.Spool, new StringReader(
                $$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","cwd":"/tmp"}"""),
            stdout: stdout);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    /// <summary>A hook stdout that has gone away mid-write — the decision is computed, and writing
    /// it out is what throws.</summary>
    sealed class ClosedPipeWriter : TextWriter {
        public bool Attempted { get; private set; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => Fail();
        public override void Write(string? value) => Fail();

        void Fail() {
            Attempted = true;
            throw new IOException("Broken pipe");
        }
    }

    sealed class Fixture : IDisposable {
        readonly TempHome      _home = new();
        readonly string         _tmpHome;
        public   ConfigRoot     Config      { get; }
        readonly string         _spoolPath;
        public   List<string>   Sent        { get; } = [];
        public   List<string>   RouteOrder  { get; } = [];
        public   HookSpool      Spool       { get; }
        public   HttpClient     Client      { get; }
        public   TimeSpan       HoldOnPost  { get; set; } = TimeSpan.Zero;
        public   string?        RespondJson { get; set; }
        readonly HttpStatusCode _postStatus;

        // The memory-index endpoint is served over real HTTP, not by the stub handler above: the
        // memory lane builds its own authenticated client rather than borrowing the hook's, so a
        // handler stub could only ever test a wiring production does not have. The POST still goes
        // through the stub client, which answers regardless of host.
        readonly WireMockServer _memoryServer = WireMockServer.Start();

        /// <summary>The URL the hook posts to — the stub client answers regardless of host, so a test
        /// building its own resolution names this rather than a second literal.</summary>
        public string MemoryServerUrl => _memoryServer.Url!;

        public string         MemoryIndexBody   { get; set; } = "[]";
        public HttpStatusCode MemoryIndexStatus { get; set; } = HttpStatusCode.OK;
        public TimeSpan       MemoryIndexDelay  { get; set; } = TimeSpan.Zero;

        /// <summary>Every request the stub server saw, for a test asserting that none arrived.</summary>
        public int ServerRequestCount => _memoryServer.LogEntries.Count;

        public bool MemoryIndexRequested    => MemoryIndexRequestCount > 0;
        public int  MemoryIndexRequestCount => _memoryServer.LogEntries
            .Count(e => e.RequestMessage.Path == "/api/memories/index");

        /// <summary>The query string of the last index GET, for a test pinning what the CLI asked for.</summary>
        public string? MemoryIndexQuery => _memoryServer.LogEntries
            .LastOrDefault(e => e.RequestMessage.Path == "/api/memories/index")?.RequestMessage.RawQuery;

        /// <summary>The resolution the hook reads its per-profile settings through. A test that
        /// needs a setting honoured passes the profile here rather than steering process-global
        /// state.</summary>
        public ProfileContext Profiles { get; }

        public Fixture(ConfigRoot config, HttpStatusCode postStatus = HttpStatusCode.OK, Profile? profile = null) {
            // The ephemeral home is what keeps the dev machine's own plugin state out — notably the
            // work-items-nudge availability gate, which reads whether the kcap plugin is effectively
            // installed under it.
            _tmpHome    = _home.Path;
            _spoolPath  = Path.Combine(_tmpHome, "spool");
            _postStatus = postStatus;
            Config      = config;
            Profiles    = profile is null
                ? Resolutions.At(_memoryServer.Url!, config)
                : Resolutions.Of(profile, serverUrl: _memoryServer.Url);
            Spool = new HookSpool(_spoolPath);
            Client = new HttpClient(new StubHandler(async (req, ct) => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/", StringComparison.Ordinal)) RouteOrder.Add(path.Replace("/hooks/", ""));
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (HoldOnPost > TimeSpan.Zero) await Task.Delay(HoldOnPost, ct);
                var resp = new HttpResponseMessage(_postStatus);
                if (RespondJson is not null) resp.Content = new System.Net.Http.StringContent(RespondJson, System.Text.Encoding.UTF8, "application/json");
                return resp;
            }));
        }

        public Task<int> HandleAsync(string stdin, TimeSpan elapsed = default) {
            StubMemoryServer();

            return new ClaudeHookCommand(Config, Profiles, Aged(elapsed), _home, new FixedCapacitorHttpClient()).HandleCore(
                Client, AuthStatus.Ok, Spool, new StringReader(stdin));
        }

        /// <summary>Registered per call, not in the constructor, so a test can set the body, status
        /// or delay after building the fixture. "None" auth keeps the real client construction off
        /// the token store.</summary>
        void StubMemoryServer() {
            _memoryServer.ResetMappings();   // mappings only — MemoryIndexRequestCount counts across calls
            _memoryServer.Given(Request.Create().WithPath("/auth/config").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json").WithBody("""{"provider":"None"}"""));

            var response = Response.Create().WithStatusCode((int) MemoryIndexStatus)
                .WithHeader("Content-Type", "application/json").WithBody(MemoryIndexBody);
            if (MemoryIndexDelay > TimeSpan.Zero) response = response.WithDelay(MemoryIndexDelay);

            _memoryServer.Given(Request.Create().WithPath("/api/memories/index").UsingGet()).RespondWith(response);
        }

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath) : [];

        /// <summary>Drops a ".ordered-*" temp (the ordered drain's withheld-remainder namespace)
        /// straight into the spool dir, simulating a session-end held back by a prior ordered pass.</summary>
        public void WriteOrderedTemp(string sid, string jsonLine) {
            Directory.CreateDirectory(_spoolPath);
            File.WriteAllText(Path.Combine(_spoolPath, $"{sid}.ordered-1-1"), jsonLine + "\n");
        }

        public void Dispose() {
            Client.Dispose();
            _memoryServer.Stop();
            _home.Dispose();
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => impl(r, ct);
    }
}
