using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end round-trip for the durable hook spool feature.
/// A transient outage (5xx) during a <c>session-end</c> POST causes the event
/// to be spooled to disk; the next <c>kcap</c> hook invocation replays it when
/// the server is healthy again.
///
/// Tests use <see cref="ClaudeHookCommand.HandleCore"/> with a real
/// <see cref="HttpClient"/> pointing at a WireMock server, and a
/// <see cref="HookSpool"/> pinned to a temp directory — hermetic, no live
/// auth, no real server required.
/// </summary>
public class SpoolOutageRecoveryTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // 32-hex dashless session id — required by HookSpool.SafeSessionId regex.
    const string Sid = "deadbeef0123456789abcdef01234567";

    readonly WireMockServer _server    = WireMockServer.Start();
    readonly TempDir        _tmp       = new();
    readonly string         _spoolDir;

    public SpoolOutageRecoveryTests() {
        _spoolDir = _tmp.CreateDir("spool");
    }

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    static string SessionEndPayload(string sid = Sid) =>
        $$"""
        {
          "hook_event_name":  "SessionEnd",
          "session_id":       "{{sid}}",
          "transcript_path":  "/tmp/fake-{{sid}}.jsonl",
          "cwd":              "/tmp/test",
          "reason":           "eof"
        }
        """;

    static string StopPayload(string sid = Sid) =>
        $$"""
        {
          "hook_event_name":  "Stop",
          "session_id":       "{{sid}}",
          "transcript_path":  "/tmp/fake-{{sid}}.jsonl",
          "cwd":              "/tmp/test"
        }
        """;

    HookSpool MakeSpool() => new(_spoolDir);

    // HandleCore takes a pre-built HttpClient so we bypass auth entirely.
    Task<int> Invoke(HttpClient client, string payload) =>
        new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .HandleCore(client, AuthStatus.Ok, MakeSpool(), new StringReader(payload));

    IEnumerable<string> SpoolFiles =>
        Directory.Exists(_spoolDir) ? Directory.EnumerateFiles(_spoolDir) : [];

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Scenario: server is down (503) when session-end fires, then recovers.
    /// Step 1 — session-end against 503 → event spooled, hook exits 0.
    /// Step 2 — a subsequent Stop hook against 200 → drain fires, server
    ///           receives POST /hooks/session-end, spool file is gone.
    /// </summary>
    [Test, NotInParallel("SpoolDir")]
    public async Task Outage_then_recovery_drains_spooled_session_end() {
        // ── Step 1: server is down ───────────────────────────────────────────
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        using var clientDown = new HttpClient();
        var exitDown = await Invoke(clientDown, SessionEndPayload());

        // Hook must exit 0 (fail-open).
        await Assert.That(exitDown).IsEqualTo(0);

        // Exactly one spool file for this session must exist.
        var spoolFiles = SpoolFiles.ToList();
        await Assert.That(spoolFiles.Count).IsEqualTo(1);
        await Assert.That(Path.GetFileName(spoolFiles[0])).IsEqualTo($"{Sid}.jsonl");

        // Spool file must contain the session-end route.
        var spooledLine = await File.ReadAllTextAsync(spoolFiles[0]);
        var spooledEntry = JsonNode.Parse(spooledLine.Trim().Split('\n')[0])!;
        await Assert.That(spooledEntry["route"]?.GetValue<string>()).IsEqualTo("session-end");

        // ── Step 2: server recovers ──────────────────────────────────────────
        _server.Reset();
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var clientUp = new HttpClient();
        var exitUp = await Invoke(clientUp, StopPayload());

        await Assert.That(exitUp).IsEqualTo(0);

        // Server must have received the replayed session-end POST.
        var replayedRequests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-end").UsingPost());
        await Assert.That(replayedRequests.Count).IsGreaterThanOrEqualTo(1);

        // Verify the replayed body contains the original session_id.
        var replayedBody = JsonNode.Parse(replayedRequests[0].RequestMessage.Body!)!;
        await Assert.That(replayedBody["session_id"]?.GetValue<string>()).IsEqualTo(Sid);

        // Spool file must be gone after successful drain.
        await Assert.That(SpoolFiles.Any()).IsFalse();
    }

    /// <summary>
    /// Scenario: server returns <c>generate_whats_done:true</c> for the replayed
    /// session-end (Task 7 deferral). Asserts:
    /// - The session-end POST was received by the server (spool drained).
    /// - The spool file is removed (drain completed successfully).
    ///
    /// Note: <see cref="WatcherManager.SpawnWhatsDoneGenerator"/> is fire-and-forget
    /// and spawns a child process. Asserting the spawned generator's execution is
    /// impractical inside a hermetic test; this test asserts the prerequisite
    /// path (drain succeeds, POST received) which exercises the full drain code
    /// path through <c>ClaudePoster</c> that would trigger the spawn. The spawn
    /// itself is best-effort by design and not safety-critical for session capture.
    /// </summary>
    [Test, NotInParallel("SpoolDir")]
    public async Task Replayed_session_end_with_generate_whats_done_true_drains_without_error() {
        // Seed a spooled session-end directly (simulates a previous outage).
        var spool = MakeSpool();
        var sessionEndBody = $$"""{"hook_event_name":"session-end","session_id":"{{Sid}}","cwd":"/tmp/test","reason":"eof"}""";
        spool.Append(Sid, "session-end", sessionEndBody);

        // Confirm the spool file exists before the test.
        await Assert.That(SpoolFiles.Any()).IsTrue();

        // Server responds with generate_whats_done:true.
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"generate_whats_done":true}"""));

        _server.Given(Request.Create().WithPath("/hooks/stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var client = new HttpClient();
        var exit = await Invoke(client, StopPayload());

        await Assert.That(exit).IsEqualTo(0);

        // Server must have received the replayed session-end POST.
        var replayedRequests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-end").UsingPost());
        await Assert.That(replayedRequests.Count).IsGreaterThanOrEqualTo(1);

        // Spool file must be gone — the generate_whats_done branch must not
        // prevent the entry from being marked Delivered.
        await Assert.That(SpoolFiles.Any()).IsFalse();
    }
}
