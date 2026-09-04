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

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

sealed class McpMemoryServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var repository = new CwdRepository(config, Directory.GetCurrentDirectory());
        var machineId  = await ResolveMachineIdAsync();
        var tools      = BuildToolsList();

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

        // The authenticated client and the cwd repository are resolved on the first tools/call,
        // not at startup: kcap-memory auto-registers, so Claude Code spawns `kcap mcp memory`
        // for every session — deferring keeps startup local-only (no GET /auth/config, token load,
        // repo detection, or stderr) for sessions that never invoke a tool. The client is created
        // on demand into a nullable field (rather than a Lazy<Task>) so a transient creation
        // failure leaves it null and the next call retries, instead of a faulted task sticking for
        // the rest of the session. Safe without locking: the stdio loop handles one request at a
        // time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, await repository.GetHashAsync(), machineId);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp memory: unexpected error handling tools/call: {ex}");
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
                McpTelemetry.ToolCalled("kcap-memory", tool, ok, CommandTiming.ElapsedMs(start));
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

    async Task<string?> ResolveMachineIdAsync() {
        try { return await MachineIdProvider.GetOrCreateAsync(config); } catch { return null; }
    }

    // Server-level usage preamble (MCP `instructions`) — steers clients to check for prior art here
    // before assuming none, and to save durable learnings.
    const string ServerInstructions =
        "Use these tools for durable team knowledge — preferences, feedback, project facts, references. " +
        "Search memories before assuming there's no prior art on a task, and before saving a new one — they " +
        "hold learnings that aren't in the code.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-memory", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash,
            string?    machineId
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "search_memories" => await client.GetAsync(BuildSearchUrl(baseUrl, arguments, cwdRepoHash, machineId)),
                "get_memory"      => await client.GetAsync(BuildGetUrl(baseUrl, arguments, cwdRepoHash, machineId)),
                "save_memory"     => await client.PostAsync($"{baseUrl}/api/memories", ToJsonContent(BuildSaveBody(arguments, cwdRepoHash, machineId))),
                "update_memory"   => await client.PutAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}", ToJsonContent(BuildUpdateBody(arguments))),
                "rescope_memory"  => await client.PostAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}/rescope", ToJsonContent(BuildRescopeBody(arguments))),
                "archive_memory"  => await client.DeleteAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}"),
                _                 => throw new ArgumentException($"Unknown tool: {toolName}")
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

    static string Id(JsonObject? args) =>
        args?["id"]?.GetValue<string>() is { Length: > 0 } id ? id : throw new ArgumentException("id is required");

    internal static string BuildSearchUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) {
        var q = args?["query"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(q)) throw new ArgumentException("query is required");

        var qs = new List<string> { $"q={Uri.EscapeDataString(q)}" };
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");
        if (TryReadInt(args, "limit", out var limit)) qs.Add($"limit={limit}");

        return $"{baseUrl}/api/memories/search?{string.Join("&", qs)}";
    }

    internal static string BuildGetUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) {
        var idOrSlug = args?["id_or_slug"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(idOrSlug)) throw new ArgumentException("id_or_slug is required");

        var qs = new List<string>();
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");

        var url = $"{baseUrl}/api/memories/{Uri.EscapeDataString(idOrSlug)}";
        return qs.Count == 0 ? url : $"{url}?{string.Join("&", qs)}";
    }

    // NOTE: request bodies use snake_case keys (repo_hash, machine_tag, source_session_id) —
    // the server's global JSON policy is JsonNamingPolicy.SnakeCaseLower.
    // Responses are passed through as raw text, so only these request-body builders are affected.
    internal static JsonObject BuildSaveBody(JsonObject? args, string? cwdRepoHash, string? machineId) {
        string Req(string name) =>
            args?[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new ArgumentException($"{name} is required");

        var global          = args?["global"]?.GetValue<bool>() == true;
        var machineSpecific = args?["machine_specific"]?.GetValue<bool>() == true;
        var project         = args?["project"]?.GetValue<string>();

        // A present-but-blank slug is malformed, not omitted: falling back to the repo or org home
        // would silently widen where the memory surfaces.
        if (project is not null && string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("project must be a project slug when supplied.");

        // Fail closed rather than silently broadening scope: the server reads repo_hash: null as an
        // org-wide memory, so a null cwdRepoHash needs an explicit home (project or global). Likewise a
        // null machineId with machine_specific: true would save an untagged (visible-to-everyone) memory.
        if (project is null && !global && cwdRepoHash is null)
            throw new ArgumentException("Cannot resolve the current repository — run from a git checkout, or pass project: \"<slug>\" for a project-scoped memory or global: true for a repo-independent one.");

        if (machineSpecific && machineId is null)
            throw new ArgumentException("Machine id unavailable — cannot save a machine-specific memory on this host.");

        return new JsonObject {
            ["audience"]          = Req("audience"),
            ["slug"]              = Req("slug"),
            ["description"]       = Req("description"),
            ["content"]           = Req("content"),
            ["kind"]              = Req("kind"),
            ["team"]              = args?["team"]?.GetValue<string>(),
            // audience 'project' target — the people axis, distinct from the place-axis 'project' below.
            ["audience_project"]  = args?["audience_project"]?.GetValue<string>(),
            // The place axis: a project home wins over the repo. The repo hash still rides along because a
            // server that does not know project homes ignores the field and reads a null hash as org-wide;
            // with the hash it lands the memory at the repo, narrower than asked rather than wider.
            ["project"]           = project,
            ["repo_hash"]         = global ? null : cwdRepoHash,
            ["machine_tag"]       = machineSpecific ? machineId : null,
            ["machine_context"]   = machineId,
            ["source_session_id"] = null,
            ["harness"]           = "mcp"
        };
    }

    internal static JsonObject BuildUpdateBody(JsonObject? args) => new() {
        ["description"] = args?["description"]?.GetValue<string>(),
        ["content"]     = args?["content"]?.GetValue<string>(),
        ["kind"]        = args?["kind"]?.GetValue<string>()
    };

    internal static JsonObject BuildRescopeBody(JsonObject? args) {
        var audience = args?["audience"]?.GetValue<string>();
        var project  = args?["project"]?.GetValue<string>();
        // Mirror the server: a project context move takes precedence and is audience-independent, so
        // audience is required ONLY when project is absent. Whitespace-only counts as absent — a blank
        // slug would never resolve server-side, so reject it locally instead of sending an invalid move.
        if (string.IsNullOrWhiteSpace(audience) && string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("audience or project is required");
        return new() {
            ["audience"] = string.IsNullOrWhiteSpace(audience) ? null : audience,
            ["team"]     = args?["team"]?.GetValue<string>(),
            ["project"]  = string.IsNullOrWhiteSpace(project) ? null : project,
            // audience 'project' target (people axis) — distinct from the place-axis 'project' above; raw
            // like team, not whitespace-nulled, since unlike project it has no precedence branch to guard.
            ["audience_project"] = args?["audience_project"]?.GetValue<string>(),
        };
    }

    /// <summary>
    /// Reads a numeric field as int, tolerant of JsonValue holding any underlying numeric type
    /// (int/long/double) — TryGetValue&lt;int&gt; on a JsonValue constructed from a long returns false.
    /// Returns false when the key is missing or the value is the wrong shape (e.g., a string).
    /// Throws <see cref="ArgumentException"/> when the value is numeric but out of range for int,
    /// so the caller (via <see cref="HandleToolCallAsync"/>) surfaces it as a JSON-RPC validation error
    /// rather than silently falling back to the default.
    /// </summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        JsonValue v;

        try {
            v = node.AsValue();
        } catch {
            return false; // wrong shape (object/array)
        }

        if (v.TryGetValue(out value)) return true;

        if (v.TryGetValue<long>(out var lv)) {
            if (lv is < int.MinValue or > int.MaxValue)
                throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

            value = (int)lv;

            return true;
        }

        if (v.TryGetValue<double>(out var dv)) {
            var rounded = (long)dv;

            if (rounded < int.MinValue || rounded > int.MaxValue || rounded != dv)
                throw new ArgumentException($"'{key}' value {dv} is out of range or non-integer for int.");

            value = (int)rounded;

            return true;
        }

        return false;
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
        new("search_memories",
            "Search the team's shared memories (hybrid semantic + keyword). Call this before saving a new memory, and before assuming there's no prior art on a task — it surfaces durable team learnings (preferences, feedback, project facts) that aren't in the code.",
            new("object", new() {
                ["query"] = new("string", "What to search for."),
                ["limit"] = new("number", "Max results (default 10, max 50).")
            }, ["query"])),
        new("get_memory",
            "Fetch a memory's full content by id or slug. Slug resolution precedence: your memories, then your teams', then org-wide; repo-scoped before global.",
            new("object", new() {
                ["id_or_slug"] = new("string", "Memory id (32 hex) or slug.")
            }, ["id_or_slug"])),
        new("save_memory",
            "Save a durable learning to the server. Two orthogonal axes. PEOPLE (audience — who can see + edit): 'user' (private), 'team', 'org' (everyone), or 'project' (that project's members — pass audience_project). PLACE (where it surfaces): the cwd's repo by default; a project with project: '<slug>' (surfaces across that project's repos — use it for learnings that span repos instead of saving them org-wide); or the whole org with global: true. project wins over the repo. If the current repo can't be resolved, pass project or global: true, or the save fails. Prefer update_memory when the result reports a nearDuplicate.",
            new("object", new() {
                ["audience"]         = new("string", "user | team | org | project"),
                ["slug"]             = new("string", "kebab-case identifier, unique within the audience+place pool"),
                ["description"]      = new("string", "One-line summary (max 300 chars)"),
                ["content"]          = new("string", "Full memory body (max 64 KiB)"),
                ["kind"]             = new("string", "preference | feedback | project | reference"),
                ["team"]             = new("string", "Team name or id — required for audience 'team' if you are in several teams"),
                ["audience_project"] = new("string", "Project slug — the PEOPLE axis: required for audience 'project'; that project's members become editors (you must be a member). Distinct from 'project' below"),
                ["project"]          = new("string", "Project slug — the PLACE axis: homes the memory at that project so it surfaces across the project's repos (wins over the cwd repo; no membership needed). Distinct from 'audience_project' above"),
                ["global"]           = new("boolean", "true = org-wide, not tied to the current repo (required if not run from a git checkout and no project is given; default: scoped to cwd repo)"),
                ["machine_specific"] = new("boolean", "true = only relevant on this machine (user audience only)")
            }, ["audience", "slug", "description", "content", "kind"])),
        new("update_memory",
            "Update an existing memory's description/content/kind (any subset).",
            new("object", new() {
                ["id"]          = new("string", "Memory id"),
                ["description"] = new("string", "New one-line summary"),
                ["content"]     = new("string", "New body"),
                ["kind"]        = new("string", "preference | feedback | project | reference")
            }, ["id"])),
        new("rescope_memory",
            "Change a memory's audience — who can see + edit it (promote your user memory to team, org, or a project's members) — or move its home context to a project (where it surfaces). Two orthogonal axes: audience_project sets the PEOPLE (audience 'project'); project sets the PLACE (context move — takes precedence and is independent of audience).",
            new("object", new() {
                ["id"]               = new("string", "Memory id"),
                ["audience"]         = new("string", "user | team | org | project"),
                ["team"]             = new("string", "Target team when audience is 'team'"),
                ["audience_project"] = new("string", "Target project slug when audience is 'project' — the PEOPLE axis (its members become editors; you must be a member). Distinct from 'project' below"),
                ["project"]          = new("string", "Target project slug — the PLACE axis: moves the memory's home context to that project (takes precedence over audience)")
            }, ["id"])),
        new("archive_memory",
            "Archive (soft-delete) a memory.",
            new("object", new() { ["id"] = new("string", "Memory id") }, ["id"]))
    ];
}
