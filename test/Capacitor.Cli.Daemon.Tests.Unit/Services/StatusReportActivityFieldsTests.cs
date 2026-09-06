using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Liveness-supervision spec §1/§2 (task 11): the daemon's per-agent activity attestation
/// (<see cref="LiveAgentInfo.ActivitySeq"/>/<see cref="LiveAgentInfo.IdleForMs"/>/
/// <see cref="LiveAgentInfo.TurnInFlight"/>/<see cref="LiveAgentInfo.LaunchStage"/>) lands on
/// <see cref="AgentOrchestrator.BuildLiveAgents"/>'s entries, and a genuine
/// <c>AgentActivityClock.SetLaunchStage</c> transition fires an immediate out-of-cycle
/// <c>DaemonStatusReport</c> alongside the unchanged 60s periodic loop. Reuses
/// <see cref="AgentOrchestratorHarness"/> (BuildOrchestrator/SeedAgentForTest/
/// SpyPtyProcessFactory).
/// </summary>
public class StatusReportActivityFieldsTests {
    [Test]
    public async Task Running_agent_report_carries_the_capability_group_without_launch_stage() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("running-1", LaunchKind.ReviewFlow, status: "Running");
        // Force a non-null clock stage on this ALREADY-Running agent — the real handshake always
        // calls ClearLaunchStage before flipping to Running, so a plain Running seed would leave the
        // clock's own LaunchStage null too, making the assertion below vacuously true regardless of
        // whether BuildLiveAgents' own Status=="Starting" gate exists at all. Setting it explicitly
        // here means the null below can ONLY come from that gate, non-vacuously pinning it.
        agent.ActivityClock.SetLaunchStage("stale");

        var entry = orch.BuildLiveAgents().Single(a => a.Id == "running-1");

