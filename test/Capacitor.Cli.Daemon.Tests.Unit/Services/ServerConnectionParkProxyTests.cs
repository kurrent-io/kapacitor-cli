using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// §2.7 B6 task 2: covers <see cref="ServerConnection.ReportParticipantParkedAsync"/> — the
/// daemon→hub park-report proxy — mapping the server's two-value wire outcome
/// (<see cref="ParkParticipantOutcome"/>) plus "no definite reply" into the daemon-local
/// <see cref="ParkAck"/>. Mirrors <c>AcpServerConnectionTests</c>'s approach exactly: no live SignalR
/// transport; <see cref="TestServerConnection"/> overrides the raw hub-invoke seam
/// (<see cref="ServerConnection.InvokeReportParticipantParkedRawAsync"/>) so each case can be driven
/// deterministically.
/// </summary>
public class ServerConnectionParkProxyTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Test double: constructed against an unreachable URL and never started, so the real
    /// <c>HubConnection</c> is never touched. <see cref="IsReady"/> and the raw park-report invoke are
    /// the only seams overridden — the same seam shape <c>AcpServerConnectionTests</c> uses.</summary>
    sealed class TestServerConnection() : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public bool Ready { get; set; } = true;

        internal override bool IsReady => Ready;

        public List<(string AgentId, string CanonicalSessionId, string Reason)> Calls { get; } = [];

        /// <summary>When set, the raw invoke returns this outcome. When <see cref="ThrowOnInvoke"/>
        /// is also set, the exception takes precedence (a definite outcome and a thrown exception
        /// can never both happen on the same call).</summary>
        public ParkParticipantOutcome Outcome { get; set; } = ParkParticipantOutcome.Parked;

        public Exception? ThrowOnInvoke { get; set; }

        internal override Task<ParkParticipantOutcome> InvokeReportParticipantParkedRawAsync(
                string agentId, string canonicalSessionId, string reason, CancellationToken ct
            ) {
            Calls.Add((agentId, canonicalSessionId, reason));

            return ThrowOnInvoke is { } ex
                ? Task.FromException<ParkParticipantOutcome>(ex)
                : Task.FromResult(Outcome);
        }
    }

    [Test]
    public async Task Definite_Parked_outcome_maps_to_ParkAck_Parked() {
        var conn = new TestServerConnection { Outcome = ParkParticipantOutcome.Parked };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Parked);
        await Assert.That(conn.Calls.Count).IsEqualTo(1);
        var call = conn.Calls[0];
        await Assert.That(call.AgentId).IsEqualTo("agent1");
        await Assert.That(call.CanonicalSessionId).IsEqualTo("canon-1");
        await Assert.That(call.Reason).IsEqualTo("reviewer_parked_resumable");
    }

    [Test]
    public async Task Definite_Rejected_outcome_maps_to_ParkAck_Rejected() {
        var conn = new TestServerConnection { Outcome = ParkParticipantOutcome.Rejected };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Rejected);
    }

    [Test]
    public async Task A_thrown_HubException_maps_to_ParkAck_Ambiguous_never_throws() {
        var conn = new TestServerConnection {
            ThrowOnInvoke = new Microsoft.AspNetCore.SignalR.HubException("Caller is not a registered daemon")
        };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Ambiguous);
    }

    /// <summary>
    /// A pre-B1 server has no <c>ReportParticipantParked</c> hub method at all: the server's
    /// <c>DefaultHubDispatcher</c> can't resolve the target and completes the invocation with the error
    /// <c>Unknown hub method '&lt;target&gt;'</c> (sent regardless of <c>EnableDetailedErrors</c>;
    /// verified against aspnetcore v10.0.11), surfaced on the client as a <c>HubException</c> carrying
    /// that message. Unlike the generic transient HubException in
    /// <see cref="A_thrown_HubException_maps_to_ParkAck_Ambiguous_never_throws"/>, this is a DEFINITE,
    /// permanent degrade: mapping it to Ambiguous would have <c>AgentOrchestrator.ParkReviewerAsync</c>
    /// retry the park forever against a server that will never grow the method — and because arm-A park
    /// precedes arm-B reap in the sweep, the reviewer would never be cleanly reaped and would leak
    /// capacity indefinitely. Rejected makes it fall back to the normal reap instead. The message string
    /// this test asserts on is the exact text SignalR produces; matching a fabricated
    /// "does not exist" phrase (which SignalR never emits) would let this degrade path never fire.
    /// </summary>
    [Test]
    public async Task Park_against_a_server_without_the_hub_method_falls_back_to_reap() {
        var conn = new TestServerConnection {
            ThrowOnInvoke = new Microsoft.AspNetCore.SignalR.HubException("Unknown hub method 'ReportParticipantParked'")
        };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Rejected);
    }

    /// <summary>
    /// High regression (qodo pre-merge review): <c>ParkReviewerAsync</c> holds the reap claim across
    /// this call, so a disconnected daemon (<see cref="ServerConnection.IsReady"/> never true) must not
    /// pin it — the readiness gate is bounded by <c>ParkAckBudget</c>. When the budget elapses with no
    /// reply the call returns <see cref="ParkAck.Ambiguous"/> (release + retry) PROMPTLY (the 5s
    /// HangGuard, far above the 200ms budget, is what would trip if the bound were absent) and — because
    /// IsReady was never observed — the hub was never invoked, so there is no half-sent report to
    /// reconcile.
    /// </summary>
    [Test]
    public async Task A_disconnected_daemon_bounds_the_park_by_the_budget_and_maps_to_Ambiguous() {
        var conn = new TestServerConnection {
            Ready         = false,                          // never becomes ready — the outage case
            ParkAckBudget = TimeSpan.FromMilliseconds(200)
        };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Ambiguous);
        await Assert.That(conn.Calls).IsEmpty();            // IsReady never true → the hub was never invoked
    }

    /// <summary>
    /// A cancellation that fires while the daemon is shut down mid-call is exactly the "no definite
    /// reply" case the arm-A park state machine must not treat as a rejection — it must fold to
    /// Ambiguous rather than propagate, so the caller never sees an exception out of this method.
    /// </summary>
    [Test]
    public async Task A_cancellation_maps_to_ParkAck_Ambiguous_never_throws() {
        using var cts = new CancellationTokenSource();
        var conn = new TestServerConnection {
            ThrowOnInvoke = new OperationCanceledException("shutting down")
        };
        await cts.CancelAsync();

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable", cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Ambiguous);
    }

    /// <summary>An unrecognized/future wire value (e.g. a newer server adds a member this daemon
    /// doesn't know) must degrade to Ambiguous rather than being silently treated as a success.</summary>
    [Test]
    public async Task An_unmapped_outcome_value_maps_to_ParkAck_Ambiguous() {
        var conn = new TestServerConnection { Outcome = (ParkParticipantOutcome) 99 };

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable")
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Ambiguous);
    }

    /// <summary>
    /// Unlike the sibling ACP proxies (which let a not-ready timeout's <see cref="OperationCanceledException"/>
    /// propagate to the caller), this proxy's "never throw" contract applies even to the
    /// readiness-gating wait itself: a caller that gives up waiting for a registered connection gets
    /// <see cref="ParkAck.Ambiguous"/> — "no definite reply" — not an exception.
    /// </summary>
    [Test]
    public async Task Not_ready_and_then_cancelled_maps_to_ParkAck_Ambiguous_never_throws() {
        var conn = new TestServerConnection { Ready = false };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var ack = await conn.ReportParticipantParkedAsync("agent1", "canon-1", "reviewer_parked_resumable", cts.Token)
            .WaitAsync(HangGuard);

        await Assert.That(ack).IsEqualTo(ParkAck.Ambiguous);
        await Assert.That(conn.Calls).IsEmpty();
    }
}
