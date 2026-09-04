using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary> P2 task 17: MCP tools for the work-items correlation surface — attach the
/// current session (and its continuation chain) to a work item, and list what a session is
/// already attached to. Cloned from <see cref="McpMemoryServer"/>'s stdio JSON-RPC loop; unlike
/// memory this server has no repo/machine context to resolve — the only per-call input is the
/// session id and the declare selector, both carried in the tool arguments.</summary>
sealed class McpWorkItemsServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    internal const string NoSessionIdMessage =
        "No session id: pass session_id explicitly or run inside a kcap-hooked session (KCAP_SESSION_ID or CODEX_THREAD_ID).";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

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

        // Created on demand (not at startup) so a session that never calls a tool pays no
        // network/token/stderr cost. Nullable field rather than Lazy<Task> so a transient
        // creation failure leaves it null and the next call retries. Safe without locking:
        // the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp workitems: unexpected error handling tools/call: {ex}");
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
                McpTelemetry.ToolCalled("kcap-workitems", tool, ok, CommandTiming.ElapsedMs(start));
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
                var method = DecodeMethod(request);

                // Notifications have no id — don't send a response
                if (id is null) continue;

                var response = method switch {
                    null         => BuildErrorResponse(id, -32600, "Invalid request: method must be a string"),
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

    // Server-level usage preamble (MCP `instructions`) — steers agents to DECLARE a work item's
    // structure (breakdown + relations, which the server never infers), not just attach to it.
    internal const string ServerInstructions =
        "Use these tools to attach the current session to its SDLC work item AND to declare that work " +
        "item's structure. When you plan or discover that a work item breaks into parts, create the part " +
        "items (declare_work_item with new_title) and declare the parent→parts breakdown " +
        "(declare_work_breakdown); when one item must land before another, declare the dependency " +
        "(declare_work_relation — 'blocks'/'blocked_by'). Breakdown and relations are DECLARED, never " +
        "inferred: if you don't declare them the work item's topology stays empty. Declare only real " +
        "structure you're confident of — every item must be visible to you, but parts and dependencies " +
        "may cross repositories — and use the retract_* tools when it changes.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-workitems", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "declare_work_item"      => await client.PostAsync($"{baseUrl}/api/work-items/declare", ToJsonContent(BuildDeclareBody(arguments))),
                "get_session_work_items" => await client.GetAsync(BuildSessionUrl(baseUrl, arguments)),

                // The declared breakdown/relation surface. Every id is a
                // REQUIRED argument here, unlike session_id: there is no ambient "current work item"
                // to fall back to, and guessing one would attach the wrong graph edge.
                "declare_work_breakdown" => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "parent_id", "breakdown"), ToJsonContent(BuildBreakdownBody(arguments))),
                "retract_work_breakdown" => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "parent_id", "breakdown/retract"), ToJsonContent(BuildBreakdownBody(arguments))),
                "declare_work_relation"  => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "from_id", "relations"), ToJsonContent(BuildRelationBody(arguments))),
                "retract_work_relation"  => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "from_id", "relations/retract"), ToJsonContent(BuildRelationBody(arguments))),
                "get_work_item_topology" => await client.GetAsync(
                    ItemUrl(baseUrl, arguments, "work_item_id", "topology")),

                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl), isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>
    /// Resolves the session id to act on: an explicit <c>session_id</c> tool argument wins,
    /// else the ambient <c>KCAP_SESSION_ID</c> (or <c>CODEX_THREAD_ID</c>) env var via
    /// <see cref="ArgParsing.ResolveSessionIdFromEnv()"/>. Throws when neither is available, so
    /// the caller (via <see cref="HandleToolCallAsync"/>) surfaces a clean tool error instead
    /// of sending a request with a missing/blank session id. Dashes are stripped from the
    /// explicit argument too — matching <see cref="ArgParsing.ResolveSessionIdFromEnv()"/> — so a
    /// caller passing a dashed GUID (e.g. copy-pasted from a UI) still resolves to the same
    /// dashless key the server expects, instead of silently missing the intended session.
    /// </summary>
    internal static string ResolveSessionId(JsonObject? args) {
        if (args?["session_id"]?.GetValue<string>() is { Length: > 0 } explicitId)
            return WorkContextIds.CanonicalSessionId(explicitId) ?? throw new ArgumentException(NoSessionIdMessage);
        if (ArgParsing.ResolveSessionIdFromEnv() is { Length: > 0 } fromEnv) return fromEnv;

        throw new ArgumentException(NoSessionIdMessage);
    }

    // NOTE: request bodies use snake_case keys — the server's global JSON policy is
    // JsonNamingPolicy.SnakeCaseLower. Responses are passed through as raw
    // text, so only this request-body builder is affected. The server enforces "exactly one of
    // issue_key/pr_number/work_item_id/new_title" (400 on violation) — this builder passes
    // through whichever selector(s) were supplied and lets that validation surface as a tool
    // error via the 4xx-body mapping in HandleToolCallAsync, rather than duplicating the rule
    // client-side.
    internal static JsonObject BuildDeclareBody(JsonObject? args) {
        var body = new JsonObject { ["session_id"] = ResolveSessionId(args) };

        if (args?["issue_key"]?.GetValue<string>() is { Length: > 0 } issueKey) body["issue_key"] = issueKey;
        if (args?["work_item_id"]?.GetValue<string>() is { Length: > 0 } workItemId) body["work_item_id"] = workItemId;
        if (args?["new_title"]?.GetValue<string>() is { Length: > 0 } newTitle) body["new_title"] = newTitle;
        if (TryReadInt(args, "pr_number", out var prNumber)) body["pr_number"] = prNumber;

        return body;
    }

    internal static string BuildSessionUrl(string baseUrl, JsonObject? args) =>
        $"{baseUrl}/api/work-items/session/{Uri.EscapeDataString(ResolveSessionId(args))}";

    /// <summary>
    /// Builds a work-item-scoped URL, reading a REQUIRED id from <paramref name="idKey"/>.
    /// Required with no fallback, deliberately: <see cref="ResolveSessionId"/> can default to the
    /// ambient session because "the session I am running in" is unambiguous, whereas there is no
    /// ambient work item — a default here would silently attach the wrong edge of the graph.
    /// Escaped, so an id containing a slash or a percent cannot walk out of its path segment and hit
    /// a different route.
    /// </summary>
    internal static string ItemUrl(string baseUrl, JsonObject? args, string idKey, string suffix) {
        // Validation before escaping, and the dot-segment refusal, are WorkContextIds' — escaping
        // alone leaves "." and ".." to walk out of the route.
        var id = WorkContextIds.ValidWorkItemId(RequireString(args, idKey))
              ?? throw new ArgumentException($"'{idKey}' is not a valid work item id.");

        return $"{baseUrl}/api/work-items/{Uri.EscapeDataString(id)}/{suffix}";
    }

    /// <summary>Reads a required non-blank string argument, throwing the clean tool-error shape when
    /// it is absent, null, blank, or the wrong JSON type. A whitespace-only id is rejected here
    /// rather than escaped into a URL that would 404 for an unrelated-looking reason.</summary>
    internal static string RequireString(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) throw new ArgumentException($"'{key}' is required.");

        // Shape-tested rather than try/catch (review finding): a bare catch would report an
        // unrelated failure as a type error.
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value))
            throw new ArgumentException($"'{key}' must be a string.");

        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{key}' must not be blank.");

        return value;
    }

    // Server-side validation is NOT duplicated here — same reasoning as BuildDeclareBody's note. The
    // rules the server owns (cross-repo edges, unknown/deleted ids, a parent listed among its own
    // parts, self-relations, an empty parts list, the relation_kind vocabulary) all surface as coded
    // 4xx bodies through HandleToolCallAsync. What IS validated locally is SHAPE: a present-but-
    // wrong-typed argument must fail loudly rather than be dropped, because a silently omitted
    // part_ids turns a malformed declare into a differently-shaped request whose rejection reads as
    // if the caller had sent nothing.
    internal static JsonObject BuildBreakdownBody(JsonObject? args) {
        var body = new JsonObject();

        // Presence, not truthiness (review finding): `{"part_ids": null}` is a PRESENT wrong shape,
        // and the `is { } node` form treated it as absence — silently omitting it and turning a
        // malformed declare into a differently-shaped request. Explicit null now fails like any other
        // wrong type.
        if (args is not null && args.TryGetPropertyValue("part_ids", out var node)) {
            if (node is null) throw new ArgumentException("'part_ids' must be an array of strings, not null.");

            body["part_ids"] = ReadStringArray(node, "part_ids");
        }

        return body;
    }

    internal static JsonObject BuildRelationBody(JsonObject? args) {
        var body = new JsonObject();

        // to_id and relation_kind are left to the server to require and to interpret: it owns the
        // vocabulary and the structural rules, and a coded 400 naming the real reason beats a guess
        // made here. Every SUPPLIED string is forwarded verbatim, including "" (review finding): the
        // previous `is { Length: > 0 }` form dropped an explicit empty string, so the caller got the
        // server's "required" error instead of its more useful "invalid value" one. Absence stays
        // absence; a present non-string still fails locally, as shape validation should.
        CopySuppliedString(args, "to_id", body);
        CopySuppliedString(args, "relation_kind", body);

        return body;
    }

    /// <summary>Copies a string argument into the request body if the caller SUPPLIED the key at
    /// all. An empty string is a supplied value and is forwarded; an explicit null or a non-string is
    /// a wrong shape and throws; an absent key is left absent so the server's own "required" error
    /// surfaces rather than a local guess.</summary>
    static void CopySuppliedString(JsonObject? args, string key, JsonObject body) {
        if (args is null || !args.TryGetPropertyValue(key, out var node)) return;

        if (node is null) throw new ArgumentException($"'{key}' must be a string, not null.");

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ArgumentException($"'{key}' must be a string.");

        body[key] = text;
    }

    /// <summary>Reads a JSON array of non-blank strings. Any other present shape — a bare string, an
    /// object, an array holding a number or a blank — throws, so a malformed argument surfaces as a
    /// validation error instead of being partially dropped.</summary>
    internal static JsonArray ReadStringArray(JsonNode node, string key) {
        if (node is not JsonArray array) throw new ArgumentException($"'{key}' must be an array of strings.");

        var result = new JsonArray();

        foreach (var element in array) {
            if (element is not JsonValue elementValue || !elementValue.TryGetValue<string>(out var value))
                throw new ArgumentException($"'{key}' must contain only strings.");

            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{key}' must not contain blank entries.");

            // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
            // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see CLAUDE.md).
            result.Add((JsonNode?)JsonValue.Create(value));
        }

        return result;
    }

    /// <summary>Decodes the JSON-RPC <c>method</c> field, returning null for a present but
    /// wrong-shaped value (e.g. an object) instead of throwing — a malformed request must yield
    /// an invalid-request response, never terminate the stdio loop.</summary>
    internal static string? DecodeMethod(JsonObject request) {
        try {
            return request["method"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Reads a numeric field as int. Returns false ONLY when the key is absent (or JSON null) —
    /// any PRESENT non-integer shape (string, object, array, fractional or out-of-range number)
    /// throws <see cref="ArgumentException"/> so the caller surfaces a validation error instead
    /// of silently dropping the selector: a malformed two-selector declare (e.g. issue_key plus
    /// a string pr_number) must fail, not degrade into a "valid" single-selector attach. Wire
    /// JSON (JsonElement-backed) is validated against the RAW token via TryGetInt32 — exact, no
    /// lossy double round-trip, so a fractional part below double precision still rejects;
    /// int/long branches cover programmatically constructed nodes.
    /// </summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        if (node is JsonValue v) {
            if (v.TryGetValue<JsonElement>(out var el)) {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;

                throw new ArgumentException($"'{key}' must be an integer within int range.");
            }

            if (v.TryGetValue(out value)) return true;

            if (v.TryGetValue<long>(out var lv)) {
                if (lv is < int.MinValue or > int.MaxValue)
                    throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

                value = (int)lv;

                return true;
            }
        }

        throw new ArgumentException($"'{key}' must be an integer.");
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

    internal static McpTool[] BuildToolsList() => [
        new("declare_work_item",
            "Attach the CURRENT session (and its continuation chain) to a work item on the Capacitor server. Provide exactly one of issue_key, pr_number, work_item_id, or new_title.",
            new("object", new() {
                ["issue_key"]    = new("string", "Attach to the work item for this issue key (e.g. 'PROJ-1234'), creating it if none exists yet."),
                ["pr_number"]    = new("integer", "Attach to the work item for this PR number, creating it if none exists yet."),
                ["work_item_id"] = new("string", "Attach directly to this work item id."),
                ["new_title"]    = new("string", "Create a brand-new work item with this title and attach to it."),
                ["session_id"]   = new("string", "Session id to attach. Defaults to the current kcap-hooked session (KCAP_SESSION_ID) when omitted.")
            }, [])),
        new("get_session_work_items",
            "List the work items the current session is attached to.",
            new("object", new() {
                ["session_id"] = new("string", "Session id to look up. Defaults to the current kcap-hooked session (KCAP_SESSION_ID) when omitted.")
            }, [])),

        // The declared work-breakdown / relation surface. NOTE: no tool
        // here accepts `source` or `declared_by`. The server resolves both from the authenticated
        // caller and rejects a `source` of "user" outright, so exposing either would be an argument
        // the server ignores at best and a spoofing surface at worst.
        new("declare_work_breakdown",
            "Declare that a work item is broken down into parts (sub-items). Idempotent: re-declaring an "
          + "existing part is accepted and reported as existing rather than created. A part can have at "
          + "most one parent, and every item must be visible to you — a part may live in a different "
          + "repository than its parent, where repository is display only.",
            new("object", new() {
                ["parent_id"] = new("string", "The work item being broken down."),
                ["part_ids"]  = new("array", "Work item ids that are parts of the parent.", new("string", "A work item id."))
            }, ["parent_id", "part_ids"])),

        new("retract_work_breakdown",
            "Retract a previously declared breakdown, detaching the named parts from the parent.",
            new("object", new() {
                ["parent_id"] = new("string", "The work item whose breakdown is being retracted."),
                ["part_ids"]  = new("array", "Work item ids to detach from the parent.", new("string", "A work item id."))
            }, ["parent_id", "part_ids"])),

        new("declare_work_relation",
            "Declare a dependency between two work items: 'blocks' means from_id blocks to_id, "
          + "'blocked_by' means from_id is blocked by to_id. Both items must be visible to you and may "
          + "live in different repositories; an item cannot relate to itself.",
            new("object", new() {
                ["from_id"]       = new("string", "The work item the relation starts from."),
                ["to_id"]         = new("string", "The work item on the other end of the relation."),
                ["relation_kind"] = new("string", "Either 'blocks' or 'blocked_by'.")
            }, ["from_id", "to_id", "relation_kind"])),

        new("retract_work_relation",
            "Retract a previously declared dependency between two work items.",
            new("object", new() {
                ["from_id"]       = new("string", "The work item the relation starts from."),
                ["to_id"]         = new("string", "The work item on the other end of the relation."),
                ["relation_kind"] = new("string", "Either 'blocks' or 'blocked_by'.")
            }, ["from_id", "to_id", "relation_kind"])),

        new("get_work_item_topology",
            "Read a work item's declared breakdown and relations — its parent, parts, and dependencies. "
          + "Scoped to what the caller can see, so items you have no access to are absent rather than hidden "
          + "placeholders.",
            new("object", new() {
                ["work_item_id"] = new("string", "The work item whose topology to read.")
            }, ["work_item_id"]))
    ];
}
