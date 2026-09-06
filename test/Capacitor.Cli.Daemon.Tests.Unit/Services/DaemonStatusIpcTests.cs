using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of the StatusSubscribe/DaemonStatus frame pair over a REAL Unix-domain
/// socket — the same <c>LocalControlServer.HandleConnectionAsync</c> routing switch a real
/// `kcap` client talks to. The harness mirrors <see cref="LocalControlHelloTests"/> (temp
/// per-test daemons directory, socket-file poll, Windows guard). Beyond the single wiring test,
/// this file pins the debounce/pulse/convergence behavior matrix: every mutation triggers a
/// re-push, a pulse burst coalesces into one trailing snapshot, two subscribers converge
/// independently via their own cursors, a mutation landing exactly at the snapshot/cursor
/// boundary still converges, subscriber EOF reaps the handler promptly, concurrent mutations
/// never produce an internally-inconsistent payload, and a shutting-down daemon just closes the
/// connection. The observable guarantee throughout is CONVERGENCE, not per-generation delivery —
/// debounce is free to collapse a burst into fewer pushes than pulses.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class DaemonStatusIpcTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40
            ) => throw new NotSupportedException("DaemonStatusIpcTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    /// <summary>
    /// Fake <see cref="Stream"/> for <see cref="HandleSubscribeAsync_absorbs_a_write_side_disconnect_without_faulting"/>:
    /// <see cref="ReadAsync"/> never completes (until cancelled) — mirroring a subscriber that
    /// vanished without an explicit EOF — so the WRITE path has to be what surfaces the
    /// disconnect. Once <see cref="ThrowOnNextWrite"/> is armed, the next write throws exactly
    /// what a broken pipe against a real socket throws: an <see cref="IOException"/> wrapping a
    /// <see cref="SocketException"/>.
    /// </summary>
    sealed class VanishedSubscriberStream : Stream {
        public volatile bool ThrowOnNextWrite;

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            await Task.Delay(Timeout.Infinite, cancellationToken);

            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            if (ThrowOnNextWrite)
                throw new IOException("Broken pipe", new SocketException((int)SocketError.ConnectionReset));

            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override void Flush()                                            { }
        public override int  Read(byte[] buffer, int offset, int count)         => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)               => throw new NotSupportedException();
        public override void SetLength(long value)                              => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)        => throw new NotSupportedException();
    }

    /// <summary>Bare orchestrator + notifier + DaemonStatusIpc, no LocalControlServer/socket —
    /// mirrors <c>AgentStatusSnapshotTests.Build</c> — for the pure write-path exception test
    /// below, which drives <see cref="DaemonStatusIpc.HandleSubscribeAsync"/> directly against a
    /// fake stream instead of a real connection.</summary>
    (AgentOrchestrator Orchestrator, DaemonStatusIpc StatusIpc, TempDaemonStore Daemons) BuildBareStatusIpc(string name) {
        var daemons   = new TempDaemonStore();
        var stateRoot = daemons.Store.StateDirectory(name);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = name,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };

        var notifier         = new DaemonStatusNotifier();
        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, tokens, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, notifier);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, notifier) {
            Debounce = TimeSpan.FromMilliseconds(1),
        };

        return (orchestrator, statusIpc, daemons);
    }

    sealed record Harness(
        LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection,
        DaemonConfig Config, string SockPath, DaemonStatusNotifier Notifier, DaemonStatusIpc StatusIpc,
        TempDaemonStore Daemons) {
        int _serverStopped;

        /// Re-entrant-safe: the shutdown test stops the server itself (to observe the
        /// subscription close), and RunAsync's finally stops it again unconditionally
        /// afterward. The guard makes the second call a no-op instead of a crash.
        internal async Task StopServerOnceAsync(CancellationToken ct) {
            if (Interlocked.Exchange(ref _serverStopped, 1) != 0) return;
            await Server.StopAsync(ct);
        }
    }

    async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var daemons   = new TempDaemonStore();
        var stateRoot = daemons.Store.StateDirectory(daemonName);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };
        var consentIpc  = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var notifier   = new DaemonStatusNotifier();
        var tokens     = AuthFixtures.NewTokenStore(Config.Root);
        var connection = new ServerConnection(
            config, tokens, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, notifier);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier);

        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, notifier) {
            Debounce = TimeSpan.FromMilliseconds(25), // fast tests; 250ms is the production default
        };

        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var restart = RestartCoordinator.ForTest(daemons.Store, daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = daemons.Store.SocketPath(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(server, orchestrator, connection, config, sockPath, notifier, statusIpc, daemons);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.StopServerOnceAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
        h.Daemons.Dispose();
    }

    /// Wraps a test body with the harness lifecycle, mirroring LocalControlHelloTests's RunAsync. The harness owns
    /// its own daemons directory, so nothing here is shared between tests; each [Test] still
    /// carries its own Windows guard, which must be visible on the test method itself.
    async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<DaemonStatusDto> ReadStatusAsync(Stream s, CancellationToken ct) {
        var f = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(f!.Type).IsEqualTo(FrameType.DaemonStatus);
        return JsonSerializer.Deserialize(f.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
    }

    /// Reads one frame or returns null when none arrives within the window — for asserting
    /// "no further push" without hanging the suite.
    static async Task<LocalFrame?> ReadOrNullAsync(Stream s, TimeSpan window, CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try { return await FrameCodec.ReadAsync(s, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// Internal-consistency invariant every DaemonStatus payload must satisfy, regardless of
    /// how many mutations landed between pushes: ActiveAgents is derived from the SAME
    /// materialized Agents array it ships with, so the two can never disagree.
    static async Task AssertConsistent(DaemonStatusDto dto) {
        var expectedActive = dto.Agents.Count(a => a.Status is "Starting" or "Running");
        await Assert.That(dto.Daemon.ActiveAgents).IsEqualTo(expectedActive);
    }

    /// <summary>
    /// Pins all four <see cref="HubConnectionState"/> spellings through
    /// <see cref="DaemonStatusIpc.ConnectionText"/> — the end-to-end tests below only ever
    /// observe "disconnected" (no live hub in tests), so the other three arms
    /// (connected/connecting/reconnecting) were untested through this mapping.
    /// </summary>
    [Test]
    [Arguments(HubConnectionState.Connected,    "connected")]
    [Arguments(HubConnectionState.Connecting,   "connecting")]
    [Arguments(HubConnectionState.Reconnecting, "reconnecting")]
    [Arguments(HubConnectionState.Disconnected, "disconnected")]
    public async Task ConnectionText_maps_every_HubConnectionState_spelling(HubConnectionState state, string expected) {
        await Assert.That(DaemonStatusIpc.ConnectionText(state)).IsEqualTo(expected);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Subscribe_pushes_an_immediate_snapshot_with_daemon_block_and_agents() {
        await RunAsync("st-a", async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1", kind: LaunchKind.ReviewFlow,
                flowRunId: "flow_1", flowRole: "reviewer", requester: "github:12345");
            h.Orchestrator.SeedAgentForTest("s2", status: "Starting");

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            var dto = await ReadStatusAsync(s, ct);
            await Assert.That(dto.Daemon.Name).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Daemon.Version).IsNotEmpty();
            await Assert.That(dto.Daemon.ServerUrl).IsEqualTo(h.Config.ServerUrl);
            await Assert.That(dto.Daemon.Connection).IsEqualTo("disconnected"); // no live hub in tests
            await Assert.That(dto.Daemon.MaxAgents).IsEqualTo(h.Config.MaxConcurrentAgents);
            await Assert.That(dto.Daemon.ActiveAgents).IsEqualTo(2); // Running + Starting
            await Assert.That(dto.Agents.Count).IsEqualTo(2);
            var r1 = dto.Agents.Single(a => a.Id == "s1");
            await Assert.That(r1.Kind).IsEqualTo("review-flow");
            await Assert.That(r1.Requester).IsEqualTo("github:12345");
        });
    }

    [Test, ExcludeOn(OS.Windows)] // pid/instance_id identity on the daemon block, first snapshot
    public async Task First_snapshot_carries_pid_and_instance_id() {
        await RunAsync("st-id", async (h, ct) => {
            h.Config.InstanceId = "inst-status-1";

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            var dto = await ReadStatusAsync(s, ct);
            await Assert.That(dto.Daemon.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(dto.Daemon.InstanceId).IsEqualTo("inst-status-1");
        });
    }

    [Test, ExcludeOn(OS.Windows)] // add / status-change / removal each trigger a re-push
    public async Task Each_mutation_triggers_a_re_push() {
        await RunAsync("st-b", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            var initial = await ReadStatusAsync(s, ct);
            await AssertConsistent(initial);
            await Assert.That(initial.Agents).IsEmpty();

            var m1 = h.Orchestrator.SeedAgentForTest("m1");
            var afterAdd = await ReadStatusAsync(s, ct);
            await AssertConsistent(afterAdd);
            await Assert.That(afterAdd.Agents.Select(a => a.Id)).Contains("m1");

            h.Orchestrator.SetAgentStatus(m1, "Completed");
            var afterStatus = await ReadStatusAsync(s, ct);
            await AssertConsistent(afterStatus);
            await Assert.That(afterStatus.Agents.Single(a => a.Id == "m1").Status).IsEqualTo("Completed");
            await Assert.That(afterStatus.Daemon.ActiveAgents).IsEqualTo(0); // Completed isn't active

            h.Orchestrator.UnpublishAgent("m1");
            var afterRemove = await ReadStatusAsync(s, ct);
            await AssertConsistent(afterRemove);
            await Assert.That(afterRemove.Agents).IsEmpty();
        });
    }

    [Test, ExcludeOn(OS.Windows)] // burst coalescing: at most one trailing snapshot after the in-flight push
    public async Task A_pulse_burst_coalesces_into_one_trailing_snapshot() {
        await RunAsync("st-c", async (h, ct) => {
            // Wide enough that 5 back-to-back synchronous pulses land inside one debounce window.
            h.StatusIpc.Debounce = TimeSpan.FromMilliseconds(150);

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(s, ct); // drain immediate snapshot

            for (var i = 0; i < 5; i++) h.Orchestrator.SeedAgentForTest($"b{i}");

            var converged = await ReadStatusAsync(s, ct);
            await AssertConsistent(converged);
            var ids = converged.Agents.Select(a => a.Id).ToHashSet();
            for (var i = 0; i < 5; i++) await Assert.That(ids).Contains($"b{i}");

            // No second trailing push for the same burst.
            await Assert.That(await ReadOrNullAsync(s, TimeSpan.FromMilliseconds(400), ct)).IsNull();
        });
    }

    [Test, ExcludeOn(OS.Windows)] // two-subscriber convergence + slow subscriber doesn't stall the fast one
    public async Task Both_subscribers_converge_after_a_change_and_a_slow_one_stalls_only_itself() {
        await RunAsync("st-d", async (h, ct) => {
            await using var a = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(a, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(a, ct); // drain A's immediate snapshot

            await using var b = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(b, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(b, ct); // drain B's immediate snapshot

            h.Orchestrator.SeedAgentForTest("both");

            // A reads promptly, converging on the mutation.
            DaemonStatusDto dtoA;
            while (true) {
                dtoA = await ReadStatusAsync(a, ct);
                await AssertConsistent(dtoA);
                if (dtoA.Agents.Any(x => x.Id == "both")) break;
            }

            // B never read until now — its cursor still converges to at least this generation
            // (buffered or next frame), and reading it does not disturb A above.
            DaemonStatusDto dtoB;
            while (true) {
                dtoB = await ReadStatusAsync(b, ct);
                await AssertConsistent(dtoB);
                if (dtoB.Agents.Any(x => x.Id == "both")) break;
            }
        });
    }

    [Test, ExcludeOn(OS.Windows)] // cursor-before-snapshot + pulse-after-mutation regressions, deterministic via the hook
    public async Task A_mutation_at_the_snapshot_boundary_still_converges() {
        await RunAsync("st-e", async (h, ct) => {
            // BEFORE subscribing: land a mutation+pulse exactly between snapshot and wait,
            // deterministically, via the self-clearing test hook.
            h.StatusIpc.AfterSnapshotForTest = () => {
                h.StatusIpc.AfterSnapshotForTest = null; // fire once
                h.Orchestrator.SeedAgentForTest("boundary"); // mutation + pulse land here
            };

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            // The snapshot was taken (and the hook fired) BEFORE the mutation, so the first
            // frame must not contain it — pinning cursor-before-snapshot.
            var first = await ReadStatusAsync(s, ct);
            await AssertConsistent(first);
            await Assert.That(first.Agents.Any(a => a.Id == "boundary")).IsFalse();

            // No further external pulses: this converges purely because the hook's seed already
            // advanced the generation past the pre-snapshot cursor — WaitBeyondAsync(seen)
            // completes synchronously and the loop immediately re-snapshots and pushes.
            var second = await ReadStatusAsync(s, ct);
            await AssertConsistent(second);
            await Assert.That(second.Agents.Any(a => a.Id == "boundary")).IsTrue();
        });
    }

    [Test, ExcludeOn(OS.Windows)] // subscriber EOF reaps the handler promptly
    public async Task Subscriber_eof_reaps_the_handler_promptly() {
        await RunAsync("st-f", async (h, ct) => {
            var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(s, ct); // drain immediate snapshot
            await Assert.That(h.StatusIpc.ActiveSubscribersForTest).IsEqualTo(1);

            await s.DisposeAsync(); // subscriber vanishes

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (h.StatusIpc.ActiveSubscribersForTest != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, ct);
            await Assert.That(h.StatusIpc.ActiveSubscribersForTest).IsEqualTo(0);
        });
    }

    /// <summary>
    /// Qodo (production): a client vanishing mid-push makes <c>FrameCodec.WriteAsync</c> throw
    /// <see cref="IOException"/>/<see cref="SocketException"/>, which only
    /// <see cref="OperationCanceledException"/> was treated as normal termination for — the
    /// exception bubbled out of <c>HandleSubscribeAsync</c> to <c>LocalControlServer</c>'s
    /// generic catch and logged "Local control connection faulted" at Warning for a routine
    /// disconnect. Drives <see cref="DaemonStatusIpc.HandleSubscribeAsync"/> directly against a
    /// fake stream (no socket/LocalControlServer involved) whose first write (the immediate
    /// snapshot) succeeds and whose second write throws — deterministic, no OS-level raciness
    /// between the read-side EOF watcher and the write path. Before the fix, this IOException
    /// escaped uncaught; after it, <c>HandleSubscribeAsync</c> returns cleanly.
    /// </summary>
    [Test]
    public async Task HandleSubscribeAsync_absorbs_a_write_side_disconnect_without_faulting() {
        var (orchestrator, statusIpc, daemons) = BuildBareStatusIpc("status-ipc-throw-test");
        try {
            var stream           = new VanishedSubscriberStream();
            var firstSnapshotSeen = false;
            statusIpc.AfterSnapshotForTest = () => {
                if (firstSnapshotSeen) stream.ThrowOnNextWrite = true; // arm for the SECOND push only
                firstSnapshotSeen = true;
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try {
                var handleTask = statusIpc.HandleSubscribeAsync(stream, cts.Token);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!firstSnapshotSeen && DateTime.UtcNow < deadline) await Task.Delay(5);
                await Assert.That(firstSnapshotSeen).IsTrue();

                // Triggers the second push, whose write hits the now-armed stream and throws —
                // exactly what a real vanished subscriber does mid-push.
                orchestrator.SeedAgentForTest("throw-trigger");

                // Must return cleanly within the deadline: before the fix this IOException would
                // propagate out of HandleSubscribeAsync uncaught (only OCE was absorbed).
                await handleTask.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(statusIpc.ActiveSubscribersForTest).IsEqualTo(0);
            } finally {
                cts.Cancel(); // unblock the fake stream's indefinitely-blocked ReadAsync promptly
            }
        } finally {
            await orchestrator.DisposeAsync();
            daemons.Dispose();
        }
    }

    [Test, ExcludeOn(OS.Windows)] // snapshot stress: no exceptions, every payload internally consistent, converges
    public async Task Concurrent_mutations_never_produce_an_inconsistent_payload() {
        await RunAsync("st-g", async (h, ct) => {
            h.StatusIpc.Debounce = TimeSpan.FromMilliseconds(25);

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(s, ct); // drain immediate snapshot

            // Known ahead of time from the fixed mutation pattern below — no mutable state
            // shared between the mutator loop and the reader task besides the notifier/registry.
            var finalIds = Enumerable.Range(0, 50).Where(i => i % 3 != 0).Select(i => $"x{i}").ToHashSet();
            var converged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var reader = Task.Run(async () => {
                while (!converged.Task.IsCompleted) {
                    var frame = await ReadOrNullAsync(s, TimeSpan.FromSeconds(5), ct);
                    if (frame is null) return; // nothing else pins this iteration; outer wait decides pass/fail
                    await Assert.That(frame.Type).IsEqualTo(FrameType.DaemonStatus);
                    var dto = JsonSerializer.Deserialize(frame.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
                    await AssertConsistent(dto);
                    if (dto.Agents.Select(a => a.Id).ToHashSet().SetEquals(finalIds)) converged.TrySetResult();
                }
            }, ct);

            // 50 iterations of seed(Starting) -> Running, then either settle (2/3) or
            // complete+unpublish (1/3) — a mix of add/status-change/removal under load.
            for (var i = 0; i < 50; i++) {
                var id = $"x{i}";
                var agent = h.Orchestrator.SeedAgentForTest(id, status: "Starting");
                h.Orchestrator.SetAgentStatus(agent, "Running");
                if (i % 3 == 0) {
                    h.Orchestrator.SetAgentStatus(agent, "Completed");
                    h.Orchestrator.UnpublishAgent(id);
                }
            }

            // Convergence, not per-generation delivery: the final registry state must show up
            // in SOME payload within a generous deadline, however many pulses got coalesced.
            await converged.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await reader; // propagate any per-payload assertion failure from inside the loop
        });
    }

    [Test, ExcludeOn(OS.Windows)] // §5: StatusSubscribe on a shutting-down daemon — the connection just closes
    public async Task Daemon_shutdown_closes_the_subscription() {
        await RunAsync("st-h", async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
            await ReadStatusAsync(s, ct); // drain immediate snapshot

            // Stop the daemon's local control server directly (RunAsync's finally will try to
            // stop it again — StopServerOnceAsync's guard makes that a no-op, not a crash).
            await h.StopServerOnceAsync(ct);

            // The peer closes the connection: a clean EOF (null) or an abrupt-close read error —
            // either is "the connection just closes"; never another DaemonStatus frame.
            try {
                var frame = await FrameCodec.ReadAsync(s, ct);
                await Assert.That(frame).IsNull();
            } catch (Exception ex) when (ex is IOException or SocketException) {
                // an abrupt close surfacing as a read error is an equally valid outcome
            }
        });
    }
}
