using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli;

sealed partial class WatcherManager(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    // The one URL this process resolved. No member takes one: a watcher spawned against a different
    // server than the hook that spawned it would stream a session nothing on this side can see.
    // Nullable because an offline invocation resolves none — the IsPostable guards refuse that.
    string? Url => profiles.Resolution.ServerUrl;

    internal string GetWatcherDir() {
        var overrideDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");

        return overrideDir ?? config.Path("watchers");
    }

    string GetPidFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.pid");

    /// <summary>
    /// Per-key heartbeat file (touched every main-loop iteration by the watcher itself —
    /// see <c>WatchCommand.RunWatch</c>) used by <see cref="IsWatcherAlive"/> to tell a
    /// wedged (hung-but-alive) watcher from a healthy one.
    /// </summary>
    internal string GetHeartbeatFilePath(string key) =>
        WatcherHeartbeat.HeartbeatPath(GetWatcherDir(), key);

    /// <summary>
    /// Per-key start-time marker, written by <see cref="SpawnWatcher"/> at the moment the
    /// process is spawned (not by the watcher itself — the probe must know when THIS
    /// instance started even if it never gets far enough to touch its own heartbeat).
    /// Backs the startup-grace window in <see cref="IsWatcherAlive"/>.
    /// </summary>
    string GetStartedFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.started");

    /// <summary>
    /// Per-key spawn lock file — same cross-platform primitive as <c>DaemonLock</c>
    /// (<c>FileShare.None</c> maps to <c>flock(LOCK_EX)</c> on POSIX and a real exclusive
    /// lock on Windows) — guarding every spawn decision in <see cref="EnsureWatcherRunning"/>
    /// (both "no watcher yet" and "reap a wedged one first") so concurrent hooks racing the
    /// same key can't double-spawn.
    /// </summary>
    string GetSpawnLockFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.spawnlock");

    /// <summary>
    /// Test-only seam: when set, <see cref="EnsureWatcherRunning"/> invokes this instead of
    /// the real <see cref="SpawnWatcher"/> (which launches a real OS process). Lets the
    /// lock-guarded reap-and-respawn logic be exercised deterministically without spawning
    /// anything. Always null in production.
    /// </summary>
    internal static Func<string, Task>? SpawnOverrideForTesting;

    /// <summary>
    /// Test seam for the ACTUAL <c>Process.Start</c> call inside <see cref="SpawnWatcher"/>,
    /// <see cref="SpawnCopilotFinalizeDrain"/> and <c>ClaudeSessionEndHandoff.TrySpawn</c>. Distinct from <see cref="SpawnOverrideForTesting"/>,
    /// which only <c>SpawnForKeyAsync</c> consults and so cannot observe those methods at all.
    ///
    /// <para>Needed because both call static <c>Process.Start</c> inside a catch-all, and the finalize
    /// drain writes no marker — so "no child was left behind" is unfalsifiable: delete the URL guard
    /// and the start merely throws or returns null in a test environment, leaving every observable
    /// effect identical. Asserting zero invocations here is the only proof the guard ran.</para>
    ///
    /// <para>Always null in production.</para>
    /// </summary>
    internal static Func<ProcessStartInfo, Process?>? ProcessStarterForTesting;

    internal static Process? StartProcess(ProcessStartInfo psi) =>
        ProcessStarterForTesting is { } fake ? fake(psi) : Process.Start(psi);

    internal static string BuildSpawnArgs(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride,
            string? cwd,
            bool    skipTitle,
            int?    parentPid,
            string  vendor
        ) {
        var sessionId = sessionIdOverride ?? key;

        var arguments = agentId is not null
            ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
            : $"watch {key} \"{transcriptPath}\"";

        if (cwd is not null) {
            arguments += $" --cwd \"{cwd}\"";
        }

        if (skipTitle) {
            arguments += " --skip-title";
        }

        if (parentPid is { } ppid and > 1) {
            arguments += $" --parent-pid {ppid}";
        }

        if (vendor != "claude") {
            arguments += $" --vendor \"{vendor}\"";
        }

        return arguments;
    }

    public async Task SpawnWatcher(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride = null,
            string? cwd               = null,
            bool    skipTitle         = false,
            string  vendor            = "claude"
        ) {
        // Defence in depth: ShouldSpawnAfter already refuses for an unusable URL, but a caller that
        // bypassed it would otherwise write a PID file asserting capture that cannot happen — a
        // watcher streams to SignalR and can never connect here.
        if (!HookHttp.IsPostable(Url)) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"watcher not started for {key}"));
            return;
        }

        try {
            var watcherDir = GetWatcherDir();
            Directory.CreateDirectory(watcherDir);

            var kcapPath = Environment.ProcessPath ?? "kcap";
            // Resolve the long-lived coding-agent PID rather than getppid(): coding
            // agents invoke hooks through a transient executor that dies the moment the
            // hook returns, so by the time the watcher checks IsProcessAlive it sees a
            // dead PID and never starts the monitor task — leaving sessions stuck
            // "active" because session-end is never POSTed. The vendor-aware resolver
            // walks the ppid ancestry to find the agent by name, which is robust to the
            // differing process-group topologies of Claude (transient hook group → bare
            // getpgrp() resolves a dead PID) and Codex (inherits the agent's group).
            var parentPid     = ProcessHelpers.GetCodingAgentPid(vendor);
            var arguments     = BuildSpawnArgs(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, parentPid, vendor);

            var psi = new ProcessStartInfo(kcapPath, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"]                 = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };

            // Stop the watcher from inheriting the coding agent's pipe descriptors —
            // std handles on Windows, any fd >= 3 on Unix; otherwise it holds the
            // agent's hook-stdout pipe open for its whole lifetime, hanging synchronous
            // subagent hooks and orphaning the watcher.
            ProcessHelpers.PreventInheritedHandles();

            var process = StartProcess(psi);

            if (process is null) {
                await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}");

                return;
            }

            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            // Line 2 is this incarnation's start-identity token (daemon pid-file layout) so
            // KillWatcher can tell the spawned watcher apart from a later recycle of its pid.
            var token = ProcessStartToken.ForPid(process.Id);
            await File.WriteAllTextAsync(
                GetPidFilePath(key), token is null ? process.Id.ToString() : $"{process.Id}\n{token}");

            // Task 9: record this instance's start time so a later staleness probe
            // knows whether it's still within the startup grace window — written here (not
            // by the watcher itself) so it exists even if the child never gets far enough to
            // touch its own heartbeat.
            try {
                WatcherHeartbeat.Touch(GetStartedFilePath(key), DateTimeOffset.UtcNow);
            } catch {
                /* best-effort — a missing marker just means IsWatcherAlive treats "now" as startupAt */
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the per-key heartbeat + started markers so they don't leak
    /// per-session the way the pid file never did. Deliberately does NOT touch the
    /// <c>{key}.spawnlock</c> file: <see cref="KillWatcher"/> can run from inside
    /// <see cref="WithSpawnLock"/> (the wedged-watcher reap path), and unlinking a held lock
    /// file on POSIX lets a racing hook open a fresh inode with a non-conflicting flock —
    /// reopening the double-spawn hole (the same reason <c>DaemonLock</c> never unlinks its
    /// lock file). The spawn lock is swept by <see cref="PurgeAuxiliaryFiles"/> / <c>kcap cleanup</c>.
    /// </summary>
    void DeleteHeartbeatFiles(string key) {
        try { File.Delete(GetHeartbeatFilePath(key)); } catch { /* best-effort */ }
        try { File.Delete(GetStartedFilePath(key)); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Removes every leftover per-key auxiliary file (<c>*.heartbeat</c>/<c>*.started</c>/
    /// <c>*.spawnlock</c>) in the watcher directory. Called by <c>kcap cleanup</c> after all
    /// watchers are killed — the one place it is safe to unlink spawn-lock files, since cleanup
    /// holds no spawn lock. Returns the number of files removed.
    /// </summary>
    public int PurgeAuxiliaryFiles() {
        var dir = GetWatcherDir();

        if (!Directory.Exists(dir)) {
            return 0;
        }

        var removed = 0;

        foreach (var pattern in new[] { "*.heartbeat", "*.started", "*.spawnlock" }) {
            foreach (var file in Directory.GetFiles(dir, pattern)) {
                try {
                    File.Delete(file);
                    removed++;
                } catch {
                    /* best-effort */
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Kills the watcher process for the given key. Returns true if the watcher was running and was killed,
    /// false if it was already dead or no PID file existed. Always removes the per-key
    /// heartbeat/started markers; see <see cref="DeleteHeartbeatFiles"/> for
    /// why the spawn lock is intentionally left behind here.
    /// </summary>
    public async Task<bool> KillWatcher(string key) {
        var pidFile = GetPidFilePath(key);

        if (!File.Exists(pidFile)) {
            // No live watcher, but sweep any orphaned heartbeat/started markers for this key.
            DeleteHeartbeatFiles(key);

            return false;
        }

        try {
            // Line 1 is the pid; line 2 (when present) is the incarnation's ProcessStartToken
            // written by SpawnWatcher — the same layout as the daemon pid file.
            var lines = await File.ReadAllLinesAsync(pidFile);

            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var pid)) {
                File.Delete(pidFile);

                return false;
            }

            // "Ambiguity never kills" (ProcessStartToken): a conclusive token mismatch means the
            // watcher died and the OS recycled its pid onto an unrelated process — sweep the
            // stale file, never signal. A missing/uncomparable token (legacy file, process gone)
            // falls through to the kill attempt, exactly as before tokens existed.
            var token = lines.Length > 1 ? lines[1].Trim() : "";

            if (token.Length > 0 && ProcessStartToken.Matches(pid, token) == false) {
                await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) was recycled by another process; sweeping stale pid file");

                return false;
            }

            try {
                var process = Process.GetProcessById(pid);

                // SIGTERM-first on Unix so the watcher runs its shutdown path (final drain +
                // undelivered-tail spool) — Process.Kill is SIGKILL there. Windows has no
                // SIGTERM; the stop is hard and recovery is left to the spool/import paths.
                if (!TrySignalTerm(pid)) {
                    process.Kill(entireProcessTree: false);
                }

                // Wait up to 5 seconds for graceful exit
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                try {
                    await process.WaitForExitAsync(cts.Token);
                    await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) exited gracefully");
                } catch (OperationCanceledException) {
                    // Force kill if it didn't exit in time
                    process.Kill(entireProcessTree: true);
                    await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) force-killed after timeout");
                }

                return true;
            } catch (ArgumentException) {
                // Process already exited
                await Console.Error.WriteLineAsync($"Watcher {key} (PID {pid}) already exited");

                return false;
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Error killing watcher {key}: {ex.Message}");

            return false;
        } finally {
            try { File.Delete(pidFile); } catch {
                /* ignore */
            }

            DeleteHeartbeatFiles(key);
        }
    }

    /// <summary>
    /// Stops every watcher in <paramref name="keys"/> concurrently (#550, a session watcher's
    /// teardown of its spawned children). <see cref="KillWatcher"/>'s SIGTERM-first gives each
    /// child its final drain; its 5s force-kill bound keeps a wedged child from stalling the caller.
    /// </summary>
    public Task KillWatchers(IEnumerable<string> keys) =>
        Task.WhenAll(keys.Select(KillWatcher));

    /// <summary>
    /// Retires this watcher's own pid file (+ heartbeat markers) on graceful exit, but only while
    /// it still names this incarnation — a successor that already overwrote the file is left
    /// alone. Keeps a later teardown/cleanup from ever acting on this watcher's recycled pid.
    /// </summary>
    public void RemoveOwnPidFile(string key, int ownPid) {
        try {
            var lines = File.ReadAllLines(GetPidFilePath(key));

            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var filePid) || filePid != ownPid) return;

            File.Delete(GetPidFilePath(key));
            DeleteHeartbeatFiles(key);
        } catch {
            /* best-effort — a missing/unreadable file means there is nothing to retire */
        }
    }

    const int Sigterm = 15;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int sys_kill(int pid, int sig);

    /// <summary>SIGTERM on Unix; false on Windows or when the signal can't be delivered.</summary>
    static bool TrySignalTerm(int pid) {
        if (OperatingSystem.IsWindows()) return false;

        try {
            return sys_kill(pid, Sigterm) == 0;
        } catch {
            return false;
        }
    }

    /// <summary>PID-only liveness: the process exists, irrespective of whether it's wedged.</summary>
    bool PidAlive(string key) {
        var pidFile = GetPidFilePath(key);

        if (!File.Exists(pidFile)) {
            return false;
        }

        try {
            var lines = File.ReadAllLines(pidFile);

            if (lines.Length == 0 || !int.TryParse(lines[0].Trim(), out var pid)) {
                return false;
            }

            try {
                var process = Process.GetProcessById(pid);

                return !process.HasExited;
            } catch (ArgumentException) {
                return false;
            }
        } catch {
            return false;
        }
    }

    /// <summary>
    /// True when the watcher's PID exists AND its heartbeat isn't stale (after the startup
    /// grace) — i.e. the process is alive AND its main loop is provably still turning, not
    /// wedged. A PID-only check (the old behavior, still available via
    /// <see cref="PidAlive"/>) can't tell a hung watcher from a healthy one.
    /// </summary>
    internal bool IsWatcherAlive(string key) {
        if (!PidAlive(key)) {
            return false;
        }

        var now       = DateTimeOffset.UtcNow;
        var lastBeat  = WatcherHeartbeat.Read(GetHeartbeatFilePath(key));
        // A missing started marker (shouldn't happen in practice — SpawnWatcher always
        // writes it) falls back to "now", i.e. the freshest possible grace window rather
        // than treating an unknown start time as long-past and immediately stale.
        var startupAt = WatcherHeartbeat.Read(GetStartedFilePath(key)) ?? now;

        return !WatcherHeartbeat.IsStale(lastBeat, startupAt, now, WatcherHeartbeat.Grace, WatcherHeartbeat.Threshold);
    }

    /// <summary>
    /// Runs <paramref name="body"/> while holding the per-key spawn lock (see
    /// <see cref="GetSpawnLockFilePath"/>). If another process already holds it, returns
    /// immediately WITHOUT running <paramref name="body"/> — the current holder is either
    /// already reaping + respawning this key, or about to, so there is nothing for the
    /// loser to do but skip (task 9: prevents two concurrent hooks from
    /// double-spawning a watcher for the same key).
    /// </summary>
    internal async Task WithSpawnLock(string key, Func<Task> body) {
        var watcherDir = GetWatcherDir();
        Directory.CreateDirectory(watcherDir);

        FileStream stream;

        try {
            // FileShare.None maps to flock(LOCK_EX) on POSIX and a real exclusive lock on
            // Windows — the same cross-platform primitive DaemonLock uses. FileMode.OpenOrCreate
            // keeps a stale lock file on disk from ever blocking acquisition; the kernel lock,
            // not file presence, is what enforces exclusion.
            stream = new FileStream(GetSpawnLockFilePath(key), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            return; // contended — the current holder wins; we skip rather than wait.
        }

        try {
            await body();
        } finally {
            stream.Dispose();
        }
    }

    Task SpawnForKeyAsync(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride,
            string? cwd,
            bool    skipTitle,
            string  vendor
        ) =>
        SpawnOverrideForTesting is { } fake
            ? fake(key)
            : SpawnWatcher(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor);

    public async Task EnsureWatcherRunning(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride = null,
            string? cwd               = null,
            bool    skipTitle         = false,
            string  vendor            = "claude"
        ) {
        if (IsWatcherAlive(key)) {
            return; // fast path: no lock needed to observe an already-healthy watcher.
        }

        // Everything past this point — the kill-if-wedged step AND the spawn itself — runs
        // under the per-key spawn lock. Guarding ONLY the kill+respawn of a wedged watcher
        // would leave a race: KillWatcher deletes the pid file before releasing the lock, so
        // a second hook arriving in that window would see "no pid" and take an unguarded
        // plain-spawn path, double-spawning anyway. Locking the whole decision — including
        // the plain "no watcher yet" spawn — closes that window.
        await WithSpawnLock(key, async () => {
            // Re-check under the lock: another hook may have already reaped + respawned (or
            // spawned from scratch) this key while we were waiting to acquire it.
            if (IsWatcherAlive(key)) {
                return;
            }

            if (PidAlive(key)) {
                // The process exists but its heartbeat is stale: wedged, not dead. Reap before
                // respawning — still holding the lock, so no other hook can race the gap
                // between the kill and the new pid file landing.
                await Console.Error.WriteLineAsync($"Watcher {key} heartbeat stale; reaping wedged watcher and respawning");
                await KillWatcher(key);
            }

            await SpawnForKeyAsync(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor);
        });
    }

    public void SpawnWhatsDoneGenerator(string sessionId, string vendor = "claude") {
        try {
            var kcapPath = Environment.ProcessPath ?? "kcap";

            var psi = new ProcessStartInfo(kcapPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"]                 = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };
            psi.ArgumentList.Add("generate-whats-done");
            psi.ArgumentList.Add(sessionId);

            // The child process picks the headless CLI runner from this flag —
            // matches the `generate-whats-done [--codex] <id>` surface in Program.cs.
            if (vendor == "codex") {
                psi.ArgumentList.Add("--codex");
            }

            // Don't let this detached child inherit the agent's pipe descriptors —
            // same pipe-leak hazard as the watcher spawn above.
            ProcessHelpers.PreventInheritedHandles();

            var process = StartProcess(psi);

            if (process is null) {
                Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}");

                return;
            }

            // Close redirected streams from parent side so the child doesn't hold pipe FDs open
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            Console.Error.WriteLine($"Spawned what's-done generator for {sessionId} (PID {process.Id})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn what's-done generator for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns a detached, short-lived <c>kcap copilot-finalize</c> process that
    /// delivers the Copilot <c>session.shutdown</c> tail (and any final assistant
    /// turn) which Copilot writes to events.jsonl only AFTER the sessionEnd hook
    /// returns. The hook calls this as its FIRST action — before killing
    /// the live watcher and POSTing session-end — so the drainer is guaranteed to
    /// exist even if the POST hangs and Copilot SIGKILLs the hook; being detached
    /// it survives that kill and is the only thing left to read the file. Fire-
    /// and-forget and idempotent (server watermark + deterministic ids); mirrors
    /// <see cref="SpawnWhatsDoneGenerator"/>.
    /// </summary>
    public void SpawnCopilotFinalizeDrain(string sessionId, string transcriptPath) {
        // A detached child that would poll for up to 45s and then exit 2 on an unusable URL, leaving
        // no marker any assertion could observe. Refuse to launch it at all.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"copilot finalize drain not started for {sessionId}"));
            return;
        }

        try {
            var kcapPath = Environment.ProcessPath ?? "kcap";

            var psi = new ProcessStartInfo(kcapPath) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    ["KCAP_URL"]                 = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };
            psi.ArgumentList.Add("copilot-finalize");
            psi.ArgumentList.Add(sessionId);
            psi.ArgumentList.Add(transcriptPath);

            // Don't let this detached child inherit the agent's pipe descriptors —
            // same pipe-leak hazard as the spawns above.
            ProcessHelpers.PreventInheritedHandles();

            var process = StartProcess(psi);

            if (process is null) {
                Console.Error.WriteLine($"Failed to spawn copilot finalize drain for {sessionId}");

                return;
            }

            // Close redirected streams from the parent side so the child doesn't
            // hold pipe FDs open (the child redirects its own output to a log file).
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            Console.Error.WriteLine($"Spawned copilot finalize drain for {sessionId} (PID {process.Id})");
        } catch (Exception ex) {
            Console.Error.WriteLine($"Failed to spawn copilot finalize drain for {sessionId}: {ex.Message}");
        }
    }

    public async Task InlineDrainAsync(
            string  sessionId,
            string  transcriptPath,
            string? agentId,
            string  vendor = "claude"
        ) {
        // Runs on session-end BEFORE the lifecycle POST. The caller's preceding KillWatcher is
        // unaffected.
        if (!HookHttp.IsPostable(Url)) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"inline drain skipped for {sessionId}"));
            return;
        }

        try {
            using var httpClient = await http.ForBackgroundAsync();

            // Get server's last recorded position
            int startLine;

            try {
                var query = agentId is not null ? $"?agentId={agentId}" : "";
                var resp  = await httpClient.GetWithRetryAsync($"{Url}/api/sessions/{sessionId}/last-line{query}");

                if (resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent) {
                    var json = await resp.Content.ReadAsStringAsync();
                    var doc  = JsonDocument.Parse(json);

                    startLine = (int?)doc.RootElement.Num("last_line_number") + 1
                     ?? WatchCommand.CountFileLines(transcriptPath);
                } else {
                    startLine = WatchCommand.CountFileLines(transcriptPath);
                }
            } catch {
                startLine = WatchCommand.CountFileLines(transcriptPath);
            }

            if (!File.Exists(transcriptPath)) {
                return;
            }

            var newLines       = new List<string>();
            var newLineNumbers = new List<int>();

            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var       reader = new StreamReader(stream);

            var lineIndex = 0;

            while (await reader.ReadLineAsync() is { } line) {
                if (lineIndex < startLine) {
                    lineIndex++;

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line)) {
                    // Redact like WatchCommand.DrainNewLines — the live watcher
                    // path already redacts, and this inline-drain can carry real
                    // assistant/tool content (e.g. the Copilot final turn the
                    // finalize drain delivers).
                    newLines.Add(SecretRedactor.RedactLine(line));
                    newLineNumbers.Add(lineIndex);
                }

                lineIndex++;
            }

            if (newLines.Count == 0) {
                await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: no new lines to send");

                return;
            }

            var batch = new TranscriptBatch {
                SessionId   = sessionId,
                AgentId     = agentId,
                Lines       = [..newLines],
                LineNumbers = [..newLineNumbers],
                Vendor      = vendor == "claude" ? null : vendor
            };

            var       batchJson = JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);
            using var content   = new StringContent(batchJson, Encoding.UTF8, "application/json");

            try {
                var resp = await httpClient.PostWithRetryAsync($"{Url}/hooks/transcript", content);

                if (resp.IsSuccessStatusCode) {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: sent {newLines.Count} line(s)");
                } else {
                    await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server returned HTTP {(int)resp.StatusCode}");
                    PrintRecoveryHint(sessionId);
                }
            } catch (HttpRequestException ex) {
                await Console.Error.WriteLineAsync($"Inline drain for {sessionId}: server unreachable after retries — {ex.Message}");
                PrintRecoveryHint(sessionId);
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Inline drain for {sessionId} failed: {ex.Message}");
            PrintRecoveryHint(sessionId);
        }
    }

    static void PrintRecoveryHint(string sessionId) =>
        Console.Error.WriteLine($"Transcript not uploaded. To import later, run: kcap import --session {sessionId}");
}
