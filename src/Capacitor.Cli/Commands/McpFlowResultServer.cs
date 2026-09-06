using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// reviewer/participant-side MCP server injected into hosted review-flow launches.
/// Exposes submit_review_result (POSTs to /api/flows/reviewer/result) and, as of E-c,
/// send_flow_message (POSTs to /api/flows/participant/message) — the only two tools a hosted
/// participant needs. Deliberately a SEPARATE command from `kcap mcp flows` — a hard security
/// boundary so no flag regression can ever expose start_review_flow to an unattended reviewer.
/// </summary>
sealed class McpFlowResultServer(
        ConfigRoot config, ProfileContext profiles, TokenStore store, ICapacitorHttpClient http) {
    internal const string AgentIdEnvVar = "KCAP_FLOW_AGENT_ID";

    /// <summary>Daemon-minted loopback capability a BORROWED reviewer delivers through: its sandbox
    /// redirects HOME, so this process has no token store to authenticate with. Mutually exclusive
    /// with KCAP_URL.</summary>
    internal const string CapabilityUrlEnvVar = "KCAP_FLOW_CAPABILITY_URL";

    /// <summary>Leaves appended to the capability BASE. Both tools ride one grant, so the daemon
    /// publishes the base rather than a leaf whose sibling would need string surgery to derive.</summary>
    const string CapabilitySubmitLeaf  = "/flow-result";
    const string CapabilityMessageLeaf = "/flow-message";

    const int MaxAttempts = 5;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    // E-0: the server no longer reads the transcript — markers deliver
    // nothing, so the only useful guidance on failure is to retry the tool itself.
    const string FallbackHint =
        "Retry this tool call — it is the ONLY delivery channel. Do NOT fall back to FINDINGS:/NO FINDINGS markers in your reply: the server does not read the transcript, so a marker delivers nothing.";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var agentId = Environment.GetEnvironmentVariable(AgentIdEnvVar);

        if (string.IsNullOrWhiteSpace(agentId)) {
            await Console.Error.WriteLineAsync(
                $"kcap mcp flow-result: {AgentIdEnvVar} is not set. This server is launched by the kcap daemon for hosted review-flow reviewers; it is not meant to be run manually.");
            return 2;
        }

        var tools = BuildToolsList();

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — re-initialise under the reportable pseudo-command
        // "mcp-server" so per-tool-call events actually leave. Best-effort: a stale token on
        // disk must never block the server from starting.
        var loggedIn = false;
        try { loggedIn = await store.LoadForProfileAsync(profiles.Name) is not null; } catch { }
        CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn, config);

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        // A borrowed reviewer delivers through the daemon capability and never authenticates; every
        // other launch keeps the token-store path. Mutually exclusive, decided once here.
        var capabilityBase = Environment.GetEnvironmentVariable(CapabilityUrlEnvVar)?.TrimEnd('/');
        var borrowed       = !string.IsNullOrWhiteSpace(capabilityBase);
        var urlOk = HttpClientExtensions.IsAcceptableUrl(borrowed ? capabilityBase! : baseUrl);

        // The authenticated client is created on the first tools/call, not at startup — mirrors
        // McpFlowsServer/McpReviewServer: keeps startup local-only (no GET /auth/config, token
        // load, or stderr re-auth hint) for a server that only ever handles one tool call.
        // Created on demand into a nullable field (rather than a Lazy<Task>) so a transient
        // creation failure leaves it null and the next call retries, instead of a faulted task
        // sticking for the rest of the session. Safe without locking: the stdio loop handles
        // one request at a time.
        HttpClient? client = null;
        var apiRoot = baseUrl.TrimEnd('/');

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                // Params extraction stays INSIDE the guard: a malformed tools/call (params
                // not an object, name not a string) must yield a JSON-RPC error, not throw
                // past the loop and kill the reviewer's only result-submission tool
                // (Qodo review on #240; matches McpFlowsServer/McpReviewServer structure).
                JsonObject? paramsNode;
                string?     toolName;
                JsonObject? arguments;

                try {
                    paramsNode = callRequest["params"]?.AsObject();
                    toolName   = paramsNode?["name"]?.GetValue<string>();
                    arguments  = paramsNode?["arguments"]?.AsObject();
                } catch (InvalidOperationException) {
                    return BuildErrorResponse(callId, -32602, "Invalid params");
                } catch (FormatException) {
                    return BuildErrorResponse(callId, -32602, "Invalid params");
                }

                if (toolName is null)
                    return BuildErrorResponse(callId, -32602, "Missing params.name");

                // Deliberately EXHAUSTIVE explicit cases (no catalog-driven dispatch): a future tool
                // added to KcapMcpRegistry.ReservedResultChannelTools without a case here hits the
                // unknown-tool default rather than being routed to some existing handler. The
                // contract test against the catalog keeps tools/list honest; this gate keeps
                // dispatch honest. Checked BEFORE creating the authenticated client so an unknown
                // name never triggers auth setup.
                if (toolName is not ("submit_review_result" or "send_flow_message"))
                    return BuildToolResult(callId, $"Error: Unknown tool: {toolName}", isError: true);

                // The borrowed path takes a lane that cannot authenticate rather than one that
                // declines to: its token store lives under a HOME this process cannot reach, and
                // reaching for one is what produced the original silent failure.
                client ??= borrowed
                    ? http.Loopback()
                    : await http.ForSessionAsync();

                var (text, isError) = toolName switch {
                    "submit_review_result" => await SubmitCoreAsync(
                        client, apiRoot, agentId, arguments, delay: Task.Delay,
                        submitUrlOverride: borrowed ? capabilityBase + CapabilitySubmitLeaf : null),
                    "send_flow_message"    => await SendMessageCoreAsync(
                        client, apiRoot, agentId, arguments, delay: Task.Delay,
                        messageUrlOverride: borrowed ? capabilityBase + CapabilityMessageLeaf : null),
                    _                      => ($"Error: Unknown tool: {toolName}", true)
                };

                return BuildToolResult(callId, text, isError);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp flow-result: unexpected error handling tools/call: {ex}");
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
                McpTelemetry.ToolCalled("kcap-flow-result", tool, ok, CommandTiming.ElapsedMs(start));
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
                    continue;
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

    /// <summary>Validation + POST + retry policy. Injectable delay so tests run instantly.
    /// Returns the tool text and error flag; never throws for expected failures.</summary>
    internal async Task<(string Text, bool IsError)> SubmitCoreAsync(
            HttpClient           client,
            string               apiRoot,
            string               agentId,
            JsonObject?          arguments,
            Func<TimeSpan, Task> delay,
            // Absolute delivery URL for a borrowed reviewer; REPLACES the apiRoot-composed path,
            // since the capability is a daemon loopback endpoint and not a kcap API root.
            string?              submitUrlOverride = null
        ) {
        var roundToken = arguments?["round_token"]?.GetValue<string>();
        var kind       = arguments?["kind"]?.GetValue<string>();
        var findings   = arguments?["findings"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(roundToken))
            return ("Error: round_token is required — copy it from the \"round token\" in your prompt.", true);
        if (kind is not ("findings" or "clean"))
            return ("Error: kind must be \"findings\" or \"clean\".", true);
        if (kind == "findings" && string.IsNullOrWhiteSpace(findings))
            return ("Error: findings text is required when kind is \"findings\".", true);

        var body = new SubmitReviewerResultDto(agentId, roundToken, kind, kind == "findings" ? findings : null);
        var url  = submitUrlOverride ?? $"{apiRoot.TrimEnd('/')}/api/flows/reviewer/result";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            using var response = await client.PostAsync(
                url, JsonContent.Create(body, McpJsonContext.Default.SubmitReviewerResultDto));
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
                return ("Result recorded. You may end your reply now.", false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), true);

            var errorNode = TryParse(responseBody);
            var code      = errorNode?["error"]?.GetValue<string>();
            var message   = errorNode?["message"]?.GetValue<string>() ?? responseBody;

            if (code is "no_active_flow" or "no_open_round") {
                // Launch race: the server's flow-assignment/round events may not be projected
                // yet when a fast reviewer submits. Retry inside the tool call.
                if (attempt < MaxAttempts) {
                    await delay(RetryDelay);
                    continue;
                }
                return ($"Error: {message}\n{FallbackHint}", true);
            }

            if (code == "stale_round_token")
                // Deliberately NO retry hint: a stale round token means this round is already
                // closed — the result must be discarded, never redelivered (spec-review round 2
                // finding; the round-token guard).
                return ($"Error: {message}", true);

            if (code == "server_catching_up")
                // Retrying inside this tool call cannot outlast a read-model rebuild; surface the
                // guidance and let the agent decide.
                return ($"Error: {message}\n{McpFlowsServer.ServerCatchingUpGuidance}", true);

            return ($"Error: HTTP {(int)response.StatusCode} — {message}\n{FallbackHint}", true);
        }

        return ("Error: unreachable", true); // loop always returns

        static JsonObject? TryParse(string s) {
            try { return JsonNode.Parse(s)?.AsObject(); } catch { return null; }
        }
    }

    /// <summary> E-c: validation + POST + retry policy for the participant's out-of-band
    /// note to the driver. Mirrors SubmitCoreAsync's structure. <paramref name="messageId"/> is
    /// generated ONCE per tool call (by the caller's default) and reused across every retry
    /// attempt below, so the server can dedupe a redelivered POST instead of recording the same
    /// note twice. Injectable so tests can pin a stable id. Never throws for expected failures.
    /// </summary>
    internal async Task<(string Text, bool IsError)> SendMessageCoreAsync(
            HttpClient           client,
            string               apiRoot,
            string               agentId,
            JsonObject?          arguments,
            Func<TimeSpan, Task> delay,
            string?              messageId = null,
            // Same contract as SubmitCoreAsync's submitUrlOverride.
            string?              messageUrlOverride = null
        ) {
        // Type-safe extraction: a non-string `text` (number/object/array) must yield this clean
        // validation error, not throw into the dispatch guard's generic "internal error"
        // (Qodo review on #278; same JsonValue.TryGetValue pattern as ParseAsyncArg).
        if (arguments?["text"] is not JsonValue textValue ||
            !textValue.TryGetValue<string>(out var text) ||
            string.IsNullOrWhiteSpace(text))
            return ("Error: text must be a non-empty string.", true);

        var body = new SendFlowMessageDto(agentId, messageId ?? Guid.NewGuid().ToString("N"), text);
        var url  = messageUrlOverride ?? $"{apiRoot.TrimEnd('/')}/api/flows/participant/message";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            using var response = await client.PostAsync(
                url, JsonContent.Create(body, McpJsonContext.Default.SendFlowMessageDto));

            if (response.IsSuccessStatusCode)
                return ("Message sent to the flow driver. It will be delivered with the driver's next flow call — you may continue.", false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), true);

            var responseBody = await response.Content.ReadAsStringAsync();
            var errorNode    = TryParse(responseBody);
            var code         = errorNode?["error"]?.GetValue<string>();
            var message      = errorNode?["message"]?.GetValue<string>() ?? responseBody;

            if (code is "no_active_flow" or "concurrent_update") {
                // Launch race or a concurrent writer on the same flow's fold — retry with the
                // SAME message_id so a redelivered POST dedupes instead of double-recording.
                if (attempt < MaxAttempts) {
                    await delay(RetryDelay);
                    continue;
                }
                return ($"Error: {message}", true);
            }

            if (code == "run_closed")
                // Terminal: the flow is already closed, so there is no driver left to deliver
                // this message to. No retry — unlike no_active_flow, more attempts can't help.
                return ($"Error: {message}", true);

            if (code == "server_catching_up")
                // Retrying inside this tool call cannot outlast a read-model rebuild; surface the
                // guidance and let the agent decide.
                return ($"Error: {message}\n{McpFlowsServer.ServerCatchingUpGuidance}", true);

            return ($"Error: HTTP {(int)response.StatusCode} — {message}", true);
        }

        return ("Error: unreachable", true); // loop always returns

        static JsonObject? TryParse(string s) {
            try { return JsonNode.Parse(s)?.AsObject(); } catch { return null; }
        }
    }

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-flow-result", "1.0.0")),
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

    /// <summary>The advertised tool list. Contract-tested to match
    /// <c>KcapMcpRegistry.ReservedResultChannelTools</c> name-for-name, in order — internal (not
    /// private) solely so that test can compare it directly against the catalog.</summary>
    internal static McpTool[] BuildToolsList() => [
        new(
            Name: "submit_review_result",
            Description: "Submit your review result for the current round. Call once. kind=\"findings\" with your findings text, or kind=\"clean\" when there are no actionable findings. round_token comes from the \"round token\" line in your prompt.",
            InputSchema: new McpInputSchema(
                Type: "object",
                Properties: new Dictionary<string, McpSchemaProperty> {
                    ["round_token"] = new("string", "The round token from your prompt (correlates this result to the round)."),
                    ["kind"]        = new("string", "\"findings\" or \"clean\"."),
                    ["findings"]    = new("string", "Your findings text; required when kind is \"findings\".")
                },
                Required: ["round_token", "kind"]
            )
        ),
        new(
            Name: "send_flow_message",
            Description: "Send a short out-of-band note to the flow DRIVER between rounds — e.g. a notable observation, a blocking question, or a heads-up about something outside the current round's scope. NOT for round results: deliver those with submit_review_result. The driver sees pending messages on its next flow call; delivery is not immediate.",
            InputSchema: new McpInputSchema(
                Type: "object",
                Properties: new Dictionary<string, McpSchemaProperty> {
                    ["text"] = new("string", "The message text for the driver.")
                },
                Required: ["text"]
            )
        )
    ];
}

/// <summary>CLI-side DTO for POST /api/flows/reviewer/result — mirrors the server's SubmitReviewerResultRequest.</summary>
record SubmitReviewerResultDto(
    [property: JsonPropertyName("agent_id")]    string  AgentId,
    [property: JsonPropertyName("round_token")] string  RoundToken,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("text")]        string? Text
);

/// <summary>CLI-side DTO for POST /api/flows/participant/message — mirrors the server's request shape (E-c).</summary>
record SendFlowMessageDto(
    [property: JsonPropertyName("agent_id")]   string AgentId,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("text")]       string Text
);
