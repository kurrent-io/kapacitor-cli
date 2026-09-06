using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// GATED live end-to-end test that drives the REAL <see cref="AcpHostedAgentRuntimeFactory"/>
/// against a REAL <c>cursor-agent acp</c> child process (no <c>FakeAcpAgent</c>, no in-memory pipe —
/// see <see cref="AcpHostedAgentRuntimeFactoryTests"/> for that coverage of the same code path) to
/// prove that model selection (<c>session/set_config_option</c>, sent from
/// <c>ConfigOptionModelSelector.TrySelectAsync</c> before the first turn) and a real
/// <c>session/prompt</c> turn actually work end-to-end against the live Cursor CLI at the daemon
/// level: real process spawn (<see cref="AcpHostedAgentRuntimeFactory"/>'s default
/// <c>connectionSource</c>, i.e. <c>connectionSource: null</c>) → real stdio JSON-RPC → real
/// <c>initialize</c>/<c>session/new</c>/<c>session/set_config_option</c>/<c>session/prompt</c>
/// handshake → real <c>session/update</c> notifications reduced by
/// <see cref="AcpHostedAgentRuntime"/> into <see cref="AcpSessionUpdate"/>.
///
/// <b>Gated</b> behind <c>KCAP_ACP_LIVE=1</c> so CI (no <c>cursor-agent</c> binary, no Cursor
/// account) never runs this, and no ordinary local test run silently spends a real Cursor turn.
/// Requires: <c>cursor-agent</c> on PATH, authenticated, Team-tier (or higher) subscription — see
/// <c>docs/ai-688-cursor-prototype-findings.md</c>'s "Free tier" plan-limit gotcha (on Free, every
/// model just returns "Upgrade your plan to continue" and no real turn runs, which would make this
/// test fail even though the daemon code path is correct).
/// </summary>
public class AcpHostedAgentRuntimeFactoryLiveTests {
    [TempHome] public required TempHome Home { get; init; }

    const string LiveGateEnvVar = "KCAP_ACP_LIVE";

    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan LiveTurnTimeout  = TimeSpan.FromSeconds(40);

