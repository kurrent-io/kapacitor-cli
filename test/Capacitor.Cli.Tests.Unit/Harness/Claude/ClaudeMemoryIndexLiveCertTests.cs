using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Tests.Unit.Commands.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>
/// Manual, env-gated release-gate cert that the shared SessionStart memory index reaches the
/// model for Claude Code — mirrors <see cref="Cursor.CursorMemoryIndexLiveCertTests"/>; routing,
/// budget math, lifecycle, and envelope ordering are covered by the unit suite in
/// <see cref="ClaudeHookCommandTests"/> against fakes, so this is the one place asserting the real
/// end-to-end claim (a nonce saved as a memory reaches the model via SessionStart's
/// <c>hookSpecificOutput.additionalContext</c>).
///
/// Gated behind <see cref="LiveGateEnvVar"/> (unset by default): never runs in CI or an ordinary
/// local run. Requires <c>claude</c> on PATH and logged in, its SessionStart hook already wired to
/// <c>kcap</c>, <see cref="ServerUrlEnvVar"/> pointing at a reachable kcap server, and
/// <c>kcap login</c> already done against it. Run manually by a human/controller before
/// (re-)certifying this wiring.
///
/// Claude Code's <c>--print</c> output shape is unverified against a live process for this
/// purpose — <see cref="ExtractAssistantAnswer"/> parses defensively (JSON-lines or plain text);
/// confirm/tighten it on the first live run and record the observed version
/// (<see cref="RecordClaudeVersionAsync"/>).
/// </summary>
public class ClaudeMemoryIndexLiveCertTests {
    const string LiveGateEnvVar  = "KCAP_CLAUDE_MEMORY_LIVE";
    const string ServerUrlEnvVar = "KCAP_URL";

