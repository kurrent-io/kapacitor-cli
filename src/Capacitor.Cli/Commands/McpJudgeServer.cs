using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

sealed class McpJudgeServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http) {
    /// <summary>
    /// Run as a session-scoped MCP server. All tool calls must use <paramref name="expectedSessionId"/>.
    /// </summary>
    public async Task<int> RunAsync(string expectedSessionId) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Validate the shape locally, then defer client construction to the first tools/call —
        // the shape every sibling MCP server already uses.
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);
        HttpClient? client = null;

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — re-initialise under the reportable pseudo-command
        // "mcp-server" so per-tool-call events actually leave. Best-effort: a stale token on
        // disk must never block the server from starting.
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }
        CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn, config);

        var tools = BuildToolsList();

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        try {
        while (await reader.ReadLineAsync() is { } line) {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject? request;

            try {
                request = JsonNode.Parse(line)?.AsObject();
            } catch {
                continue; // skip malformed JSON
            }

            if (request is null) continue;

            var id     = request["id"];
            var method = request["method"]?.GetValue<string>();

            // Notifications have no id — don't send a response
            if (id is null) continue;

            var response = method switch {
                "initialize" => BuildInitializeResponse(id, request),
                "tools/list" => BuildToolsListResponse(id, tools),
                "tools/call" => await TimedDispatchToolCallAsync(id, request),
                _            => McpProtocol.TryHandleStandardMethod(method, id)
                                ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
        }
        } finally {
            // Deferring construction must not also defer disposal: the loop ends when stdin closes.
            client?.Dispose();
        }

        return 0;

        // Report an unusable server_url as a JSON-RPC tool error rather than dying: a server is
        // expected to keep serving, and the caller can see the reason.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk) return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            client ??= await http.ForSessionAsync();
            return await HandleToolCallAsync(callId, callRequest, client, baseUrl, expectedSessionId);
        }

        // Records which MCP tools agents actually reach for. Never touches the response path:
        // the result (or the exception) is returned exactly as DispatchToolCallAsync produced it.
        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = Stopwatch.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                McpTelemetry.ToolCalled("kcap-judge", tool, ok, CommandTiming.ElapsedMs(start));
            }
        }
    }

    /// <summary>Test hook: execute a tool-call handler and return the JSON-RPC envelope.</summary>
    internal static async Task<string> HandleToolCallForTests(
            string     toolName,
            JsonObject arguments,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        JsonNode id = 1;

        var request = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["method"]  = "tools/call",
            ["params"] = new JsonObject {
                ["name"]      = toolName,
                ["arguments"] = arguments.DeepClone()
            }
        };

        return await HandleToolCallAsync(id, request, client, baseUrl, expectedSessionId);
    }

    static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var apiRoot   = baseUrl.TrimEnd('/');
            var sessionId = arguments?["session_id"]?.GetValue<string>();

            if (sessionId is null) {
                return BuildToolResult(id, "Error: missing required argument: session_id", isError: true);
            }

            if (sessionId != expectedSessionId) {
                return BuildToolResult(
                    id,
                    $"Error: session_id '{sessionId}' does not match this judge's bound session",
                    isError: true
                );
            }

            var encoded = Uri.EscapeDataString(sessionId);

            using var httpResponse = toolName switch {
                "get_session_recap"   => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/recap?chain=true"),
                "get_session_errors"  => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/errors?chain=true"),
                "get_transcript"      => await client.GetAsync(BuildTranscriptUrl(apiRoot, sessionId, arguments)),
                "get_session_summary" => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/eval-summary"),
                "search_session"      => await client.PostAsync(
                    $"{apiRoot}/api/sessions/{encoded}/search",
                    JsonContent.Create(BuildSearchBody(arguments), McpJsonContext.Default.SessionSearchQuery)
                ),
                "get_tool_result"     => await client.GetAsync(
                    $"{apiRoot}/api/sessions/{encoded}/tool-results/{Uri.EscapeDataString(GetRequiredString(arguments, "call_id"))}"
                ),
                _                     => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return !httpResponse.IsSuccessStatusCode
                ? BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true)
                : BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-judge", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    static string BuildErrorResponse(JsonNode id, int code, string message) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        };

        return envelope.ToJsonString();
    }

    static string ToResponse<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        };

        return envelope.ToJsonString();
    }

    static McpTool[] BuildToolsList() => [
        new(
            "get_session_recap",
            "Get a short narrative recap of the session's user inputs, assistant replies, and tool invocations. "
          + "Start here — it's the cheapest way to orient yourself before pulling specific transcript slices.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID to recap (must match the judge's bound session)") },
                ["session_id"]
            )
        ),
        new(
            "get_session_errors",
            "List errors and failures recorded in the session (tool errors, non-zero exits, exceptions). "
          + "Use when you need to ground a finding in specific failures instead of inferring from the transcript.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID (must match the judge's bound session)") },
                ["session_id"]
            )
        ),
        new(
            "get_transcript",
            "Get the full transcript of a specific session: user messages, assistant reasoning, tool calls, and results. Paginated (default 100 events). Use file_path filter to scope to events mentioning a specific file. This is the deepest level of detail — use when you need to trace the exact reasoning chain.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID to retrieve the transcript for (must match the judge's bound session)"),
                    ["file_path"]  = new("string", "Optional file path to filter transcript events"),
                    ["skip"]       = new("integer", "Number of events to skip (for pagination)"),
                    ["take"]       = new("integer", "Number of events to return (for pagination)")
                },
                ["session_id"]
            )
        ),
        new(
            "get_session_summary",
            "Get a structured orientation digest for the session: title, turn count, plan text, files touched (path + change type + event count), "
          + "test runs with pass/fail, error flag, and which tool results were truncated in the compacted trace (pair call_ids with get_tool_result). "
          + "START HERE when you need a quick overview before deciding which MCP tool to call next.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID (must match the judge's bound session)") },
                ["session_id"]
            )
        ),
        new(
            "search_session",
            "Full-text search scoped to one session. Returns ranked excerpts with speaker (user/assistant/tool), timestamp, and a highlighted snippet. "
          + "Use for targeted lookups ('where did the agent run rm', 'what error mentioned permission') rather than paging the full trace. "
          + "limit defaults to 20, capped at 50.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["query"]      = new("string", "Free-text FTS5 query"),
                    ["limit"]      = new("integer", "Max number of ranked excerpts to return (default 20, max 50)")
                },
                ["session_id", "query"]
            )
        ),
        new(
            "get_tool_result",
            "Fetch the untruncated body of a single tool result by its call_id. Use when get_session_summary reported the result was truncated "
          + "(see the truncated_tool_results field) and the finding depends on what the tool actually produced — e.g. full dotnet test output, "
          + "full git diff, full search hit list. 1 MB hard cap on response size.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["call_id"]    = new("string", "call_id from a tool_invocation/tool_result pair (e.g. toolu_01abc...)")
                },
                ["session_id", "call_id"]
            )
        )
    ];

    static SessionSearchQuery BuildSearchBody(JsonObject? arguments) {
        var query = GetRequiredString(arguments, "query");

        int? limit = null;

        if (arguments?["limit"] is { } limitNode) {
            try {
                limit = limitNode.GetValue<int>();
            } catch (InvalidOperationException) {
                throw new ArgumentException("limit must be an integer");
            } catch (FormatException) {
                throw new ArgumentException("limit must be an integer");
            }
        }

        return new(query, limit);
    }

    static string GetRequiredString(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Missing required argument: {name}") : value;
    }

    static string BuildTranscriptUrl(string baseUrl, string sessionId, JsonObject? arguments) {
        var url         = $"{baseUrl}/api/review/sessions/{Uri.EscapeDataString(sessionId)}/transcript";
        var queryParams = new List<string>();

        if (arguments?["file_path"]?.GetValue<string>() is { } filePath) {
            queryParams.Add($"file_path={Uri.EscapeDataString(filePath)}");
        }

        if (arguments?["skip"]?.ToString() is { } skip) {
            queryParams.Add($"skip={Uri.EscapeDataString(skip)}");
        }

        if (arguments?["take"]?.ToString() is { } take) {
            queryParams.Add($"take={Uri.EscapeDataString(take)}");
        }

        if (queryParams.Count > 0) {
            url += "?" + string.Join("&", queryParams);
        }

        return url;
    }
}
