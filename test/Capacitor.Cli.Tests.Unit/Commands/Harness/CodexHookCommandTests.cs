using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

// Shares the AuthProviderDiscoveryCache cohort with the rest of the assembly's /auth/config
// stubbers, so this class's own stub cannot race one of theirs.
[NotInParallel("AuthProviderDiscoveryCache")]
public class CodexHookCommandTests : IDisposable {

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Every test that mutates Console.Out or the KCAP_DAEMON_URL env
    // var is decorated [NotInParallel] (no group) so it runs strictly alone.
    // A group key was insufficient: parallel tests in *other* files (e.g.
    // ImportDisplayGridTests / CliResolverTests) still mutate the same
    // process-global state under different group keys, and the cross-group
    // race nondeterministically corrupted Console captures (CI).

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    void StubNoAuthRequired() =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

    [Test]
    public async Task SessionStart_posts_to_session_start_codex_with_normalized_session_id() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
                      {
                        "hook_event_name": "SessionStart",
                        "session_id": "019e0322-05fc-7570-be65-75719c3ea861",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp",
                        "model": "gpt-5"
                      }
                      """;

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
        await Assert.That(root.GetProperty("home_dir").GetString()).IsNotNull();

        // SessionStart also writes Codex's required JSON output to stdout.
        // We can't reliably capture stdout here because the test path spawns a
        // real watcher child process via Process.Start, which under TUnit's
        // Console capture corrupts subsequent stdout reads. The Stop test covers
        // the identical write pattern (Console.Write(SessionScopedOutputJson)),
        // and HandleSessionStart writes the same constant in the same way before
        // the watcher spawn.
    }

    // Codex 'Stop' fires at turn end, not session end. The hook now best-effort
    // POSTs to /hooks/stop so the server can emit the idle-wait marker that
    // clears the chat "working" indicator — but it must NOT POST session-end
    // (that path is reserved for the watcher's parent-exit fallback), and must
    // still emit {"continue":true} on stdout with no watcher chatter.
    //
    // Globally sequential: captures Console.Out for the duration.
    [Test, NotInParallel]
    public async Task Stop_posts_to_hooks_stop_and_emits_continue_json() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();


        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        await Assert.That(exit).IsEqualTo(0);

        // Stop now POSTs /hooks/stop exactly once, carrying the full payload.
        var stopRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
        await Assert.That(stopRequests.Count).IsEqualTo(1);
        var body = JsonDocument.Parse(stopRequests[0].RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo("abc");
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo("/tmp/rollout.jsonl");
        await Assert.That(body.GetProperty("cwd").GetString()).IsEqualTo("/tmp");
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("Stop");

        // Must NOT POST session-end.
        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);

        // Invariant: valid JSON object on stdout, no chatter.
        var stdout = capture.GetCapturedOutput();
        var doc    = JsonDocument.Parse(stdout);
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
        await Assert.That(stdout.Contains("Watcher ")).IsFalse();
        await Assert.That(stdout.Contains("Spawned")).IsFalse();
    }

    // The POST is best-effort: a slow/unreachable server must never hang the hook
    // or break its stdout contract. Cap at 2s and swallow — assert we still emit
    // {"continue":true} and return under 5s even when /hooks/stop stalls 10s.
    [Test, NotInParallel]
    public async Task Stop_still_emits_continue_json_when_server_is_slow() {
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        var payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();
        var sw           = System.Diagnostics.Stopwatch.StartNew();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        var doc = JsonDocument.Parse(capture.GetCapturedOutput());
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        // 2s POST cap + slack for CI jitter.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // Symmetric with PermissionRequest_returns_quickly_when_auth_discovery_is_slow:
    // the shared best-effort POST helper's single deadline must cover /auth/config
    // discovery for the Stop path too, not just the POST itself.
    [Test, NotInParallel]
    public async Task Stop_still_emits_continue_json_when_auth_discovery_is_slow() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();
        var sw           = System.Diagnostics.Stopwatch.StartNew();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        var doc = JsonDocument.Parse(capture.GetCapturedOutput());
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // A malformed (scheme-less) server URL must not crash the hook: it still emits
    // {"continue":true} and returns 0. The guard in PostBestEffortAsync bails before
    // any client is built, so an unusable URL spends no budget or lease.
    [Test, NotInParallel]
    public async Task Stop_with_malformed_base_url_still_emits_continue_and_returns_zero() {
        var payload = """
                      {
                        "hook_event_name": "Stop",
                        "session_id": "abc",
                        "transcript_path": "/tmp/rollout.jsonl",
                        "cwd": "/tmp"
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();

        // Scheme-less URL → IsAcceptableUrl is false → PostBestEffortAsync returns
        // before any client is built, spending no budget or lease.
        var exit = await new CodexHookCommand(Config.Root, Resolutions.At("localhost:5108", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        await Assert.That(exit).IsEqualTo(0);

        var doc = JsonDocument.Parse(capture.GetCapturedOutput());
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
    }

    // Globally sequential alongside Stop_is_turn_end_no_op_and_does_not_post_session_end —
    // see that test for why ConsoleSerialGroup alone has been observed to
    // interleave in suite runs and drop captured Console output.
    [Test, NotInParallel]
    public async Task PermissionRequest_records_event_and_yields_decision_to_codex() {
        // The Codex permission-request hook must not silently auto-allow tool
        // calls; in the stub branch (no KCAP_DAEMON_URL set) it records
        // the request server-side via /hooks/permission-record (the same
        // fire-and-forget endpoint Claude's terminal path uses) and emits an
        // empty hookSpecificOutput so Codex's normal in-CLI approval prompt
        // asks the user. Regression test for this behavior.
        var previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", null);

        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();

        try {

            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

            await Assert.That(exit).IsEqualTo(0);

            // stdout must NOT carry a hook decision — Codex falls back to its
            // own approval prompt when hookSpecificOutput.decision is absent.
            var stdout = capture.GetCapturedOutput();
            var doc    = JsonDocument.Parse(stdout);

            var hasDecision = doc.RootElement.TryGetProperty("hookSpecificOutput", out var hso)
             && hso.TryGetProperty("decision", out _);
            await Assert.That(hasDecision).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }

        // Recording must land on /hooks/permission-record, not on the
        // long-poll /hooks/permission-request/{vendor} route.
        var recorded = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-record").UsingPost());
        await Assert.That(recorded.Count).IsEqualTo(1);

        var wrong = _server.FindLogEntries(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost());
        await Assert.That(wrong.Count).IsEqualTo(0);
    }

    // the hook must return well under Codex's 30 s timeout even when
    // the recording endpoint is slow. We cap the HTTP client at 2 s and
    // swallow the resulting TaskCanceledException — guard against future
    // regressions where someone reintroduces an unbounded await.
    [Test, NotInParallel]
    public async Task PermissionRequest_returns_quickly_when_server_is_slow() {
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        var payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();
        var sw           = System.Diagnostics.Stopwatch.StartNew();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        // 2 s HTTP timeout + a generous slack for build/CI jitter.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // Qodo finding: bounding only the POST left auth discovery
    // (GET /auth/config) running on HttpClient's 100 s default. Stub
    // /auth/config slow and assert the hook still returns under 5 s — the
    // shared 2 s CTS in CodexHookCommand covers discovery too.
    //
    // Separately, this test redirects Console.Out, so it must share
    // ConsoleSerialGroup with every other Console.Out-redirecting test in
    // the suite. Previously it lived in its own "CodexPermissionRequestStdout"
    // group and raced against the ConsoleSerialGroup tests, occasionally
    // causing both this test and the unrelated stdout-asserting ones to
    // observe a stale Console.Out writer.
    [Test, NotInParallel]
    public async Task PermissionRequest_returns_quickly_when_auth_discovery_is_slow() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithDelay(TimeSpan.FromSeconds(10)));

        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "transcript_path": "/tmp/r.jsonl",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();
        var sw           = System.Diagnostics.Stopwatch.StartNew();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task UserPromptSubmit_PreToolUse_PostToolUse_are_swallowed() {
        // v1: pass-through events not consumed server-side. CLI should
        // exit 0 without making any HTTP request.
        foreach (var evt in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse" }) {
            var payload = $$"""
                            {
                              "hook_event_name": "{{evt}}",
                              "session_id": "abc",
                              "transcript_path": "/tmp/r.jsonl",
                              "cwd": "/tmp"
                            }
                            """;

            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);
        }

        // No HTTP requests should have been issued.
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_event_returns_zero_and_no_request() {
        var payload = """{"hook_event_name": "BogusEvent", "session_id": "abc"}""";

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_hook_event_name_returns_zero_silently() {
        var payload = """{"session_id": "abc"}""";

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Malformed_json_returns_zero_silently() {
        var payload = "{not json";

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
    }

    // Fix #3: non-string hook_event_name (e.g. a number) must not crash — return 0 silently.
    [Test]
    public async Task Hook_event_name_as_number_returns_zero_without_crash() {
        var payload = """{"hook_event_name": 99, "session_id": "abc", "transcript_path": "/tmp/r.jsonl"}""";

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    // Fix #3: non-string session_id in a Stop payload must not crash.
    // session_id falls to null via the safe TryGetString helper, so HandleStop
    // short-circuits before EnsureWatcherRunning. No server POST is expected
    // (Stop's session-end POST was removed), but we still stub
    // /hooks/session-end/codex so a regression that reintroduces the POST
    // surfaces as a test failure via the WireMock log assertion below.
    [Test]
    public async Task Stop_with_numeric_session_id_returns_zero_without_crash() {
        var payload = """{"hook_event_name": "Stop", "session_id": 12345, "transcript_path": "/tmp/r.jsonl"}""";

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);

        // Stop must never POST session-end, even with a malformed payload.
        var endRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(endRequests.Count).IsEqualTo(0);
    }

    // When the user runs `kcap disable`, the marker file
    // under the config root's "disabled" directory must short-circuit the Codex
    // hook the same way it does for Claude. Without this guard, the next
    // Codex Stop hook restarts the watcher (HandleStop calls
    // WatcherManager.EnsureWatcherRunning) and re-enlivens a session whose
    // data was just deleted server-side.
    //
    // Globally sequential: this test captures Console.Out, and shares the
    // same NotInParallel(ConsoleSerialGroup) lock as every other
    // stdout-redirecting test in the suite.
    [Test, NotInParallel]
    public async Task Handle_skips_dispatch_when_session_is_disabled() {
        // session_id without dashes — NormalizeGuidField is a no-op on this
        // shape, so the value written to disk matches the value the hook
        // looks up after normalization.
        const string sessionId = "disabledsess123abc";

        // Stub every route the dispatch path could hit so a regression that
        // re-enables dispatch surfaces as a recorded request.
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload = $$"""
                        {
                          "hook_event_name": "Stop",
                          "session_id": "{{sessionId}}",
                          "transcript_path": "/tmp/rollout.jsonl",
                          "cwd": "/tmp"
                        }
                        """;

        DisabledSessions.Mark(sessionId, Config.Root);

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

            await Assert.That(exit).IsEqualTo(0);

            // Disabled-session branch must skip every server POST and the
            // watcher restart.
            await Assert.That(_server.LogEntries.Count).IsEqualTo(0);

            // Codex's Stop parser still expects valid JSON on stdout.
            var stdout = capture.GetCapturedOutput();
            var doc    = JsonDocument.Parse(stdout);
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
        } finally {
            DisabledSessions.RemoveMarker(sessionId, Config.Root);
        }
    }

    // ---- Guard-1: envelope-sourced hosted session suppresses the watcher (§2.5) ----

    [Test, NotInParallel]
    public async Task Stop_skips_the_watcher_for_an_envelope_sourced_hosted_session() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var previousMarker = Environment.GetEnvironmentVariable("KCAP_HOSTED_APPSERVER");
        var spawned = new List<string>();
        Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        using var capture = ConsoleOutput.StartCapture();

        try {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", "1");
            var payload = """{"hook_event_name":"Stop","session_id":"g1-stop-suppressed","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""";

            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);

            // The watcher never spawns for an envelope-sourced session...
            await Assert.That(spawned.Count).IsEqualTo(0);
            // ...but only the watcher is suppressed: the idle-marker stop POST still fires.
            var stopRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/stop").UsingPost());
            await Assert.That(stopRequests.Count).IsEqualTo(1);

            var doc = JsonDocument.Parse(capture.GetCapturedOutput());
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", previousMarker);
            Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    [Test, NotInParallel]
    public async Task Stop_spawns_the_watcher_without_the_hosted_appserver_marker() {
        // Positive control for the suppression test above: without the marker the watcher spawns, so the
        // suppression assertion can actually fail if guard-1 regresses.
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var previousMarker = Environment.GetEnvironmentVariable("KCAP_HOSTED_APPSERVER");
        var spawned = new List<string>();
        Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };
        using var capture = ConsoleOutput.StartCapture();

        try {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", null);
            var payload = """{"hook_event_name":"Stop","session_id":"g1-stop-control","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""";

            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);

            await Assert.That(spawned.Count).IsEqualTo(1);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", previousMarker);
            Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    [Test, NotInParallel]
    public async Task SessionStart_skips_the_watcher_for_an_envelope_sourced_hosted_session() {
        StubNoAuthRequired();
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var previousMarker = Environment.GetEnvironmentVariable("KCAP_HOSTED_APPSERVER");
        var spawned = new List<string>();
        Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = key => { spawned.Add(key); return Task.CompletedTask; };

        try {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", "1");
            var payload = """{"hook_event_name":"SessionStart","session_id":"g1-start-suppressed","transcript_path":"/tmp/r.jsonl","cwd":"/tmp"}""";

            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);

            // No watcher for an envelope-sourced session; the session-start POST is untouched.
            await Assert.That(spawned.Count).IsEqualTo(0);
            var startRequests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
            await Assert.That(startRequests.Count).IsEqualTo(1);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_HOSTED_APPSERVER", previousMarker);
            Capacitor.Cli.WatcherManager.SpawnOverrideForTesting = null;
        }
    }

    // ---- PermissionRequest daemon-bridge tests ----
    //
    // The no-daemon-URL "stub" branch is covered by the regression test
    // PermissionRequest_records_event_and_yields_decision_to_codex above; it
    // asserts the new record-and-yield contract (no in-CLI decision, fall back
    // to Codex's own approval prompt). The tests below cover the new
    // daemon-bridge branch added since then.

    [Test, NotInParallel]
    public async Task PermissionRequest_with_daemon_url_set_posts_to_bridge_and_forwards_response_to_stdout() {
        using var bridge = WireMockServer.Start();
        var       token  = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""")
            );

        var previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capture.GetCapturedOutput()).Contains("\"behavior\":\"allow\"");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel]
    public async Task PermissionRequest_with_daemon_url_emits_deny_and_exits_nonzero_on_500() {
        using var bridge = WireMockServer.Start();
        var       token  = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capture.GetCapturedOutput()).Contains("\"behavior\":\"deny\"");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel]
    public async Task PermissionRequest_with_daemon_url_emits_deny_on_connection_refused() {
        // Use a deliberately-unreachable port (e.g. 1) so the connection fails immediately.
        var previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", "http://127.0.0.1:1/abc");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capture.GetCapturedOutput()).Contains("\"behavior\":\"deny\"");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel]
    public async Task PermissionRequest_with_non_loopback_daemon_url_emits_deny_without_posting() {
        using var bridge      = WireMockServer.Start();
        var       previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        // Non-loopback host should be rejected by DaemonBridgeUrl.TryParseLoopback
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", $"http://example.com:{bridge.Ports[0]}/abc");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capture.GetCapturedOutput()).Contains("\"behavior\":\"deny\"");
            await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel]
    public async Task PermissionRequest_with_https_daemon_url_emits_deny_without_posting() {
        using var bridge      = WireMockServer.Start();
        var       previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", $"https://127.0.0.1:{bridge.Ports[0]}/abc");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(exit).IsEqualTo(1);
            await Assert.That(capture.GetCapturedOutput()).Contains("\"behavior\":\"deny\"");
            await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    [Test, NotInParallel]
    public async Task PermissionRequest_with_daemon_url_does_not_double_post_to_server_hooks_endpoint() {
        using var bridge = WireMockServer.Start();
        using var server = WireMockServer.Start();
        var       token  = "abc123";

        bridge.Given(Request.Create().WithPath($"/{token}/codex/permission-request").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""")
            );

        server.Given(Request.Create().WithPath("/hooks/permission-request/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var previousEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/{token}");

        using var capture = ConsoleOutput.StartCapture();

        try {
            await new CodexHookCommand(Config.Root, Resolutions.At($"http://127.0.0.1:{server.Ports[0]}", Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"s1"}""")
            );

            await Assert.That(server.LogEntries.Count).IsEqualTo(0); // server NOT touched
            await Assert.That(bridge.LogEntries.Count).IsEqualTo(1);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_DAEMON_URL", previousEnv);
        }
    }

    // KCAP_SKIP=1 marks a kcap-launched headless Codex flow
    // (CodexCliRunner sets it). The dispatcher must still satisfy Codex's
    // hook output contract — empty stdout on SessionStart / Stop is a
    // protocol error — while suppressing every side effect: no server
    // POST, no watcher spawn, no git enrichment.

    [Test, NotInParallel]
    public async Task KcapSkip_SessionStart_emits_continue_json_and_skips_server() {
        var previousSkip = Environment.GetEnvironmentVariable("KCAP_SKIP");
        Environment.SetEnvironmentVariable("KCAP_SKIP", "1");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"SessionStart","session_id":"abc","cwd":"/tmp","transcript_path":"/tmp/r.jsonl"}"""));

            await Assert.That(exit).IsEqualTo(0);

            var doc = JsonDocument.Parse(capture.GetCapturedOutput());
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

            await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SKIP", previousSkip);
        }
    }

