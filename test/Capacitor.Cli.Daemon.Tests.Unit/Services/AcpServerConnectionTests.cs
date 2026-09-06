using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Option B task 3: covers <see cref="ServerConnection"/>'s two new gated ACP hub-invoke
/// methods (<see cref="ServerConnection.AcpSessionStartedAsync"/>/<see cref="ServerConnection.SendAcpEventsAsync"/>)
/// and the reconnect re-bind mechanism (<see cref="ServerConnection.RegisterAcpBinding"/>/
/// <see cref="ServerConnection.UnregisterAcpBinding"/>/<see cref="ServerConnection.ReBindAcpSessionsAsync"/>).
/// Mirrors the existing <c>ConnectionRetryTests</c>/<c>RegistrationGateTests</c> approach — no live
/// SignalR transport; <see cref="TestServerConnection"/> overrides the internal seams
/// (<c>IsReady</c>, the raw hub-invoke methods) so gating/ordering can be driven deterministically.
/// </summary>
public class AcpServerConnectionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test double: constructed against an unreachable URL and never started, so the real
    /// <c>HubConnection</c> is never touched. <see cref="IsReady"/> and the two raw hub-invoke
    /// methods are overridden — the only seams <see cref="ServerConnection"/> exposes for this.
    /// </summary>
    sealed class TestServerConnection() : ServerConnection(
        new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public bool Ready { get; set; }

        internal override bool IsReady => Ready;

        public List<(string AgentId, string Vendor, string AcpSessionId, string? Cwd, string? Model, IReadOnlyDictionary<string, string>? Metadata)>
            SessionStartedCalls { get; } = [];

        public List<(string AgentId, string AcpSessionId, AcpEventEnvelope[] Envelopes)> EventsCalls { get; } = [];

        /// <summary>Fired synchronously from inside the raw session-started invoke — lets a test
        /// observe ordering/gating state at the exact moment the (re-)bind call happens.</summary>
        public Action? OnRawSessionStartedInvoked { get; set; }

        /// <summary> reliability fix (Codex P1 #1): how many MORE times the raw
        /// AcpSessionStarted invoke should throw for a given agentId before it starts succeeding —
        /// drives <c>ReBindAcpSessionsAsyncBoundedRetryTests</c>'s bounded-retry-then-give-up
        /// and transient-failure-then-recover cases. Absent/zero entries always succeed.</summary>
        public Dictionary<string, int> FailSessionStartedRemaining { get; } = [];

        /// <summary>agent ids whose raw AcpSessionStarted invoke should return
        /// <see cref="AcpBindOutcome.Rejected"/> (a server stand-down, NOT a thrown transport failure).</summary>
        public HashSet<string> RejectSessionStarted { get; } = [];

        internal override Task<AcpBindOutcome> InvokeAcpSessionStartedRawAsync(
                string agentId, string vendor, string acpSessionId, string? cwd, string? model,
                IReadOnlyDictionary<string, string>? metadata, CancellationToken ct
            ) {
            SessionStartedCalls.Add((agentId, vendor, acpSessionId, cwd, model, metadata));
            OnRawSessionStartedInvoked?.Invoke();

            if (FailSessionStartedRemaining.TryGetValue(agentId, out var remaining) && remaining > 0) {
                FailSessionStartedRemaining[agentId] = remaining - 1;

                return Task.FromException<AcpBindOutcome>(new InvalidOperationException($"simulated AcpSessionStarted failure for {agentId}"));
            }

            return Task.FromResult(RejectSessionStarted.Contains(agentId) ? AcpBindOutcome.Rejected : AcpBindOutcome.Bound);
        }

        internal override Task<AcpBatchAck> InvokeAcpSessionEventsRawAsync(
                string agentId, string acpSessionId, AcpEventEnvelope[] envelopes, CancellationToken ct
            ) {
            EventsCalls.Add((agentId, acpSessionId, envelopes));

            return Task.FromResult(new AcpBatchAck(envelopes[^1].Seq, envelopes[^1].Seq));
        }
    }

    // ── AcpSessionStartedAsync gating + payload ──────────────────────────────────────────────────

    [Test]
    public async Task AcpSessionStartedAsync_never_invokes_the_raw_call_while_not_ready() {
        var conn = new TestServerConnection { Ready = false };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.That(async () => await conn.AcpSessionStartedAsync(
                "agent1", "cursor", "sess-1", "/wt", "model-x", null, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(conn.SessionStartedCalls).IsEmpty();
    }

    [Test]
    public async Task AcpSessionStartedAsync_invokes_the_raw_call_with_the_right_payload_once_ready() {
        var conn = new TestServerConnection { Ready = true };

        await conn.AcpSessionStartedAsync("agent1", "cursor", "sess-1", "/wt", "model-x", null)
            .WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(1);
        var call = conn.SessionStartedCalls[0];
        await Assert.That(call.AgentId).IsEqualTo("agent1");
        await Assert.That(call.Vendor).IsEqualTo("cursor");
        await Assert.That(call.AcpSessionId).IsEqualTo("sess-1");
        await Assert.That(call.Cwd).IsEqualTo("/wt");
        await Assert.That(call.Model).IsEqualTo("model-x");
    }

    [Test]
    public async Task AcpSessionStartedAsync_unblocks_and_fires_once_readiness_flips_true() {
        var conn = new TestServerConnection { Ready = false };

        var callTask = conn.AcpSessionStartedAsync("agent1", "cursor", "sess-1", "/wt", null, null);

        // Give the (incorrectly-gated) call a chance to fire early — proves it's actually blocked
        // on readiness rather than coincidentally slow.
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await Assert.That(conn.SessionStartedCalls).IsEmpty();

        conn.Ready = true;

        await callTask.WaitAsync(HangGuard);
        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(1);
    }

    // ── SendAcpEventsAsync gating + payload ──────────────────────────────────────────────────────

    [Test]
    public async Task SendAcpEventsAsync_waits_for_IsReady_then_invokes_with_the_right_payload() {
        var conn      = new TestServerConnection { Ready = false };
        var envelopes = new[] { new AcpEventEnvelope(Seq: 1, Kind: AcpEventKind.AssistantText, Text: "hi") };

        var callTask = conn.SendAcpEventsAsync("agent1", "sess-1", envelopes);

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await Assert.That(conn.EventsCalls).IsEmpty();

        conn.Ready = true;
        var ack = await callTask.WaitAsync(HangGuard);

        await Assert.That(conn.EventsCalls.Count).IsEqualTo(1);
        var call = conn.EventsCalls[0];
        await Assert.That(call.AgentId).IsEqualTo("agent1");
        await Assert.That(call.AcpSessionId).IsEqualTo("sess-1");
        await Assert.That(call.Envelopes).IsEquivalentTo(envelopes);
        await Assert.That(ack.AcceptedSeq).IsEqualTo(1);
    }

    // ── RegisterAcpBinding / UnregisterAcpBinding / ReBindAcpSessionsAsync ───────────────────────

    [Test]
    public async Task ReBindAcpSessionsAsync_reinvokes_the_raw_call_for_every_registered_binding_bypassing_IsReady() {
        // Ready = false proves the rebind does NOT go through the gated AcpSessionStartedAsync path
        // (which would hang forever here) — see this method's doc comment on ServerConnection.
        var conn = new TestServerConnection { Ready = false };

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", "model-a"));
        conn.RegisterAcpBinding("agentB", new AcpBindInfo("cursor", "sess-b", "/wt-b", null));

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(2);
        await Assert.That(conn.SessionStartedCalls.Select(c => c.AgentId)).IsEquivalentTo(new[] { "agentA", "agentB" });

        var callA = conn.SessionStartedCalls.Single(c => c.AgentId == "agentA");
        await Assert.That(callA.Vendor).IsEqualTo("cursor");
        await Assert.That(callA.AcpSessionId).IsEqualTo("sess-a");
        await Assert.That(callA.Cwd).IsEqualTo("/wt-a");
        await Assert.That(callA.Model).IsEqualTo("model-a");
    }

    [Test]
    public async Task UnregisterAcpBinding_stops_future_rebinds_for_that_agent() {
        var conn = new TestServerConnection();

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", null));
        conn.RegisterAcpBinding("agentB", new AcpBindInfo("cursor", "sess-b", "/wt-b", null));
        conn.UnregisterAcpBinding("agentA");

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Select(c => c.AgentId)).IsEquivalentTo(new[] { "agentB" });
    }

    [Test]
    public async Task ReBindAcpSessionsAsync_is_harmless_with_no_registered_bindings() {
        var conn = new TestServerConnection();

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls).IsEmpty();
    }

    // ── Bounded re-bind retry (Codex P1 #1: a re-bind-miss must not stall the transcript forever) ─

    /// <summary>
    /// A binding whose re-bind NEVER succeeds must not be retried forever — <see cref="ServerConnection.ReBindAcpSessionsAsync"/>
    /// bounds the attempts (<see cref="ServerConnection.AcpRebindMaxAttempts"/>) and then unregisters
    /// the binding so it isn't replayed on the NEXT reconnect either (the permanent-failure half of
    /// the fix — otherwise <c>IsReady</c> flips true regardless and the forwarder retries the same
    /// batch against a binding the server never got, forever).
    /// </summary>
    [Test]
    public async Task ReBindAcpSessionsAsync_retries_a_permanently_failing_binding_up_to_the_bound_then_unregisters_it() {
        var conn = new TestServerConnection();
        conn.AcpRebindRetryDelay = TimeSpan.FromMilliseconds(5); // don't slow the test down

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", null));
        conn.FailSessionStartedRemaining["agentA"] = int.MaxValue; // never succeeds

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentA")).IsEqualTo(ServerConnection.AcpRebindMaxAttempts);

        // The binding was unregistered on permanent failure: a LATER reconnect must not replay it —
        // no additional SessionStarted calls for agentA.
        var callsBefore = conn.SessionStartedCalls.Count;
        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);
        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(callsBefore);
    }

    /// <summary>
    /// The other half: a re-bind that fails a few times but recovers WITHIN the bound must succeed —
    /// the binding stays registered (a later reconnect still replays it), proving the bound doesn't
    /// give up too eagerly on a merely-transient failure.
    /// </summary>
    [Test]
    public async Task ReBindAcpSessionsAsync_retries_a_transient_failure_and_recovers_within_the_bound() {
        var conn = new TestServerConnection();
        conn.AcpRebindRetryDelay = TimeSpan.FromMilliseconds(5);

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", null));
        conn.FailSessionStartedRemaining["agentA"] = ServerConnection.AcpRebindMaxAttempts - 1; // fails all but the last attempt

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentA")).IsEqualTo(ServerConnection.AcpRebindMaxAttempts);

        // Still registered — a later reconnect replays it again (proof it was NOT unregistered).
        var callsBefore = conn.SessionStartedCalls.Count;
        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);
        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(callsBefore + 1);
    }

    /// <summary>One binding permanently failing must not stop a DIFFERENT binding from being
    /// re-bound in the same <see cref="ServerConnection.ReBindAcpSessionsAsync"/> pass — per-binding
    /// isolation, mirroring the existing best-effort-per-binding guarantee.</summary>
    [Test]
    public async Task ReBindAcpSessionsAsync_isolates_a_permanently_failing_binding_from_others() {
        var conn = new TestServerConnection();
        conn.AcpRebindRetryDelay = TimeSpan.FromMilliseconds(5);

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", null));
        conn.RegisterAcpBinding("agentB", new AcpBindInfo("cursor", "sess-b", "/wt-b", null));
        conn.FailSessionStartedRemaining["agentA"] = int.MaxValue;

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentB")).IsEqualTo(1);
        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentA")).IsEqualTo(ServerConnection.AcpRebindMaxAttempts);
    }

    // ── reconnect re-bind stand-down on a server Rejected outcome ────────────────────────

    /// <summary>
    /// a <see cref="AcpBindOutcome.Rejected"/> outcome is a terminal stand-down, NOT a
    /// transient failure — the server declined a stale/foreign/conflicting binding. It must be
    /// invoked exactly ONCE (no 3× retry storm) and the binding unregistered so a later reconnect
    /// never replays it.
    /// </summary>
    [Test]
    public async Task ReBindAcpSessionsAsync_stands_down_without_retrying_on_a_Rejected_outcome() {
        var conn = new TestServerConnection();
        conn.AcpRebindRetryDelay = TimeSpan.FromMilliseconds(5);

        conn.RegisterAcpBinding("agentA", new AcpBindInfo("cursor", "sess-a", "/wt-a", null));
        conn.RejectSessionStarted.Add("agentA");

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        // Exactly one attempt — no retry storm.
        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentA")).IsEqualTo(1);

        // Unregistered: a later reconnect replays nothing for agentA.
        var callsBefore = conn.SessionStartedCalls.Count;
        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);
        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(callsBefore);
    }

    /// <summary>
    /// belt-and-braces: a binding whose agent is no longer hosted as live (per
    /// <see cref="ServerConnection.GetLiveAgentIds"/>) is not replayed at all — it is skipped and
    /// unregistered, so the server is never asked to bind a gone agent.
    /// </summary>
    [Test]
    public async Task ReBindAcpSessionsAsync_skips_and_unregisters_a_binding_whose_agent_is_not_live() {
        var conn = new TestServerConnection { GetLiveAgentIds = () => ["agentLive"] };

        conn.RegisterAcpBinding("agentGone", new AcpBindInfo("cursor", "sess-gone", "/wt", null));
        conn.RegisterAcpBinding("agentLive", new AcpBindInfo("cursor", "sess-live", "/wt", null));

        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);

        // The gone agent was never re-bound; the live one was.
        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentGone")).IsEqualTo(0);
        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentLive")).IsEqualTo(1);

        // agentGone's stale binding was unregistered — a later reconnect (even without the live gate)
        // never replays it.
        conn.GetLiveAgentIds = null;
        var callsBefore = conn.SessionStartedCalls.Count;
        await conn.ReBindAcpSessionsAsync().WaitAsync(HangGuard);
        await Assert.That(conn.SessionStartedCalls.Count(c => c.AgentId == "agentGone")).IsEqualTo(0);
        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(callsBefore + 1); // only agentLive replayed
    }

    /// <summary>
    /// an INITIAL bind that the server declines surfaces as a local
    /// <c>AcpBindRejectedException</c> (never a <c>HubException</c>), so the launch path's existing
    /// "bind threw ⇒ don't register / don't start a forwarder" catch handles it unchanged.
    /// </summary>
    [Test]
    public async Task AcpSessionStartedAsync_throws_AcpBindRejectedException_on_a_Rejected_initial_bind() {
        var conn = new TestServerConnection { Ready = true };
        conn.RejectSessionStarted.Add("agent1");

        await Assert.That(async () => await conn.AcpSessionStartedAsync("agent1", "cursor", "sess-1", "/wt", "model-x", null))
            .Throws<AcpBindRejectedException>();
    }

    /// <summary>
    /// mixed-version invariant: <see cref="AcpBindOutcome.Bound"/> must be the zero value, so
    /// an OLD server whose hub method still returns void decodes to <c>default</c> == Bound (legacy
    /// success) on a new daemon.
    /// </summary>
    [Test]
    public async Task AcpBindOutcome_default_is_Bound() {
        // Constant vs constant is the point: the zero value is the wire contract, not an outcome.
#pragma warning disable TUnitAssertions0005
        await Assert.That(default(AcpBindOutcome)).IsEqualTo(AcpBindOutcome.Bound);
        await Assert.That((int)AcpBindOutcome.Bound).IsEqualTo(0);
#pragma warning restore TUnitAssertions0005
    }

    // ── Reconnect re-bind ordering (design spec §2.3) ────────────────────────────────────────────

    [Test]
    public async Task ReRegisterAgentsAndAcpBindingsAsync_runs_the_agent_hook_before_the_ACP_rebind() {
        var conn  = new TestServerConnection();
        var order = new List<string>();

        conn.ReRegisterAgentsHook = () => {
            order.Add("agents");
            return Task.CompletedTask;
        };
        conn.RegisterAcpBinding("agentX", new AcpBindInfo("cursor", "sess-x", "/wt", null));
        conn.OnRawSessionStartedInvoked = () => order.Add("acp:agentX");

        await conn.ReRegisterAgentsAndAcpBindingsAsync().WaitAsync(HangGuard);

        await Assert.That(order).IsEquivalentTo(new[] { "agents", "acp:agentX" });
    }

    [Test]
    public async Task ReRegisterAgentsAndAcpBindingsAsync_runs_even_with_no_agent_hook_wired() {
        var conn = new TestServerConnection();
        conn.RegisterAcpBinding("agentX", new AcpBindInfo("cursor", "sess-x", "/wt", null));

        await conn.ReRegisterAgentsAndAcpBindingsAsync().WaitAsync(HangGuard);

        await Assert.That(conn.SessionStartedCalls.Count).IsEqualTo(1);
    }

    /// <summary>
    /// The critical anti-deadlock/ordering assertion (design spec §2.3 "Enforcement"): the ACP
    /// rebind step must run — and complete — WHILE the (real) <see cref="RegistrationGate"/> still
    /// reports not-ready, and only once it returns does the gate flip ready. This is exactly the
    /// bracket <c>ServerConnection.RegisterDaemon</c> wires
    /// <see cref="ServerConnection.ReRegisterAgentsAndAcpBindingsAsync"/> into.
    /// </summary>
    [Test]
    public async Task Acp_rebind_step_runs_while_the_registration_gate_still_reports_not_ready() {
        var conn = new TestServerConnection();
        conn.RegisterAcpBinding("agentX", new AcpBindInfo("cursor", "sess-x", "/wt", null));

        var gate = new RegistrationGate();
        bool? readyDuringRebind = null;

        conn.OnRawSessionStartedInvoked =
            () => readyDuringRebind = gate.IsReady(HubConnectionState.Connected);

        await gate.RunRegistrationAsync(
                daemonConnect: () => Task.CompletedTask,
                reRegisterAgents: conn.ReRegisterAgentsAndAcpBindingsAsync)
            .WaitAsync(HangGuard);

        await Assert.That(readyDuringRebind).IsFalse();
        await Assert.That(gate.IsReady(HubConnectionState.Connected)).IsTrue();
    }
}