    static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(90);

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
    // CursorMemoryIndexLiveCertTests' own precedent — they must never interleave with each other
    // (a concurrent negative-control run could leave disable_memory_index=true set while the
    // positive test is trying to observe an injection).
    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_claude_sessionStart() {
        SkipUnlessLiveGateReady();

        var baseUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;
        var nonce   = $"kcap-live-nonce-{Guid.NewGuid():N}";

        var       root   = ConfigRoot.FromEnvironment();
        using var sp     = await AuthenticatedContainerAsync(root, baseUrl);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        var memoryId = await SaveNonceMemoryAsync(client, baseUrl, nonce);

        try {
            await RecordClaudeVersionAsync();

            using var tmp = new TempDir();
            var answer = await RunClaudePrintAsync(
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
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_claude_sessionStart() {
        SkipUnlessLiveGateReady();

        var baseUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar)!;
        var nonce   = $"kcap-live-nonce-{Guid.NewGuid():N}";

        var       root   = ConfigRoot.FromEnvironment();
        using var sp     = await AuthenticatedContainerAsync(root, baseUrl);
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        var memoryId = await SaveNonceMemoryAsync(client, baseUrl, nonce);

        // Capture the ORIGINAL disable_memory_index value BEFORE the first mutation, and put the
        // mutate/run/restore sequence in a try/finally that begins right here — so a failed
        // assertion (or the config-set call itself throwing) still restores the setting and
        // archives the memory instead of leaking a "disabled" profile on this machine.
        var originalDisableMemoryIndex = await ReadDisableMemoryIndexAsync();

        try {
            var configResult = await RunKcapConfigAsync("disable_memory_index", "true");
            await Assert.That(configResult.ExitCode).IsEqualTo(0);

            using var tmp = new TempDir();
            var answer = await RunClaudePrintAsync(
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

        var jsonPart = ExtractLeadingJsonBlock(stdout);

        try {
            var root          = JsonNode.Parse(jsonPart);
            var activeProfile = root?["active_profile"]?.GetValue<string>();
            if (activeProfile is null) return null;

            return root?["profiles"]?[activeProfile]?["disable_memory_index"]?.GetValue<bool>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Isolates the leading JSON block from <c>kcap config show</c>'s stdout — the text before the
    /// first blank line — tolerating both <c>\n</c> and Windows <c>\r\n</c> line endings so a CRLF
    /// stdout doesn't fail to find the blank-line boundary (which would otherwise return the whole
    /// blob, trailing "Path:" line included, and fail to parse as JSON).
    /// </summary>
    internal static string ExtractLeadingJsonBlock(string stdout) =>
        stdout.Replace("\r\n", "\n").Split("\n\n", 2)[0];

    static void SkipUnlessLiveGateReady() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live model-receipt certification — set {LiveGateEnvVar}=1 and {ServerUrlEnvVar}=<reachable kcap server> " +
            "to run (spends a real `claude --print` turn; requires `claude` on PATH with its SessionStart " +
            "hook already wired to `kcap`, and `kcap login` already done against that server).");
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
            ["description"] = $"kcap claude memory live-cert nonce: {nonce}",
            ["content"]     = $"kcap claude memory live-cert nonce: {nonce}. Safe to archive after the run.",
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
                    $"[claude-memory-live] failed to archive live-cert memory {memoryId}: HTTP {(int)resp.StatusCode}");
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[claude-memory-live] failed to archive live-cert memory {memoryId}: {ex.Message}");
        }
    }

    static async Task RecordClaudeVersionAsync() {
        try {
            var (exitCode, stdout, _) = await RunProcessAsync("claude", ["--version"], workingDirectory: null);
            await Console.Out.WriteLineAsync($"[claude-memory-live] claude --version (exit {exitCode}): {stdout.Trim()}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[claude-memory-live] could not record claude version: {ex.Message}");
        }
    }

    static async Task<(int ExitCode, string Stdout, string Stderr)> RunKcapConfigAsync(string key, string value) =>
        await RunProcessAsync("kcap", ["config", "set", key, value], workingDirectory: null);

    /// <summary>
    /// Runs <c>claude --print &lt;prompt&gt;</c> in <paramref name="cwd"/> (a throwaway worktree —
    /// this must be a real session start so the SAME sessionStart hook path under test actually
    /// fires) and returns the parsed assistant answer.
    /// </summary>
    static async Task<string> RunClaudePrintAsync(string cwd, string prompt) {
        var (exitCode, stdout, stderr) = await RunProcessAsync("claude", ["--print", prompt], cwd);
        await Console.Out.WriteLineAsync($"[claude-memory-live] claude exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);
        return ExtractAssistantAnswer(stdout);
    }

    /// <summary>
    /// Claude Code's own <c>--print</c> output shape (plain text by default — no
    /// <c>--output-format</c> flag is passed here) is NOT independently verified against a live
    /// process for THIS purpose in this repo. Parses defensively: try JSON — either a single
    /// object or newline-delimited JSON objects (in case a future default changes, or an ambient
    /// environment variable forces one) — pulling the first string-valued
    /// <c>text</c>/<c>message</c>/<c>content</c>/<c>result</c> field found; fall back to the raw
    /// trimmed stdout if nothing parses. Tighten this the first time this test is actually run
    /// live.
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
                foreach (var key in new[] { "text", "message", "content", "answer", "result" }) {
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

    /// <summary>Test-only seam: notified with the PID of every process this method spawns, so the
    /// orphan-cleanup test below can confirm a timed-out child is actually gone afterward. Never
    /// set by production code paths.</summary>
    internal static Action<int>? OnProcessStarted;

    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
            string fileName, IReadOnlyList<string> args, string? workingDirectory, TimeSpan? timeout = null) {
        var psi = new ProcessStartInfo(fileName) {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        OnProcessStarted?.Invoke(process.Id);

        using var timeoutCts = new CancellationTokenSource(timeout ?? ProcessTimeout);
        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        } finally {
            // On normal completion the process has already exited; on a timeout cancellation (or
            // any other exception mid-read) it may still be running — kill the whole tree so a
            // hung `claude`/`kcap` child is never left orphaned. Best-effort: it may already have
            // exited by the time we check.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        }
    }
}

/// <summary>
/// Ungated, CI-safe coverage for the pure <see cref="ClaudeMemoryIndexLiveCertTests.ExtractAssistantAnswer"/>
/// parser — the one piece of the live-cert scaffold that doesn't need a live process to exercise.
/// </summary>
public class ClaudeMemoryIndexLiveCertParsingTests {
    [Test]
    public async Task Plain_text_output_is_returned_as_is() {
        var result = ClaudeMemoryIndexLiveCertTests.ExtractAssistantAnswer("  kcap-live-nonce-abc123  \n");
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Single_json_object_with_a_result_field_extracts_the_text() {
        var result = ClaudeMemoryIndexLiveCertTests.ExtractAssistantAnswer(
            """{"type":"result","result":"kcap-live-nonce-abc123"}""");
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Newline_delimited_json_stream_extracts_the_first_matching_text_field() {
        var stdout = """
                     {"type":"system","subtype":"init"}
                     {"type":"assistant","message":{"content":[{"type":"text","text":"kcap-live-nonce-abc123"}]}}
                     """;
        var result = ClaudeMemoryIndexLiveCertTests.ExtractAssistantAnswer(stdout);
        await Assert.That(result).IsEqualTo("kcap-live-nonce-abc123");
    }

    [Test]
    public async Task Empty_output_returns_empty() {
        var result = ClaudeMemoryIndexLiveCertTests.ExtractAssistantAnswer("   \n  ");
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task Leading_json_block_extraction_splits_on_an_lf_blank_line() {
        var stdout = "{\"active_profile\":\"default\"}\n\nPath: ~/.config/kcap/config.json\n";
        var result = ClaudeMemoryIndexLiveCertTests.ExtractLeadingJsonBlock(stdout);
        await Assert.That(result).IsEqualTo("{\"active_profile\":\"default\"}");
    }

    [Test]
    public async Task Leading_json_block_extraction_tolerates_a_crlf_blank_line() {
        // Windows `kcap config show` stdout: the same "JSON, blank line, Path:" shape but with
        // \r\n line endings — must still isolate the JSON, not the whole CRLF blob.
        var stdout = "{\"active_profile\":\"default\"}\r\n\r\nPath: C:\\Users\\me\\config.json\r\n";
        var result = ClaudeMemoryIndexLiveCertTests.ExtractLeadingJsonBlock(stdout);
        await Assert.That(result).IsEqualTo("{\"active_profile\":\"default\"}");
    }
}

/// <summary>
/// Ungated, CI-safe coverage for <see cref="ClaudeMemoryIndexLiveCertTests.RunProcessAsync"/>'s
/// timeout-cleanup path — the other piece of the live-cert scaffold that doesn't need a live
/// `claude`/`kcap` process to exercise.
/// </summary>
public class ClaudeMemoryIndexLiveCertProcessTests {
    [Test]
    public async Task A_timed_out_process_is_killed_rather_than_left_running() {
        // A process that vastly outlives the tiny timeout below, spawned the same cross-platform
        // way as Daemon.DummyProcess.StartSleep (ping's ~1s cadence needs no stdin, unlike
        // `timeout /t`, so it reliably stays alive on a headless CI runner).
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>) ["/c", "ping", "-n", "120", "127.0.0.1"])
            : ("sleep", (IReadOnlyList<string>) ["120"]);

        int? pid = null;
        ClaudeMemoryIndexLiveCertTests.OnProcessStarted = p => pid = p;
        try {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                ClaudeMemoryIndexLiveCertTests.RunProcessAsync(
                    fileName, args, workingDirectory: null, TimeSpan.FromMilliseconds(200)));
        } finally {
            ClaudeMemoryIndexLiveCertTests.OnProcessStarted = null;
        }

        await Assert.That(pid).IsNotNull();

        // Confirm the child is actually gone (not merely that our await unblocked) — a brief
        // grace window covers the OS finishing the teardown after the kill signal.
        var deadline    = DateTime.UtcNow.AddSeconds(5);
        var stillAlive  = true;
        while (DateTime.UtcNow < deadline) {
            try {
                using var proc = System.Diagnostics.Process.GetProcessById(pid!.Value);
                if (proc.HasExited) { stillAlive = false; break; }
            } catch (ArgumentException) {
                stillAlive = false; // no such process — already reaped
                break;
            }
            await Task.Delay(50);
        }

        await Assert.That(stillAlive).IsFalse();
    }
}
