using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Verifies the permission-request hook self-heals the transcript watcher
/// (<see cref="PermissionRequestCommand.TryEnsureWatcher"/>): a frequently-firing
/// mid-session recovery point that re-spawns a dead/never-started watcher so the
/// session does not stay stuck "active". Mirrors <see cref="WatcherLifecycleTests"/> —
/// uses <c>KCAP_WATCHER_DIR</c> and deliberately does NOT capture Console, since
/// spawning the watcher's child process corrupts TUnit's Console capture.
/// </summary>
[NotInParallel]
public class PermissionRequestWatcherSelfHealTests {
    static readonly TempDir Tmp = new();
    static readonly TempDir Transcripts = new();
    static string TempDir => Tmp.Path;

    // KCAP_WATCHER_DIR is pinned below and wins over the root, so nothing here reads it — it points
    // at the same temp directory anyway, so a lapse in that precedence cannot escape into the
    // developer's own config.
    static readonly ConfigRoot Root = new(Tmp.Path);

    // One manager over that root and the URL these spawns target — the two values production hands
    // it, so a watcher here can never point at a second server.
    static readonly WatcherManager Watchers = new(Root, Resolutions.At("http://localhost:0", Root), new FixedCapacitorHttpClient());

    static string? _previousWatcherDir;

    [Before(Class)]
    public static void SetUp() {
        _previousWatcherDir = Environment.GetEnvironmentVariable("KCAP_WATCHER_DIR");
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", TempDir);
    }

    [After(Class)]
    public static void TearDown() {
        // Restore any preexisting value rather than clobbering to null, so a test process
        // started with KCAP_WATCHER_DIR set isn't left altered for later test classes.
        Environment.SetEnvironmentVariable("KCAP_WATCHER_DIR", _previousWatcherDir);
        Tmp.Dispose();
        Transcripts.Dispose();
    }

    static (string sessionId, string transcriptPath, string pidFile) NewSession() {
        var sessionId      = $"permreq{Guid.NewGuid():N}";
        var transcriptPath = Transcripts.CreateFile($"{sessionId}.jsonl");

        return (sessionId, transcriptPath, Path.Combine(TempDir, $"{sessionId}.pid"));
    }

    [Test]
    public async Task SpawnsWatcher_WhenMainSessionTranscriptPresent() {
        var (sessionId, transcriptPath, pidFile) = NewSession();

        var node = new JsonObject {
            ["transcript_path"] = transcriptPath,
            ["cwd"]             = "/tmp/test"
        };

        await new PermissionRequestCommand(Root, Resolutions.At("http://localhost:0", Root), new FixedCapacitorHttpClient()).TryEnsureWatcher(sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsTrue();
        var lines = await File.ReadAllLinesAsync(pidFile);
        await Assert.That(int.TryParse(lines[0].Trim(), out _)).IsTrue();

        await Watchers.KillWatcher(sessionId);
    }

    [Test]
    public async Task SkipsWatcher_WhenAgentIdPresent() {
        // A present agent_id means a subagent tool call; its watcher uses a distinct
        // key + transcript and is ensured at subagent-start, so self-heal must not spawn here.
        var (sessionId, transcriptPath, pidFile) = NewSession();

        var node = new JsonObject {
            ["transcript_path"] = transcriptPath,
            ["agent_id"]        = "agent-123"
        };

        await new PermissionRequestCommand(Root, Resolutions.At("http://localhost:0", Root), new FixedCapacitorHttpClient()).TryEnsureWatcher(sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsFalse();

        await Watchers.KillWatcher(sessionId);
    }

    [Test]
    public async Task SkipsWatcher_WhenTranscriptPathMissing() {
        var (sessionId, _, pidFile) = NewSession();

        var node = new JsonObject {
            ["cwd"] = "/tmp/test"
        };

        await new PermissionRequestCommand(Root, Resolutions.At("http://localhost:0", Root), new FixedCapacitorHttpClient()).TryEnsureWatcher(sessionId, node);

        await Assert.That(File.Exists(pidFile)).IsFalse();

        await Watchers.KillWatcher(sessionId);
    }
}
