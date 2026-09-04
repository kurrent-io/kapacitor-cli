using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="ServerConnection.DisposeAsync"/> must be idempotent and non-throwing: the DI
/// container tracks the singleton AND <c>DaemonRunner</c> disposes it explicitly, so it runs twice
/// by construction on every shutdown. Before the run-once guard, the second pass called
/// <c>CancelAsync</c> on the already-disposed terminal-sender CTS — an
/// <see cref="ObjectDisposedException"/> that escaped into DI teardown and, under NativeAOT,
/// aborted the process (SIGABRT) instead of exiting cleanly. Mirrors
/// <see cref="ConnectWithRetryTests"/>' harness: no live SignalR transport; the internal seams are
/// overridden so <c>ConnectAsync</c> reaches the CTS-creating line without a server.
/// </summary>
public class ServerConnectionDisposeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class DisposeTestConnection(ILogger<ServerConnection>? logger = null) : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        logger ?? NullLogger<ServerConnection>.Instance
    ) {
        // Always "ready": ConnectAsync's retry loop returns immediately without ever touching the
        // (never-started) real hub — but only AFTER the terminal-sender CTS has been created,
        // which is exactly the state production disposes from.
        internal override bool IsReady => true;
    }

    sealed class CapturingLogger : ILogger<ServerConnection> {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Test, NotInParallel(nameof(ServerConnectionDisposeTests))]
    public async Task Dispose_twice_does_not_throw() {
        var conn = new DisposeTestConnection();

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);
        // Pass 2 = the DI container's dispose walk re-entering after DaemonRunner's explicit call.
        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);
        // Reaching here without ObjectDisposedException is the assertion.
    }

    [Test, NotInParallel(nameof(ServerConnectionDisposeTests))]
    public async Task Dispose_twice_after_connect_does_not_throw() {
        var conn = new DisposeTestConnection();

        // Creates the linked terminal-sender CTS — the object the unguarded second pass
        // re-cancelled after disposal (the production crash).
        await conn.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);
        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);
    }

    [Test, NotInParallel(nameof(ServerConnectionDisposeTests))]
    public async Task Second_dispose_does_not_reenter_the_body_and_the_cts_ends_cancelled_and_disposed() {
        var conn = new DisposeTestConnection();

        await conn.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        var cts = conn.TerminalSenderCtsForTests;
        await Assert.That(cts).IsNotNull();

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(conn.DisposeBodyRuns).IsEqualTo(1);
        // Cancelled (IsCancellationRequested is safe to read post-dispose) AND disposed
        // (the Token property throws once the source is disposed).
        await Assert.That(cts!.IsCancellationRequested).IsTrue();
        await Assert.That(() => _ = cts.Token).Throws<ObjectDisposedException>();

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);

        // Durable run-once contract: the second call must not have re-entered the body.
        await Assert.That(conn.DisposeBodyRuns).IsEqualTo(1);
    }

    [Test, NotInParallel(nameof(ServerConnectionDisposeTests))]
    public async Task A_faulting_awaited_task_is_logged_and_later_cleanup_still_runs() {
        var log  = new CapturingLogger();
        var conn = new DisposeTestConnection(log);

        await conn.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        var cts = conn.TerminalSenderCtsForTests;
        await Assert.That(cts).IsNotNull();

        // Inject a faulted pipeline task: the await must be contained + logged, never skipping
        // the sibling awaits or the mandatory resource release.
        conn.EventProcessorTaskForTests = Task.FromException(new InvalidOperationException("boom"));

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard);

        // (a) the failure is logged with the failing step's name…
        await Assert.That(log.Messages.Any(m =>
            m.Contains("dispose step 'event-processor' failed", StringComparison.Ordinal))).IsTrue();

        // (b) …and later cleanup still ran: the CTS was cancelled AND disposed.
        await Assert.That(cts!.IsCancellationRequested).IsTrue();
        await Assert.That(() => _ = cts.Token).Throws<ObjectDisposedException>();
    }

    [Test, NotInParallel(nameof(ServerConnectionDisposeTests))]
    public async Task Explicit_dispose_then_host_teardown_over_the_same_tracked_instance_does_not_throw() {
        // The true production shape: DaemonRunner explicitly disposes the ServerConnection in its
        // finally, THEN stops and disposes the host — whose DI container tracks the SAME instance
        // (factory-created singleton) and walks its DisposeAsync a second time.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ServerConnection>(_ => new DisposeTestConnection());
        var host = builder.Build();

        var conn = host.Services.GetRequiredService<ServerConnection>();

        await host.StartAsync();
        await conn.ConnectAsync(CancellationToken.None).WaitAsync(HangGuard);

        await conn.DisposeAsync().AsTask().WaitAsync(HangGuard); // DaemonRunner's explicit dispose
        await host.StopAsync().WaitAsync(HangGuard);
        await DaemonRunner.DisposeHostAsync(host).WaitAsync(HangGuard); // DI walk re-disposes

        await Assert.That(((DisposeTestConnection)conn).DisposeBodyRuns).IsEqualTo(1);
    }
}
