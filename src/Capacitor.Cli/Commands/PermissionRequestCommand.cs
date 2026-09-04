using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Claude;

// ReSharper disable MethodHasAsyncOverload

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class PermissionRequestCommand(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    string Url => profiles.Resolution.ServerUrl!;

    public Task<int> Handle() => Handle(body: null);

    /// <param name="selfHealWatcher">False when the caller determined the session is excluded
    /// (excluded_repos/excluded_paths). It gates two things: the transcript-uploading watcher, which
    /// must not start for a project the user opted out of recording, and the approval policy —
    /// an excluded session's decisions cannot be recorded, and the audit contract is that every
    /// engine decision is, so it is ungoverned at this seam exactly as it is at PreToolUse. The
    /// permission record/long-poll itself still runs: hosted agents need the decision regardless.</param>
    public async Task<int> Handle(string? body, bool selfHealWatcher = true, TextWriter? stdout = null) {
        body ??= await Console.In.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            Console.Error.WriteLine("[kcap] Failed to parse permission-request input");

            return 0;
        }

        if (node is null)
            return 0;

        var sessionId = node["session_id"]?.GetValue<string>()?.Replace("-", "");

        if (sessionId is null) {
            Console.Error.WriteLine("[kcap] No session_id in permission-request");

            return 0;
        }

        // Self-heal the transcript watcher (see TryEnsureWatcher). Done before the
        // rendered/non-rendered split so both the interactive path and the daemon-hosted
        // long-poll recover a dead or never-started watcher; idempotent, so a no-op when
        // one is already running. Skipped for excluded sessions (the caller passes
        // selfHealWatcher: false) so exclusions are honored just like at session-start.
        if (selfHealWatcher) {
            await TryEnsureWatcher(sessionId, node);
        }

        var isRenderedAgent = Environment.GetEnvironmentVariable("KCAP_RENDERED_AGENT") is "1";

        // A rendered session's prompt is the daemon bridge's to decide — one full evaluation per
        // raised prompt, off the same files — so evaluating it here too would decide it twice with
        // no journal shared across the two processes. An excluded session is ungoverned entirely
        // (see selfHealWatcher).
        if (selfHealWatcher && !isRenderedAgent
            && await new ClaudePolicySeam(config).HandlePermissionRequestAsync(node, sessionId, stdout ?? Console.Out)
                == SeamAnswer.Answered) {
            return 0;
        }

        if (isRenderedAgent) {
            return await HandleRenderedAgent(node, sessionId, stdout);
        }

        // Non-rendered agent: record the permission event and return immediately
        return await HandleRecordOnly(node, sessionId);
    }

    async Task<int> HandleRenderedAgent(JsonNode node, string sessionId, TextWriter? stdout = null) {
        var toolName    = node["tool_name"]?.GetValue<string>() ?? "Unknown";
        var toolInput   = node["tool_input"];
        var suggestions = node["permission_suggestions"];

        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = toolName,
            ["tool_input"]             = toolInput?.DeepClone(),
            ["permission_suggestions"] = suggestions?.DeepClone()
        };

        // Prefer the daemon's local SignalR bridge when present — that path runs the
        // long-poll over the daemon's persistent SignalR connection, bypassing
        // Cloudflare's HTTP-request timeout (~120s) that severs the equivalent route
        // on the server. Older daemon builds don't set this env var, so we fall back
        // to the original /hooks/permission-request HTTPS path.
        //
        // Validate the daemon URL is loopback before posting — the env var carries no
        // auth, so an accidentally / maliciously set non-loopback value would leak the
        // hook payload (tool name, raw tool input) to an arbitrary endpoint.
        if (TryGetLoopbackDaemonUrl(out var daemonUrl)) {
            var       bridgePayload = BuildBridgePayload(node, sessionId, HookAgentId.FromEnvironment());
            using var bridge        = http.Loopback();

            return await PostAsync(bridge, daemonUrl + "/claude/permission-request", bridgePayload, stdout);
        }

        // The hook verb, so a lapsed credential is a value rather than a line: Claude reads this
        // hook's stderr as its result. Sent regardless of that value — the agent is blocked on a
        // decision, and the lane's recovery still gets its attempt at rotating the bearer.
        var       attempt      = await http.ForHookAsync();
        using var authenticated = attempt.Client;

        return await PostAsync(authenticated, Url + "/hooks/permission-request", payload, stdout);
    }

    /// <summary>
    /// Self-heals the transcript watcher from a permission-request payload. Permission
    /// requests fire frequently mid-session, so this is a cheap recovery point when the
    /// watcher died or never started — an abruptly-killed agent orphans its watcher,
    /// leaving the session stuck "active" because session-end is never POSTed.
    /// Idempotent: <see cref="WatcherManager.EnsureWatcherRunning"/> is a no-op when the
    /// watcher is already alive. Scoped to the MAIN session — a present <c>agent_id</c>
    /// marks a subagent tool call, whose watcher uses a distinct key + transcript and is
    /// ensured at subagent-start. Best-effort: never throws into the hook path.
    /// </summary>
    internal async Task TryEnsureWatcher(string sessionId, JsonNode node) {
        try {
            if (GetString(node, "agent_id") is { Length: > 0 }) {
                return;
            }

            var transcriptPath = GetString(node, "transcript_path");

            if (string.IsNullOrEmpty(transcriptPath)) {
                return;
            }

            await _watchers.EnsureWatcherRunning(sessionId, transcriptPath, agentId: null, cwd: GetString(node, "cwd"));
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[kcap] permission-request watcher self-heal failed: {ex.Message}");
        }
    }

    // Reads an optional string field, treating null / missing / non-string uniformly as
    // absent — a frequently-firing hook should not throw + log on a variant payload.
    static string? GetString(JsonNode node, string field) =>
        node[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool TryGetLoopbackDaemonUrl(out string daemonUrl) {
        daemonUrl = "";
        var raw = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");
        if (string.IsNullOrEmpty(raw)) return false;

        if (DaemonBridgeUrl.TryParseLoopback(raw, out daemonUrl)) {
            return true;
        }

        Console.Error.WriteLine($"[kcap] Ignoring non-loopback KCAP_DAEMON_URL: {raw}");
        return false;
    }

    /// The server payload plus what the daemon's attribution ladder reads: agent_id when this
    /// process runs inside a hosted agent, and the hook's cwd. The server-bound payload never
    /// carries either.
    internal static JsonObject BuildBridgePayload(JsonNode node, string sessionId, string? agentId) {
        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = node["tool_name"]?.GetValue<string>() ?? "Unknown",
            ["tool_input"]             = node["tool_input"]?.DeepClone(),
            ["permission_suggestions"] = node["permission_suggestions"]?.DeepClone(),
        };
        if (agentId is not null) payload["agent_id"] = agentId;
        if (node["cwd"] is JsonValue cwd && cwd.TryGetValue<string>(out var c)) payload["cwd"] = c;
        return payload;
    }

    static async Task<int> PostAsync(HttpClient client, string url, JsonObject payload, TextWriter? stdout = null) {
        // The server-side long-poll waits up to 10 hours for the user to decide; the
        // daemon-side bridge inherits that bound transparently. Add a minute of slack
        // so the HTTP client doesn't tear down a still-pending request first.
        client.Timeout = TimeSpan.FromHours(10) + TimeSpan.FromMinutes(1);

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            using var response = await client.PostAsync(url, content);

            if (!response.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kcap] permission-request failed: HTTP {(int)response.StatusCode}");

                return 2;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            (stdout ?? Console.Out).Write(responseBody);

            return 0;
        } catch (TaskCanceledException) {
            Console.Error.WriteLine("[kcap] permission-request timed out");

            return 2;
        } catch (HttpRequestException ex) {
            Console.Error.WriteLine($"[kcap] permission-request error: {ex.Message}");

            return 2;
        }
    }

    async Task<int> HandleRecordOnly(JsonNode node, string sessionId) {
        var toolName  = node["tool_name"]?.GetValue<string>() ?? "Unknown";
        var toolInput = node["tool_input"];

        var       attempt = await http.ForHookAsync();
        using var client  = attempt.Client;

        // Recording is best-effort, and without a credential the POST can only 401. Skipping returns
        // the two-second budget to the approval prompt this hook is holding up.
        if (!attempt.Usable) return 0;

        client.Timeout = TimeSpan.FromSeconds(2);

        var payload = new JsonObject {
            ["session_id"] = sessionId,
            ["tool_name"]  = toolName,
            ["tool_input"] = toolInput?.DeepClone()
        };

        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            using var response = await client.PostAsync($"{Url}/hooks/permission-record", content);
        } catch {
            // Silently ignore — don't block Claude Code for recording failures
        }

        return 0;
    }
}
