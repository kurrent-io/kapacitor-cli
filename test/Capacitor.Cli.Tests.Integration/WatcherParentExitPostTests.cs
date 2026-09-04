using System.Text.Json;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Validates the watcher's parent-exit session-end POST: when the watcher detects
/// its parent coding-agent process has died without firing session-end, it takes
/// over the role and POSTs to <c>/hooks/session-end/{vendor}</c> with
/// <c>reason: parent_exited</c>. The server's idempotent HandleSessionEnd writes
/// SessionEnded and tells us whether to spawn the what's-done generator.
/// </summary>
public class WatcherParentExitPostTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    // Instance, not static: the parent-exit POST resolves its client against a config dir, so it
    // must be this test's own root — which a static helper cannot see, TUnit injecting it after
    // construction.
    WatchCommand Watch() => new(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient(), new FixedCredentialSource());

    public void Dispose() => _server.Stop();

    [Test]
    public async Task ParentExit_PostsSessionEnd_WithExpectedPayload() {
        // Auth discovery: "None" means no Bearer token needed for subsequent calls.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/claude").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"generate_whats_done":false}""")
            );

        var sessionId = $"test-{Guid.NewGuid():N}";

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      sessionId,
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "claude",
            repository:     null
        );

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo("/tmp/fake.jsonl");
        await Assert.That(body.GetProperty("cwd").GetString()).IsEqualTo("/repo");
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("session_end");
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("parent_exited");
    }

    [Test]
    public async Task ParentExit_IncludesRepository_WhenProvided() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/claude").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        var sessionId = $"test-{Guid.NewGuid():N}";

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      sessionId,
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "claude",
            repository: new() {
                Owner    = "kurrent-io",
                RepoName = "kcap",
                Branch   = "main",
            }
        );

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        var repo = body.GetProperty("repository");
        await Assert.That(repo.GetProperty("owner").GetString()).IsEqualTo("kurrent-io");
        await Assert.That(repo.GetProperty("repo_name").GetString()).IsEqualTo("kcap");
        await Assert.That(repo.GetProperty("branch").GetString()).IsEqualTo("main");
    }

    [Test]
    public async Task ParentExit_SkipsPost_ForUnknownVendor() {
        // Vendor is interpolated into the URL path. Validate against a known whitelist
        // to defend against malformed --vendor values producing path traversal or
        // hitting unintended endpoints.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        // Catch-all for any session-end variant so we can assert nothing was posted.
        _server.Given(Request.Create().WithPath("/hooks/session-end/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "../admin",
            repository:     null
        );

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/*").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParentExit_PostsToVendorSpecificRoute_ForCodex() {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "codex",
            repository:     null
        );

        var codexHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        var claudeHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/claude").UsingPost());
        await Assert.That(codexHits.Count).IsEqualTo(1);
        await Assert.That(claudeHits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ParentExit_PostsToVendorSpecificRoute_ForKiro() {
        // Kiro has NO session-end hook, so this watcher-synthesized POST on
        // kiro-cli exit is the ONLY way a Kiro session is marked ended — the
        // vendor must be in the whitelist and route to /hooks/session-end/kiro.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "kiro",
            repository:     null
        );

        var kiroHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/kiro").UsingPost());
        await Assert.That(kiroHits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ParentExit_PostsToVendorSpecificRoute_ForPi() {
        // Regression (PR #162 review): the Pi watcher starts with vendor "pi", so
        // the parent-exit fallback must accept it — otherwise a crashed/closed Pi
        // session stays active until a manual import.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      $"test-{Guid.NewGuid():N}",
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "pi",
            repository:     null
        );

        var piHits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/pi").UsingPost());
        await Assert.That(piHits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IdleTimeout_PostsSessionEnd_WithIdleTimeoutReason() {
        // When a Codex rollout file goes idle, the watcher calls PostSessionEndOnParentExitAsync
        // with reason "idle_timeout". Validate the payload reaches /hooks/session-end/codex
        // and the reason field is "idle_timeout" (not "parent_exited").
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        _server.Given(Request.Create().WithPath("/hooks/session-end/codex").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"generate_whats_done":false}""")
            );

        var sessionId = $"test-{Guid.NewGuid():N}";

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      sessionId,
            transcriptPath: "/tmp/fake.jsonl",
            cwd:            "/repo",
            vendor:         "codex",
            repository:     null,
            reason:         "idle_timeout"
        );

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("session_end");
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("idle_timeout");
    }

    [Test]
    public async Task ParentExit_FinalizesGeminiSubagents_BeforeSessionEnd() {
        // Regression (PR #170 review, BLOCKING): the watcher's parent-exit fallback must run
        // the Gemini subagent teardown. Gemini fires no subagent-stop hook and its child
        // watchers carry no parent-pid watchdog, so when the parent process dies WITHOUT the
        // session-end hook this is the only thing that finalizes live subagents — otherwise
        // the server keeps SubagentStarted + content but never SubagentCompleted until a
        // manual re-import.
        const string dashedParent = "0a900000-0000-4000-8000-000000000777";
        const string dashedSub    = "57d9b498-2705-4af5-b060-ebaba4878c96";
        const string dashlessSub  = "57d9b49827054af5b060ebaba4878c96";

        using var tmp = new TempDir();
        var chats = tmp.CreateDir("chats");

        var parent = chats.PathTo("session-2026-06-22T14-31-0a900000.jsonl");
        File.WriteAllLines(parent, new[] {
            $$"""{"sessionId":"{{dashedParent}}","projectHash":"h","kind":"main"}""",
            $$"""{"id":"m1","type":"gemini","content":"","toolCalls":[{"id":"invoke_agent__x","name":"invoke_agent","args":{"agent_name":"codebase_investigator"},"agentId":"{{dashedSub}}","status":"success"}]}"""
        });

        var subDir = chats.CreateDir(dashedParent);
        subDir.CreateFile(dashedSub + ".jsonl", new[] {
            $$"""{"sessionId":"{{dashedSub}}","kind":"subagent","directories":[]}""",
            """{"id":"s1","type":"gemini","content":"done"}"""
        });

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        // 404 watermark → inline drain finds no new lines and posts no transcript batch;
        // subagent-stop must still fire (that is the regression under test).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] { "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/gemini" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"generate_whats_done":false}"""));
        }

        await Watch().PostSessionEndOnParentExitAsync(
            sessionId:      "0a900000000040008000000000000777",
            transcriptPath: parent,
            cwd:            "/repo",
            vendor:         "gemini",
            repository:     null
        );

        // subagent-stop was posted, carrying the canonical (dashless) agent_id.
        var stops = _server.FindLogEntries(Request.Create().WithPath("/hooks/subagent-stop").UsingPost());
        await Assert.That(stops.Count).IsEqualTo(1);
        await Assert.That(stops[0].RequestMessage.Body!).Contains($"\"agent_id\":\"{dashlessSub}\"");

        // ... and it landed BEFORE session-end, so SubagentCompleted precedes SessionEnded.
        var postPaths = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();
        await Assert.That(postPaths.IndexOf("/hooks/subagent-stop"))
            .IsLessThan(postPaths.IndexOf("/hooks/session-end/gemini"));
    }
}