    /// <summary>
    /// A real (non-connecting) <see cref="ServerConnection"/> subclass, matching this project's
    /// established <c>CaptureServerConnection</c>-style pattern (see
    /// <see cref="AcpHostedAgentRuntimeFactoryTests"/>) — not a mocking framework, since
    /// <see cref="ServerConnection"/> is not an interface. The HELLO-only prompt this test sends
    /// exercises no tool calls, so <see cref="RequestAcpInteractionAsync"/> is not expected to fire;
    /// it is still wired to a well-formed "cancel" response (rather than throwing) so an unexpected
    /// inbound request degrades gracefully instead of crashing the whole run.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance
        ) {
        public bool RequestAcpInteractionAsyncCalled { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            RequestAcpInteractionAsyncCalled = true;
            Console.WriteLine($"[ai-688-live] UNEXPECTED RequestAcpInteractionAsync: kind={request.Kind} tool={request.ToolName}");

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }

    [Test]
    public async Task StartAsync_AgainstRealCursorAgentAcp_SelectsModelAndProducesHelloTurn() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' process — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Team-tier `cursor-agent` on PATH).");

        using var worktreeDir = new TempDir();

        // A real (console) logger factory rather than NullLoggerFactory — AcpHostedAgentRuntime logs
        // a warning if the requested model can't be resolved against session/new's availableModels,
        // or if session/set_config_option itself fails (both non-fatal — see
        // ConfigOptionModelSelector.TrySelectAsync's remarks) — so a real logger is the only way this test can surface those failures instead
        // of silently swallowing them.
        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        var connection = new CaptureServerConnection();

        // config.CursorModel stays at its DaemonConfig default ("claude-sonnet-4-5") — ctx.Model
        // below is "" so AcpHostedAgentRuntimeFactory.ResolveRequestedModel falls back to it,
        // proving the daemon-wide default reaches a real cursor-agent process, not just the fake.
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig(), // CursorPath="cursor-agent", CursorModel="claude-sonnet-4-5"
            loggerFactory: liveLoggerFactory,
            connection: connection,
            connectionSource: null // real cursor-agent acp spawn — the production path
        );

        var ctx = new RuntimeStartContext(
            AgentId: "ai-688-live-gap1",
            Vendor: "cursor",
            SourceRepoPath: worktreeDir.Path,
            Worktree: WorktreeInfo.Borrowed(worktreeDir.Path),
            Prompt: "Respond with only the single word HELLO and nothing else.",
            Model: "", // falls back to DaemonConfig.CursorModel
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 80,
            Rows: 24,
            ServerUrl: null,
            DaemonBridgeUrl: null,
            CapacitorPath: "/usr/local/bin/kcap");

        using var startCts = new CancellationTokenSource();

        var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);
        var runtime = (AcpHostedAgentRuntime)started.Runtime;

        try {
            var result = await CollectUntilHelloAsync(runtime.Updates, LiveTurnTimeout);

            Console.WriteLine($"[ai-688-live] observed {result.Updates.Count} session/update notification(s):");
            foreach (var update in result.Updates)
                Console.WriteLine($"[ai-688-live]   kind={update.Kind} text={update.Text} raw={update.Raw?.GetRawText()}");
            Console.WriteLine($"[ai-688-live] concatenated agent_message_chunk text: \"{result.ConcatenatedText}\"");

            await Assert.That(result.SawHello).IsTrue();
        } finally {
            startCts.Cancel();
            await runtime.DisposeAsync();
        }
    }

    // The three content classes a borrowed reviewer has to be able to see, and which a stale
    // committed base would silently withhold. Named constants so the prompt, the fixture and the
    // assertions cannot drift apart.
    const string BranchOnlySentinel      = "BRANCH-ONLY-SENTINEL-a1b2c3";
    const string TrackedModifiedSentinel = "TRACKED-MODIFIED-SENTINEL-d4e5f6";
    const string UntrackedSentinel       = "UNTRACKED-SENTINEL-g7h8i9";

    /// <summary>Borrowed-snapshot certification probe: Cursor runs in a daemon-owned copy of the
    /// authorized checkout. Even an explicit mutation changes only that disposable snapshot; the
    /// source checkout remains byte-identical and the result MCP completes with zero interaction.
    ///
    /// <para><b>Extended with three read sentinels</b> — branch-only, tracked-modified and untracked
    /// content — because a single COMMITTED sentinel would pass just as well for a reviewer working
    /// from a stale committed base, proving nothing about borrowed-snapshot visibility. All three must
    /// come back <b>through the result channel</b> — read by the reviewer, not by the test process. A
    /// test-process read proves the snapshot builder works and says nothing about whether a reviewer
    /// can see it.</para></summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task ReviewFlow_AgainstRealCursorAgentAcp_CallsResultMcp_WithZeroInteractionRequests() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' process — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Cursor subscription).");
        Skip.When(OperatingSystem.IsWindows(), "The gated probe's tiny stdio MCP fixture is a POSIX executable script.");

        // Holds only what must live outside the borrowed repository: the fake binary, its marker,
        // and the snapshot root WorktreeManager builds into.
        using var rootDirTemp = new TempDir();
        var markerPath  = rootDirTemp.PathTo("result-called");
        var mcpPath     = rootDirTemp.PathTo("fake-kcap");

        using var source = GitRepo.Create("borrowed-source");
        var protectedPath = source.CreateFile("protected.txt", "ORIGINAL\n");
        source.CreateFile("tracked_modified.txt", "BASE-ORIGINAL\n");

        source.Add("protected.txt", "tracked_modified.txt");
        source.Commit("initial");

        // Branch-only: committed, but only on a branch the daemon's own checkout has never seen.
        source.Checkout("feature", create: true);
        source.CreateFile("branch_only.txt", BranchOnlySentinel + "\n");
        source.Add("branch_only.txt");
        source.Commit("branch-only commit");
        // Tracked-but-dirty, and never-added: neither is reachable from any commit.
        source.CreateFile("tracked_modified.txt", TrackedModifiedSentinel + "\n");
        source.CreateFile("untracked.txt", UntrackedSentinel + "\n");

        File.WriteAllText(mcpPath, FakeFlowResultMcpScript);
        File.SetUnixFileMode(mcpPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        var connection = new CaptureServerConnection();
        var config = new DaemonConfig {
            WorktreeRoot = rootDirTemp.PathTo("snapshots"),
            Home = Home,
            DebugFrames = true
        };
        var manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(
            source.Path, "live-review", CancellationToken.None);
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: config,
            loggerFactory: liveLoggerFactory,
            connection: connection,
            connectionSource: null);
        var ctx = new RuntimeStartContext(
            AgentId: markerPath,
            Vendor: "cursor",
            SourceRepoPath: source.Path,
            Worktree: snapshot,
            Prompt: "Read all four of these files: protected.txt, branch_only.txt, "
                  + "tracked_modified.txt, untracked.txt. Try to replace protected.txt with "
                  + "MUTATED using any file-edit or shell tool available, but do not work around "
                  + "unavailable tools. Then call submit_review_result exactly once with verdict "
                  + "CLEAN and put the exact contents of branch_only.txt, tracked_modified.txt and "
                  + "untracked.txt into summary, separated by spaces.",
            Model: "",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: true,
            Review: null,
            Cols: 80,
            Rows: 24,
            ServerUrl: "http://kcap.test",
            DaemonBridgeUrl: null,
            CapacitorPath: mcpPath,
            Work: WorkLocation.OwnedWorktree,
            IsBorrowedSnapshot: true);

        using var startCts = new CancellationTokenSource();
        var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);
        var runtime = (AcpHostedAgentRuntime)started.Runtime;

        try {
            var deadline = DateTime.UtcNow + LiveTurnTimeout;
            while (!File.Exists(markerPath) && !runtime.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            await Assert.That(File.Exists(markerPath)).IsTrue();
            await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();
            await Assert.That(runtime.HasExited).IsFalse();
            await Assert.That(File.ReadAllText(protectedPath)).IsEqualTo("ORIGINAL\n");
            var snapshotPath = Path.Combine(snapshot.Path, "protected.txt");
            await Assert.That(File.ReadAllText(snapshotPath)).StartsWith("MUTATED");

            // The read probe: all three classes came back THROUGH THE RESULT CHANNEL, so a
            // reviewer read them. Asserted individually — a combined "contains all three" check
            // would let a partially-blind reviewer pass on the strength of the one class a stale
            // committed base would also have supplied.
            var submitted = File.ReadAllText(markerPath);
            Console.WriteLine($"[borrowed-read-probe] result channel payload: {submitted}");
            await Assert.That(submitted).Contains(BranchOnlySentinel)
                .Because("a borrowed reviewer must see commits that exist only on the requester's branch");
            await Assert.That(submitted).Contains(TrackedModifiedSentinel)
                .Because("a borrowed reviewer must see uncommitted modifications to tracked files");
            await Assert.That(submitted).Contains(UntrackedSentinel)
                .Because("a borrowed reviewer must see untracked, non-ignored files");

            // Same process, next round: do not refresh until the prior ACP turn is terminal,
            // then rebuild the complete snapshot generation and require Cursor to observe it.
            await runtime.WaitForTurnIdleAsync(startCts.Token);
            File.WriteAllText(protectedPath, "ROUND2\n");
            await manager.SyncFromSourceAsync(
                source.Path, source.Path, snapshot.Path, [], startCts.Token);
            File.Delete(markerPath);
            await runtime.SendUserInputAndWaitForWriteAsync(
                "Read protected.txt and call submit_review_result exactly once with verdict CLEAN and put its exact contents in summary. Do not modify files.");

            deadline = DateTime.UtcNow + LiveTurnTimeout;
            while (!File.Exists(markerPath) && !runtime.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            await runtime.WaitForTurnIdleAsync(startCts.Token).WaitAsync(LiveTurnTimeout);
            await Assert.That(File.Exists(markerPath)).IsTrue();
            await Assert.That(File.ReadAllText(markerPath)).Contains("ROUND2");
            await Assert.That(File.ReadAllText(protectedPath)).IsEqualTo("ROUND2\n");
        } finally {
            startCts.Cancel();
            await runtime.DisposeAsync();
            await WorktreeManager.RemoveAsync(snapshot);
        }
    }

    internal const string FakeFlowResultMcpScript = """
#!/usr/bin/env python3
import json
import os
import sys

def send(message):
    print(json.dumps(message, separators=(",", ":")), flush=True)

for line in sys.stdin:
    try:
        request = json.loads(line)
        method = request.get("method")
        request_id = request.get("id")
        if request_id is None:
            continue
        if method == "initialize":
            send({"jsonrpc":"2.0","id":request_id,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"live-flow-result","version":"1"}}})
        elif method == "tools/list":
            send({"jsonrpc":"2.0","id":request_id,"result":{"tools":[{"name":"submit_review_result","description":"Submit the final review result","inputSchema":{"type":"object","properties":{"verdict":{"type":"string","enum":["CLEAN","FINDINGS"]},"summary":{"type":"string"},"findings":{"type":"array"}},"required":["verdict","summary"]}}]}})
        elif method == "tools/call":
            with open(os.environ["KCAP_FLOW_AGENT_ID"], "w", encoding="utf-8") as marker:
                marker.write(json.dumps(request.get("params", {})))
            send({"jsonrpc":"2.0","id":request_id,"result":{"content":[{"type":"text","text":"review result accepted"}]}})
        else:
            send({"jsonrpc":"2.0","id":request_id,"error":{"code":-32601,"message":"Method not found"}})
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
""";

    sealed record HelloCollectionResult(bool SawHello, string ConcatenatedText, List<AcpSessionUpdate> Updates);

    /// <summary>
    /// Drains <paramref name="updates"/> until an <c>agent_message_chunk</c> (concatenated across
    /// however many chunks Cursor streams the answer in — the probe observed the reply
    /// arriving split across multiple chunks) contains "HELLO" (case-insensitive), or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    static async Task<HelloCollectionResult> CollectUntilHelloAsync(ChannelReader<AcpSessionUpdate> updates, TimeSpan timeout) {
        var collected   = new List<AcpSessionUpdate>();
        var textBuffer  = new StringBuilder();

        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await updates.WaitToReadAsync(timeoutCts.Token)) {
                while (updates.TryRead(out var update)) {
                    collected.Add(update);

                    if (update.Kind == AcpUpdateKind.AgentMessageChunk && update.Text is { Length: > 0 } text) {
                        textBuffer.Append(text);

                        if (textBuffer.ToString().Contains("HELLO", StringComparison.OrdinalIgnoreCase))
                            return new HelloCollectionResult(true, textBuffer.ToString(), collected);
                    }
                }
            }
        } catch (OperationCanceledException) {
            // Timed out waiting for the turn to produce HELLO — fall through and report what we saw
            // so the caller can log the observed updates either way.
        }

        return new HelloCollectionResult(false, textBuffer.ToString(), collected);
    }
}
