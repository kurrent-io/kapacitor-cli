using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The daemon half of the correlated status report: a server-requested report echoes the request's
/// nonce (<see cref="DaemonStatusReport.EchoNonce"/>), which is what lets the server's idle-marker
/// coordinator confirm a claim against THIS report rather than any report — without it every
/// durable idle marker dies un-confirmed. Plus the falling-edge turn report: a turn ending fires an
/// out-of-cycle report so the server learns about idleness at turn end, not at the next 60s tick.
/// </summary>
public class CorrelatedStatusReportTests {
    [Test]
    public async Task Sent_report_echoes_the_requested_nonce() {
        var capture = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            capture, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        await orch.SendDaemonStatusReportOnceAsync(echoNonce: "nonce-1");

        await Assert.That(capture.StatusReports[^1].EchoNonce).IsEqualTo("nonce-1");
    }

    [Test]
    public async Task Unsolicited_reports_carry_no_echo_nonce() {
        var capture = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            capture, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        await orch.SendDaemonStatusReportOnceAsync();

        await Assert.That(capture.StatusReports[^1].EchoNonce).IsNull();
    }

    /// <summary>The wire tokens the server binds by name — a drift here reads as a legacy daemon
    /// and the whole confirmation handshake silently goes inert.</summary>
    [Test]
    public async Task Correlation_fields_serialize_as_the_pinned_wire_tokens() {
        var report = new DaemonStatusReport(0, [], [], EchoNonce: "n-1");
        var reportJson = JsonSerializer.Serialize(report, CapacitorJsonContext.Default.DaemonStatusReport);
        await Assert.That(reportJson).Contains("\"echo_nonce\":\"n-1\"");

        var connect = new DaemonConnect("d", "mac", [], 1, [], SupportsCorrelatedStatusReports: true);
        var connectJson = JsonSerializer.Serialize(connect, CapacitorJsonContext.Default.DaemonConnect);
        await Assert.That(connectJson).Contains("\"supports_correlated_status_reports\":true");

        var request = JsonSerializer.Deserialize(
            """{"nonce":"n-2"}""", CapacitorJsonContext.Default.StatusReportRequest);
        await Assert.That(request.Nonce).IsEqualTo("n-2");
    }

    /// <summary>The advertisement is the server's licence to send RequestStatusReport2, and the
    /// receive seam answers through a null-conditional invoke — so a connection nothing subscribed to
    /// must not claim it, or the request is met with silence rather than a report.</summary>
    [Test]
    public async Task An_unwired_connection_does_not_advertise_correlated_status_reports() {
        var unwired = new ServerConnection(
            new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance);
        await Assert.That(unwired.AdvertisesCorrelatedStatusReports).IsFalse();

        unwired.OnRequestStatusReport2 += _ => Task.CompletedTask;
        await Assert.That(unwired.AdvertisesCorrelatedStatusReports).IsTrue();
    }

    [Test]
    public async Task Turn_end_triggers_exactly_one_out_of_cycle_send() {
        var server = new StatusReportCountingConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("turn-edge-1", status: "Running");

        agent.ActivityClock.SetTurnInFlight(true);
        await Task.Delay(50);
        await Assert.That(server.SendCount).IsEqualTo(0);

        agent.ActivityClock.SetTurnInFlight(false);
        await WaitHarness.PollUntilAsync(() => server.SendCount >= 1);
        await Assert.That(server.SendCount).IsEqualTo(1);

        // The repeat is the point: the trigger is the falling edge, not the level.
        agent.ActivityClock.SetTurnInFlight(false);
        await Task.Delay(50);
        await Assert.That(server.SendCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clock_fires_turn_ended_only_on_the_falling_edge() {
        var clock = new AgentActivityClock(TimeProvider.System);
        var fired = 0;
        clock.OnTurnEnded = () => fired++;

        clock.SetTurnInFlight(true);
        await Assert.That(fired).IsEqualTo(0);

        clock.SetTurnInFlight(false);
        await Assert.That(fired).IsEqualTo(1);

        clock.SetTurnInFlight(false);
        await Assert.That(fired).IsEqualTo(1);

        clock.SetTurnInFlight(true);
        clock.SetTurnInFlight(false);
        await Assert.That(fired).IsEqualTo(2);
    }

    sealed class StatusReportCountingConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        public override Task DaemonStatusReportAsync(DaemonStatusReport report) {
            Interlocked.Increment(ref _sendCount);
            return Task.CompletedTask;
        }
    }
}
