using System.Diagnostics;
using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Integration;

[NotInParallel]
public class WatcherLifecycleTests {
    static readonly TempDir Tmp = new();
    // Not Tmp: that is KCAP_WATCHER_DIR, which the watcher enumerates.
    static readonly TempDir Transcripts = new();
    static string TempDir => Tmp.Path;

    // KCAP_WATCHER_DIR is pinned below and wins over the root, so nothing here reads it — it points
    // at the same temp directory anyway, so a lapse in that precedence cannot escape into the
    // developer's own config.
    static readonly ConfigRoot Root = new(Tmp.Path);

    // One manager over that root and a resolution naming the URL these spawns target — the same two
    // values production hands it, so nothing here can point a watcher at a second server.
    static readonly WatcherManager Watchers = new(Root, Resolutions.At("http://localhost:0", Root), new FixedCapacitorHttpClient());

    [Before(Class)]
    public static void SetUp() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", null);
        Tmp.Dispose();
        Transcripts.Dispose();
    }

    static (string key, string transcriptPath, string pidFile) SetUpWatcher() {
        var key            = $"test-watcher-{Guid.NewGuid():N}";
        var transcriptPath = Transcripts.CreateFile($"{key}.jsonl");

        return (key, transcriptPath, Path.Combine(TempDir, $"{key}.pid"));
    }

    static async Task AssertPidFileValid(string pidFile) {
        await Assert.That(File.Exists(pidFile)).IsTrue();
        var lines = await File.ReadAllLinesAsync(pidFile);
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(1);
        await Assert.That(int.TryParse(lines[0].Trim(), out _)).IsTrue();
    }

    [Test]
    public async Task SpawnAndKill_ManagesPidFile() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        await Watchers.SpawnWatcher(key, transcriptPath, agentId: null);
        await AssertPidFileValid(pidFile);

        await Watchers.KillWatcher(key);
        await Assert.That(File.Exists(pidFile)).IsFalse();
    }

    [Test]
    public async Task EnsureWatcherRunning_SpawnsIfDead() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        await Watchers.EnsureWatcherRunning(key, transcriptPath, agentId: null);
        await AssertPidFileValid(pidFile);

        await Watchers.KillWatcher(key);
    }

    // The pid file must identify the watcher INCARNATION, not just the pid — a recycled pid must
    // never be killable through a stale file. Line 2 carries the spawn-time ProcessStartToken,
    // mirroring the daemon pid file.
    [Test]
    public async Task SpawnWatcher_records_the_process_start_token_beside_the_pid() {
        var (key, transcriptPath, pidFile) = SetUpWatcher();

        await Watchers.SpawnWatcher(key, transcriptPath, agentId: null);

        var lines = await File.ReadAllLinesAsync(pidFile);
        await Assert.That(lines.Length).IsGreaterThanOrEqualTo(2);

        var pid = int.Parse(lines[0].Trim(), CultureInfo.InvariantCulture);
        await Assert.That(lines[1].Trim()).IsEqualTo(ProcessStartToken.ForPid(pid));

        await Watchers.KillWatcher(key);
    }

    // Process.Kill is SIGKILL on Unix, so before this fix a "killed" watcher never ran its
    // SIGTERM shutdown path (final drain + undelivered-tail spool). Exit code 42 from the trap
    // proves the process saw SIGTERM and exited on its own terms.
    [Test]
    public async Task KillWatcher_sigterms_first_so_the_watcher_can_run_its_drain() {
        if (OperatingSystem.IsWindows()) return; // no SIGTERM semantics on Windows

        var (key, _, pidFile) = SetUpWatcher();
        var readyMarker = Path.Combine(TempDir, $"{key}.trap-ready");
        var psi = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"trap 'exit 42' TERM; echo ready > '{readyMarker}'; while true; do sleep 0.2; done");
        using var trapped = Process.Start(psi)!;

        try {
            // Signalling before the trap is installed would hit bash's default disposition (143).
            var readyDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(readyMarker) && DateTimeOffset.UtcNow < readyDeadline) await Task.Delay(20);
            await Assert.That(File.Exists(readyMarker)).IsTrue();

            await File.WriteAllTextAsync(pidFile, $"{trapped.Id}\n{ProcessStartToken.ForPid(trapped.Id)}");

            var wasRunning = await Watchers.KillWatcher(key);

            await Assert.That(wasRunning).IsTrue();
            await trapped.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            await Assert.That(trapped.ExitCode).IsEqualTo(42);
        } finally {
            try { if (!trapped.HasExited) trapped.Kill(); } catch { /* best effort */ }
        }
    }

    // "Ambiguity never kills": a pid file whose token conclusively belongs to a different process
    // incarnation (the watcher died, the OS recycled its pid) must be swept without signalling
    // the unrelated process now holding that pid.
    [Test]
    public async Task KillWatcher_spares_a_recycled_pid_and_sweeps_the_stale_file() {
        if (OperatingSystem.IsWindows()) return; // sacrificial sleeper is unix-only

        var (key, _, pidFile) = SetUpWatcher();
        using var bystander = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false })!;

        try {
            // Same token scheme, provably different incarnation: the current test process's token.
            await File.WriteAllTextAsync(pidFile, $"{bystander.Id}\n{ProcessStartToken.ForCurrent()}");

            var wasRunning = await Watchers.KillWatcher(key);

            await Assert.That(wasRunning).IsFalse();
            await Assert.That(bystander.HasExited).IsFalse();
            await Assert.That(File.Exists(pidFile)).IsFalse();
        } finally {
            try { if (!bystander.HasExited) bystander.Kill(); } catch { /* best effort */ }
        }
    }

    // A watcher retires its own pid file on graceful exit (self-reap, StopWatcher, parent-exit) —
    // but only its own: a successor incarnation's file must be left alone.
    [Test]
    public async Task RemoveOwnPidFile_removes_only_this_incarnations_file() {
        var (ownKey, _, ownPidFile)             = SetUpWatcher();
        var (successorKey, _, successorPidFile) = SetUpWatcher();

        await File.WriteAllTextAsync(ownPidFile, $"{Environment.ProcessId}\ntk:1");
        await File.WriteAllTextAsync(successorPidFile, $"{Environment.ProcessId + 1}\ntk:2");

        Watchers.RemoveOwnPidFile(ownKey, Environment.ProcessId);
        Watchers.RemoveOwnPidFile(successorKey, Environment.ProcessId);

        await Assert.That(File.Exists(ownPidFile)).IsFalse();
        await Assert.That(File.Exists(successorPidFile)).IsTrue();
    }

    // #550: a session watcher must stop the child watchers it spawned on its own way out — they
    // have no parent-pid watchdog and the server's StopWatcher only reaches the session watcher's
    // connection, so the parent's teardown is the only thing that knows they exist. One live and
    // one already-dead child in the same batch: both pid files must be gone afterwards (the dead
    // entry is swept, never an error that aborts the batch).
    [Test]
    public async Task KillWatchers_stops_every_tracked_child_and_clears_their_pid_files() {
        var (liveKey, liveTranscript, livePidFile) = SetUpWatcher();
        var (deadKey, _, deadPidFile) = SetUpWatcher();

        await Watchers.SpawnWatcher(liveKey, liveTranscript, agentId: null);
        await AssertPidFileValid(livePidFile);

        // A pid that cannot belong to a live process — exercises the already-exited sweep arm.
        await File.WriteAllTextAsync(deadPidFile, "99999999");

        await Watchers.KillWatchers([liveKey, deadKey]);

        await Assert.That(File.Exists(livePidFile)).IsFalse();
        await Assert.That(File.Exists(deadPidFile)).IsFalse();
    }
}
