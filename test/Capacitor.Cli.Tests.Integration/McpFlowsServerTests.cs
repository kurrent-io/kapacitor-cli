using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end stdio JSON-RPC tests for <c>kcap mcp flows</c>.
/// Spawns the freshly-built CLI binary, points it at a WireMock-stubbed
/// Capacitor server (via <c>KCAP_URL</c>), seeds an isolated config
/// directory (via <c>KCAP_CONFIG_DIR</c>) so token/profile state never
/// leaks between tests, and asserts on the wire-level JSON-RPC envelopes
/// the server emits plus the HTTP calls WireMock observed.
/// </summary>
public class McpFlowsServerTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [GitRepo]         public required GitRepo         Repo    { get; init; }

    readonly WireMockServer _server           = WireMockServer.Start();
    readonly List<Process>  _spawnedProcesses = [];

    [Before(Test)]
    public void InitWorkspaceRepo() => Repo.AddRemote("https://github.com/test-owner/test-repo.git");

    public void Dispose() {
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
    /// Spawns <c>kcap mcp flows</c> as a child process with WireMock as the backend.
    /// <paramref name="urlOverride"/> replaces the WireMock URL (used to exercise the
    /// invalid-server_url path).
    /// </summary>
    Process SpawnMcpServer(string provider = "None", string? workingDirectory = null, string? urlOverride = null) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "flows");
        psi.WorkingDirectory = workingDirectory ?? Repo.Path;
        psi.Environment["KCAP_URL"] = urlOverride ?? _server.Url!;

        ApplyHarnessSignals(psi, harnessSessionId: null, harnessProjectDir: null);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    /// <summary>
    /// Spawns the server with KCAP_SESSION_ID set so requester context includes a session ID.
    /// <paramref name="harnessSessionId"/>/<paramref name="harnessProjectDir"/> simulate the running
    /// harness's own per-process signals, which must take precedence over the ambient
    /// KCAP_SESSION_ID / process cwd (see HarnessRequesterContext).
    /// </summary>
    Process SpawnMcpServerWithSession(
            string  sessionId,
            string  provider          = "None",
            string? workingDirectory  = null,
            string? harnessSessionId  = null,
            string? harnessProjectDir = null) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"provider":"{{provider}}"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", "flows");
        psi.WorkingDirectory = workingDirectory ?? Repo.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;
        psi.Environment["KCAP_SESSION_ID"] = sessionId;

        ApplyHarnessSignals(psi, harnessSessionId, harnessProjectDir);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        _spawnedProcesses.Add(process);
        return process;
    }

    /// <summary>
    /// Sets (or clears) the running-harness env signals on a spawn. ProcessStartInfo inherits this
    /// process's own environment, so a suite run inside a real harness session would otherwise leak
    /// its CLAUDE_CODE_SESSION_ID / CLAUDE_PROJECT_DIR into every spawned server. Every spawn helper
    /// goes through here so the child's harness identity is exactly what the test asked for.
    ///
    /// <para>CODEX_THREAD_ID is removed unconditionally: the resolver treats a co-present value as
    /// "nested inside another harness, so the Claude signals are unprovable" and falls back to
    /// ambient resolution — an inherited one would silently route requester-context tests down that
    /// fallback branch whenever the suite runs from a Codex session.</para>
    /// </summary>
    static void ApplyHarnessSignals(ProcessStartInfo psi, string? harnessSessionId, string? harnessProjectDir) {
        if (harnessSessionId is null) psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        else psi.Environment["CLAUDE_CODE_SESSION_ID"] = harnessSessionId;

        if (harnessProjectDir is null) psi.Environment.Remove("CLAUDE_PROJECT_DIR");
        else psi.Environment["CLAUDE_PROJECT_DIR"] = harnessProjectDir;

        psi.Environment.Remove("CODEX_THREAD_ID");
    }

    static async Task<JsonObject> SendRequest(Process proc, JsonObject request, TimeSpan? timeout = null) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();

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

    /// <summary>
    /// The server is spawned for every agent session, so the working directory's repository is
    /// resolved by the first tool call that needs it, not at startup. Detection is the only
    /// startup-time writer under the config root's cache directory, so its absence after the
    /// handshake proves detection never ran; the start that follows then names the repo resolved
    /// on demand.
    /// </summary>
    [Test]
    public async Task Repository_is_resolved_by_the_first_tool_call_not_at_startup() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"flow_run_id":"flow-1","round_id":"round-1","round_number":1,"status":"completed","result_kind":"FINDINGS","result_text":"ok","reviewer_agent_id":null,"reviewer_session_id":null}""")
            );

        using var proc = SpawnMcpServer();
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(Directory.Exists(Config.Root.Path("cache"))).IsFalse();

            var args = new JsonObject {
                ["kind"]         = "spec-review",
                ["target_kind"]  = "spec",
                ["target_ref"]   = "docs/feature.md",
                ["target_title"] = "Feature spec",
                ["context"]      = "Review."
            };
            await SendRequest(proc, ToolsCallRequest(3, "start_review_flow", args));

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var body = JsonNode.Parse(hits[0].RequestMessage.Body ?? "{}")!.AsObject();
            await Assert.That(body["repo_owner"]?.GetValue<string>()).IsEqualTo("test-owner");
            await Assert.That(body["repo_name"]?.GetValue<string>()).IsEqualTo("test-repo");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Initialize_returns_kcap_flows_server_info() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, InitializeRequest(1));

            await Assert.That(response["id"]?.GetValue<int>()).IsEqualTo(1);
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo("kcap-flows");
            await Assert.That(result["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");
            // flows deliberately omits server-level instructions — no routing to a paid
            // hosted reviewer — so the field must be omitted, not empty.
            await Assert.That(result["instructions"]).IsNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Handshake probe: clients that send <c>resources/list</c> / <c>prompts/list</c> /
    /// <c>ping</c> before treating the server as ready must get empty-but-successful responses,
    /// not <c>-32601 Method not found</c> — and the negotiated protocolVersion must echo back a
    /// client-requested version we support, not always the hardcoded baseline.
    /// </summary>
    [Test]
    public async Task ResourcesList_and_PromptsList_return_empty_not_method_not_found() {
        using var proc = SpawnMcpServer();
        try {
            var init = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 1,
                ["method"]  = "initialize",
                ["params"]  = new JsonObject { ["protocolVersion"] = "2025-06-18" }
            });
            await Assert.That(init["result"]?["protocolVersion"]?.GetValue<string>()).IsEqualTo("2025-06-18");

            var resources = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 2,
                ["method"]  = "resources/list"
            });
            await Assert.That(resources["error"]).IsNull();
            await Assert.That(resources["result"]?["resources"]?.AsArray()?.Count).IsEqualTo(0);

            var prompts = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 3,
                ["method"]  = "prompts/list"
            });
            await Assert.That(prompts["error"]).IsNull();
            await Assert.That(prompts["result"]?["prompts"]?.AsArray()?.Count).IsEqualTo(0);

            var ping = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 4,
                ["method"]  = "ping"
            });
            await Assert.That(ping["error"]).IsNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Malformed-initialize survival probe: a client sending a non-string
    /// <c>protocolVersion</c> (e.g. a bare JSON number) must not crash <c>McpProtocol.NegotiateVersion</c> —
    /// the initialize dispatch arm has no try/catch, so an uncaught exception there would kill the
    /// whole stdio server. The server must fall back to the baseline version and stay responsive.
    /// </summary>
    [Test]
    public async Task Initialize_with_non_string_protocol_version_falls_back_and_server_survives() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, new JsonObject {
                ["jsonrpc"] = "2.0",
                ["id"]      = 1,
                ["method"]  = "initialize",
                ["params"]  = new JsonObject { ["protocolVersion"] = 2025 }
            });

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["protocolVersion"]?.GetValue<string>()).IsEqualTo("2024-11-05");

            // Server survived the malformed request — a follow-up still gets a response.
            var again = await SendRequest(proc, ToolsListRequest(2));
            await Assert.That(again["result"]?["tools"]).IsNotNull();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Tools_list_returns_eight_flow_tools() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));

            var tools = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();
            await Assert.That(tools!.Count).IsEqualTo(9);

            var names = tools.Select(t => t?["name"]?.GetValue<string>()).ToHashSet();
            await Assert.That(names.Contains("start_review_flow")).IsTrue();
            await Assert.That(names.Contains("submit_review_round")).IsTrue();
            await Assert.That(names.Contains("get_review_flow_status")).IsTrue();
            await Assert.That(names.Contains("close_review_flow")).IsTrue();
            await Assert.That(names.Contains("start_flow")).IsTrue();
            await Assert.That(names.Contains("send_to_participant")).IsTrue();
            await Assert.That(names.Contains("get_flow_status")).IsTrue();
            await Assert.That(names.Contains("close_flow")).IsTrue();
            await Assert.That(names.Contains("list_reviewer_vendors")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Pins the four review-tool schemas byte-stably: definition_id/participant/message must
    /// NEVER leak into these schemas — old clients (and old skills) depend on the exact
    /// property/required sets that shipped before the generic tools were added. The one deliberate
    /// exception is `get_review_flow_status`'s additive, optional `wait` (the liveness-supervision
    /// status-wait argument) — pinned explicitly below rather than silently allowed, so a future
    /// accidental property change still fails loudly.
    /// </summary>
    [Test]
    public async Task Review_tool_schemas_are_unchanged() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));
            var tools    = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var byName = tools!.ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);

            await AssertSchema(
                byName["start_review_flow"],
                properties: ["kind", "target_kind", "target_ref", "target_title", "context", "instructions", "mode", "vendor", "model"],
                required:   ["kind", "target_kind", "target_ref", "target_title", "context"]
            );

            await AssertSchema(
                byName["submit_review_round"],
                properties: ["flow_run_id", "context", "instructions"],
                required:   ["flow_run_id", "context"]
            );

            await AssertSchema(
                byName["get_review_flow_status"],
                // Liveness-supervision status wait: additive optional `wait` — blocks until the
                // round is terminal instead of a single snapshot GET. Backwards-compatible by
                // construction (optional, not in `required`), so pinning it here is deliberate, not a
                // relaxation of the byte-stable contract this test otherwise enforces.
                properties: ["flow_run_id", "wait"],
                required:   ["flow_run_id"]
            );

            await AssertSchema(
                byName["close_review_flow"],
                properties: ["flow_run_id"],
                required:   ["flow_run_id"]
            );
        } finally {
            await ShutdownAsync(proc);
        }
    }

    static async Task AssertSchema(JsonNode tool, string[] properties, string[] required) {
        var schema = tool["inputSchema"]?.AsObject();
        await Assert.That(schema).IsNotNull();

        var propNames = schema!["properties"]?.AsObject().Select(kv => kv.Key).ToHashSet() ?? [];
        var reqNames  = schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() ?? [];

        await Assert.That(propNames.SetEquals(properties)).IsTrue();
        await Assert.That(reqNames.SetEquals(required)).IsTrue();
    }

    /// <summary>
    /// Generic alias for start_review_flow: definition_id maps onto the wire "kind" field, so the
    /// server (which treats kind as the definition id) doesn't need to know about the generic tool
    /// name at all.
    /// </summary>
    [Test]
    public async Task Start_flow_posts_kind_from_definition_id() {
        const string flowRunId = "flow-generic-1";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-1",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "generic flow result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["definition_id"] = "my-custom-flow",
                ["target_kind"]   = "pr",
                ["target_ref"]    = "https://github.com/x/y/pull/1",
                ["target_title"]  = "My PR",
                ["context"]       = "please look at this"
            };

            var response = await SendRequest(proc, ToolsCallRequest(50, "start_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("generic flow result")).IsTrue();

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["kind"]?.GetValue<string>()).IsEqualTo("my-custom-flow");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// send_to_participant is the generic alias for submit_review_round: "message" maps onto the
    /// wire "context" field and "participant" is a new field the server validates against the
    /// flow definition — today's definitions have a single participant, "reviewer".
    /// </summary>
    [Test]
    public async Task Send_to_participant_posts_participant_and_message_as_context() {
        const string flowRunId = "flow-generic-2";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-2",
              "round_number": 2,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "round two",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "ctx2"
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["context"]?.GetValue<string>()).IsEqualTo("ctx2");
            await Assert.That(bodyNode["participant"]?.GetValue<string>()).IsEqualTo("reviewer");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// send_to_participant declares an optional "async" arg (kept for symmetry with
    /// submit_review_round's own Async field) — pin that it actually flows through onto the
    /// wire. Stubs a terminal (non-"running") POST response so ResolveRoundResultAsync takes
    /// the no-poll path and no GET calls happen.
    /// </summary>
    [Test]
    public async Task Send_to_participant_async_false_posts_async_false() {
        const string flowRunId = "flow-generic-async-false";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-2",
              "round_number": 2,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "sync round result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "ctx2",
                ["async"]       = false
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["async"]?.GetValue<bool>()).IsFalse();
            await Assert.That(bodyNode["context"]?.GetValue<string>()).IsEqualTo("ctx2");
            await Assert.That(bodyNode["participant"]?.GetValue<string>()).IsEqualTo("reviewer");

            // No polling should have happened — the POST response was already terminal.
            var getHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet());
            await Assert.That(getHits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A non-boolean JSON "async" (e.g. an LLM caller passing the string "yes") must NOT crash
    /// the request with an uncaught GetValue&lt;bool&gt;() exception — it must surface as a clean
    /// isError:true tool result, and the stdio loop must stay alive for the next request. No
    /// WireMock stub is needed: the bad arg is rejected before any HTTP call is made, mirroring
    /// Submit_review_round_without_flow_run_id_returns_error above.
    /// </summary>
    [Test]
    public async Task Send_to_participant_non_boolean_async_returns_clean_error() {
        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = "flow-bad-async",
                ["participant"] = "reviewer",
                ["message"]     = "ctx",
                ["async"]       = "yes"
            };

            var response = await SendRequest(proc, ToolsCallRequest(51, "send_to_participant", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("async must be a boolean")).IsTrue();

            // No POST should have fired — the bad arg is rejected before the HTTP call.
            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/flow-bad-async/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(0);

            // The stdio loop must still be alive: a follow-up request gets a normal response.
            var followUp = await SendRequest(proc, ToolsCallRequest(52, "submit_review_round", new JsonObject {
                ["context"] = "no flow id, expect a clean error too"
            }));
            var followUpResult = followUp["result"]?.AsObject();
            await Assert.That(followUpResult).IsNotNull();
            await Assert.That(followUpResult!["isError"]?.GetValue<bool>()).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The review alias must stay byte-compatible with old servers that don't know about
    /// "participant" — the field is null-omitted, so the POST body must not carry the key at all.
    /// </summary>
    [Test]
    public async Task Submit_review_round_body_has_no_participant_key() {
        const string flowRunId = "flow-generic-3";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-3",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "ok",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "addressed all feedback"
            };

            var response = await SendRequest(proc, ToolsCallRequest(52, "submit_review_round", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var body     = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!.ContainsKey("participant")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Guardrail errors (max_rounds, wrong participant, etc.) come back from the server as a
    /// ProblemDetails 400 body — it must surface verbatim in the tool's error text, same as the
    /// review tools do.
    /// </summary>
    [Test]
    public async Task Guardrail_400_body_surfaces_in_tool_error() {
        const string flowRunId = "flow-generic-4";

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(400)
                    .WithHeader("Content-Type", "application/problem+json")
                    .WithBody("""{"detail":"max_rounds (2) reached for this run — close the flow."}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["participant"] = "reviewer",
                ["message"]     = "one more please"
            };

            var response = await SendRequest(proc, ToolsCallRequest(53, "send_to_participant", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("max_rounds (2)")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// get_flow_status / close_flow are pure aliases: they must hit the exact same endpoints as
    /// get_review_flow_status / close_review_flow.
    /// </summary>
    [Test]
    public async Task Get_flow_status_and_close_flow_hit_same_endpoints_as_review_tools() {
        const string flowRunId = "flow-generic-5";

        var stubbedStatus = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "definition_id": "my-custom-flow",
              "status": "completed",
              "target_title": "Generic target",
              "round_count": 1,
              "last_result_kind": "APPROVED",
              "last_result_text": "Looks good."
            }
            """;

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedStatus));

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var statusArgs     = new JsonObject { ["flow_run_id"] = flowRunId };
            var statusResponse = await SendRequest(proc, ToolsCallRequest(54, "get_flow_status", statusArgs));
            var statusResult   = statusResponse["result"]?.AsObject();
            await Assert.That(statusResult).IsNotNull();
            await Assert.That(statusResult!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var statusText = statusResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(statusText).IsNotNull();
            await Assert.That(statusText!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(statusText.Contains("Looks good.")).IsTrue();

            var getHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet());
            await Assert.That(getHits.Count).IsEqualTo(1);

            var closeArgs     = new JsonObject { ["flow_run_id"] = flowRunId };
            var closeResponse = await SendRequest(proc, ToolsCallRequest(55, "close_flow", closeArgs));
            var closeResult   = closeResponse["result"]?.AsObject();
            await Assert.That(closeResult).IsNotNull();
            await Assert.That(closeResult!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var closeText = closeResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(closeText).IsNotNull();
            await Assert.That(closeText!.Contains("status: closed")).IsTrue();

            var closeHits = _server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost());
            await Assert.That(closeHits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// kcap-flows auto-registers via the Claude plugin, so Claude Code
    /// spawns `kcap mcp flows` for every session. initialize / tools/list must stay local-only —
    /// the authenticated client (and its GET /auth/config round-trip + re-auth stderr hint) is
    /// created lazily on the first tools/call, so sessions that never use a flows tool pay
    /// nothing. Mirrors McpSessionsServer.
    /// </summary>
    [Test]
    public async Task Initialize_and_tools_list_do_not_consult_auth() {
        using var proc = SpawnMcpServer(provider: "GitHub");
        try {
            await SendRequest(proc, InitializeRequest(1));
            await SendRequest(proc, ToolsListRequest(2));

            var authHits = _server.FindLogEntries(Request.Create().WithPath("/auth/config").UsingGet());
            await Assert.That(authHits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A scheme-less server_url is refused before dispatch, so a tool call returns a graceful
    /// JSON-RPC tool error and the server keeps serving.
    /// </summary>
    [Test]
    public async Task Tool_call_with_invalid_server_url_returns_error_and_server_survives() {
        using var proc = SpawnMcpServer(urlOverride: "not-a-valid-url");
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "get_review_flow_status", new JsonObject()));
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
    /// Verifies that start_review_flow posts to /api/flows/review/start/v2, that the POST body
    /// includes the resolved requester context (requesting_repo_root = git root, requesting_session_id
    /// from KCAP_SESSION_ID), and that the MCP tool response surfaces both the flow_run_id/status
    /// envelope and the FINDINGS result text from the server.
    /// </summary>
    [Test]
    public async Task Start_review_flow_posts_requester_context_and_returns_plain_text_result() {
        const string flowRunId  = "flow-abc-123";
        const string roundId    = "round-001";
        const string sessionId  = "claude-session-aabbccdd";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "## Review findings\n\nThe spec looks good.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath("/api/flows/review/start/v2")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        // Create a subdirectory inside Repo.Path so the server starts there — we can then
        // verify requesting_repo_root points to Repo.Path (the git root) while requesting_cwd
        // points to the subdirectory.
        var subdir = Path.Combine(Repo.Path, "src", "feature");
        Directory.CreateDirectory(subdir);

        using var proc = SpawnMcpServerWithSession(sessionId, workingDirectory: subdir);
        try {
            var args = new JsonObject {
                ["kind"]         = "spec-review",
                ["target_kind"]  = "spec",
                ["target_ref"]   = "docs/feature.md",
                ["target_title"] = "Feature spec",
                ["context"]      = "Please review this spec for completeness."
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "start_review_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("result_kind: FINDINGS")).IsTrue();
            await Assert.That(text.Contains("## Review findings")).IsTrue();
            await Assert.That(text.Contains("The spec looks good.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath("/api/flows/review/start/v2").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            var body = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();

            // Stripped of dashes.
            var reqSessionId = bodyNode!["requesting_session_id"]?.GetValue<string>();
            await Assert.That(reqSessionId).IsNotNull();
            await Assert.That(reqSessionId!.Contains("claudesessionaabbccdd") || reqSessionId.Contains("claude-session-aabbccdd")).IsTrue();

            var reqCwd = bodyNode["requesting_cwd"]?.GetValue<string>();
            await Assert.That(reqCwd).IsNotNull();
            await Assert.That(
                reqCwd!.Contains(Path.Combine("src", "feature")) ||
                reqCwd.Equals(subdir, StringComparison.OrdinalIgnoreCase)
            ).IsTrue();

            // requesting_repo_root should be Repo.Path (the git root), but compared by containment
            // rather than equality: Directory.GetCurrentDirectory() in the spawned process can
            // resolve symlinks that the test's own path does not, so the two strings can differ on
            // macOS even though they name the same directory.
            var reqRepoRoot = bodyNode["requesting_repo_root"]?.GetValue<string>();
            await Assert.That(reqRepoRoot).IsNotNull();
            var cwdDirName = Path.GetFileName(Repo.Path.TrimEnd(Path.DirectorySeparatorChar));
            await Assert.That(reqRepoRoot!.Contains(cwdDirName, StringComparison.OrdinalIgnoreCase)).IsTrue();

            await Assert.That(bodyNode["kind"]?.GetValue<string>()).IsEqualTo("spec-review");
            await Assert.That(bodyNode["target_kind"]?.GetValue<string>()).IsEqualTo("spec");
            await Assert.That(bodyNode["context"]?.GetValue<string>()).IsEqualTo("Please review this spec for completeness.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The requester-context defect, end to end through the real spawned process: a driver session
    /// launched from another session's shell inherits the LAUNCHER's KCAP_SESSION_ID and (with no cwd
    /// pinned on the MCP registration) the launcher's working directory. Both must lose to the running
    /// harness's own per-process signals, or every flow the driver starts is attributed to the parent
    /// session and reviewed in the parent's checkout — which silently hands the reviewer the wrong
    /// diff.
    ///
    /// The environment here is deliberately WRONG in both dimensions and the assertions demand the
    /// right answers, so a regression to reading the ambient values cannot pass.
    /// </summary>
    [Test]
    public async Task Start_review_flow_prefers_the_running_harness_over_the_inherited_environment() {
        const string inheritedParentSession = "aaaaaaaabbbbbbbbccccccccdddddddd";
        const string runningDriverSession   = "11111111-2222-3333-4444-555555555555";

        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""
                    {"flow_run_id":"flow-1","round_id":"r1","round_number":1,"status":"completed",
                     "result_kind":"CLEAN","result_text":"looks good"}
                    """));

        // Two separate git checkouts: the one the process is launched in (the parent's, inherited)
        // and the one the running harness reports as its project (the driver's own worktree).
        using var driverRepo = GitRepo.Create("driver");
        driverRepo.AddRemote("https://github.com/test-owner/test-repo.git");

        using var proc = SpawnMcpServerWithSession(
            inheritedParentSession,
            workingDirectory:  Repo.Path,      // the launching (parent) checkout — inherited, wrong
            harnessSessionId:  runningDriverSession,
            harnessProjectDir: driverRepo);  // the driver's own checkout — correct

        try {
            var args = new JsonObject {
                ["kind"]         = "code-review",
                ["target_kind"]  = "pr",
                ["target_ref"]   = "42",
                ["target_title"] = "Some PR",
                ["context"]      = "Review this."
            };

            var response = await SendRequest(proc, ToolsCallRequest(3, "start_review_flow", args));
            await Assert.That(response["result"]?["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var hits = _server.FindLogEntries(
                Request.Create().WithPath("/api/flows/review/start/v2").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var body = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")!.AsObject();

            // The running driver's session, dash-stripped — NOT the inherited one.
            await Assert.That(body["requesting_session_id"]?.GetValue<string>())
                .IsEqualTo("11111111222233334444555555555555");
            await Assert.That(body["requesting_session_id"]?.GetValue<string>())
                .IsNotEqualTo(inheritedParentSession);

            // The driver's checkout, not the directory the process was launched in. Compared by
            // directory name because macOS can resolve the path through a symlink.
            var driverRepoName = Path.GetFileName(driverRepo.Path);
            var parentRepoName = Path.GetFileName(Repo.Path);
            foreach (var field in (string[])["requesting_cwd", "requesting_repo_root", "repo_path"]) {
                var value = body[field]?.GetValue<string>();
                await Assert.That(value).IsNotNull();
                await Assert.That(value!.Contains(driverRepoName, StringComparison.OrdinalIgnoreCase)).IsTrue();
                await Assert.That(value.Contains(parentRepoName, StringComparison.OrdinalIgnoreCase)).IsFalse();
            }
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_review_flow_status_calls_correct_endpoint_and_surfaces_envelope() {
        const string flowRunId = "flow-status-xyz";

        var stubbedStatus = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "definition_id": "spec-review",
              "status": "completed",
              "target_title": "My Spec",
              "round_count": 2,
              "last_result_kind": "APPROVED",
              "last_result_text": "Approved with minor comments."
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}")
                .UsingGet()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedStatus)
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(4, "get_review_flow_status", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: completed")).IsTrue();
            await Assert.That(text.Contains("Approved with minor comments.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_posts_to_rounds_endpoint() {
        const string flowRunId = "flow-round-abc";
        const string roundId   = "round-002";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "{{roundId}}",
              "round_number": 2,
              "status": "completed",
              "result_kind": "APPROVED",
              "result_text": "Changes look good now.",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/rounds")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(stubbedResponse)
        );

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "I have addressed all feedback."
            };
            var response = await SendRequest(proc, ToolsCallRequest(5, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("round_id:")).IsTrue();
            await Assert.That(text.Contains("Changes look good now.")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);

            // Verify the POST body has context
            var body     = hits[0].RequestMessage.Body ?? "";
            var bodyNode = JsonNode.Parse(body)?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["context"]?.GetValue<string>()).IsEqualTo("I have addressed all feedback.");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Close_review_flow_posts_to_close_endpoint() {
        const string flowRunId = "flow-close-abc";

        _server.Given(
            Request.Create()
                .WithPath($"/api/flows/{flowRunId}/close")
                .UsingPost()
        ).RespondWith(
            Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""")
        );

        using var proc = SpawnMcpServer();
        try {
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(6, "close_review_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: closed")).IsTrue();
            // FormatCloseResponse must NOT emit round_id or result_kind lines
            await Assert.That(text.Contains("round_id:")).IsFalse();
            await Assert.That(text.Contains("result_kind:")).IsFalse();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// If the GET for a running round returns HTTP 500 once and then 200 with a terminal result,
    /// the poll should survive the transient error and return the terminal result. Guards
    /// PollUntilTerminalAsync's !IsSuccessStatusCode → continue branch.
    /// </summary>
    [Test]
    public async Task Start_review_flow_async_survives_transient_500_on_poll() {
        const string flowRunId = "flow-retry-500";
        const string scenario  = "retry-500";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET #1: 500 (transient). GET #2: terminal findings.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WillSetStateTo("after-500")
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("Internal Server Error"));
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WhenStateIs("after-500")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"findings","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- P1"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            var response = await SendRequest(proc, ToolsCallRequest(30, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("FINDINGS:")).IsTrue();
            await Assert.That(text.Contains("result_kind: findings")).IsTrue();

            // Exactly 2 GETs: the 500 and then the terminal 200.
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count).IsEqualTo(2);
        } finally { await ShutdownAsync(proc); }
    }

    /// <summary>
    /// If the GET returns a run-level <c>status: "failed"</c> while <c>round_status</c> is still
    /// "running" (round didn't produce a result), the poll must stop immediately (not hang until
    /// the 8-min cap) and return an explicit isError:true result — NOT "Review still running" and
    /// NOT a partial envelope. Guards the run-terminal early-exit in PollUntilTerminalAsync.
    /// </summary>
    [Test]
    public async Task Start_review_flow_async_stops_when_run_level_fails() {
        const string flowRunId = "flow-run-failed";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET always returns run-level "failed" (round_status still "running" — no terminal result).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"failed","target_title":"t","round_count":1,"round_number":1,"round_status":"running","round_result_kind":null,"round_result_text":null}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            // This must resolve quickly (run-terminal path exits on first "failed" GET),
            // well within 15 s (compared to the 8-min cap if we polled indefinitely).
            var response = await SendRequest(proc, ToolsCallRequest(31, "start_review_flow", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            var text = result!["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            // Run failed without producing a terminal round — must be isError:true,
            // must NOT be the graceful-cap message, must mention "failed".
            await Assert.That(result["isError"]?.GetValue<bool>()).IsTrue();
            await Assert.That(text!.Contains("Review still running")).IsFalse();
            await Assert.That(text.Contains("failed")).IsTrue();
        } finally { await ShutdownAsync(proc); }
    }

    // Graceful-cap behaviour (poll exceeds the 8-min PollCap → returns "Review still running …
    // call get_review_flow_status") is exercised manually only: the cap has no injectable test
    // seam, so a CI test would have to either run for 8 minutes or add a source-level seam.
    // Manual e2e: start a flow against a server that never completes the round, wait 8 min,
    // assert the graceful-cap message appears in the MCP tool output.

    /// <summary>
    /// When run-level status is "failed" but the projected round_number doesn't match the round we
    /// submitted (e.g. projection still shows prior round 1 when we submitted round 2), the result
    /// MUST be an explicit run-failed error (isError:true), NOT the prior round's findings.
    /// </summary>
    [Test]
    public async Task Run_failed_before_requested_round_returns_explicit_error_not_stale_findings() {
        const string flowRunId = "flow-run-failed-stale";

        // POST submits round 2.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/rounds").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r2","round_number":2,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET returns run-level "failed" but still shows round 1's findings (stale projection).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"failed","target_title":"t","round_count":2,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- Round 1 stale data"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["flow_run_id"] = flowRunId,
                ["context"]     = "Re-review after fixes."
            };
            var response = await SendRequest(proc, ToolsCallRequest(40, "submit_review_round", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            // Must be an error result (isError:true), NOT the prior round's findings.
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            // Must NOT contain the stale round 1 findings text.
            await Assert.That(text!.Contains("Round 1 stale data")).IsFalse();
            // Must contain an explicit failure message for round 2.
            await Assert.That(text.Contains("failed")).IsTrue();
            await Assert.That(text.Contains('2')).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Persistent 500 on every GET exhausts the transient-retry budget and returns isError:true
    /// well before the 8-min cap. The result must NOT be "Review still running" (which is only
    /// for genuine running-at-cap). Budget is 5 consecutive transient failures.
    /// </summary>
    [Test]
    public async Task Persistent_500_exhausts_retry_budget_and_returns_isError() {
        const string flowRunId = "flow-persistent-500";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // Every GET returns 500.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("Internal Server Error"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // Must complete well before 8 min (expect ~20-30s for 6 GETs at 3s poll + budget logic).
            var response = await SendRequest(proc, ToolsCallRequest(41, "start_review_flow", args), TimeSpan.FromSeconds(60));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            // Must be an error (isError:true), NOT "Review still running" graceful cap.
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("Review still running")).IsFalse();
            await Assert.That(text.Contains("Error:")).IsTrue();

            // Should have hit exactly MaxTransientRetries + 1 GETs before giving up.
            var getCount = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            ).Count;
            // Budget is 5; 6th failure (index 5) triggers the error return.
            await Assert.That(getCount).IsEqualTo(6);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Non-transient 403 on GET returns immediate isError:true with no retry — same for 400.
    /// These 4xx errors must fail immediately, not be treated as transient.
    /// </summary>
    [Test]
    public async Task Non_transient_403_on_poll_returns_immediate_isError() {
        const string flowRunId = "flow-403";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET returns 403 (non-transient 4xx, not 401 which has refresh-retry logic).
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("Forbidden"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // Must complete almost immediately (no retry, no delay loop for 4xx).
            var response = await SendRequest(proc, ToolsCallRequest(42, "start_review_flow", args), TimeSpan.FromSeconds(15));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("403")).IsTrue();

            // Exactly 1 GET (immediate fail on first 403, no retry).
            var getCount = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()
            ).Count;
            await Assert.That(getCount).IsEqualTo(1);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A 404 that occurs after the grace deadline (anchored to poll start) must return
    /// isError:true — grace is relative to when polling began, not when the first 404 was
    /// observed. NotFoundGrace = 10s and PollInterval = 3s, so stubbing every GET as 404 and
    /// waiting past 10s must make the poll give up before the 8-min cap.
    /// </summary>
    [Test]
    public async Task NotFound_past_grace_deadline_returns_isError() {
        const string flowRunId = "flow-404-grace";

        // POST returns running.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // Every GET returns 404 indefinitely.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("Not Found"));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"] = "spec-review", ["target_kind"] = "spec", ["target_ref"] = "r",
                ["target_title"] = "t", ["context"] = "please review"
            };
            // NotFoundGrace = 10s, PollInterval = 3s → should fail within ~15s (grace + one more poll).
            // Allow 30s to be safe.
            var response = await SendRequest(proc, ToolsCallRequest(43, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();

            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("not found")).IsTrue();
            // Must NOT be the 8-min graceful cap message.
            await Assert.That(text.Contains("Review still running")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Submit_review_round_without_flow_run_id_returns_error() {
        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["context"] = "Some context but no flow ID"
            };
            var response = await SendRequest(proc, ToolsCallRequest(7, "submit_review_round", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("flow_run_id")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Start_review_flow_async_polls_until_terminal_findings() {
        const string flowRunId = "flow-poll-1";
        const string scenario  = "poll";

        // POST returns running + round_number 1.
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"running","result_kind":null,"result_text":null,"reviewer_agent_id":"a1","reviewer_session_id":"s1"}"""));

        // GET #1: still running. GET #2: terminal findings.
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WillSetStateTo("seen-once")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"running","target_title":"t","round_count":1,"round_number":1,"round_status":"running","round_result_kind":null,"round_result_text":null}"""));
        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
            .InScenario(scenario).WhenStateIs("seen-once")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","definition_id":"spec-review","status":"findings","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","round_result_kind":"findings","round_result_text":"FINDINGS:\n- P1"}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject {
                ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r",
                ["target_title"]="t", ["context"]="please review"
            };
            var response = await SendRequest(proc, ToolsCallRequest(20, "start_review_flow", args), TimeSpan.FromSeconds(30));
            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("FINDINGS:")).IsTrue();
            await Assert.That(text.Contains("result_kind: findings")).IsTrue();

            // The POST carried async:true.
            var postBody = JsonNode.Parse(_server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(postBody!["async"]?.GetValue<bool>()).IsTrue();
            // At least 2 GETs (running then terminal).
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count >= 2).IsTrue();
        } finally { await ShutdownAsync(proc); }
    }

    [Test]
    public async Task Start_review_flow_uses_terminal_result_from_post_without_polling() {
        const string flowRunId = "flow-old-server";
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type","application/json")
                .WithBody($$"""{"flow_run_id":"{{flowRunId}}","round_id":"r1","round_number":1,"status":"completed","result_kind":"FINDINGS","result_text":"## done","reviewer_agent_id":null,"reviewer_session_id":null}"""));

        using var proc = SpawnMcpServer();
        try {
            var args = new JsonObject { ["kind"]="spec-review", ["target_kind"]="spec", ["target_ref"]="r", ["target_title"]="t", ["context"]="c" };
            var response = await SendRequest(proc, ToolsCallRequest(21, "start_review_flow", args));
            var text = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text!.Contains("## done")).IsTrue();
            // No GET polling happened (status was already terminal).
            await Assert.That(_server.FindLogEntries(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet()).Count).IsEqualTo(0);
        } finally { await ShutdownAsync(proc); }
    }

    /// <summary>
    /// Writes a non-expired token to the per-test config dir's token store so the send path
    /// attaches a Bearer header. Exercises the long-lived-server path (the MCP server holds a
    /// single HttpClient for the agent's whole session).
    /// </summary>
    void SeedToken(string accessToken = "seed-token") {
        var tokensDir = Config.Root.Path("tokens");
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
    /// The MCP server caches a single <c>HttpClient</c> for the whole agent session, so if the auth
    /// header expires mid-session every tool call must not be stuck returning the friendly 401
    /// message until the server is restarted: it must retry once on 401 after calling
    /// <c>TokenStore.GetValidTokensAsync</c>.
    ///
    /// We seed a non-expired token in the per-test config dir and stub WireMock so the first call
    /// returns 401 and the second returns 200, proving the retry path runs at all. This does not
    /// exercise the real refresh-token flow — that would also require stubbing the GitHub refresh
    /// endpoint.
    /// </summary>
    [Test]
    public async Task Refreshed_token_succeeds_after_401() {
        const string flowRunId   = "flow-retry-abc";
        const string stubbedBody = $$"""{"flow_run_id":"{{flowRunId}}","status":"closed"}""";
        const string scenario    = "auth-retry";

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
            .InScenario(scenario)
            .WillSetStateTo("after-401")
            .RespondWith(Response.Create().WithStatusCode(401).WithBody(""));

        _server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost())
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
            var args     = new JsonObject { ["flow_run_id"] = flowRunId };
            var response = await SendRequest(proc, ToolsCallRequest(8, "close_review_flow", args));

            var result = response["result"]?.AsObject();
            // Must be a success — the 401 was retried and the second call succeeded.
            await Assert.That(result?["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result?["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains($"flow_run_id: {flowRunId}")).IsTrue();
            await Assert.That(text.Contains("status: closed")).IsTrue();

            var hits = _server.FindLogEntries(
                Request.Create().WithPath($"/api/flows/{flowRunId}/close").UsingPost()
            );
            await Assert.That(hits.Count).IsEqualTo(2);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    const string DynamicDefinitionYaml = """
        id: my-dynamic-flow
        participants:
          reviewer:
            vendor: claude
            model: claude-sonnet-4-5
            workspace: none
        """;

    static JsonObject DynamicStartTargetArgs() => new() {
        ["target_kind"]  = "pr",
        ["target_ref"]   = "https://github.com/x/y/pull/1",
        ["target_title"] = "My PR",
        ["context"]      = "please look at this"
    };

    /// <summary>
    /// Dynamic flows: start_flow with definition_yaml posts the YAML doc on the snake_case
    /// definition_yaml wire field and must NOT carry "kind" at all — the server treats the two
    /// as mutually exclusive and rejects a body with both.
    /// </summary>
    [Test]
    public async Task Start_flow_with_definition_yaml_posts_it_and_omits_kind() {
        const string flowRunId = "flow-dynamic-1";

        var stubbedResponse = $$"""
            {
              "flow_run_id": "{{flowRunId}}",
              "round_id": "round-1",
              "round_number": 1,
              "status": "completed",
              "result_kind": "FINDINGS",
              "result_text": "dynamic flow result",
              "reviewer_agent_id": null,
              "reviewer_session_id": null
            }
            """;

        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(stubbedResponse));

        using var proc = SpawnMcpServer();
        try {
            var args = DynamicStartTargetArgs();
            args["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(60, "start_flow", args));

            var result = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsNotEqualTo(true);

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("dynamic flow result")).IsTrue();

            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(1);

            var bodyNode = JsonNode.Parse(hits[0].RequestMessage.Body ?? "")?.AsObject();
            await Assert.That(bodyNode).IsNotNull();
            await Assert.That(bodyNode!["definition_yaml"]?.GetValue<string>()).IsEqualTo(DynamicDefinitionYaml);
            await Assert.That(bodyNode.ContainsKey("kind")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// The definition_id / definition_yaml xor can't be expressed in the MCP schema (both are
    /// optional there), so the handler must enforce exactly-one BEFORE any HTTP call and return
    /// a clean isError tool result naming the mutual exclusion.
    /// </summary>
    [Test]
    public async Task Start_flow_with_both_or_neither_id_and_yaml_errors_before_http() {
        using var proc = SpawnMcpServer();
        try {
            var both = DynamicStartTargetArgs();
            both["definition_id"]   = "catalog-flow";
            both["definition_yaml"] = DynamicDefinitionYaml;

            var bothResponse = await SendRequest(proc, ToolsCallRequest(61, "start_flow", both));
            var bothResult   = bothResponse["result"]?.AsObject();
            await Assert.That(bothResult).IsNotNull();
            await Assert.That(bothResult!["isError"]?.GetValue<bool>()).IsTrue();

            var bothText = bothResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(bothText).IsNotNull();
            await Assert.That(bothText!.Contains("exactly one of definition_id")).IsTrue();

            var neitherResponse = await SendRequest(proc, ToolsCallRequest(62, "start_flow", DynamicStartTargetArgs()));
            var neitherResult   = neitherResponse["result"]?.AsObject();
            await Assert.That(neitherResult).IsNotNull();
            await Assert.That(neitherResult!["isError"]?.GetValue<bool>()).IsTrue();

            var neitherText = neitherResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(neitherText).IsNotNull();
            await Assert.That(neitherText!.Contains("exactly one of definition_id")).IsTrue();

            // Neither call may have reached the server.
            var hits = _server.FindLogEntries(Request.Create().WithPath("/api/flows/review/start").UsingPost());
            await Assert.That(hits.Count).IsEqualTo(0);
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Dynamic-rejection contract: any non-2xx body carrying a string "error" code plus "message"
    /// is a NEW-server coded rejection — the CLI surfaces the server message verbatim (prefixed
    /// with the code) and must NOT add the "may not support dynamic flows" old-server hint.
    /// </summary>
    [Test]
    public async Task Coded_400_surfaces_server_message_verbatim() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(400)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"error":"model_unpriced","message":"participant 'reviewer': model 'x' has no known pricing — pick a priced model."}""")
            );

        using var proc = SpawnMcpServer();
        try {
            var args = DynamicStartTargetArgs();
            args["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(63, "start_flow", args));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("model_unpriced")).IsTrue();
            await Assert.That(text.Contains("participant 'reviewer': model 'x' has no known pricing — pick a priced model.")).IsTrue();
            await Assert.That(text.Contains("may not support dynamic flows")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// An UNCODED non-2xx on a start that included definition_yaml means the server may predate
    /// dynamic flows — the tool error must carry the upgrade hint plus the raw body. The same
    /// uncoded failure on a definition_id (catalog) start must NOT get the hint.
    /// </summary>
    [Test]
    public async Task Uncoded_500_on_dynamic_start_maps_to_unsupported_server_hint() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("upstream exploded"));
        _server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("upstream exploded"));

        using var proc = SpawnMcpServer();
        try {
            var dynamicArgs = DynamicStartTargetArgs();
            dynamicArgs["definition_yaml"] = DynamicDefinitionYaml;

            var dynamicResponse = await SendRequest(proc, ToolsCallRequest(64, "start_flow", dynamicArgs));
            var dynamicResult   = dynamicResponse["result"]?.AsObject();
            await Assert.That(dynamicResult).IsNotNull();
            await Assert.That(dynamicResult!["isError"]?.GetValue<bool>()).IsTrue();

            var dynamicText = dynamicResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(dynamicText).IsNotNull();
            await Assert.That(dynamicText!.Contains("may not support dynamic flows")).IsTrue();
            await Assert.That(dynamicText.Contains("upstream exploded")).IsTrue();

            var catalogArgs = DynamicStartTargetArgs();
            catalogArgs["definition_id"] = "catalog-flow";

            var catalogResponse = await SendRequest(proc, ToolsCallRequest(65, "start_flow", catalogArgs));
            var catalogResult   = catalogResponse["result"]?.AsObject();
            await Assert.That(catalogResult).IsNotNull();
            await Assert.That(catalogResult!["isError"]?.GetValue<bool>()).IsTrue();

            var catalogText = catalogResult["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(catalogText).IsNotNull();
            await Assert.That(catalogText!.Contains("upstream exploded")).IsTrue();
            await Assert.That(catalogText.Contains("may not support dynamic flows")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// A non-2xx body that IS valid JSON but not an object (e.g. a proxy's quoted scalar string)
    /// must not throw past the coded-rejection check — <c>JsonNode.Parse(...).AsObject()</c> throws
    /// <see cref="InvalidOperationException"/> on a scalar/array node, which would otherwise escape
    /// to the dispatcher catch-all and replace the useful status/body/hint with a generic internal
    /// error. It must fall through to the uncoded path exactly like non-JSON bodies do.
    /// </summary>
    [Test]
    public async Task NonObject_json_body_falls_through_to_uncoded_path() {
        _server.Given(Request.Create().WithPath("/api/flows/review/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502).WithHeader("Content-Type", "application/json").WithBody("\"Bad Gateway\""));

        using var proc = SpawnMcpServer();
        try {
            var dynamicArgs = DynamicStartTargetArgs();
            dynamicArgs["definition_yaml"] = DynamicDefinitionYaml;

            var response = await SendRequest(proc, ToolsCallRequest(66, "start_flow", dynamicArgs));
            var result   = response["result"]?.AsObject();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!["isError"]?.GetValue<bool>()).IsTrue();

            var text = result["content"]?[0]?["text"]?.GetValue<string>();
            await Assert.That(text).IsNotNull();
            await Assert.That(text!.Contains("may not support dynamic flows")).IsTrue();
            await Assert.That(text.Contains("\"Bad Gateway\"")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>
    /// Pins the start_flow schema after the dynamic-flows change: definition_yaml is offered,
    /// required drops definition_id (the xor can't be expressed in the schema — it lives in both
    /// property descriptions and is enforced by the handler), and the definition_yaml description
    /// carries the parser's hard requirements (workspace: none, concrete model).
    /// </summary>
    [Test]
    public async Task Start_flow_schema_offers_definition_yaml_and_requires_neither_definition_arg() {
        using var proc = SpawnMcpServer();
        try {
            var response = await SendRequest(proc, ToolsListRequest(2));
            var tools    = response["result"]?["tools"]?.AsArray();
            await Assert.That(tools).IsNotNull();

            var startFlow = tools!.First(t => t?["name"]?.GetValue<string>() == "start_flow")!;

            await AssertSchema(
                startFlow,
                properties: ["definition_id", "definition_yaml", "target_kind", "target_ref", "target_title", "context", "instructions", "mode", "vendor", "model"],
                required:   ["target_kind", "target_ref", "target_title", "context"]
            );

            var props    = startFlow["inputSchema"]!["properties"]!.AsObject();
            var idDesc   = props["definition_id"]?["description"]?.GetValue<string>() ?? "";
            var yamlDesc = props["definition_yaml"]?["description"]?.GetValue<string>() ?? "";

            await Assert.That(idDesc.Contains("exactly one")).IsTrue();
            await Assert.That(yamlDesc.Contains("exactly one")).IsTrue();
            await Assert.That(yamlDesc.Contains("workspace: none")).IsTrue();
        } finally {
            await ShutdownAsync(proc);
        }
    }
}
