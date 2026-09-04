using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Tests.Unit.Commands.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

/// <summary>
/// §7 — the manual, recorded release-gate certification that the shared SessionStart
/// memory index actually reaches the model for Cursor's <c>cursor-agent</c> CLI. Everything else
/// in this feature (routing, budget math, lifecycle, output bytes) is proven by the unit suite in
/// <see cref="CursorHookCommandTests"/> against fakes; THIS file is the one place that asserts the
/// real end-to-end claim — a fresh, high-entropy nonce placed only in a saved memory reaches the
/// model's own answer via <c>additional_context</c> — because that claim can't be certified any
/// other way.
///
/// <b>Gated</b> behind <see cref="LiveGateEnvVar"/> (unset by default) so CI, and any ordinary
/// local test run, never executes this: it spends a real <c>cursor-agent</c> turn against a real,
/// reachable kcap server. Requires:
///   • <c>cursor-agent</c> on PATH (Cursor CLI installed, logged in — <c>cursor-agent status</c>).
///   • <see cref="ServerUrlEnvVar"/> pointing at a reachable kcap server.
///   • <c>kcap login</c> already done against that server (this process's own token store is
///     reused — see <see cref="ICapacitorHttpClient.ForCommandAsync"/>) so a memory can be saved
///     and later read back via <c>GET /api/memories/index</c>.
///
/// This is a MANUAL release-gate run by a human/controller before certifying Cursor CLI support —
/// it is intentionally NOT wired into any CI job and this implementation does not attempt to run
/// it. cursor-agent's own <c>--print --mode ask</c> output shape has not been verified against a
/// live process in this repo; <see cref="ExtractAssistantAnswer"/> parses defensively (JSON-lines
/// OR plain text) — whoever runs this live for the first time should confirm/tighten that parsing
/// and record the observed Cursor CLI version (<see cref="RecordCursorVersionAsync"/>) alongside
/// the result.
/// </summary>
public class CursorMemoryIndexLiveCertTests {
    const string LiveGateEnvVar  = "KCAP_CURSOR_MEMORY_LIVE";
    const string ServerUrlEnvVar = "KCAP_URL";

    static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The real container, built against this machine's actual config — a stub client
    /// cannot exercise the real refresh/discovery chain a live-cert run is meant to certify.</summary>
    static async Task<ServiceProvider> AuthenticatedContainerAsync(ConfigRoot root, string baseUrl) {
        var profiles = await AppConfig.ResolveActiveProfile([], root);
        var services = new ServiceCollection();

        services.AddSingleton(root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(baseUrl, root, profiles));
        services.AddCapacitorHttp();

        return services.BuildServiceProvider();
    }

    // Both gated live tests are [NotInParallel]: they read/mutate the SAME process-global
    // `disable_memory_index` profile config (a real `kcap config set` subprocess writing the
    // actual config file this machine's `kcap` resolves), so — mirroring
    // CursorHookCommandTests' own bare-NotInParallel-for-a-shared-resource precedent — they
    // must never interleave with each other (a concurrent negative-control run could leave
    // disable_memory_index=true set while the positive test is trying to observe an injection).
    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_cursor_agent_sessionStart() {
        SkipUnlessLiveGateReady();

        var baseUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;
        var nonce   = $"kcap-live-nonce-{Guid.NewGuid():N}";

        var       root   = ConfigRoot.FromEnvironment();
        using var sp     = await AuthenticatedContainerAsync(root, baseUrl);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        var memoryId = await SaveNonceMemoryAsync(client, baseUrl, nonce);

        try {
            await RecordCursorVersionAsync();

            using var tmp = new TempDir();
            var answer = await RunCursorAgentAskAsync(
                tmp.Path,
                "A team-memory index may have been injected into your context under a " +
                "'## Team memory' heading. If a memory slug looks relevant, call get_memory " +
                $"on it and reply with ONLY the exact string it contains matching this pattern: " +
                $"kcap-live-nonce-<32 hex chars>. Reply with nothing else.");

            await Assert.That(answer).Contains(nonce);
        } finally {
            await ArchiveMemoryAsync(client, baseUrl, memoryId);
        }
    }

