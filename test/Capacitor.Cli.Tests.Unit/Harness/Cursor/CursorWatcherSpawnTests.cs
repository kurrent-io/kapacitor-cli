using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// Task 9 — the precedence-ordered per-session Cursor watcher spawn.
/// <see cref="CursorHookCommand.ShouldSpawnWatcher"/> is pure (no I/O); the
/// <see cref="CursorHookCommand.MaybeSpawnWatcherAsync"/> tests use
/// <see cref="WatcherManager.SpawnOverrideForTesting"/> so no real OS process is ever spawned.
/// [NotInParallel] because the spawn override is process-wide. Tests in other
/// classes also mutate those values, so a class-specific constraint key is not sufficient.
/// </summary>
[NotInParallel]
public class CursorWatcherSpawnTests {
    [TempHome] public required TempHome Home { get; init; }

    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    CursorHookCommand Hook(ConfigRoot root) => new(root, Resolutions.At("http://s", root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient());

    [Test]
    public async Task SessionEnd_never_spawns() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: false)).IsFalse();

    [Test]
    public async Task SessionEnd_never_spawns_even_for_a_correlated_child() =>
        // Precedence ①: terminal beats everything, including a would-be child spawn.
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionEnd", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task Correlated_child_never_spawns_toplevel() =>
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: true)).IsFalse();

    [Test]
    public async Task NonTerminal_toplevel_spawns() {
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("sessionStart", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("afterAgentResponse", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("beforeSubmitPrompt", isSubagentChild: false)).IsTrue();
        await Assert.That(CursorHookCommand.ShouldSpawnWatcher("postToolUse", isSubagentChild: false)).IsTrue();
    }

    [Test]
    public async Task Spawn_is_suppressed_when_session_is_quarantined() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        Markers.Quarantine(sid, "test");
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await Hook(Config.Root).MaybeSpawnWatcherAsync(sid, "/tmp/qsid.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_sessionEnd() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await Hook(Config.Root).MaybeSpawnWatcherAsync(sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionEnd", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_for_a_correlated_child() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await Hook(Config.Root).MaybeSpawnWatcherAsync(sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: true);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task Spawn_is_suppressed_when_transcript_path_is_empty() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await Hook(Config.Root).MaybeSpawnWatcherAsync(sid, "", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEmpty();
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    [Test]
    public async Task NonQuarantined_toplevel_spawn_invokes_the_watcher_manager_keyed_on_the_session_id() {
        var sid     = NewSessionId();
        var spawned = new List<string>();
        WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        try {
            await Hook(Config.Root).MaybeSpawnWatcherAsync(sid, "/tmp/x.jsonl", cwd: null, eventName: "sessionStart", isSubagentChild: false);
            await Assert.That(spawned).IsEquivalentTo([sid]);
        } finally { WatcherManager.SpawnOverrideForTesting = null; }
    }

    // Task 12's invariant — no child watcher before the diverted subagent-start is acked, and
    // once acked the key is {parent}-{child} — is covered by
    // Deferred_spool_drain_delivering_a_spooled_subagent_start_spawns_the_child_watcher below,
    // which drives BOTH halves through real drain attempts: a first HandleCore whose drain retries
    // the start and gets a 503 (asserting the attempt happened, the entry stayed queued, and no
    // watcher spawned), then a second whose drain succeeds and must spawn {parent}-{child}. The
    // divert's own nonterminal no-ack gate is separately covered by
    // Later_nonterminal_child_hook_does_not_spawn_when_never_acked.
    //
    // Two tests that asserted the same thing by invoking the divert's start arm with a child
    // `sessionStart` were removed: a real Cursor subagent child never fires that event, so they
    // encoded a trigger that cannot occur. See
    // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
    [Test]
    public async Task Child_watcher_not_spawned_when_the_parent_session_is_quarantined() {
        using var tmp = new TempDir();
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client   = new HttpClient(handler);
            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = tmp.PathTo($"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");
            Markers.Quarantine(parent, "test");

            // Seed the ack so the no-ack gate cannot be what suppresses the spawn — otherwise
            // this test would pass for the wrong reason and prove nothing about quarantine. Then
            // drive a NON-lifecycle hook: the self-heal spawn path a real child actually reaches.
            // (This previously used a child `sessionStart`, an event a real Cursor subagent child
            // never fires.)
            Markers.MarkSubagentStartAcked(child);

            var spool = new HookSpool(tmp.PathTo("spool"));
            await Hook(Config.Root).HandleSubagentChildEventAsync(
                client, spool, child, "afterAgentThought", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    // Task 12 — the deferred half of the acked-start gate: a subagent-start that failed
    // its first live POST (spooled by HandleSubagentChildEventAsync) must still spawn the child
    // watcher once a LATER hook invocation's generic spool drain (HandleCore, top of method —
    // runs before the isSubagentChild divert) finally delivers it. Exercises the real dispatcher
    // end to end rather than calling HandleSubagentChildEventAsync directly.
    [Test]
    public async Task Deferred_spool_drain_delivering_a_spooled_subagent_start_spawns_the_child_watcher() {
        using var tmp = new TempDir();
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child  = NewSessionId();
            var parent = NewSessionId();
            var childFile = tmp.PathTo($"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Seed the persisted link so TryLoadLink activates the divert without re-running the
            // correlator. This models a marker already on disk — NOT a marker produced by a child
            // sessionStart, which is an event a real Cursor subagent child never fires.
            CursorLiveSubagentLinker.SaveLink(Config.Root, child, parent, "task");

            var startFails    = false;
            var startAttempts = 0;
            using var handler = new StubHandler((req, _) => {
                if (req.RequestUri!.AbsolutePath == "/hooks/subagent-start") {
                    startAttempts++;
                    if (startFails) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            // Seed the undelivered subagent-start DIRECTLY (as a prior transient POST failure
            // would have left it), rather than producing one by driving the child's own
            // sessionStart. A real Cursor subagent child never fires sessionStart, so using it
            // as the vehicle would bake in a trigger that cannot occur — see
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            spool.Append(child, "subagent-start",
                $$"""{"hook_event_name":"subagent_start","session_id":"{{parent}}","agent_id":"{{child}}","transcript_path":"{{childFile.Replace(@"\", @"\\")}}"}""");

            // FIRST invocation: the drain RETRIES the seeded start and the server 503s, so it
            // stays queued. Asserting "no spawn" only means something after a real drain attempt
            // — asserting it straight after Append (as this test previously did) could not fail,
            // since no production code had run yet.
            startFails = true;
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{child}}","tool_name":"Bash"}"""),
                spool);

            await Assert.That(startAttempts).IsGreaterThan(0);   // the drain really tried...
            await Assert.That(spool.HasBacklog(child)).IsTrue(); // ...and the start is still queued
            await Assert.That(spawned).IsEmpty();                // so NO watcher yet

            // SECOND invocation: the server recovers, the drain redelivers the start FIRST
            // (before the isSubagentChild divert even runs), and that success is what must
            // trigger the deferred spawn.
            startFails = false;
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{child}}","tool_name":"Bash"}"""),
                spool);

            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    // a subagent-start that hits a non-transient 4xx on retry (via
    // HandleCore's generic top-of-method spool drain) is permanently DROPPED — HookSpool removes
    // the entry, so HasBacklog goes false even though no AgentSubsession stream was ever opened
    // server-side. Before the fix, that emptied backlog let the child's own content-less hooks
    // (and its own subagent-stop) run the agent-routed transcript backfill unconditionally. The
    // fix gates on the durable ack marker instead of "no backlog", so a dropped start must
    // permanently block ALL child transcript delivery — not just the watcher spawn.
    [Test]
    public async Task Permanently_dropped_subagent_start_gates_all_child_transcript_delivery_forever() {
        using var tmp = new TempDir();
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = tmp.PathTo($"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Pre-link the child to its parent so every hook for `child` diverts through
            // HandleSubagentChildEventAsync without needing the correlator to re-run.
            CursorLiveSubagentLinker.SaveLink(Config.Root, child, parent, "task");

            var transcriptPosts = 0;
            using var handler = new StubHandler((req, _) => {
                // Every subagent-start attempt 400s: a non-transient 4xx, which HookSpool treats
                // as a permanent Drop (the entry is discarded, not re-queued).
                if (req.RequestUri!.AbsolutePath == "/hooks/subagent-start") {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                if (req.RequestUri!.AbsolutePath == "/hooks/transcript") {
                    transcriptPosts++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                return req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });
            using var client = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            var childFileEscaped = childFile.Replace(@"\", @"\\");

            // Seed the undelivered subagent-start DIRECTLY, as a prior transient POST failure
            // would have left it. Driving the child's own sessionStart to produce it would bake
            // in a trigger a real Cursor child never fires — see
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            spool.Append(child, "subagent-start",
                $$"""{"hook_event_name":"subagent_start","session_id":"{{parent}}","agent_id":"{{child}}","transcript_path":"{{childFileEscaped}}"}""");

            await Assert.That(spawned).IsEmpty();

            // 2nd invocation: any later hook for this child. HandleCore's generic top-of-method
            // spool drain retries the spooled subagent-start FIRST — this time it 400s, which
            // HookSpool treats as a permanent Drop (the entry is discarded, not re-queued).
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"afterAgentThought","session_id":"{{child}}","generation_id":"g","text":"t","transcript_path":"{{childFileEscaped}}"}"""),
                spool);

            // 3rd invocation: another content-less hook. Before the fix, the dropped start left
            // HasBacklog false and this would run the agent-routed transcript backfill despite
            // SubagentStarted never having been appended.
            await new CursorHookCommand(Config.Root, Resolutions.At("http://s", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(
                client,
                new StringReader($$"""{"hook_event_name":"postToolUse","session_id":"{{child}}","tool_name":"Bash","transcript_path":"{{childFileEscaped}}"}"""),
                spool);

            await Assert.That(spawned).IsEmpty();      // never acked -> no child watcher ever
            await Assert.That(transcriptPosts).IsEqualTo(0); // never acked -> no child transcript ever
            await Assert.That(Markers.HasSubagentStartAck(child)).IsFalse();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    // Once subagent-start is acked, every LATER NONTERMINAL hook for the same child must attempt
    // to (re)spawn its watcher. Before the fix the spawn was attempted only on the start arm —
    // which a real Cursor subagent child never reaches, since it fires no sessionStart — so a
    // child watcher that later exited (the newly-enabled idle ceiling), crashed, or never started
    // (the acking invocation carried no transcript path) was never restarted. The nonterminal
    // hooks exercised here are the ones a real child actually fires.
    [Test]
    public async Task Later_nonterminal_child_hook_self_heals_a_dead_or_never_started_child_watcher_via_the_ack_marker() {
        using var tmp = new TempDir();
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = tmp.PathTo($"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");

            // Subagent-start was acked in an EARLIER process invocation (durable marker) — the
            // watcher spawned then may since have died; this is a LATER, separate hook call.
            Markers.MarkSubagentStartAcked(child);

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client  = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            await Hook(Config.Root).HandleSubagentChildEventAsync(
                client, spool, child, "postToolUse", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEquivalentTo([$"{parent}-{child}"]);
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    // A later nonterminal hook for a child WITHOUT the ack marker must still self-heal nothing —
    // the existing no-ack gate (review fix #5's round-1 sibling) already returns before this
    // point, so the self-heal spawn never even gets a chance to run.
    [Test]
    public async Task Later_nonterminal_child_hook_does_not_spawn_when_never_acked() {
        using var tmp = new TempDir();
        try {
            var spawned = new List<string>();
            WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

            var child     = NewSessionId();
            var parent    = NewSessionId();
            var childFile = tmp.PathTo($"{child}.jsonl");
            await File.WriteAllTextAsync(childFile, """{"role":"assistant","message":{"content":[]}}""" + "\n");
            // No CursorMarkers.MarkSubagentStartAcked call — never acked.

            using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var client  = new HttpClient(handler);
            var spool = new HookSpool(tmp.PathTo("spool"));

            await Hook(Config.Root).HandleSubagentChildEventAsync(
                client, spool, child, "postToolUse", childFile, parent, "task",
                budgetExpired: () => false, ct: CancellationToken.None);

            await Assert.That(spawned).IsEmpty();
        } finally {
            WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> impl) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return impl(request, body);
        }
    }
}
