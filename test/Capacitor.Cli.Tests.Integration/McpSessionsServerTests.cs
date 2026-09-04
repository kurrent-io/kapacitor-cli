using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp sessions</c>: spawns the freshly-built CLI
/// binary against a WireMock-stubbed server, in an isolated config dir so token/profile state
/// never leaks between tests.
/// </summary>
public class McpSessionsServerTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    readonly WireMockServer _server           = WireMockServer.Start();
    readonly List<Process>  _spawnedProcesses = [];

    public void Dispose() {
        // Safety net: per-test `using`/`finally` blocks should already shut down processes,
        // but track + sweep here so a throw between Process.Start and the using-wrap can't leak.
        foreach (var p in _spawnedProcesses) {
            try {
                if (!p.HasExited) p.Kill(entireProcessTree: true);
                p.Dispose();
            } catch {
                // best-effort cleanup
            }
        }

        _server.Stop();
    }

    /// <summary>
    /// Spawns <c>kcap mcp sessions</c> as a child process. <paramref name="provider"/>
    /// controls the response to <c>/auth/config</c> — "None" lets the server skip token
    /// resolution entirely; "GitHub" forces token-store consultation so the unauthenticated
    /// path can be exercised.
    /// </summary>
    Process SpawnMcpServer(string provider = "None", string? urlOverride = null, string? workingDirectory = null) {
        // Auth discovery stub — primed before spawn so the child sees a response when it asks.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "sessions");
        psi.WorkingDirectory = workingDirectory ?? Tmp.Path;
        psi.Environment["KCAP_URL"] = urlOverride ?? _server.Url!;

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);

        return process;
    }

    /// <summary>
    /// A repository whose <c>origin</c> points at <c>https://github.com/{owner}/{repoName}.git</c>, to be
    /// handed to the server as its working directory so the implicit cwd-repo pin resolves — unlike the
    /// bare temp dir every other test in this file runs in. No commit is needed:
    /// <c>git branch --show-current</c> reads the symbolic HEAD ref and works at zero commits.
    /// </summary>
    static GitRepo CwdRepo(string owner, string repoName) {
        var repo = GitRepo.Create();

        repo.AddRemote($"https://github.com/{owner}/{repoName}.git");

        return repo;
    }

    /// <summary>
    /// A scheme-less server_url fails the server's own <c>IsAcceptableUrl</c> pre-check, so the
    /// dispatcher returns a JSON-RPC tool error before any client is built, and the server keeps serving.
    /// </summary>
    [Test]
    public async Task Tool_call_with_invalid_server_url_returns_error_and_server_survives() {
        using var proc = SpawnMcpServer(urlOverride: "not-a-valid-url");
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "search_sessions", new JsonObject { ["query"] = "x" }));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            // Server survived the bad request — a follow-up still gets a response.
            var again = await SendRequest(proc, ToolsListRequest(3));
            await Assert.That(again["result"]?["tools"]).IsNotNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The server is spawned for every agent session, so the working directory's repository is
    /// resolved by the first tool call, not at startup. Detection is the only startup-time writer
    /// under the config root's cache directory, so its absence after the handshake proves detection
    /// never ran; the search that follows then carries the repo hash resolved on demand.
    /// </summary>
    [Test]
    public async Task Repository_is_resolved_by_the_first_tool_call_not_at_startup() {
        using var repo = CwdRepo("acme", "widget");

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"hits":[]}"""));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(Directory.Exists(Config.Root.Path("cache"))).IsFalse();

            await SendRequest(proc, ToolsCallRequest(3, "search_sessions", new JsonObject { ["query"] = "anything" }));

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").UsingGet());
            await Assert.That(hits.Count).IsGreaterThanOrEqualTo(1);
            await Assert.That(hits[0].RequestMessage.RawQuery ?? "")
                .Contains($"repo={RepoHashHelper.ComputeRepoHash("acme", "widget")}");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

        // Use a bounded read so a hung child doesn't deadlock the test suite.
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        var line      = await proc.StandardOutput.ReadLineAsync(cts.Token);

        if (line is null) {
            var stderr = await proc.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"MCP server closed stdout without responding. Stderr: {stderr}");
        }

        return JsonNode.Parse(line)?.AsObject()
            ?? throw new InvalidOperationException($"Could not parse response as JSON object: {line}");
    }

    static async Task ShutdownAsync(Process proc) {
        try { proc.StandardInput.Close(); } catch { /* already closed */ }
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);
        } catch {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
    }

    static JsonObject InitializeRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "initialize",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsListRequest(int id) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/list",
        ["params"]  = new JsonObject()
    };

    static JsonObject ToolsCallRequest(int id, string name, JsonObject arguments) => new() {
        ["jsonrpc"] = "2.0",
        ["id"]      = id,
        ["method"]  = "tools/call",
        ["params"]  = new JsonObject {
            ["name"]      = name,
            ["arguments"] = arguments
        }
    };

    [Test]
    public async Task Initialize_returns_server_info_with_correct_name() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-sessions");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
            await Assert.That(result["instructions"]?.GetValue<string>()).IsNotNull();
            await Assert.That(result["instructions"]!.GetValue<string>()).IsNotEmpty();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_six_tools_with_correct_names() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(6);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("search_sessions")).IsTrue();
            await Assert.That(names.Contains("get_session_summary")).IsTrue();
            await Assert.That(names.Contains("get_session_transcript")).IsTrue();
            await Assert.That(names.Contains("get_turn")).IsTrue();
            await Assert.That(names.Contains("list_turns")).IsTrue();
            await Assert.That(names.Contains("list_repo_sessions")).IsTrue();

            // Hard gate: search_sessions carries the comparative routing cue.
            var searchDesc = tools.First(t => t?["name"]?.GetValue<string>() == "search_sessions")!["description"]!.GetValue<string>();
            await Assert.That(searchDesc).Contains("before grepping the code or git log");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task List_repo_sessions_hits_the_repo_route_for_the_cwd_repo() {
        using var repo = CwdRepo("acme", "widgets");
        var       hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

        _server.Given(Request.Create().WithPath($"/api/repositories/{hash}/sessions").UsingGet().WithParam("state", "active"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"items":[{"session_id":"s-1","slug":null,"title":"Fix","owner":null,"vendor":"claude","status":"active","access_level":"full","stale":false,"started_at":"2026-09-02T09:00:00+00:00","ended_at":null,"last_activity_at":"2026-09-02T10:00:00+00:00","primary_repo_hash":null,"is_primary":true,"branch":"main","cwd":"/w","last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0}],"total":1,"limit":20,"offset":0}"""));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "list_repo_sessions", new JsonObject()));
            var text     = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();

            await Assert.That(response["result"]?["isError"]).IsNull();
            await Assert.That(text).Contains("\"session_id\":\"s-1\"");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Search_sessions_calls_server_and_passes_through_response() {
        const string stubbedBody = """{"hits":[{"session_id":"abc","title":"Batch import","snippet":"batch …"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["query"] = "batch",
                // explicit "all" to bypass cwd repo-hash detection; keeps the test independent
                // of whatever git state happens to surround the test process.
                ["repo"]  = "all"
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "search_sessions", args));

            // Returned JSON-RPC envelope wraps a content array; assert on the body it ships back.
            var content = response["result"]?["content"]?[0];
            await Assert.That(content?["type"]?.GetValue<string>()).IsEqualTo("text");
            await Assert.That(content?["text"]?.GetValue<string>()).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);

            var rawUrl   = hits[0].RequestMessage.RawQuery ?? "";
            await Assert.That(rawUrl.Contains("q=batch")).IsTrue();
            // "repo=all" is a sentinel — must NOT be propagated as a real filter.
            await Assert.That(rawUrl.Contains("repo=")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_session_summary_projects_recap_to_summary_text_and_plan() {
        const string recap = """[{"type":"whats_done","content":"did X"},{"type":"plan","content":"do Y"}]""";

        _server.Given(
            Request.Create()
                .WithPath("/api/sessions/abc/recap")
                .WithParam("chain", "false")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(recap)
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["session_id"] = "abc" };
            var response = await SendRequest(proc, ToolsCallRequest(4, "get_session_summary", args));

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();

            var projected = JsonNode.Parse(text!)?.AsObject();
            await Assert.That(projected).IsNotNull();
            await Assert.That(projected!["summary_text"]?.GetValue<string>()).IsEqualTo("did X");
            await Assert.That(projected["plan"]?.GetValue<string>()).IsEqualTo("do Y");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_session_transcript_passes_around_event_and_agent_id_through() {
        const string stubbedBody = """{"events":[{"index":42,"speaker":"user","text":"hi"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/abc/transcript").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["session_id"]   = "abc",
                ["around_event"] = 42,
                ["agent_id"]     = "agent-xyz"
            };

            var response = await SendRequest(proc, ToolsCallRequest(5, "get_session_transcript", args));

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/abc/transcript").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(1);

            var rawQuery = hits[0].RequestMessage.RawQuery ?? "";
            await Assert.That(rawQuery.Contains("around_event=42")).IsTrue();
            await Assert.That(rawQuery.Contains("agent_id=agent-xyz")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Writes a non-expired token to the per-test config dir's token store so the
    /// send-path handler attaches a Bearer header. Exercises the long-lived-server
    /// path (the MCP server holds a single HttpClient for the agent's whole session).
    /// </summary>
    void SeedToken(string accessToken = "seed-token") {
        var tokensDir  = Config.Root.Path("tokens");
        Directory.CreateDirectory(tokensDir);
        var tokenJson = $$"""
            {
              "access_token": "{{accessToken}}",
              "expires_at": "{{DateTimeOffset.UtcNow.AddHours(1):O}}",
              "github_username": "seed-user",
              "provider": "GitHubApp"
            }
            """;
        File.WriteAllText(Path.Combine(tokensDir, "default.json"), tokenJson);
    }

    /// <summary>
    /// The MCP server caches a single <c>HttpClient</c> for the whole agent session, so a 401
    /// must retry once via <c>TokenStore.GetValidTokensAsync</c> rather than surface the friendly
    /// 401 message until the process is restarted. WireMock returns 401 then 200 for the same
    /// seeded token, proving the retry path runs — the real refresh-token flow is out of scope.
    /// </summary>
    [Test]
    public async Task Refreshed_token_succeeds_after_401() {
        const string stubbedBody = """{"hits":[{"session_id":"abc","title":"OK"}]}""";
        const string scenario    = "auth-retry";

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .InScenario(scenario)
            .WillSetStateTo("after-401")
            .RespondWith(Response.Create().WithStatusCode(401).WithBody(""));

        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .InScenario(scenario)
            .WhenStateIs("after-401")
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(stubbedBody)
            );

        SeedToken();

        using var proc = SpawnMcpServer(provider: "GitHubApp");
        try {
            var args     = new JsonObject { ["query"] = "anything", ["repo"] = "all" };
            var response = await SendRequest(proc, ToolsCallRequest(7, "search_sessions", args));

            var result = response["result"]?.AsObject();
            // Must be a success — the 401 was retried and the second call succeeded.
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var content = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(content).IsEqualTo(stubbedBody);

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").UsingGet());
            await Assert.That(hits.Count).IsEqualTo(2);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// On a 401, <see cref="Capacitor.Cli.Commands.McpSessionsServer"/> must surface the friendly
    /// message "Not logged in. Run 'kcap login' on the host shell." inside the tool result itself
    /// — MCP clients don't forward CLI stderr to the model, so <c>AuthRejectionNotice</c>'s message
    /// would otherwise be invisible.
    /// The stub's 401 body is empty, so a non-empty message here can only come from this fallback.
    /// </summary>
    [Test]
    public async Task Unauthenticated_returns_friendly_error() {
        _server.Given(Request.Create().WithPath("/api/sessions/search").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(401)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("")
            );

        // Provider != "None" forces the auth path; no tokens.json exists under the config root,
        // so the request goes out without a Bearer header.
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            var args     = new JsonObject { ["query"] = "anything", ["repo"] = "all" };
            var response = await SendRequest(proc, ToolsCallRequest(6, "search_sessions", args));

            var result   = response["result"]?.AsObject();
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsTrue();

            var content = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(content).IsEqualTo(McpSessionsServer.NotLoggedInMessage);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Auto-widen, happy path: an implicit cwd pin (no explicit `repo` arg, a real git repo under
    /// the spawned process's cwd) that comes back thinner than the default limit (10) triggers a
    /// second widen request; the two bodies merge cwd-first with `session_id` dedup, capped at
    /// the limit, and `widened_to_all_repos: true`.
    ///
    /// The widened request carries no `repo` query param at all — `BuildSearchUrl` treats `"all"`
    /// as "omit the repo filter entirely", so the widen stub below matches on the param's absence
    /// via <see cref="MatchBehaviour.RejectOnMatch"/>, not a literal `repo=all` value.
    /// </summary>
    [Test]
    public async Task Search_sessions_thin_result_auto_widens_to_all_repos() {
        const string owner    = "acme";
        const string repoName = "widget";
        var          repoHash = RepoHashHelper.ComputeRepoHash(owner, repoName);

        using var repo = CwdRepo(owner, repoName);

        const string firstBody   = """{"hits":[{"session_id":"s1","title":"A"},{"session_id":"s2","title":"B"}]}""";
        const string widenedBody = """{"hits":[{"session_id":"s1","title":"A"},{"session_id":"s3","title":"C"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/search").WithParam("repo", repoHash).UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(firstBody)
            );

        _server.Given(Request.Create().WithPath("/api/sessions/search").WithParam("repo", MatchBehaviour.RejectOnMatch).UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(widenedBody)
            );

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            var args     = new JsonObject { ["query"] = "batch" }; // no `repo` — implicit cwd pin
            var response = await SendRequest(proc, ToolsCallRequest(10, "search_sessions", args), TimeSpan.FromSeconds(30));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();

            var merged = JsonNode.Parse(text!)?.AsObject();
            await Assert.That(merged).IsNotNull();
            await Assert.That(merged!["widened_to_all_repos"]?.GetValue<bool>()).IsTrue();

            var hits = merged["hits"]?.AsArray();
            await Assert.That(hits).IsNotNull();
            await Assert.That(hits!.Count).IsEqualTo(3);
            await Assert.That(hits[0]?["session_id"]?.GetValue<string>()).IsEqualTo("s1");
            await Assert.That(hits[1]?["session_id"]?.GetValue<string>()).IsEqualTo("s2");
            await Assert.That(hits[2]?["session_id"]?.GetValue<string>()).IsEqualTo("s3");

            var pinnedHits  = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").WithParam("repo", repoHash).UsingGet());
            var widenedHits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").WithParam("repo", MatchBehaviour.RejectOnMatch).UsingGet());
            await Assert.That(pinnedHits.Count).IsEqualTo(1);
            await Assert.That(widenedHits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A failed widen must never cost the caller the successful first result. This WireMock-based
    /// harness cannot force HttpClient to throw on the widen call, so this test exercises the
    /// HTTP-500 shape of the same contract; the thrown-exception shape is covered by the
    /// catch-all by inspection, not a test.
    /// </summary>
    [Test]
    public async Task Search_sessions_widen_failure_returns_first_body_untouched() {
        const string owner    = "acme";
        const string repoName = "widget-fail";
        var          repoHash = RepoHashHelper.ComputeRepoHash(owner, repoName);

        using var repo = CwdRepo(owner, repoName);

        const string firstBody = """{"hits":[{"session_id":"s1","title":"A"}]}""";

        _server.Given(Request.Create().WithPath("/api/sessions/search").WithParam("repo", repoHash).UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(firstBody)
            );

        // Widened request carries no `repo` param at all (see the happy-path test above).
        _server.Given(Request.Create().WithPath("/api/sessions/search").WithParam("repo", MatchBehaviour.RejectOnMatch).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            var args     = new JsonObject { ["query"] = "batch" }; // no `repo` — implicit cwd pin
            var response = await SendRequest(proc, ToolsCallRequest(11, "search_sessions", args), TimeSpan.FromSeconds(30));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            // Must NOT be an error — the widen failure is swallowed, first body wins.
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsEqualTo(firstBody);

            var widenedHits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/search").WithParam("repo", MatchBehaviour.RejectOnMatch).UsingGet());
            await Assert.That(widenedHits.Count).IsEqualTo(1); // the widen call did happen and did fail
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