    /// <summary>Negative control: with <c>disable_memory_index</c> set, the SAME nonce must NOT
    /// reach the model — proving a false positive (e.g. the nonce leaking via some other channel,
    /// or the assertion being trivially satisfiable) isn't what the positive test is actually
    /// observing.</summary>
    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_cursor_agent_sessionStart() {
        SkipUnlessLiveGateReady();

        var baseUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;
        var nonce   = $"kcap-live-nonce-{Guid.NewGuid():N}";

        var       root   = ConfigRoot.FromEnvironment();
        using var sp     = await AuthenticatedContainerAsync(root, baseUrl);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        var memoryId = await SaveNonceMemoryAsync(client, baseUrl, nonce);

        // Capture the ORIGINAL disable_memory_index value BEFORE the first mutation, and put
        // the mutate/run/restore sequence in a try/finally that begins right here — so a failed
        // assertion (or the config-set call itself throwing) still restores the setting and
        // archives the memory instead of leaking a "disabled" profile on this machine.
        var originalDisableMemoryIndex = await ReadDisableMemoryIndexAsync();

        try {
            var configResult = await RunKcapConfigAsync("disable_memory_index", "true");
            await Assert.That(configResult.ExitCode).IsEqualTo(0);

            using var tmp = new TempDir();
            var answer = await RunCursorAgentAskAsync(
                tmp.Path,
                $"Reply with ONLY the string kcap-live-nonce-<32 hex chars> if you can see it " +
                "anywhere in your context; otherwise reply NONE.");

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            // Restore the ORIGINAL value, not an unconditional "false" — `kcap config` has no
            // "unset" primitive, so an originally-absent (null) flag is restored as "false",
            // which is observably identical (both read as "not disabled" via `is true`).
            await RunKcapConfigAsync("disable_memory_index", (originalDisableMemoryIndex ?? false) ? "true" : "false");
            await ArchiveMemoryAsync(client, baseUrl, memoryId);
        }
    }

    /// <summary>
    /// Reads the active profile's current <c>disable_memory_index</c> value via
    /// <c>kcap config show</c> (JSON printed followed by a blank line and a "Path:" line — see
    /// <c>ConfigCommand.Show</c> — so the JSON is the text before the first blank line). Returns
    /// null if the value is unset (absent/JSON null) or the command failed.
    /// </summary>
    static async Task<bool?> ReadDisableMemoryIndexAsync() {
        var (exitCode, stdout, _) = await RunProcessAsync("kcap", ["config", "show"], workingDirectory: null);
        if (exitCode != 0) return null;

        try {
            var jsonPart      = stdout.Split("\n\n", 2)[0];
            var root          = JsonNode.Parse(jsonPart);
            var activeProfile = root?["active_profile"]?.GetValue<string>();
            if (activeProfile is null) return null;

            return root?["profiles"]?[activeProfile]?["disable_memory_index"]?.GetValue<bool>();
        } catch {
            return null;
        }
    }