    [Test, NotInParallel]
    public async Task KcapSkip_Stop_emits_continue_json_and_skips_server() {
        var previousSkip = Environment.GetEnvironmentVariable("KCAP_SKIP");
        Environment.SetEnvironmentVariable("KCAP_SKIP", "1");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"Stop","session_id":"abc","cwd":"/tmp","transcript_path":"/tmp/r.jsonl"}"""));

            await Assert.That(exit).IsEqualTo(0);

            var doc = JsonDocument.Parse(capture.GetCapturedOutput());
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

            await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SKIP", previousSkip);
        }
    }

    [Test, NotInParallel]
    public async Task KcapSkip_PermissionRequest_emits_empty_object_and_skips_server() {
        // Empty hookSpecificOutput → Codex falls back to its own approval
        // prompt. Same shape as the non-skip stub path, just without the
        // /hooks/permission-record POST.
        var previousSkip = Environment.GetEnvironmentVariable("KCAP_SKIP");
        Environment.SetEnvironmentVariable("KCAP_SKIP", "1");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PermissionRequest","session_id":"abc","tool_name":"shell"}"""));

            await Assert.That(exit).IsEqualTo(0);

            var doc = JsonDocument.Parse(capture.GetCapturedOutput());
            await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);

            await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SKIP", previousSkip);
        }
    }

    [Test, NotInParallel]
    public async Task KcapSkip_PreToolUse_is_silent_and_skips_server() {
        // Non-Stop / SessionStart / PermissionRequest events have no Codex
        // stdout contract; the skip must produce nothing.
        var previousSkip = Environment.GetEnvironmentVariable("KCAP_SKIP");
        Environment.SetEnvironmentVariable("KCAP_SKIP", "1");

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader("""{"hook_event_name":"PreToolUse","session_id":"abc","tool_name":"shell"}"""));

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(capture.GetCapturedOutput()).IsEqualTo(string.Empty);
            await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        } finally {
            Environment.SetEnvironmentVariable("KCAP_SKIP", previousSkip);
        }
    }

    [Test]
    public async Task Bridge_payload_adds_agent_id_beside_the_hooks_own_cwd() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","cwd":"/repo","tool_name":"shell","tool_input":{"command":"ls"}}""")!;
        var bridge = CodexHookCommand.BuildBridgePayload(node, "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(node["agent_id"]).IsNull().Because("the hook's own node is not mutated");
    }

    sealed class Accepting : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// The bridge post carries the raw tool input to an address the daemon minted, so it must draw the
    /// lane that ignores an ambient proxy — the agent's own environment supplies one often enough, and
    /// a proxied hop would take the payload off the machine.
    /// </summary>
    [Test, NotInParallel]
    public async Task The_permission_request_bridge_draws_the_loopback_lane() {
        using var daemonUrl = EnvScope.Exclusive("KCAP_DAEMON_URL", "http://127.0.0.1:51234/bridge");
        using var handler   = new Accepting();
        var       http      = new RecordingCapacitorHttpClient(handler);

        var payload = """
                      {
                        "hook_event_name": "PermissionRequest",
                        "session_id": "abc",
                        "cwd": "/tmp",
                        "tool_name": "shell",
                        "tool_input": { "command": "ls" }
                      }
                      """;

        using var capture = ConsoleOutput.StartCapture();

        var exit = await new CodexHookCommand(
            Config.Root, Resolutions.At(_server.Url!, Config.Root),
            new HookClock(TimeProvider.System), Home, http).Handle(new StringReader(payload));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "Loopback" });
    }
}
