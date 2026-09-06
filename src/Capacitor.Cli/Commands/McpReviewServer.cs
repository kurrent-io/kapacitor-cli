using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

sealed class McpReviewServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http) {
    /// <summary>
    /// Run with an explicit session-default PR (used by <c>kcap review &lt;pr&gt;</c>).
    /// Tool calls may still override the default by passing a <c>pr</c> argument.
    /// </summary>
    public Task<int> RunAsync(string owner, string repo, int prNumber)
        => RunCoreAsync(profiles.Resolution.ServerUrl!, new PrIdentity(owner, repo, prNumber));

    /// <summary>
    /// Run without an explicit session default (used by the plugin's argless MCP
    /// registration). PR identity comes from each tool call's <c>pr</c> argument,
    /// or as a fallback from git auto-detection against the current branch.
    /// </summary>
    public Task<int> RunAutoAsync() => RunCoreAsync(profiles.Resolution.ServerUrl!, null);

    async Task<int> RunCoreAsync(string baseUrl, PrIdentity? startupDefault) {
        // Compute the session default once: explicit startup args win; otherwise
        // try git auto-detect (local). Result is cached for the lifetime of the server
        // and used as the fallback when a tool call doesn't carry an explicit `pr`.
        var sessionDefault = startupDefault ?? await DetectPrFromGitAsync();

        var tools = BuildToolsList();

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — re-initialise under the reportable pseudo-command
        // "mcp-server" so per-tool-call events actually leave. Best-effort: a stale token on
        // disk must never block the server from starting.
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }
        CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn, config);

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup:
        // kcap-review auto-registers, so Claude Code (and Codex) spawn `kcap mcp review` for
        // every session — deferring keeps startup local-only (no GET /auth/config, token load,
        // or stderr re-auth hint) for sessions that never invoke a review tool. Created on demand
        // into a nullable field (rather than a Lazy<Task>) so a transient creation failure leaves
        // it null and the next call retries, instead of a faulted task sticking for the rest of
        // the session. Safe without locking: the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, sessionDefault);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp review: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
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
                McpTelemetry.ToolCalled("kcap-review", tool, ok, CommandTiming.ElapsedMs(start));
            }
        }

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
            if (client is not null) {
                try { client.Dispose(); } catch {
                    /* swallow — best-effort cleanup */
                }
            }
        }

        return 0;
    }

    async Task<PrIdentity?> DetectPrFromGitAsync() {
        try {
            var cwd      = Directory.GetCurrentDirectory();
            var repoInfo = await RepositoryDetection.DetectRepositoryAsync(config, cwd);

            if (repoInfo?.Owner is not null && repoInfo.RepoName is not null && repoInfo.PrNumber is not null) {
                return new PrIdentity(repoInfo.Owner, repoInfo.RepoName, repoInfo.PrNumber.Value);
            }

            return null;
        } catch {
            return null;
        }
    }

    // Server-level usage preamble (MCP `instructions`) — a model-facing hint some clients add to the
    // system prompt. Same "reach for kcap before native git/GitHub/grep for why/history" framing as the
    // per-tool descriptions, at the server level (belt-and-suspenders behind the descriptions).
    const string ServerInstructions =
        "Use these tools when reviewing a PR or asking why code was written this way. Reach for them before " +
        "native git/GitHub/grep for why / history / prior-work questions — they surface the implementer's " +
        "reasoning from the recorded session transcript, which commits and diffs don't capture — while you " +
        "still read the diff itself for correctness.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-review", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static async Task<string> HandleToolCallAsync(
            JsonNode    id,
            JsonObject  request,
            HttpClient  client,
            string      baseUrl,
            PrIdentity? sessionDefault
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        switch (toolName) {
            case null:
                return BuildErrorResponse(id, -32602, "Missing params.name");
            // get_transcript keys off session_id, not PR — skip PR resolution.
            case "get_transcript":
                return await DispatchAsync(id, toolName, arguments, client, baseUrl, pr: null);
        }

        var resolution = PrResolution.Resolve(arguments, sessionDefault);

        if (resolution.Identity is null) {
            return BuildToolResult(id, resolution.Error!, isError: true);
        }

        return await DispatchAsync(id, toolName, arguments, client, baseUrl, resolution.Identity);
    }

    static async Task<string> DispatchAsync(
            JsonNode    id,
            string      toolName,
            JsonObject? arguments,
            HttpClient  client,
            string      baseUrl,
            PrIdentity? pr
        ) {
        try {
            var prBase = pr is null
                ? null
                : $"{baseUrl}/api/review/{Uri.EscapeDataString(pr.Owner)}/{Uri.EscapeDataString(pr.Repo)}/pulls/{pr.PrNumber}";

            var httpResponse = toolName switch {
                "get_pr_summary"   => await client.GetAsync(prBase),
                "list_pr_files"    => await client.GetAsync($"{prBase}/files"),
                "get_file_context" => await client.GetAsync($"{prBase}/files/{GetRequiredArg(arguments, "file_path").TrimStart('/')}"),
                "search_context" => await client.PostAsync(
                    $"{prBase}/search",
                    JsonContent.Create(new(GetRequiredArg(arguments, "query")), McpJsonContext.Default.SearchQuery)
                ),
                "list_sessions"  => await client.GetAsync($"{prBase}/sessions"),
                "get_transcript" => await client.GetAsync(BuildTranscriptUrl(baseUrl, arguments)),
                _                => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return !httpResponse.IsSuccessStatusCode ? BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true) : BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string GetRequiredArg(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();

        return value ?? throw new ArgumentException($"Missing required argument: {name}");
    }

    static string BuildTranscriptUrl(string baseUrl, JsonObject? arguments) {
        var sessionId = arguments?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

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

    static McpTool[] BuildToolsList() {
        const string PrArgDescription =
            "Optional PR reference (e.g. 'owner/repo#123' or a github.com PR URL). " +
            "Defaults to the session's PR if launched via `kcap review`, otherwise auto-detected from current branch.";

        return [
            new(
                "get_pr_summary",
                "Get an overview of the PR: which Claude Code sessions contributed, which files were changed (with event counts), and what test commands were run with their pass/fail outcomes. Call this first to orient yourself. When reviewing a PR or asking why code was written this way, reach for this before git blame or git log — and alongside the diff — because it surfaces the implementer's actual reasoning from the session transcript, which commits and diffs don't capture.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "list_pr_files",
                "List all files changed in the PR with aggregated metadata: change types (read/edit/create), how many sessions touched each file, and total event count. Use this to understand the scope of changes.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "get_file_context",
                "Get deep context for a specific file: which sessions modified it, when, and relevant transcript excerpts where the file was discussed or changed. When a reviewer asks 'why was this file changed?', reach for this before git blame or git log — it surfaces the reasoning behind the change from the session transcript, complementing (not replacing) the diff.",
                new(
                    "object",
                    new() {
                        ["file_path"] = new("string", "Path of the file to get context for"),
                        ["pr"]        = new("string", PrArgDescription)
                    },
                    ["file_path"]
                )
            ),
            new(
                "search_context",
                "Full-text search across all session transcripts linked to this PR. Returns ranked excerpts with speaker (user/assistant/tool), content, and highlighted snippets. For 'why' questions ('why retry logic', 'what alternatives', 'error handling rationale'), search here before git blame or git log — it finds the implementer's reasoning across the session, which commits and diffs don't capture; still read the diff for correctness.",
                new(
                    "object",
                    new() {
                        ["query"] = new("string", "Free-text search query"),
                        ["pr"]    = new("string", PrArgDescription)
                    },
                    ["query"]
                )
            ),
            new(
                "list_sessions",
                "List all Claude Code sessions that contributed to this PR, with session IDs, titles, timestamps, and models used. Use this to understand the work timeline and pick sessions to drill into with get_transcript.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "get_transcript",
                "Get the full transcript of a specific session: user messages, assistant reasoning, tool calls, and results. Paginated (default 100 events). Use file_path filter to scope to events mentioning a specific file. This is the deepest level of detail — use when you need to trace the exact reasoning chain.",
                new(
                    "object",
                    new() {
                        ["session_id"] = new("string", "Session ID to retrieve the transcript for"),
                        ["file_path"]  = new("string", "Optional file path to filter transcript events"),
                        ["skip"]       = new("integer", "Number of events to skip (for pagination)"),
                        ["take"]       = new("integer", "Number of events to return (for pagination)")
                    },
                    ["session_id"]
                )
            )
        ];
    }
}

/// <summary>
/// PR identity resolved from one of: a tool call's <c>pr</c> argument, the
/// server's startup args, or git auto-detection at startup.
/// </summary>
record PrIdentity(string Owner, string Repo, int PrNumber);

/// <summary>
/// Resolves a tool call's effective PR identity. Tool args take precedence
/// over the session default. Returning a <c>null</c> identity carries an
/// error message ready to surface to the LLM.
/// </summary>
static class PrResolution {
    public readonly record struct Result(PrIdentity? Identity, string? Error);

    public static Result Resolve(JsonObject? toolArgs, PrIdentity? sessionDefault) {
        if (TryGetStringArg(toolArgs, "pr", out var prRef)) {
            if (PrRefParser.TryParse(prRef, out var owner, out var repo, out var prNumber)) {
                return new Result(new PrIdentity(owner, repo, prNumber), null);
            }

            return new Result(
                null,
                $"Could not parse `pr` argument: '{prRef}'. Use 'owner/repo#123' or a github.com PR URL."
            );
        }

        if (sessionDefault is not null) {
            return new Result(sessionDefault, null);
        }

        return new Result(
            null,
            "This tool needs a PR reference. Pass `pr` as a tool argument "  +
            "(e.g. 'owner/repo#123' or a github.com PR URL), or run from a " +
            "branch with an open PR for auto-detection."
        );
    }

    static bool TryGetStringArg(JsonObject? args, string name, out string value) {
        value = "";

        if (args?[name] is not JsonValue node) return false;
        if (!node.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return false;

        value = s;

        return true;
    }
}

// MCP protocol types — serialized with source-generated McpJsonContext for AOT compatibility.
// `Instructions` is the optional MCP-standard server-level usage preamble (camelCase + omitted when
// null via McpJsonContext's global options); servers that pass none simply omit it.
record McpInitResult(string ProtocolVersion, McpCapabilities Capabilities, McpServerInfo ServerInfo, string? Instructions = null);

record McpCapabilities(McpToolsCapability Tools);

record McpToolsCapability;

record McpServerInfo(string Name, string Version);

record McpToolsResult(McpTool[] Tools);

record McpTool(string Name, string Description, McpInputSchema InputSchema);

record McpInputSchema(string Type, Dictionary<string, McpSchemaProperty> Properties, string[] Required);

// Items was added when the work-items server declared the first `array`-typed properties in any of
// these MCP servers, and an array with no `items` is incomplete JSON Schema — a strict client can
// reject it, and a model has to guess the element type. Optional and trailing, so every existing
// `new("string", "…")` call is unchanged, and omitted from the wire entirely when null
// (DefaultIgnoreCondition = WhenWritingNull below).
record McpSchemaProperty(string Type, string Description, McpSchemaProperty? Items = null);

record McpToolCallResult(McpContentItem[] Content, bool? IsError = null);

record McpContentItem(string Type, string Text);

record McpError(int Code, string Message);

record SearchQuery(string Query);

record SessionSearchQuery(string Query, int? Limit = null);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
[JsonSerializable(typeof(McpInitResult))]
[JsonSerializable(typeof(McpToolsResult))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(SearchQuery))]
[JsonSerializable(typeof(SessionSearchQuery))]
// StartReviewFlowDto now also carries the optional reviewer-model override
// (`model` + `client_flow_protocol_version: 3`); the added member rides this existing registration,
// so the flows MCP v3 transport stays fully source-generated (AOT-safe, no reflection fallback).
[JsonSerializable(typeof(StartReviewFlowDto))]
[JsonSerializable(typeof(SubmitReviewRoundDto))]
[JsonSerializable(typeof(SubmitReviewerResultDto))]
[JsonSerializable(typeof(AckFlowMessagesDto))]
[JsonSerializable(typeof(SendFlowMessageDto))]
[JsonSerializable(typeof(ReviewerVendorsResult))]
partial class McpJsonContext : JsonSerializerContext;
