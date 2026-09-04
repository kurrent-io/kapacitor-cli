using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The daemon-service capability performed after setup writes its settings, rather than inside the
/// browser leg.
///
/// <para>What these pin is mostly what it must NOT do: act when nothing was asked, report a refusal over
/// a machine something was attempted on, or give up on a single failed report and leave the request
/// outstanding under a screen saying it asked.</para>
/// </summary>
public class SetupDaemonServiceTests {
    const string ServerUrl = "https://tenant.example";
    const string FlowId    = "aaaaaaaaaaaaaaaaaaaaaa";

    static readonly DateTimeOffset Requested = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    sealed class Channel(FirstRunFlowResponse? view, params int[] reportCodes) : IFirstRunFlowChannel {
        public List<ReportFirstRunMachineActionRequest> Reports { get; } = [];

        public int Polls { get; private set; }

        public Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
            Polls++;

            return Task.FromResult(new FirstRunPollOutcome(view is null ? 500 : 200, view));
        }

        public Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
                string serverUrl, string flowId, ReportFirstRunMachineActionRequest request, CancellationToken ct) {
            Reports.Add(request);

            var code = Reports.Count <= reportCodes.Length ? reportCodes[Reports.Count - 1] : 200;

            return Task.FromResult(new FirstRunActionReportOutcome(code));
        }

        public Task<FirstRunCreateOutcome> CreateAsync(string s, string f, FirstRunMachineReport r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportAsync(string s, string f, ReportFirstRunImportRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(string s, string f, ReportFirstRunImportOutcomeRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunRelinquishOutcome> RelinquishAsync(string s, string f, string reason, CancellationToken ct) => throw new NotSupportedException();
        public Task<FirstRunHeartbeatOutcome> HeartbeatAsync(string s, string f, CancellationToken ct) => throw new NotSupportedException();
    }

    static FirstRunFlowResponse View(params string[] outstanding) =>
        new() {
            FlowId         = FlowId,
            Step           = "Done",
            CanFinish      = true,
            MachineActions = [.. outstanding.Select(c => new FirstRunMachineActionResponse {
                Capability = c, RequestedAt = Requested
            })]
        };

    static Task RunAsync(Channel channel, Func<Task<ServiceEnsureJson?>>? ladder = null) =>
        SetupDaemonService.RunAsync(
            channel, ServerUrl, FlowId,
            new ConfigRoot("/nonexistent-config-root"),
            new ProfileContext(new(ServerUrl, "default", null, null), new ProfileConfig()),
            new UserHome("/nonexistent-home"),
            ladder ?? (() => Task.FromResult<ServiceEnsureJson?>(null)));

    /// <summary>Nothing asked, so nothing runs — and nothing is reported, which is what keeps a flow that
    /// declined the offer from carrying an outcome it never requested.</summary>
    [Test]
    public async Task Nothing_outstanding_performs_and_reports_nothing() {
        var channel = new Channel(View());
        var ran     = false;

        await RunAsync(channel, () => { ran = true; return Task.FromResult<ServiceEnsureJson?>(null); });

        await Assert.That(ran).IsFalse();
        await Assert.That(channel.Reports).IsEmpty();
    }

    /// <summary>A capability this step does not own is left alone: the shim is the live lane's, and
    /// performing it here would perform it twice.</summary>
    [Test]
    public async Task Another_capability_outstanding_is_not_this_step_s_to_perform() {
        var channel = new Channel(View(FirstRunMachineCapabilities.PathShim));

        await RunAsync(channel);

        await Assert.That(channel.Reports).IsEmpty();
    }

    /// <summary>A poll that answered nothing readable leaves the request where it is rather than acting on
    /// a view this build could not read.</summary>
    [Test]
    public async Task An_unreadable_poll_performs_nothing() {
        var channel = new Channel(null);

        await RunAsync(channel);

        await Assert.That(channel.Reports).IsEmpty();
    }

    /// <summary>The outcome is reported against the REQUEST's stamp, not a fresh one: the stamp is the
    /// request's identity, and a report carrying another would answer whichever one happens to stand.</summary>
    [Test]
    public async Task The_outcome_is_reported_against_the_request_it_answers() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService));

        await RunAsync(channel, () => Task.FromResult<ServiceEnsureJson?>(
            new("kcap", "running", "none", "already_enabled")));

        await Assert.That(channel.Reports.Count).IsEqualTo(1);
        await Assert.That(channel.Reports[0].Capability).IsEqualTo(FirstRunMachineCapabilities.DaemonService);
        await Assert.That(channel.Reports[0].RequestedAt).IsEqualTo(Requested);
        await Assert.That(channel.Reports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.AlreadyEnabled);
    }

    /// <summary>No service manager on this platform attempted nothing, so it refuses rather than reporting
    /// a transaction that failed.</summary>
    [Test]
    public async Task A_platform_with_no_service_manager_refuses_rather_than_failing() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService));

        await RunAsync(channel);

        await Assert.That(channel.Reports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Refused);
        await Assert.That(channel.Reports[0].Reason).IsEqualTo(FirstRunMachineActionReasons.UnsupportedPlatform);
    }

    /// <summary>A ladder that threw attempted something, so it is a failure and not a refusal — and the
    /// step still reports, because a screen left waiting on an outcome that threw is the state this lane
    /// exists to avoid.</summary>
    [Test]
    public async Task A_ladder_that_throws_is_reported_as_a_failure() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService));

        await RunAsync(channel, () => throw new InvalidOperationException("the manager query died"));

        await Assert.That(channel.Reports.Count).IsEqualTo(1);
        await Assert.That(channel.Reports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(channel.Reports[0].Reason).IsNull();
    }

    /// <summary>
    /// A refused report is retried, because there is no next tick here to retry on.
    ///
    /// <para><b>And the action is not repeated with it.</b> The ladder runs once; only the report is
    /// retried — repeating a service transaction to deliver its own result would be a second mutation for
    /// a message that failed.</para>
    /// </summary>
    [Test]
    public async Task A_refused_report_is_retried_without_repeating_the_action() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService), 500, 200);
        var runs    = 0;

        await RunAsync(channel, () => {
            runs++;

            return Task.FromResult<ServiceEnsureJson?>(new("kcap", "running", "none", "already_enabled"));
        });

        await Assert.That(runs).IsEqualTo(1);
        await Assert.That(channel.Reports.Count).IsEqualTo(2);
    }

    /// <summary>A report nothing ever takes stops rather than looping: the request staying outstanding is
    /// the honest reading, and the screen goes on saying it asked.</summary>
    [Test]
    public async Task A_report_that_never_lands_gives_up_bounded() {
        var channel = new Channel(View(FirstRunMachineCapabilities.DaemonService), 500, 500, 500, 500, 500);

        await RunAsync(channel);

        await Assert.That(channel.Reports.Count).IsEqualTo(3);
    }
}