        // Capability-complete: all three steady-state fields present. This is the exact shape the
        // server pins as "still capable despite no LaunchStage" — a Running reviewer must never lose
        // supervision just because it finished its handshake.
        await Assert.That(entry.ActivitySeq).IsNotNull();
        await Assert.That(entry.IdleForMs).IsNotNull();
        await Assert.That(entry.TurnInFlight).IsNotNull();
        await Assert.That(entry.LaunchStage).IsNull();
    }

    [Test]
    public async Task Starting_agent_report_carries_all_four_fields() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("starting-1", LaunchKind.ReviewFlow, status: "Starting");
        agent.ActivityClock.SetLaunchStage("spawned");

        var entry = orch.BuildLiveAgents().Single(a => a.Id == "starting-1");

        await Assert.That(entry.ActivitySeq).IsNotNull();
        await Assert.That(entry.IdleForMs).IsNotNull();
        await Assert.That(entry.TurnInFlight).IsNotNull();
        await Assert.That(entry.LaunchStage).IsEqualTo("spawned");
    }

    /// <summary>Guards the specific defect class called out in the brief: a mismatch here means the
    /// server silently sees a legacy daemon and the whole feature goes inert, with no other symptom.
    /// Serializes through the SAME source-gen <see cref="CapacitorJsonContext"/> typed overload the
    /// production DTO round-trip test (<c>DaemonStatusDtoTests</c>) uses — its
    /// <c>[JsonSourceGenerationOptions(PropertyNamingPolicy = SnakeCaseLower)]</c> is exactly what
    /// <c>ServerConnection</c>'s SignalR hub protocol config applies at runtime.</summary>
    [Test]
    public async Task Serialized_property_names_are_exactly_the_pinned_snake_case_wire_tokens() {
        var info = new LiveAgentInfo(
            "a1", "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer",
            ActivitySeq: 5, IdleForMs: 10, TurnInFlight: true, LaunchStage: "spawned");

        var json = JsonSerializer.Serialize(info, CapacitorJsonContext.Default.LiveAgentInfo);

        await Assert.That(json).Contains("\"activity_seq\":5");
        await Assert.That(json).Contains("\"idle_for_ms\":10");
        await Assert.That(json).Contains("\"turn_in_flight\":true");
        await Assert.That(json).Contains("\"launch_stage\":\"spawned\"");
    }

    [Test]
    public async Task Stage_transition_triggers_exactly_one_send_and_a_repeat_sends_nothing() {
        var server = new StatusReportCountingServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("stage-1", LaunchKind.ReviewFlow, status: "Starting");

        agent.ActivityClock.SetLaunchStage("spawned"); // genuine transition #1 (null -> "spawned")
        await WaitHarness.PollUntilAsync(() => server.SendCount >= 1);
        await Assert.That(server.SendCount).IsEqualTo(1);

        agent.ActivityClock.SetLaunchStage("spawned"); // SAME value: no transition, no send
        await Task.Delay(50); // give a wrongly-firing send a chance to land before asserting the count
        await Assert.That(server.SendCount).IsEqualTo(1);

        agent.ActivityClock.SetLaunchStage("initialized"); // genuine transition #2
        await WaitHarness.PollUntilAsync(() => server.SendCount >= 2);
        await Assert.That(server.SendCount).IsEqualTo(2);
    }

    [Test]
    public async Task Failing_out_of_cycle_send_does_not_throw_or_fail_the_launch() {
        var server = new StatusReportCountingServerConnection(throwOnSend: true);
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("stage-2", LaunchKind.ReviewFlow, status: "Starting");

        // Exercises SendStatusReportNowAsync's own build+send+swallow behavior directly, AWAITED
        // (rather than via the fire-and-forget `_ = ...` the clock callback uses) — a dropped
        // try/catch would throw synchronously right here and fail this test. Awaiting through the
        // fire-and-forget discard instead would never observe the exception either way (a faulted,
        // never-awaited Task is not surfaced to its caller), so it would not catch a missing catch.
        await orch.SendStatusReportNowAsync();
        await Assert.That(server.SendCount).IsEqualTo(1);

        // The stage-transition hook itself (the actual production call site, fire-and-forget) also
        // must not throw synchronously out of SetLaunchStage.
        agent.ActivityClock.SetLaunchStage("spawned");
        await WaitHarness.PollUntilAsync(() => server.SendCount >= 2);

        // The agent/launch is completely unaffected by the failed sends.
        await Assert.That(agent.Status).IsEqualTo("Starting");
        await Assert.That(orch.BuildLiveAgents().Select(a => a.Id)).Contains("stage-2");

        // A second, distinct transition still fires (the failure didn't wedge the hook).
        agent.ActivityClock.SetLaunchStage("initialized");
        await WaitHarness.PollUntilAsync(() => server.SendCount >= 3);
    }

    // Old (pre-liveness-supervision) shape a previous server release's contract would declare: no
    // ActivitySeq/IdleForMs/TurnInFlight/LaunchStage. Mirrors the server-side WireCompatTests case
    // (ii) precedent, in the opposite (daemon -> old server) direction this CLI-side task owns.
    readonly record struct OldLiveAgentInfo(
        string Id, string Kind, DateTimeOffset CreatedAt,
        string? FlowRunId = null, string? FlowRole = null);

    readonly record struct OldDaemonStatusReport(
        int ActiveCount, OldLiveAgentInfo[] LiveAgents, QuarantinedAgentInfo[] Quarantined,
        string?                       Epoch                       = null,
        long?                         LastProcessedSeq            = null,
        long?                         HighestAcceptedSeq          = null,
        bool?                         StartupReapComplete         = null,
        ResolvedStartupCandidate[]?   ResolvedStartupCandidates   = null,
        UnresolvedStartupCandidate[]? UnresolvedStartupCandidates = null,
        StartupDiscovery?             StartupDiscovery            = null,
        long?                         HighestResolutionGeneration = null
    );

    /// <summary>Compat is proven, not asserted (spec §2/backwards-compatibility section): a fully
    /// populated CURRENT payload — produced through the exact production serializer
    /// (<see cref="CapacitorJsonContext"/>'s source-gen typed overload, same naming policy
    /// <c>ServerConnection</c> configures on the live hub) — is deserialized by a LOCALLY DECLARED
    /// pre-Task-11 contract shape. If the deployed SignalR JSON settings ever rejected unknown
    /// members, this is the test that would catch it (a thrown <c>JsonException</c>), not a
    /// same-shape round-trip.</summary>
    [Test]
    public async Task Old_server_contract_shape_deserializes_the_new_payload_ignoring_unknown_members() {
        var fullShapeReport = new DaemonStatusReport(
            1,
            [
                new LiveAgentInfo("a1", "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer",
                    ActivitySeq: 5, IdleForMs: 1000, TurnInFlight: false, LaunchStage: "handshake")
            ],
            []);
        var json = JsonSerializer.Serialize(fullShapeReport, CapacitorJsonContext.Default.DaemonStatusReport);

        var oldShape = JsonSerializer.Deserialize<OldDaemonStatusReport>(json, OldServerOptions);

        await Assert.That(oldShape.ActiveCount).IsEqualTo(1);
        await Assert.That(oldShape.LiveAgents).Count().IsEqualTo(1);
        await Assert.That(oldShape.LiveAgents[0].Id).IsEqualTo("a1");
        await Assert.That(oldShape.LiveAgents[0].FlowRunId).IsEqualTo("flow-1");
        await Assert.That(oldShape.LiveAgents[0].FlowRole).IsEqualTo("reviewer");
    }

    static readonly JsonSerializerOptions OldServerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Counts <see cref="ServerConnection.DaemonStatusReportAsync"/> calls, optionally
    /// throwing to simulate a send failure — completely synchronous (no real hub/network), so the
    /// out-of-cycle send triggered by <c>AgentActivityClock.OnLaunchStageChanged</c> completes before
    /// <c>SetLaunchStage</c> returns in practice; the poll helper above is still used defensively so
    /// this test does not depend on that being true forever.</summary>
    sealed class StatusReportCountingServerConnection(bool throwOnSend = false) : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        public override Task DaemonStatusReportAsync(DaemonStatusReport report) {
            Interlocked.Increment(ref _sendCount);
            if (throwOnSend) throw new InvalidOperationException("simulated status-report send failure");
            return Task.CompletedTask;
        }
    }
}
