using System.Net;
using System.Text;
using Capacitor.Cli.SessionStartMemory;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class SessionStartMemoryFoundationTests {
    /// The lanes resolve their client inside the fetch, so a test hands them one already built.
    static Func<CancellationToken, Task<HttpClient>> Lazy(HttpClient client) => _ => Task.FromResult(client);

    [Test]
    public async Task Canonical_key_distinguishes_absent_token_from_literal_text() {
        var absent = SessionStartMemoryIdentity.Create(HarnessId.Claude, "session", null);
        var literal = SessionStartMemoryIdentity.Create(HarnessId.Claude, "session", "native-session");

        await Assert.That(absent).IsNotEqualTo(literal);
        await Assert.That(absent).Matches("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Canonical_key_is_length_delimited_and_lifecycle_scoped() {
        var left = SessionStartMemoryIdentity.Create(HarnessId.Claude, "ab", "c");
        var right = SessionStartMemoryIdentity.Create(HarnessId.Claude, "a", "bc");
        var resumed = SessionStartMemoryIdentity.Create(HarnessId.Claude, "ab", "resume-2");

        await Assert.That(left).IsNotEqualTo(right);
        await Assert.That(left).IsNotEqualTo(resumed);
    }

    [Test]
    public async Task Uuid_harnesses_use_lowercase_N_identity() {
        var id = SessionStartMemoryIdentity.NormalizeSessionId(
            HarnessId.Cursor, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E");

        await Assert.That(id).IsEqualTo("a0d44a4a50594d1f9c932a1adce89c2e");
    }

    // Kiro's agentSpawn fires per prompt, so an id spelled differently between firings would mean two
    // lease keys and a re-injected index. Non-GUID ids must still be accepted (the dispatcher's id is
    // whatever Kiro sends), so Kiro shares Claude's permissive arm rather than the fail-closed one.
    [Test]
    public async Task Kiro_uuid_identity_is_canonical_across_spellings_but_still_accepts_non_uuids() {
        var dashed    = SessionStartMemoryIdentity.Create(HarnessId.Kiro, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E", null);
        var compact   = SessionStartMemoryIdentity.Create(HarnessId.Kiro, "a0d44a4a50594d1f9c932a1adce89c2e", null);
        var uppercase = SessionStartMemoryIdentity.Create(HarnessId.Kiro, "A0D44A4A50594D1F9C932A1ADCE89C2E", null);

        await Assert.That(compact).IsEqualTo(dashed);
        await Assert.That(uppercase).IsEqualTo(dashed);

        // Not fail-closed: an id that is not a GUID still yields a usable identity.
        await Assert.That(SessionStartMemoryIdentity.NormalizeSessionId(HarnessId.Kiro, "kiro-session"))
            .IsEqualTo("kiro-session");
    }

    [Test]
    public async Task Claude_uuid_identity_is_canonical_across_dashed_and_compact_forms() {
        var dashed = SessionStartMemoryIdentity.Create(
            HarnessId.Claude, "A0D44A4A-5059-4D1F-9C93-2A1ADCE89C2E", null);
        var compact = SessionStartMemoryIdentity.Create(
            HarnessId.Claude, "a0d44a4a50594d1f9c932a1adce89c2e", null);

        await Assert.That(dashed).IsEqualTo(compact);
    }

    [Test]
    public async Task Lifecycle_policy_does_not_poison_unknown_or_subagent_callbacks() {
        var unknown = SessionStartMemoryLifecyclePolicy.Decide(new(
            HarnessId.Kiro, "s", null, true, false,
            SessionLifecycleReason.Unknown, CallbackMayRepeat: true));
        var subagent = SessionStartMemoryLifecyclePolicy.Decide(new(
            HarnessId.Kiro, "s", null, false, true,
            SessionLifecycleReason.New, CallbackMayRepeat: true));
        var top = SessionStartMemoryLifecyclePolicy.Decide(new(
            HarnessId.Kiro, "s", null, true, true,
            SessionLifecycleReason.New, CallbackMayRepeat: true));

        await Assert.That(unknown).IsEqualTo(SessionMemoryLifecycleDecision.RetryLaterNoCommit);
        await Assert.That(subagent).IsEqualTo(SessionMemoryLifecycleDecision.IneligibleNoCommit);
        await Assert.That(top).IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    [Test]
    public async Task Compact_is_ineligible_in_v1() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(new(
            HarnessId.Claude, "s", null, true, true,
            SessionLifecycleReason.Compact, CallbackMayRepeat: false));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.IneligibleNoCommit);
    }

    [Test]
    public async Task Authoritative_top_level_repeated_callback_uses_the_lease_store() {
        var decision = SessionStartMemoryLifecyclePolicy.Decide(new(
            HarnessId.Kiro, "session", null, true, true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true));

        await Assert.That(decision).IsEqualTo(SessionMemoryLifecycleDecision.EligibleWithLease);
    }

    [Test]
    public async Task Typed_emitter_adds_marker_groups_and_never_accepts_bodies() {
        var entries = new[] {
            new SessionStartMemoryEntry("1", "org-rule", "org", "fact", "feedback"),
            new SessionStartMemoryEntry("2", "mine", "user", "my fact", "preference")
        };

        var fragment = MemoryIndexEmitter.BuildFragment(entries);

        await Assert.That(fragment).StartsWith("<!-- kcap-memory-index:v1 -->\n## Team memory");
        await Assert.That(fragment).Contains("### Org\n- org-rule: fact");
        await Assert.That(fragment).Contains("### Yours\n- mine: my fact");
        await Assert.That(Encoding.UTF8.GetByteCount(fragment!)).IsLessThanOrEqualTo(24 * 1024);
    }

    [Test]
    public async Task Output_adapters_match_exact_golden_bytes() {
        const string fragment = "F";

        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Claude, fragment))
            .IsEqualTo("{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Claude, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Codex, fragment))
            .IsEqualTo("{\"continue\":true,\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Cursor, fragment))
            .IsEqualTo("{\"additional_context\":\"F\"}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Copilot, fragment))
            .IsEqualTo("{\"additionalContext\":\"F\"}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Copilot, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Gemini, fragment))
            .IsEqualTo("{\"hookSpecificOutput\":{\"hookEventName\":\"SessionStart\",\"additionalContext\":\"F\"}}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Gemini, null)).IsEqualTo("{}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Kiro, fragment)).IsEqualTo("F\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Kiro, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Pi, fragment)).IsEqualTo("F\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.OpenCode, null)).IsEqualTo("");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Antigravity, fragment))
            .IsEqualTo("{\"injectSteps\":[{\"userMessage\":\"F\"}]}\n");
        await Assert.That(SessionStartMemoryOutputAdapters.Render(HarnessId.Antigravity, null)).IsEqualTo("{}\n");
    }

    [Test]
    public async Task Extension_state_is_first_nonempty_wins_and_delivers_once() {
        var state = new SessionStartMemoryExtensionState();
        await state.ObserveBridgeResultAsync("key", "first");
        await state.ObserveBridgeResultAsync("key", "second");
        await state.ObserveBridgeResultAsync("key", null);

        await Assert.That(await state.TakeForDeliveryAsync("key")).IsEqualTo("first");
        await Assert.That(await state.TakeForDeliveryAsync("key")).IsNull();
    }

    [Test]
    public async Task Lease_store_has_one_winner_and_fences_stale_owner() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var store = new SessionStartMemoryLeaseStore(root.Path, time);
        var first = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
        var blocked = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
        await Assert.That(first).IsNotNull();
        await Assert.That(blocked).IsNull();

        time.Advance(TimeSpan.FromSeconds(31));
        var replacement = await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1));
        await Assert.That(replacement).IsNotNull();
        await Assert.That(await store.CompleteAsync(first!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(await store.CompleteAsync(replacement!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(await store.TryBeginAsync(new string('a', 64), TimeSpan.FromSeconds(1))).IsNull();
    }

    [Test]
    public async Task Concurrent_lease_attempts_have_exactly_one_winner() {
        using var root = new TempDir();
        var key = new string('d', 64);
        var attempts = Enumerable.Range(0, 16)
            .Select(_ => new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System).TryBeginAsync(key, TimeSpan.FromSeconds(2)));
        var winners = (await Task.WhenAll(attempts)).Count(static lease => lease is not null);

        await Assert.That(winners).IsEqualTo(1);
    }

    [Test]
    public async Task Completion_guarantee_expires_at_thirty_day_sweep_boundary() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var store = new SessionStartMemoryLeaseStore(root.Path, time);
        var key = new string('e', 64);
        var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
        await store.CompleteAsync(lease!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromTicks(1));
        await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNull();
        time.Advance(TimeSpan.FromTicks(1));
        await store.SweepAsync(TimeSpan.FromSeconds(1));
        await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNotNull();
    }

    [Test]
    public async Task Sweep_advances_past_poison_record() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var store = new SessionStartMemoryLeaseStore(root.Path, time);
        foreach (var key in new[] { new string('a', 64), new string('c', 64) }) {
            var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
            await store.CompleteAsync(lease!, SessionStartMemoryDisposition.Ready, TimeSpan.FromSeconds(1));
        }
        var poison = root.PathTo(new string('b', 64) + ".json");
        await File.WriteAllTextAsync(poison, "not-json");
        time.Advance(TimeSpan.FromDays(30));

        await store.SweepAsync(TimeSpan.FromSeconds(1));

        await Assert.That(File.Exists(root.PathTo(new string('a', 64) + ".json"))).IsFalse();
        await Assert.That(File.Exists(poison)).IsTrue();
        await Assert.That(File.Exists(root.PathTo(new string('c', 64) + ".json"))).IsFalse();
    }

    [Test]
    public async Task Retry_pending_obeys_cooldown_and_then_heals() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero));
        var store = new SessionStartMemoryLeaseStore(root.Path, time);
        var key = new string('b', 64);
        var lease = await store.TryBeginAsync(key, TimeSpan.FromSeconds(1));
        await Assert.That(await store.RetryAsync(lease!, null, TimeSpan.FromSeconds(1))).IsTrue();
        await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNull();

        time.Advance(TimeSpan.FromSeconds(5));
        await Assert.That(await store.TryBeginAsync(key, TimeSpan.FromSeconds(1))).IsNotNull();
    }

    [Test]
    public async Task Provider_maps_empty_malformed_and_ready_responses() {
        var scope = new FixedScopeResolver("repo", "machine");
        var empty = new SessionStartMemoryContextProvider(scope,
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, "[]"))));
        var malformed = new SessionStartMemoryContextProvider(scope,
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, "[{}]"))));
        var ready = new SessionStartMemoryContextProvider(scope,
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
        var request = new SessionStartMemoryContextRequest("https://example", "/repo", false,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        await Assert.That((await empty.GetAsync(request)).Disposition)
            .IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        await Assert.That((await malformed.GetAsync(request)).Disposition)
            .IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
        var result = await ready.GetAsync(request);
        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment).Contains("- s: d");
    }

    [Test]
    public async Task Provider_omits_only_unresolved_scope_axes() {
        var handler = new CapturingHandler(HttpStatusCode.NoContent, "");
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, "machine tag"),
            Lazy(new HttpClient(handler)));

        await provider.GetAsync(new SessionStartMemoryContextRequest(
            "https://example.test/", null, false, TimeSpan.FromSeconds(1), CancellationToken.None));

        await Assert.That(handler.Uri).IsEqualTo("https://example.test/api/memories/index?machine=machine%20tag&include=projects");
    }

    [Test]
    public async Task Provider_reads_the_index_in_either_body_shape() {
        var scope = new FixedScopeResolver("repo", "machine");
        SessionStartMemoryContextProvider Provider(string body) => new(scope,
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, body))));
        const string entry = "{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}";
        var request = new SessionStartMemoryContextRequest("https://example", "/repo", false,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        // An older server ignores include=projects and answers with the bare array it always sent.
        var bareArray = await Provider($"[{entry}]").GetAsync(request);
        var withProjects = await Provider(
            $"{{\"entries\":[{entry}],\"projects\":[{{\"slug\":\"capacitor\",\"name\":\"Kurrent Capacitor\"}}]}}").GetAsync(request);
        var projectsOnly = await Provider(
            "{\"entries\":[],\"projects\":[{\"slug\":\"capacitor\",\"name\":\"Kurrent Capacitor\"}]}").GetAsync(request);
        var neither = await Provider("{\"entries\":[],\"projects\":[]}").GetAsync(request);

        await Assert.That(bareArray.Fragment).Contains("- s: d");
        await Assert.That(bareArray.Fragment).DoesNotContain("This repo belongs");
        await Assert.That(withProjects.Fragment).Contains("This repo belongs to project \"capacitor\"");
        await Assert.That(withProjects.Fragment).Contains("- s: d");
        await Assert.That(projectsOnly.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(projectsOnly.Fragment).Contains("This repo belongs to project \"capacitor\"");
        await Assert.That(neither.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    [Arguments("\"not-an-index\"")]
    [Arguments("{}")]
    [Arguments("{\"entries\":null,\"projects\":null}")]
    [Arguments("{\"unrelated\":1}")]
    public async Task Provider_retries_a_body_that_is_not_an_index(string body) {
        // An object carrying neither member must stay retryable rather than read as an empty index:
        // Empty completes the once-per-session lease, so a session would never ask again.
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver("repo", "machine"),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, body))));

        var result = await provider.GetAsync(new SessionStartMemoryContextRequest("https://example", "/repo", false,
            TimeSpan.FromSeconds(1), CancellationToken.None));

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    [Arguments("{\"entries\":[],\"projects\":[]}")]
    [Arguments("{\"entries\":[]}")]
    [Arguments("[]")]
    public async Task Provider_completes_a_genuinely_empty_index(string body) {
        // The mirror of the case above: a present-but-empty array IS an answer, and completing the
        // lease on it is what stops a session re-fetching an index the server says is empty.
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver("repo", "machine"),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, body))));

        var result = await provider.GetAsync(new SessionStartMemoryContextRequest("https://example", "/repo", false,
            TimeSpan.FromSeconds(1), CancellationToken.None));

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    /// <summary>The lane does not follow a redirect, so a 3xx arrives here as a status like any
    /// other — and it is not an answer, so it must read as retryable rather than as "no memory".</summary>
    [Test]
    public async Task Provider_reports_a_redirect_as_a_retryable_failure() {
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var redirected = await new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.Redirect, "")))).GetAsync(request);

        await Assert.That(redirected.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task Orchestrator_returns_ready_fragment_only_to_commit_winner() {
        using var root = new TempDir();
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);
        var lifecycle = new SessionMemoryLifecycle(HarnessId.Claude, "session", null,
            true, true, SessionLifecycleReason.New, true);
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var first = await orchestrator.GetFragmentAsync(lifecycle, request);
        var repeated = await orchestrator.GetFragmentAsync(lifecycle, request);

        await Assert.That(first).Contains("- s: d");
        await Assert.That(repeated).IsNull();
    }

    // A caller can only discover its fragment is undeliverable AFTER the fetch has run (Copilot's
    // lifecycle POST failing permanently means the hook exits non-zero, and Copilot reads hook stdout
    // only on a zero exit). A refused commit must therefore RELEASE the once-per-session lease rather
    // than spend it — proved behaviourally: a later start of the SAME session still gets its fragment,
    // which spending the lease makes permanently impossible.
    //
    // Released means retry_pending with a backoff, not immediately retryable, so the clock is advanced
    // past the store's 1h backoff cap rather than asserting an instant second attempt.
    [Test]
    public async Task A_refused_commit_gate_releases_the_lease_so_a_later_start_still_injects() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
        var orchestrator = new SessionStartMemoryOrchestrator(
            new SessionStartMemoryLeaseStore(root.Path, time), provider);
        var lifecycle = new SessionMemoryLifecycle(HarnessId.Copilot, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", null,
            true, true, SessionLifecycleReason.New, true);
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var refused = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(false));

        time.Advance(TimeSpan.FromHours(2));

        var retried = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));

        await Assert.That(refused).IsNull();
        await Assert.That(retried).Contains("- s: d");
    }

    // The gate must not become a second way to lose the fragment: granted behaves exactly as the
    // ungated path, including still being once-per-session.
    [Test]
    public async Task A_granted_commit_gate_commits_the_lease_exactly_as_the_ungated_path() {
        using var root = new TempDir();
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK,
                "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]"))));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);
        var lifecycle = new SessionMemoryLifecycle(HarnessId.Copilot, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", null,
            true, true, SessionLifecycleReason.New, true);
        var request = new SessionStartMemoryContextRequest(
            "https://example.test", null, false, TimeSpan.FromSeconds(1), CancellationToken.None);

        var first = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));
        var repeated = await orchestrator.GetFragmentAsync(lifecycle, request, _ => Task.FromResult(true));

        await Assert.That(first).Contains("- s: d");
        await Assert.That(repeated).IsNull();
    }

    const string OneMemoryJson =
        "[{\"memory_id\":\"1\",\"slug\":\"s\",\"audience\":\"org\",\"description\":\"d\",\"kind\":\"feedback\"}]";

    // Exactly what KiroHookCommand builds: agentSpawn fires per PROMPT, so the callback repeats and
    // the lease is the only thing preventing re-injection.
    static SessionMemoryLifecycle KiroLifecycle(string sessionId) =>
        new(HarnessId.Kiro, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true);

    static SessionStartMemoryContextRequest KiroRequest(double seconds = 1) =>
        new("https://example.test", null, false, TimeSpan.FromSeconds(seconds), CancellationToken.None);

    // THE Kiro acceptance criterion. Kiro has no once-per-session hook: agentSpawn fires on every
    // prompt with the same session id. Without the lease the index would be re-injected — and
    // re-charged — every turn, and would steadily bias the conversation.
    [Test]
    public async Task Kiro_repeated_agent_spawn_injects_once_then_yields_nothing() {
        using var root = new TempDir();
        using var handler = new CountingHandler(HttpStatusCode.OK, OneMemoryJson);
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        var first  = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
        var second = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
        var third  = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

        await Assert.That(first).Contains("- s: d");
        await Assert.That(second).IsNull();
        await Assert.That(third).IsNull();

        // Not merely "no output" — no repeat FETCH either, or every prompt would still pay the call.
        await Assert.That(handler.Sends).IsEqualTo(1);
    }

    // The lease gate runs BEFORE the client is asked for, so a repeat callback that will inject
    // nothing does no auth work either. Kiro and Pi fire this path on every prompt for the life of a
    // session, so a credential resolved ahead of the gate is a cost every one of those turns pays for
    // a fetch the gate then refuses.
    [Test]
    public async Task A_declined_repeat_callback_never_asks_for_a_client() {
        using var root    = new TempDir();
        using var handler = new CountingHandler(HttpStatusCode.OK, OneMemoryJson);
        using var client  = new HttpClient(handler);

        var asked    = 0;
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null),
            _ => { asked++; return Task.FromResult(client); });
        var orchestrator = new SessionStartMemoryOrchestrator(
            new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
        await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
        await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

        await Assert.That(asked).IsEqualTo(1);
    }

    // A genuinely new Kiro session brings a new session id, hence a new lease key. No Kiro-specific
    // "is this new?" logic exists or should: identity is the whole mechanism.
    [Test]
    public async Task Kiro_distinct_session_ids_inject_independently() {
        using var root = new TempDir();
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, OneMemoryJson))));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        var a = await orchestrator.GetFragmentAsync(KiroLifecycle("session-a"), KiroRequest());
        var b = await orchestrator.GetFragmentAsync(KiroLifecycle("session-b"), KiroRequest());

        await Assert.That(a).Contains("- s: d");
        await Assert.That(b).Contains("- s: d");
    }

    // A transient server failure must NOT burn the session's one injection — a later prompt's
    // agentSpawn recovers it. Released means retry_pending behind a backoff, so the clock is advanced
    // past the store's 1h cap rather than asserting an instant retry.
    [Test]
    public async Task Kiro_retryable_failure_lets_a_later_prompt_still_inject() {
        using var root = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero));
        using var handler = new FailsOnceHandler(OneMemoryJson);
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var orchestrator = new SessionStartMemoryOrchestrator(
            new SessionStartMemoryLeaseStore(root.Path, time), provider);

        var failed = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

        time.Advance(TimeSpan.FromHours(2));

        var recovered = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

        await Assert.That(failed).IsNull();
        await Assert.That(recovered).Contains("- s: d");
    }

    // A successful-but-empty index must still COMMIT, or a team with no memories yet would re-fetch on
    // every single Kiro prompt forever.
    [Test]
    public async Task Kiro_a_successful_empty_index_still_suppresses_later_prompts() {
        using var root = new TempDir();
        using var handler = new CountingHandler(HttpStatusCode.NoContent, "");
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());
        var second = await orchestrator.GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest());

        await Assert.That(second).IsNull();
        await Assert.That(handler.Sends).IsEqualTo(1);
    }

    // A losing agentSpawn callback must be fenced by a lease that is genuinely HELD — not merely
    // already-completed. This is ordered deterministically rather than raced, because a race is
    // exactly what cannot be asserted: an all-synchronous provider lets the winner commit before the
    // next caller is even constructed, and counting "how many callers started" proves nothing about
    // whether any of them reached the lease.
    //
    // So: start the winner, wait until its provider signals from INSIDE the fetch (at which point the
    // lease is provably held), run the losers to completion against that held lease, and only then
    // release the winner. No timeout participates in the passing path.
    [Test]
    public async Task Kiro_agent_spawns_arriving_while_the_lease_is_held_are_fenced_out() {
        using var root = new TempDir();
        var winnerHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new GatedHandler(winnerHolding, releaseWinner, OneMemoryJson);
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var store = new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System);

        var winner = Task.Run(() => new SessionStartMemoryOrchestrator(store, provider)
            .GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest(20)));

        // The winner is now inside its fetch, holding the lease.
        await winnerHolding.Task;

        var losers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            new SessionStartMemoryOrchestrator(store, provider)
                .GetFragmentAsync(KiroLifecycle("kiro-session"), KiroRequest(20))));

        // The winner is provably STILL inside its fetch, so the lease was genuinely held for the
        // whole of the losers' run — this is what separates the test from the sequential case.
        await Assert.That(winner.IsCompleted).IsFalse();

        // Every loser was refused while that lease was held — and none of them fetched.
        await Assert.That(losers.All(r => r is null)).IsTrue();
        await Assert.That(handler.Sends).IsEqualTo(1);

        releaseWinner.TrySetResult();

        await Assert.That(await winner).Contains("- s: d");
        await Assert.That(handler.Sends).IsEqualTo(1);
    }

    // Exactly what AntigravityHookCommand.LifecycleFor builds: PreInvocation fires once per
    // INVOCATION within a conversation, so the callback repeats like Kiro's agentSpawn and the
    // lease is the only thing preventing re-injection every turn.
    static SessionMemoryLifecycle AntigravityLifecycle(string sessionId) =>
        new(HarnessId.Antigravity, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true);

    static SessionStartMemoryContextRequest AntigravityRequest(double seconds = 1) =>
        new("https://example.test", null, false, TimeSpan.FromSeconds(seconds), CancellationToken.None);

    // THE Antigravity acceptance criterion, mirroring Kiro's: without the lease the index would be
    // re-injected — and re-charged — on every invocation within the same conversation.
    [Test]
    public async Task Antigravity_repeated_pre_invocation_injects_once_and_does_not_refetch() {
        using var root = new TempDir();
        using var handler = new CountingHandler(HttpStatusCode.OK, OneMemoryJson);
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        // A real GUID — Antigravity is on the fail-closed identity arm, which normalizes any
        // non-GUID id to null and would short-circuit before the lease is ever consulted,
        // making this whole test pass vacuously without proving anything.
        const string sessionId = "e80c33bfc10f4d2fb626b0043f488fc0";

        var first  = await orchestrator.GetFragmentAsync(AntigravityLifecycle(sessionId), AntigravityRequest());
        var second = await orchestrator.GetFragmentAsync(AntigravityLifecycle(sessionId), AntigravityRequest());

        await Assert.That(first).Contains("- s: d");
        await Assert.That(second).IsNull();
        // The lease must prevent the WORK, not merely the output. One fetch, not two.
        await Assert.That(handler.Sends).IsEqualTo(1);
    }

    [Test]
    public async Task Antigravity_distinct_conversations_each_inject_once() {
        using var root = new TempDir();
        var provider = new SessionStartMemoryContextProvider(new FixedScopeResolver(null, null),
            Lazy(new HttpClient(new StaticHandler(HttpStatusCode.OK, OneMemoryJson))));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);

        var a = await orchestrator.GetFragmentAsync(
            AntigravityLifecycle("e80c33bfc10f4d2fb626b0043f488fc0"), AntigravityRequest());
        var b = await orchestrator.GetFragmentAsync(
            AntigravityLifecycle("5450cb7feaf841189dec0ebd0018f024"), AntigravityRequest());

        await Assert.That(a).Contains("- s: d");
        await Assert.That(b).Contains("- s: d");
    }

    [Test]
    public async Task Antigravity_identity_is_fail_closed_on_a_non_guid() {
        // Guard for the fixture trap: a non-GUID id normalizes to null, so any assertion built on
        // such an id would pass without the code under test ever running.
        await Assert.That(
            SessionStartMemoryIdentity.NormalizeSessionId(
                HarnessId.Antigravity, "ag-test-sess-0001")).IsNull();
        await Assert.That(
            SessionStartMemoryIdentity.NormalizeSessionId(
                HarnessId.Antigravity, "e80c33bfc10f4d2fb626b0043f488fc0")).IsNotNull();
    }

    [Test]
    public async Task Disabled_request_does_not_fetch_or_write_a_lease_record() {
        using var root = new TempDir();
        using var handler = new CountingHandler(HttpStatusCode.NoContent, "");
        var provider = new SessionStartMemoryContextProvider(
            new FixedScopeResolver(null, null), Lazy(new HttpClient(handler)));
        var orchestrator = new SessionStartMemoryOrchestrator(new SessionStartMemoryLeaseStore(root.Path, TimeProvider.System), provider);
        var lifecycle = new SessionMemoryLifecycle(HarnessId.Claude, "session", null,
            true, true, SessionLifecycleReason.New, false);

        var fragment = await orchestrator.GetFragmentAsync(lifecycle,
            new SessionStartMemoryContextRequest(
                "https://example.test", null, true, TimeSpan.FromSeconds(1), CancellationToken.None));

        await Assert.That(fragment).IsNull();
        await Assert.That(handler.Sends).IsEqualTo(0);
        await Assert.That(Directory.EnumerateFiles(root.Path)).IsEmpty();
    }

    sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider {
        DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }

    sealed class FixedScopeResolver(string? repo, string? machine) : ISessionStartMemoryScopeResolver {
        public Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) =>
            Task.FromResult(new SessionStartMemoryScope(repo, machine));
    }

    sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// Counts the sends a lane actually made. The client is built once at the call site now, so a
    /// fetch is only observable at the wire — which is the question these tests ask anyway.
    /// </summary>
    sealed class CountingHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref _sends);

            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Fails the first send and succeeds afterwards, so a retry is distinguishable from a
    /// first attempt that simply worked.</summary>
    sealed class FailsOnceHandler(string body) : HttpMessageHandler {
        int _sends;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(
                    Interlocked.Increment(ref _sends) == 1 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    /// <summary>Announces that it is mid-send and stays there until released, so a caller can be held
    /// inside its fetch while others contend for the lease it holds.</summary>
    sealed class GatedHandler(TaskCompletionSource holding, TaskCompletionSource release, string body) : HttpMessageHandler {
        int _sends;

        public int Sends => Volatile.Read(ref _sends);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref _sends);
            holding.TrySetResult();
            // A suite-safety net only: never reached on the passing path, and reaching it fails the
            // test anyway, since the losers would no longer be contending for a held lease.
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler {
        public string? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Uri = request.RequestUri?.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
