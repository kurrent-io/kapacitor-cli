using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Regression tests for issue #374: the daemon reconnect loop spun forever calling
/// <c>HubConnection.StartAsync</c> against a hub that was not <c>Disconnected</c> — a healthy,
/// registered connection kept logging "cannot be started if it is not in the Disconnected state"
/// at Warning every 30s, and every <c>Closed</c> event minted another concurrent loop. Mirrors the
/// <c>AcpServerConnectionTests</c> approach: no live SignalR transport;
/// <see cref="RetryTestConnection"/> overrides the internal seams (<c>HubState</c>,
/// <c>StartHubAsync</c>, <c>RegisterDaemonAsync</c>, <c>IsReady</c>) and emulates the real
/// <c>HubConnection.StartAsync</c> contract of throwing when not Disconnected.
/// </summary>
public class ConnectWithRetryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class RetryTestConnection(DaemonStatusNotifier? notifier = null) : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance,
        notifier
    ) {
        public HubConnectionState State { get; set; } = HubConnectionState.Disconnected;
        public bool Ready { get; set; }

        public int StartCalls    { get; private set; }
        public int RegisterCalls { get; private set; }

        /// <summary>How many RegisterDaemonAsync calls should throw before they start
        /// succeeding — drives the "register failed after a successful start" path.</summary>
        public int FailRegisterRemaining { get; set; }

        /// <summary>When set, every StartHubAsync call throws this instead of attempting the
        /// normal Disconnected-state contract — drives the initial-start-failure pulse test,
        /// which needs StartAsync to fail regardless of State.</summary>
        public Exception? StartThrows { get; set; }

        /// <summary>When set, StartHubAsync awaits this before returning — lets a test hold the
        /// first loop mid-connect while a concurrent ConnectWithRetryAsync call is issued.</summary>
        public TaskCompletionSource? StartHold { get; set; }

        /// <summary>When set, StopHubAsync returns this instead of completing — lets a test wedge
        /// the transport stop that ForceReconnectAsync must abandon at its cap.</summary>
        public TaskCompletionSource? StopHold { get; set; }

        /// <summary>Fired synchronously on every RegisterDaemonAsync call — lets a test observe
        /// failed attempts deterministically instead of sleeping.</summary>
        public Action? OnRegisterAttempt { get; set; }

        internal override HubConnectionState HubState => State;
        internal override bool               IsReady  => Ready;

        internal override async Task StartHubAsync(CancellationToken ct) {
            StartCalls++;

            if (StartThrows is { } ex) throw ex;

            if (State != HubConnectionState.Disconnected)
                throw new InvalidOperationException("The HubConnection cannot be started if it is not in the Disconnected state.");

            if (StartHold is { } hold) await hold.Task.WaitAsync(ct);

            State = HubConnectionState.Connected;
        }

        internal override Task StopHubAsync(CancellationToken ct) =>
            StopHold?.Task ?? Task.CompletedTask;

        internal override Task RegisterDaemonAsync() {
            RegisterCalls++;
            OnRegisterAttempt?.Invoke();

            // Mirror the real hub-invoke contract: DaemonConnect can only succeed on a
            // Connected transport ("cannot be called if the connection is not active").
            if (State != HubConnectionState.Connected)
                return Task.FromException(new InvalidOperationException("The 'InvokeCoreAsync' method cannot be called if the connection is not active"));

            if (FailRegisterRemaining > 0) {
                FailRegisterRemaining--;

                return Task.FromException(new InvalidOperationException("simulated DaemonConnect failure"));
            }

            Ready = true;

            return Task.CompletedTask;
        }
    }

    static TimeSpan[] FastDelays => [TimeSpan.FromMilliseconds(10)];

    [Test]
    public async Task Live_registered_connection_is_left_alone() {
        var conn = new RetryTestConnection {
            State             = HubConnectionState.Connected,
            Ready             = true,
            ConnectRetryDelays = FastDelays
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await conn.ConnectWithRetryAsync(cts.Token).WaitAsync(HangGuard);

        await Assert.That(conn.StartCalls).IsEqualTo(0);
        await Assert.That(conn.RegisterCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Connected_but_unregistered_hub_is_registered_without_a_start_attempt() {
        var conn = new RetryTestConnection {
            State             = HubConnectionState.Connected,
            Ready             = false,
            ConnectRetryDelays = FastDelays
        };

        await conn.ConnectWithRetryAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(conn.StartCalls).IsEqualTo(0);
        await Assert.That(conn.RegisterCalls).IsEqualTo(1);
        await Assert.That(conn.Ready).IsTrue();
    }

    [Test]
    public async Task Register_failure_after_a_successful_start_retries_without_restarting_the_hub() {
        var conn = new RetryTestConnection {
            State               = HubConnectionState.Disconnected,
            FailRegisterRemaining = 1,
            ConnectRetryDelays   = FastDelays
        };

        await conn.ConnectWithRetryAsync(CancellationToken.None).WaitAsync(HangGuard);

        // One physical start; the failed registration is retried on the already-started hub
        // instead of a doomed second StartAsync (which the fake, like the real HubConnection,
        // rejects when not Disconnected).
        await Assert.That(conn.StartCalls).IsEqualTo(1);
        await Assert.That(conn.RegisterCalls).IsEqualTo(2);
        await Assert.That(conn.Ready).IsTrue();
    }

    [Test]
    public async Task Reconnecting_hub_is_never_started_and_the_loop_exits_once_it_heals() {
        var conn = new RetryTestConnection {
            State             = HubConnectionState.Reconnecting,
            Ready             = false,
            ConnectRetryDelays = FastDelays
        };

        // Registration fails while the fake is Reconnecting (see RegisterDaemonAsync), so the
        // loop must back off and retry. Observe two failed attempts deterministically before
        // emulating SignalR's automatic reconnect healing the transport.
        var twoAttemptsFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.OnRegisterAttempt = () => {
            if (conn.RegisterCalls >= 2) twoAttemptsFailed.TrySetResult();
        };

        var loop = conn.ConnectWithRetryAsync(CancellationToken.None);

        await twoAttemptsFailed.Task.WaitAsync(HangGuard);
        await Assert.That(loop.IsCompleted).IsFalse();

        conn.State = HubConnectionState.Connected;

        await loop.WaitAsync(HangGuard);

        await Assert.That(conn.StartCalls).IsEqualTo(0);
        await Assert.That(conn.Ready).IsTrue();
    }

    [Test]
    public async Task ForceReconnect_abandons_a_wedged_stop_at_the_cap() {
        var conn = new RetryTestConnection {
            ForceStopCap = TimeSpan.FromMilliseconds(50)
        };
        // Never completed: emulates the pinned client's StopAsync hanging on its internal
        // connection-lock wait, which ignores the caller's cancellation token entirely.
        conn.StopHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Must return at the cap instead of hanging the heartbeat tick forever.
        await conn.ForceReconnectAsync().WaitAsync(HangGuard);
    }

    [Test]
    public async Task Concurrent_calls_share_one_loop_and_one_start() {
        var conn = new RetryTestConnection { ConnectRetryDelays = FastDelays };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.StartHold = hold;

        var first  = conn.ConnectWithRetryAsync(CancellationToken.None);
        var second = conn.ConnectWithRetryAsync(CancellationToken.None);

        // Let the second call reach the lock while the first is held mid-StartAsync — before the
        // fix it would have raced ahead and called StartAsync on the same hub concurrently.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await Assert.That(conn.StartCalls).IsEqualTo(1);

        hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(HangGuard);

        await Assert.That(conn.StartCalls).IsEqualTo(1);
        await Assert.That(conn.RegisterCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_during_backoff_propagates() {
        var conn = new RetryTestConnection {
            State               = HubConnectionState.Connected,
            FailRegisterRemaining = int.MaxValue,
            ConnectRetryDelays   = [TimeSpan.FromSeconds(30)]
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await conn.ConnectWithRetryAsync(cts.Token).WaitAsync(HangGuard))
            .Throws<OperationCanceledException>();
    }

    /// <summary>
    /// Codex P2 (production): ConnectWithRetryAsync only pulsed <c>_statusNotifier</c> on the
    /// success path (after LogConnected). An initial-start failure fired neither
    /// OnReconnecting nor OnClosed, so a status subscriber that snapshotted while the hub was
    /// Connecting kept displaying "connecting" for the whole outage — no pulse ever refreshed
    /// it, and <c>DaemonRunner</c> starts <c>LocalControlServer</c> before <c>ConnectAsync</c>,
    /// so the race is reachable. Drives a StartHubAsync that always throws (simulating repeated
    /// initial-connect failures) with a fast retry schedule, and polls the notifier's Version
    /// until it advances past its pre-call snapshot — proving every failed attempt now pulses,
    /// not just the eventual success.
    /// </summary>
    [Test]
    public async Task Failed_connect_attempts_pulse_the_status_notifier() {
        var notifier = new DaemonStatusNotifier();
        var conn = new RetryTestConnection(notifier) {
            State              = HubConnectionState.Disconnected,
            StartThrows        = new InvalidOperationException("simulated transport failure"),
            ConnectRetryDelays = [TimeSpan.FromMilliseconds(1)]
        };

        var versionBefore = notifier.Version;

        using var cts = new CancellationTokenSource();
        var loop = conn.ConnectWithRetryAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (notifier.Version <= versionBefore && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        cts.Cancel();
        try { await loop.WaitAsync(HangGuard); } catch (OperationCanceledException) { /* expected teardown */ }

        await Assert.That(notifier.Version).IsGreaterThan(versionBefore);
    }
}