    static void SkipUnlessLiveGateReady() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live model-receipt certification — set {LiveGateEnvVar}=1 and {ServerUrlEnvVar}=<reachable kcap server> " +
            "to run (spends a real cursor-agent turn; requires `cursor-agent` on PATH, already logged in via " +
            "`kcap login` against that server, and a Cursor plan that can actually run a turn).");
        Skip.Unless(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ServerUrlEnvVar)),
            $"{ServerUrlEnvVar} must point at a reachable kcap server exposing GET /api/memories/index.");
    }

    /// <summary>
    /// Saves a small, user-scoped, repo-independent ("global") memory whose description embeds
    /// the nonce, via the same <c>POST /api/memories</c> contract <c>kcap mcp memory</c>'s
    /// <c>save_memory</c> tool uses (<see cref="McpMemoryServer.BuildSaveBody"/>) — reused here
    /// rather than hand-building a second copy of the request shape. "global: true" sidesteps
    /// needing a real repo-hash for this throwaway worktree.
    /// </summary>
    static async Task<string> SaveNonceMemoryAsync(HttpClient client, string baseUrl, string nonce) {
        var body = McpMemoryServer.BuildSaveBody(new JsonObject {
            ["audience"]    = "user",
            ["slug"]        = $"live-cert-{nonce}",
            ["description"] = $"kcap cursor memory live-cert nonce: {nonce}",
            ["content"]     = $"kcap cursor memory live-cert nonce: {nonce}. Safe to archive after the run.",
            ["kind"]        = "reference",
            ["global"]      = true
        }, cwdRepoHash: null, machineId: null);

        using var resp = await client.PostAsJsonAsync($"{baseUrl}/api/memories", body);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<JsonObject>();
        return created?["memory_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Save response carried no memory_id.");
    }

    static async Task ArchiveMemoryAsync(HttpClient client, string baseUrl, string memoryId) {
        try {
            using var resp = await client.DeleteAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(memoryId)}");
            if (!resp.IsSuccessStatusCode) {
                await Console.Error.WriteLineAsync(
                    $"[cursor-memory-live] failed to archive live-cert memory {memoryId}: HTTP {(int)resp.StatusCode}");
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[cursor-memory-live] failed to archive live-cert memory {memoryId}: {ex.Message}");
        }
    }

    static async Task RecordCursorVersionAsync() {
        try {
            var (exitCode, stdout, _) = await RunProcessAsync("cursor-agent", ["--version"], workingDirectory: null);
            await Console.Out.WriteLineAsync($"[cursor-memory-live] cursor-agent --version (exit {exitCode}): {stdout.Trim()}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[cursor-memory-live] could not record cursor-agent version: {ex.Message}");
        }
    }

    // ConfigCommand.HandleAsync routes on args[1] as the subcommand ("show"/"set") — the
    // "set" segment is required, `kcap config <key> <value>` is not a valid invocation shape.
    static async Task<(int ExitCode, string Stdout, string Stderr)> RunKcapConfigAsync(string key, string value) =>
        await RunProcessAsync("kcap", ["config", "set", key, value], workingDirectory: null);

    /// <summary>
    /// Runs <c>cursor-agent --print --mode ask &lt;prompt&gt;</c> in <paramref name="cwd"/> (a
    /// throwaway worktree — this must be a real session start so the SAME sessionStart hook path
    /// under test actually fires) and returns the parsed assistant answer.
    /// </summary>
    static async Task<string> RunCursorAgentAskAsync(string cwd, string prompt) {
        // -f (force/trust) is required: the throwaway worktree is an untrusted directory, and
        // without it cursor-agent halts on a "Workspace Trust Required" prompt instead of running
        // the turn (verified against the installed CLI). --mode ask keeps the turn read-only.
        var (exitCode, stdout, stderr) = await RunProcessAsync(
            "cursor-agent", ["--print", "-f", "--mode", "ask", prompt], cwd);
        await Console.Out.WriteLineAsync($"[cursor-memory-live] cursor-agent exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);
        return ExtractAssistantAnswer(stdout);
    }

    /// <summary>
    /// cursor-agent's own <c>--print</c> output shape is NOT verified against a live process in
    /// this repo (no prior probe recorded it — see <c>docs/acp-probe-findings.md</c>, which only
    /// covers the JSON-RPC <c>acp</c> subcommand). Parses defensively: try JSON — either a single
    /// object or newline-delimited JSON objects (mirroring Claude's <c>stream-json</c> convention)
    /// — pulling the first string-valued <c>text</c>/<c>message</c>/<c>content</c> field found;
    /// fall back to the raw trimmed stdout if nothing parses. Tighten this the first time this
    /// test is actually run live.
    /// </summary>
    internal static string ExtractAssistantAnswer(string stdout) {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0) return trimmed;

        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (line.Length == 0 || line[0] is not ('{' or '[')) continue;
            try {
                var node = JsonNode.Parse(line);
                if (FindFirstTextField(node) is { } text) return text;
            } catch {
                // Not JSON (or not the shape we expect) — fall through to plain-text handling below.
            }
        }

        try {
            var whole = JsonNode.Parse(trimmed);
            if (FindFirstTextField(whole) is { } text) return text;
        } catch {
            // Plain text output — return as-is.
        }

        return trimmed;
    }

    static string? FindFirstTextField(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var key in new[] { "text", "message", "content", "answer" }) {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
                }
                foreach (var (_, child) in obj) {
                    if (FindFirstTextField(child) is { } nested) return nested;
                }
                return null;
            case JsonArray arr:
                foreach (var item in arr) {
                    if (FindFirstTextField(item) is { } nested) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory) {
        var psi = new ProcessStartInfo(fileName) {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        using var timeoutCts = new CancellationTokenSource(ProcessTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}

/// <summary>
/// Ungated, CI-safe coverage for the pure <see cref="CursorMemoryIndexLiveCertTests.ExtractAssistantAnswer"/>
/// parser — the one piece of the live-cert scaffold that doesn't need a live process to exercise.
/// </summary>
public class CursorMemoryIndexLiveCertParsingTests {
    [Test]
    public async Task Plain_text_output_is_returned_as_is() {
        var result = CursorMemoryIndexLiveCertTests.ExtractAssistantAnswer("  kcap-live-nonce-abc123  \n");
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Single_json_object_with_a_text_field_extracts_the_text() {
        var result = CursorMemoryIndexLiveCertTests.ExtractAssistantAnswer(
            """{"type":"assistant","text":"kcap-live-nonce-abc123"}""");
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Newline_delimited_json_stream_extracts_the_first_matching_text_field() {
        var stdout = """
                     {"type":"system","subtype":"init"}
                     {"type":"assistant","message":{"content":[{"type":"text","text":"kcap-live-nonce-abc123"}]}}
                     """;
        var result = CursorMemoryIndexLiveCertTests.ExtractAssistantAnswer(stdout);
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Empty_output_returns_empty() {
        var result = CursorMemoryIndexLiveCertTests.ExtractAssistantAnswer("   \n  ");
        await Assert.That(result).IsEqualTo("");
    }
}
