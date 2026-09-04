using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The loop, the guards and the backoff — everything FirstRunFlowPoll was extracted out of. Driven
// over a fake channel and a FakeTimeProvider, so none of it needs a socket or a wall clock.
public class BrowserFirstRunFlowTests {
    const string Server = "https://acme.kcap.ai";

    static readonly DateTimeOffset ClockBase = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An order of events, so "created before opened" is assertable rather than inferred.</summary>
    sealed class Log {
        readonly List<string> _entries = [];

        public IReadOnlyList<string> Entries => _entries;

        public void Add(string entry) => _entries.Add(entry);

        /// <summary>Whether <paramref name="first"/> was logged before <paramref name="second"/>. -1 for an
        /// absent entry makes a missing one read as "before", so callers assert its presence too.</summary>
        public bool Precedes(string first, string second) =>
            _entries.IndexOf(first) < _entries.IndexOf(second);
    }

    sealed class FakeChannel(Log log, FakeTimeProvider? clock = null) : IFirstRunFlowChannel {
        public Queue<FirstRunCreateOutcome> Creates { get; } = new();
        public Queue<FirstRunPollOutcome>   Polls   { get; } = new();

        public FirstRunPollOutcome Tail { get; set; } = new(200, Running());

        public List<string>                CreatedIds { get; } = [];
        public List<FirstRunMachineReport> Reports    { get; } = [];
        public List<DateTimeOffset> PollTimes  { get; } = [];
        public int                 PollCount  { get; private set; }

        public List<ReportFirstRunMachineActionRequest> ActionReports { get; } = [];

        /// <summary>Status for each report in turn; the last one repeats. A non-2xx is how the retry
        /// path is driven.</summary>
        public Queue<int> ReportStatuses { get; } = new();

        public Task<FirstRunCreateOutcome> CreateAsync(
                string serverUrl, string flowId, FirstRunMachineReport report, CancellationToken ct) {
            log.Add("create");
            CreatedIds.Add(flowId);
            Reports.Add(report);

            var outcome = Creates.Count > 0 ? Creates.Dequeue() : new FirstRunCreateOutcome(200, Running());

            // The real server echoes the id it was sent. A canned body carrying a different one is
            // the mismatch case, and is set up explicitly by the test that wants it.
            return Task.FromResult(outcome.Body is { FlowId: "" }
                ? outcome with { Body = outcome.Body with { FlowId = flowId } }
                : outcome);
        }

        /// <summary>Run on each poll, to model something happening to this process mid-wait.</summary>
        public Action? OnPoll { get; set; }

        public Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
            PollCount++;
            OnPoll?.Invoke();
            log.Add("poll");
            if (clock is not null) PollTimes.Add(clock.GetUtcNow());

            var outcome = Polls.Count > 0 ? Polls.Dequeue() : Tail;

            // The same echo as the create path: a canned body with an empty id becomes the id asked
            // for, and one carrying a different id is the mismatch case the test set up.
            return Task.FromResult(outcome.Body is { FlowId: "" }
                ? outcome with { Body = outcome.Body with { FlowId = flowId } }
                : outcome);
        }

        public List<ReportFirstRunImportRequest> ImportReports { get; } = [];

        /// <summary>Status for each import report in turn; the last one repeats.</summary>
        public Queue<int> ImportReportStatuses { get; } = new();

        public Task<FirstRunActionReportOutcome> ReportMachineActionAsync(
                string serverUrl, string flowId, ReportFirstRunMachineActionRequest report, CancellationToken ct) {
            log.Add("report");
            ActionReports.Add(report);

            return Task.FromResult(new FirstRunActionReportOutcome(
                ReportStatuses.Count > 0 ? ReportStatuses.Dequeue() : 200));
        }

        public Task<FirstRunImportReportOutcome> ReportImportAsync(
                string serverUrl, string flowId, ReportFirstRunImportRequest report, CancellationToken ct) {
            log.Add("import-report");
            ImportReports.Add(report);

            return Task.FromResult(new FirstRunImportReportOutcome(
                ImportReportStatuses.Count > 0 ? ImportReportStatuses.Dequeue() : 200));
        }

        public List<ReportFirstRunImportOutcomeRequest> OutcomeReports { get; } = [];

        /// <summary>Status per outcome report in turn; the last repeats. A non-2xx drives the retry.</summary>
        public Queue<int> OutcomeStatuses { get; } = new();

        /// <summary>Run on each outcome report, to model one that takes real time on the wire.</summary>
        public Action? OnOutcomeReport { get; set; }

        public Task<FirstRunImportReportOutcome> ReportImportOutcomeAsync(
                string serverUrl, string flowId, ReportFirstRunImportOutcomeRequest report,
                CancellationToken ct) {
            log.Add("outcome-report");
            OnOutcomeReport?.Invoke();
            OutcomeReports.Add(report);

            return Task.FromResult(new FirstRunImportReportOutcome(
                OutcomeStatuses.Count > 0 ? OutcomeStatuses.Dequeue() : 200));
        }

        public List<string> Relinquished { get; } = [];

        /// <summary>Status per relinquish in turn; the last repeats. A non-2xx is how "the leg finishes
        /// anyway" is driven.</summary>
        public Queue<int> RelinquishStatuses { get; } = new();

        /// <summary>Thrown on the next relinquish, to prove an unexpected fault does not escape the leg.</summary>
        public Exception? RelinquishThrows { get; set; }

        /// <summary>Run on each relinquish, to model something happening at that exact moment.</summary>
        public Action? OnRelinquish { get; set; }

        public Task<FirstRunRelinquishOutcome> RelinquishAsync(
                string serverUrl, string flowId, string reason, CancellationToken ct) {
            log.Add("relinquish");
            Relinquished.Add(reason);
            OnRelinquish?.Invoke();

            if (RelinquishThrows is { } boom) throw boom;

            return Task.FromResult(new FirstRunRelinquishOutcome(
                RelinquishStatuses.Count > 0 ? RelinquishStatuses.Dequeue() : 200));
        }

        /// <summary>Counted rather than logged: the beat runs on its own task off the fake clock, so
        /// putting it in the shared log would interleave it into every ordering assertion in this class
        /// at a point no test chose.</summary>
        public int Beats => Volatile.Read(ref _beats);

        int _beats;

        public Task<FirstRunHeartbeatOutcome> HeartbeatAsync(
                string serverUrl, string flowId, CancellationToken ct) {
            Interlocked.Increment(ref _beats);

            return Task.FromResult(new FirstRunHeartbeatOutcome(200));
        }
    }

    /// <summary>
    /// Stands in for the process-global sink, which is what keeps this class out of assembly-wide
    /// exclusion. It hands out the REAL <see cref="FirstRunNotice"/>, so the claim arbitration under test
    /// is the production one rather than a second implementation of it.
    /// </summary>
    sealed class FakeInterrupts(Log log) : IFirstRunInterrupts {
        readonly Log _log = log;

        // The notice itself is the LEG's to dispose, so only its interrupt entry point is held here.
        Action<TimeSpan>? _interrupt;

        public int Arms { get; private set; }

        public IFirstRunNotice Arm(Func<string, CancellationToken, Task> send, Func<string?> interruptReason) {
            Arms++;
            _log.Add("arm");

            var notice = new FirstRunNotice(send, interruptReason, onDispose: _ => _log.Add("disarm"));

            _interrupt = notice.RunBeforeExit;

            return notice;
        }

        /// <summary>Fires the interrupt path, as a signal handler would.</summary>
        public void Interrupt(TimeSpan? budget = null) =>
            _interrupt?.Invoke(budget ?? TimeSpan.FromSeconds(5));
    }

    sealed class RecordingProgress(Log log) : IFirstRunFlowProgress {
        public string? Url { get; private set; }
        public int Ticks    { get; private set; }
        public int WaitEnds { get; private set; }

        /// <summary>Every wait, in order, so "the unhealthy poll looked unhealthy" is assertable.</summary>
        public List<(FirstRunFlowStep? Step, bool Healthy)> Waits { get; } = [];

        /// <summary>Every step announced, in order. One entry per step is the whole point.</summary>
        public List<(FirstRunFlowStep Step, FirstRunStepOutcome Outcome, string? Detail)> Settles { get; } = [];

        public List<string> Performing { get; } = [];

        public void Opening(string setupUrl) {
            Url = setupUrl;
            log.Add("open");
        }

        public int Discoveries  { get; private set; }
        public int ImportEnds   { get; private set; }
        public int ActionsEnded { get; private set; }

        public List<(int Repos, int? Sessions)> Imports { get; } = [];

        /// <summary>Run when the wait ends, which is between the poll publishing its result and the leg
        /// sending its own reason — the one window where an interrupt can claim with a result in view.</summary>
        public Action? OnWaitEnded { get; set; }

        public void Waiting(FirstRunFlowStep? flowStep, bool healthy) {
            Ticks++;
            Waits.Add((flowStep, healthy));
            log.Add(healthy ? "waiting" : "unreachable");
        }

        public void Settled(FirstRunFlowStep flowStep, FirstRunStepOutcome outcome, string? detail) {
            log.Add("settled");
            Settles.Add((flowStep, outcome, detail));
        }

        public void WaitEnded() {
            WaitEnds++;
            OnWaitEnded?.Invoke();
        }

        public void PerformingAction(string capability) {
            log.Add("warn");
            Performing.Add(capability);
        }

        public void ActionEnded() {
            log.Add("action-ended");
            ActionsEnded++;
        }

        public void Discovering() {
            log.Add("discovering");
            Discoveries++;
        }

        public void Importing(int repos, int? sessions) {
            log.Add("importing");
            Imports.Add((repos, sessions));
        }

        public void ImportEnded() => ImportEnds++;
    }

    /// <summary>A host that can scan and import. <c>Found</c> is what the scan returns; null models a
    /// scan that produced nothing usable.</summary>
    sealed class FakeImportLane(Log log) : IFirstRunImportLane {
        public ReportFirstRunImportRequest? Found { get; set; } = Report();

        public List<IReadOnlyList<HarnessId>?> Scans   { get; } = [];
        public List<FirstRunImportAnswer>   Imports { get; } = [];

        /// <summary>Set to throw out of the scan, which must leave the screen waiting rather than
        /// reporting an empty disk.</summary>
        public Exception? ScanThrows { get; set; }

        /// <summary>Set to throw out of the import, which must not end the leg.</summary>
        public Exception? ImportThrows { get; set; }

        /// <summary>Run at the start of each half, to model one that takes real time. The loop's clock
        /// only moves from outside, so a lane that should look slow has to move it itself.</summary>
        public Action? Advance { get; set; }

        /// <summary>Awaited at the start of each half, for a lane that has to PARK rather than jump the
        /// clock — the poll loop is then genuinely suspended inside it, and Drive goes on pumping.
        /// Separate from <see cref="Advance"/> because blocking the loop's thread instead would starve
        /// the very continuation such a test is waiting on.</summary>
        public Func<Task>? Waits { get; set; }

        public List<DateTimeOffset> ScanStamps { get; } = [];

        public async Task<ReportFirstRunImportRequest?> DiscoverAsync(
                IReadOnlyList<HarnessId>? vendors, DateTimeOffset asOf, CancellationToken ct) {
            log.Add("scan");
            ScanStamps.Add(asOf);
            Advance?.Invoke();
            Scans.Add(vendors);

            if (Waits is { } wait) await wait();
            if (ScanThrows is { } boom) throw boom;

            return Found;
        }

        public List<DateOnly> Dates { get; } = [];

        /// <summary>What the run reports. Null models a run that lost a pass, whose counts are
        /// unaccounted rather than zero.</summary>
        public FirstRunImportTotals? Moved { get; set; } = new(3, 1, 0);

        public async Task<FirstRunImportTotals?> ImportAsync(
                FirstRunImportAnswer answer, DateOnly today, CancellationToken ct) {
            log.Add("import");
            Advance?.Invoke();
            Imports.Add(answer);
            Dates.Add(today);

            if (Waits is { } wait) await wait();
            if (ImportThrows is { } boom) throw boom;

            return Moved;
        }

        public static ReportFirstRunImportRequest Report(int sessions = 12) => new() {
            Repos = [
                new FirstRunImportRepoReport {
                    Owner    = "kurrent-io",
                    Name     = "kcap-server",
                    Sessions = new Dictionary<string, int> {
                        [FirstRunImportWindows.Last30]     = sessions,
                        [FirstRunImportWindows.Last90]     = sessions,
                        [FirstRunImportWindows.Everything] = sessions
                    }
                }
            ],
            Unmatched = new Dictionary<string, int>(),
            RepoTotal = 1
        };
    }

    /// <summary>A host that can act on the machine. <c>Results</c> is consumed in turn so a retry can be
    /// given a different answer from the first attempt.</summary>
    sealed class FakeActions(Log log, params string[] capabilities) : IFirstRunMachineActions {
        public IReadOnlyCollection<string> Capabilities { get; } = capabilities;

        public Queue<FirstRunMachineActionResult> Results { get; } = new();
        public List<string>                       Performed { get; } = [];

        /// <summary>Set to throw out of PerformAsync, which the loop has to turn into a reported failure
        /// rather than an unanswered request.</summary>
        public Exception? Throws { get; set; }

        public Task<FirstRunMachineActionResult> PerformAsync(string capability, CancellationToken ct) {
            log.Add("perform");
            Performed.Add(capability);

            if (Throws is { } ex) throw ex;

            return Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Installed, null));
        }
    }

    static FirstRunFlowResponse Running() => new() {
        FlowId    = "",
        Step      = "Agents",
        CanFinish = true,
        Steps     = new() { ["SignIn"] = "Completed", ["Agents"] = "Active", ["Import"] = "Pending", ["Done"] = "Pending" }
    };

    static FirstRunFlowResponse Done() => new() {
        FlowId    = "",
        Step      = "Done",
        CanFinish = true,
        Steps     = new() {
            ["SignIn"] = "Completed", ["Agents"] = "Completed", ["Import"] = "Skipped", ["Done"] = "Completed"
        }
    };

    /// <summary>A keyboard with ONE keypress: it appears when first looked for at or after
    /// <paramref name="pressAfter"/> (zero means it is already down when the wait starts), and is
    /// gone once drained — a real press, not a flag that re-presses at every look.</summary>
    sealed class FakeKeys(bool canWatch, int pressAfter = int.MaxValue, char key = BrowserFirstRunFlow.HandoverKey) : IKeyWatcher {
        int  _looks;
        bool _armed;   // the single press has been consumed
        bool _pressed; // the press is waiting to be drained

        public int Drains { get; private set; }

        public bool CanWatch => canWatch;

        public bool KeyAvailable {
            get {
                if (_pressed) return true;
                if (_armed) return false;

                if (_looks++ < pressAfter) return false;

                _armed = true;

                return _pressed = true;
            }
        }

        /// <summary>Consumes the press, as the real one does — a fake that left it buffered would spin
        /// the loop that reads past keys it does not act on.</summary>
        public char ReadKey() {
            _pressed = false;

            return key;
        }

        public void Drain() {
            Drains++;
            _pressed = false;
        }
    }

    sealed record Harness(
        BrowserFirstRunFlow Flow,
        FakeChannel         Channel,
        RecordingProgress   Progress,
        FakeTimeProvider    Clock,
        Log                 Log,
        List<string>        Opened,
        FakeKeys            Keys,
        FakeActions?        Actions,
        FakeImportLane?     Importing,
        FakeInterrupts      Interrupts);

    // No keyboard by default: the escape hatch is one test's subject, and left live it would read the
    // host's own console, where a stray keypress during a CI run would end an unrelated test's wait.
    /// <param name="capabilities">Non-null gives the flow a host that can act, advertising exactly these.
    /// The fake shares the harness's log, so "performed before reported" is assertable rather than inferred.</param>
    static Harness Build(FakeKeys? keys = null, string[]? capabilities = null, bool importing = false) {
        var log      = new Log();
        var clock    = new FakeTimeProvider(ClockBase);
        var channel  = new FakeChannel(log, clock);
        var progress = new RecordingProgress(log);
        var browser  = new RecordingBrowser();
        var actions  = capabilities is null ? null : new FakeActions(log, capabilities);
        var lane     = importing ? new FakeImportLane(log) : null;
        var signals  = new FakeInterrupts(log);

        keys ??= new FakeKeys(canWatch: false);

        return new(
            new BrowserFirstRunFlow(channel, progress, browser, clock, keys, actions, lane, signals),
            channel, progress, clock, log, browser.Urls, keys, actions, lane, signals);
    }

    static readonly string[] PathShimOnly = [FirstRunMachineCapabilities.PathShim];

    /// <summary>
    /// Runs the flow, pumping the fake clock while it waits.
    ///
    /// <para>The loop sleeps via <c>Task.Delay</c> on the injected provider, so a frozen fake never
    /// wakes it — time has to move from outside. The step matches the delay slices' granularity, so
    /// every wake lands exactly on a slice boundary.</para>
    /// </summary>
    static async Task<FirstRunFlowResult> Drive(Task<FirstRunFlowResult> running, FakeTimeProvider clock) {
        while (!running.IsCompleted) {
            clock.Advance(TimeSpan.FromMilliseconds(200));

            await Task.Yield();
        }

        return await running;
    }

    // codex is reported present-in-the-map and absent-on-the-machine, which is what makes "reported"
    // and "detected" two different sets — the distinction the refusal rule turns on.
    static readonly FirstRunMachineReport Report = new(
        "nostromo", "machine-1",
        new Dictionary<string, FirstRunHarnessReport> {
            ["claude"] = new() { BinaryOnPath = true,  ConfigFound = false, AlreadyWired = false },
            ["codex"]  = new() { BinaryOnPath = false, ConfigFound = false, AlreadyWired = false }
        },
        ["cursor"], LoginShellFindsCli: false);

    static Task<FirstRunFlowResult> Run(Harness h) =>
        Drive(h.Flow.RunAsync(Server, Report, CancellationToken.None), h.Clock);

    [Test]
    public async Task Creates_the_flow_BEFORE_opening_the_browser() {
        // The whole point of the ticket. Reversed, the first browser to open the link owns the flow,
        // and the server's ownership check has nothing to check against until one turns up.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Log.Entries[0]).IsEqualTo("create");
        await Assert.That(h.Log.Entries[1]).IsEqualTo("open");
    }

    // The create is the report's ONLY carrier: detection needs no auth and has already run, and the
    // Agents screen must find its rows populated rather than waiting on a second round trip. A retry
    // on a taken id carries it again, or the flow that survives is the one with no machine behind it.
    [Test]
    public async Task Carries_the_machine_report_on_every_create_attempt() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(409, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.Reports.Count).IsEqualTo(2);

        // Every field, not just the name: a retry that rebuilt an empty report would keep the machine
        // tag and lose exactly what the screen renders from.
        await Assert.That(h.Channel.Reports.All(r => ReferenceEquals(r, Report))).IsTrue();
    }

    [Test]
    public async Task Opens_the_setup_url_it_composed_itself() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var id = h.Channel.CreatedIds.Single();

        // Composed locally from an origin already probed and signed in to, which is why there is no
        // origin check here to match the retired pairing's: no server-supplied URL ever reaches the
        // shell-executed open.
        await Assert.That(h.Opened.Single()).IsEqualTo($"{Server}/setup?s={id}");
        await Assert.That(h.Progress.Url).IsEqualTo(h.Opened.Single());
    }

    [Test]
    public async Task Sends_a_flow_id_the_server_will_accept() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.CreatedIds.Single()).Length().IsEqualTo(22);
    }

    [Test]
    public async Task Finishes_when_every_step_has_settled() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.PollCount).IsEqualTo(2);
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task Polls_once_before_its_first_sleep() {
        // A flow the browser has already finished — a resumed link, or a tab quicker than this
        // process — should not wait out an interval to be noticed.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
        await Assert.That(h.Clock.GetUtcNow()).IsEqualTo(ClockBase);
    }

    [Test]
    [Arguments(404)]
    [Arguments(401)]
    [Arguments(403)]
    [Arguments(405)]
    public async Task Reads_a_missing_route_as_unavailable__and_never_opens_a_browser(int status) {
        // The routes are mapped only on a tenant that has the flow turned on, so their absence is a
        // fact to observe rather than a server version to guess at. A gateway answering 401/403/405
        // on a route it does not know is indistinguishable from that.
        var h = Build();
        h.Channel.Creates.Enqueue(new(status, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Unavailable>();
        await Assert.That(h.Opened).IsEmpty();
        await Assert.That(h.Channel.PollCount).IsEqualTo(0);
    }

    [Test]
    public async Task Reports_a_429_with_the_servers_own_retry_after() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(429, null, TimeSpan.FromMinutes(10)));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.RateLimited>();
        await Assert.That(((FirstRunFlowResult.RateLimited)result).RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Falls_back_to_ten_minutes_when_a_429_carries_no_retry_after() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(429, null));

        var result = await Run(h);

        await Assert.That(((FirstRunFlowResult.RateLimited)result).RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task Retries_a_409_with_a_FRESH_id() {
        // 409 means the id belongs to someone else, not that the credentials are wrong — which is
        // exactly why the server chose that status over a 403. Retrying the SAME id would loop.
        var h = Build();
        h.Channel.Creates.Enqueue(new(409, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.CreatedIds).Count().IsEqualTo(2);
        await Assert.That(h.Channel.CreatedIds[0]).IsNotEqualTo(h.Channel.CreatedIds[1]);
    }

    [Test]
    public async Task Gives_up_after_three_conflicting_ids() {
        var h = Build();

        for (var i = 0; i < 4; i++) h.Channel.Creates.Enqueue(new(409, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(h.Channel.CreatedIds).Count().IsEqualTo(3);
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Refuses_a_create_that_answers_about_a_different_flow() {
        // Impossible against the server this was written for, which is why a disagreement is worth
        // stopping on rather than polling an id this process never generated.
        var h = Build();
        h.Channel.Creates.Enqueue(new(200, Running() with { FlowId = "someoneelsesflowid1234" }));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Reports_a_transport_failure_on_create_as_unreachable() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(0, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("reach");
    }

    [Test]
    public async Task Reports_a_200_create_with_an_unreadable_body_as_failed() {
        // Distinct from a refusal: the server answered, and the reply was not readable by this build.
        // The message must not quote the success status as though the server rejected the request.
        var h = Build();
        h.Channel.Creates.Enqueue(new(200, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("could not be read");
        await Assert.That(h.Opened).IsEmpty();
    }

    [Test]
    public async Task Ends_on_a_410() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(410, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Expired>();
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task Ends_on_a_404_rather_than_polling_a_flow_that_will_never_be_ours() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(404, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
    }

    [Test]
    public async Task Ends_on_a_401_with_a_message_of_its_own() {
        // Distinct from a 404's: the authenticated client refreshes on a 401 once, so meeting this at
        // all means the refresh failed — the remedy is a re-login, not a new link, and the copy says so.
        var h = Build();
        h.Channel.Polls.Enqueue(new(401, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("kcap login");
    }

    [Test]
    public async Task Keeps_waiting_through_a_5xx_and_a_transport_blip() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(500, null));
        h.Channel.Polls.Enqueue(new(0,   null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();

        // Two unhappy polls and the good one that ended it: the wait is set from every poll that
        // produced a state, including the finishing one, so the line cannot outlive what it describes.
        await Assert.That(h.Progress.Waits.Count(w => !w.Healthy)).IsEqualTo(2);
        await Assert.That(h.Progress.Ticks).IsEqualTo(3);
    }

    [Test]
    public async Task Backs_off_on_a_429_and_keeps_polling() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        // Two polls, and the gap between them longer than the base interval it started on.
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Gives_up_after_its_own_budget__not_the_flows_twelve_hours() {
        // The commonest way this ends unfinished is a closed tab, and the flow's TTL is sized for a
        // link surviving a working day rather than for a terminal sitting open on one. The backstop
        // is not extended: no poll fires once the deadline has passed.
        var h = Build();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThanOrEqualTo(TimeSpan.FromMinutes(31));
        await Assert.That(h.Channel.PollTimes[^1]).IsLessThan(ClockBase + TimeSpan.FromMinutes(30));
        await Assert.That(((FirstRunFlowResult.Abandoned)result).View).IsNotNull();
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task A_keypress_during_the_wait_ends_it_without_waiting_out_the_budget() {
        // The answer to a closed tab. Thirty minutes of dots is a backstop for a terminal nobody is
        // sitting at, not something to make a person who IS sitting there watch. The press lands
        // while the first interval's delay is being slept, not on the pre-wait drain.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 2));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThan(TimeSpan.FromMinutes(1));
        await Assert.That(h.Progress.WaitEnds).IsEqualTo(1);
    }

    [Test]
    public async Task A_keypress_that_preceded_the_wait_is_drained__not_treated_as_a_dismiss() {
        // A byte left in stdin from an earlier step — the Return that confirmed "Logged in as …" —
        // is not an answer to "press any key to carry on here". It is drained once before the prompt
        // renders, and the flow goes on polling rather than dismissing on it.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 0));

        var result = await Run(h);

        await Assert.That(h.Keys.Drains).IsEqualTo(1);
        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
    }

    [Test]
    public async Task A_keypress_made_in_response_to_the_prompt_is_a_real_dismissal() {
        // The pre-wait drain exists for keys that preceded the leg; a key pressed after the prompt
        // has rendered is a genuine "carry on here" and must dismiss — not be drained as stale, as
        // the sibling test's pre-prompt key is. The one drain here is the dismissal's own, so the
        // key's trailing Return is not the next prompt's answer.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 1));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Keys.Drains).IsEqualTo(1);
    }

    [Test]
    public async Task A_key_that_is_not_the_handover_key_is_consumed_and_the_wait_carries_on() {
        // Enter and Space get pressed by accident, and a stray byte must not hand a half-finished
        // browser flow back to the terminal. Consumed rather than left buffered, or the same byte
        // would be re-examined on every slice for the rest of the wait.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 1, key: '\r'));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Keys.Drains).IsEqualTo(0);
    }

    // --- What the terminal is told ---

    [Test]
    public async Task Each_settled_step_is_announced_once__however_many_polls_repeat_it() {
        // Edge-triggered off the loop's own record, not off what the renderer last printed: a poll
        // blip that returns the same state would otherwise tick the same step twice.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Select(x => x.Step))
                    .IsEquivalentTo([
                        FirstRunFlowStep.SignIn, FirstRunFlowStep.Agents,
                        FirstRunFlowStep.Import, FirstRunFlowStep.Done
                    ]);
    }

    [Test]
    public async Task A_resumed_link_announces_everything_that_had_already_settled() {
        // The honest history, and what tells the user it is the same link rather than a fresh one.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Count).IsEqualTo(4);
        await Assert.That(h.Progress.Settles[0].Step).IsEqualTo(FirstRunFlowStep.SignIn);
    }

    [Test]
    public async Task The_step_outcome_crosses_as_the_server_reported_it() {
        // Import is Skipped on the Done fixture, and a skip is not a tick — the renderer draws the
        // glyph off this, so collapsing every settled step to "completed" would show four ticks for a
        // flow that skipped one.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Single(x => x.Step == FirstRunFlowStep.Import).Outcome)
                    .IsEqualTo(FirstRunStepOutcome.Skipped);
    }

    [Test]
    public async Task The_agents_detail_names_the_harnesses_and_nothing_else_carries_one() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Single(x => x.Step == FirstRunFlowStep.Agents).Detail)
                    .IsEqualTo("Claude Code");
        await Assert.That(h.Progress.Settles.Single(x => x.Step == FirstRunFlowStep.SignIn).Detail)
                    .IsNull();
    }

    [Test]
    public async Task An_answer_changed_in_the_browser_is_announced_again() {
        // Back, change the harnesses, re-confirm. The step stays settled, so presence alone would
        // suppress the second tick and leave the terminal's only statement about what step 4 installs
        // naming the answer that was abandoned.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("cursor") with {
            AgentsDecidedAt = ClockBase.AddMinutes(1)
        }));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(string.Join(" | ", h.Progress.Settles
                                                .Where(x => x.Step == FirstRunFlowStep.Agents)
                                                .Select(x => x.Detail)))
                    .IsEqualTo("Claude Code | Cursor");
    }

    [Test]
    public async Task Re_confirming_the_same_answer_is_not_announced_again() {
        // The server advances the stamp only when the answer CHANGES, which is what makes it the
        // trigger — re-confirming is not a new fact and must not print a second identical tick.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Count(x => x.Step == FirstRunFlowStep.Agents)).IsEqualTo(1);
    }

    [Test]
    public async Task A_view_that_carries_no_answer_does_not_un_say_the_one_it_had() {
        // The stamp is absent exactly when the decision is, so a later view without either tells us
        // nothing new. Re-announcing off its absence would say "no agents to set up" about a user who
        // chose some — the Done fixture is that view.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Settles.Single(x => x.Step == FirstRunFlowStep.Agents).Detail)
                    .IsEqualTo("Claude Code");
    }

    [Test]
    public async Task The_wait_is_updated_before_the_scan_it_starts__not_after_it() {
        // A scan runs for as long as the disk takes, and the poll that triggers it is also the poll
        // that disproved the unreachable warning. Setting the wait afterwards spends the whole scan
        // repeating a warning about a server that has just answered.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(503, null));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var order = h.Log.Entries.ToList();

        await Assert.That(order.IndexOf("waiting")).IsGreaterThanOrEqualTo(0);
        await Assert.That(order.IndexOf("waiting")).IsLessThan(order.IndexOf("scan"));
    }

    [Test]
    public async Task An_unhappy_poll_says_so_and_keeps_the_last_step_it_knew() {
        // A spinner naming the screen the user is supposedly on, off a poll that answered nothing, is
        // a confident lie — the flag is what lets the renderer say the server has gone quiet instead.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(503, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Waits[0]).IsEqualTo((FirstRunFlowStep.Agents, true));
        await Assert.That(h.Progress.Waits[1]).IsEqualTo((FirstRunFlowStep.Agents, false));
    }

    [Test]
    public async Task An_unhappy_first_poll_has_no_step_to_name_at_all() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(503, null));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Waits[0]).IsEqualTo(((FirstRunFlowStep?)null, false));
    }

    [Test]
    public async Task A_keypress_during_a_backoff_delay_ends_the_wait_promptly() {
        // A 429 widens the gap; a key pressed while that longer delay is being slept must still end
        // the wait within a slice, not after the whole widened interval.
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 4));
        h.Channel.Polls.Enqueue(new(429, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThan(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task Rejects_a_poll_that_answers_about_a_different_flow() {
        // The create path's guard, applied to the poll: the server echoes the id, so a disagreement
        // is a malformed or misrouted response — not something to report as this flow's outcome.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running() with { FlowId = "someoneelsesflowid1234" }));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(((FirstRunFlowResult.Failed)result).Message).Contains("different setup link");
    }

    [Test]
    public async Task Widens_the_gap_after_an_unhappy_poll_and_snaps_back_after_a_good_one() {
        // The 2s cadence is for a healthy flow with a human clicking; an unhappy poll doubles the
        // gap so a down or rate-limiting server is not hammered, and a good state restores the cadence.
        var h = Build();
        h.Channel.Polls.Enqueue(new(0,   null));        // transport blip → gap doubles
        h.Channel.Polls.Enqueue(new(200, Running()));   // healthy → gap back to base
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(4));
        await Assert.That(h.Channel.PollTimes[2] - h.Channel.PollTimes[1]).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task Honours_a_poll_429s_retry_after() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromSeconds(10)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Honours_a_poll_429s_retry_after_beyond_the_local_cap() {
        // The server's Retry-After is its rate-limit window: 60s stays 60s even though a locally
        // computed gap would never exceed 30s.
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromSeconds(60)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.PollTimes[1] - h.Channel.PollTimes[0]).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Test]
    public async Task A_retry_after_longer_than_the_budget_does_not_extend_the_wait() {
        // The budget is the backstop, not the interval: a route that asks for an hour must not turn
        // a 30-minute wait into an hour-long one — even on a host with no keyboard to end it early.
        var h = Build();
        h.Channel.Polls.Enqueue(new(429, null, TimeSpan.FromHours(1)));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThanOrEqualTo(TimeSpan.FromMinutes(31));
        await Assert.That(h.Channel.PollCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_keyboard_that_cannot_be_watched_is_never_read() {
        // Redirected stdin, or no console at all. Polling it would throw, and the flow must not care.
        var h = Build(new FakeKeys(canWatch: false, pressAfter: 0));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Keys.Drains).IsEqualTo(0);
    }

    static readonly DateTimeOffset Asked = new(2026, 8, 21, 12, 5, 0, TimeSpan.Zero);

    /// <summary>A running flow with one outstanding request on it.</summary>
    static FirstRunFlowResponse Asking(
            string capability = FirstRunMachineCapabilities.PathShim, DateTimeOffset? requestedAt = null) =>
        Running() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = capability, RequestedAt = requestedAt ?? Asked
            }]
        };

    [Test]
    public async Task An_advertised_request_is_performed_and_reported_against_its_own_timestamp() {
        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Results.Enqueue(new(FirstRunMachineActionOutcomes.Cancelled, null));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions.Performed).IsEquivalentTo(PathShimOnly);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports[0].Capability).IsEqualTo(FirstRunMachineCapabilities.PathShim);
        await Assert.That(h.Channel.ActionReports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Cancelled);

        // The request's own stamp, not the clock's: the server drops a report answering a superseded ask.
        await Assert.That(h.Channel.ActionReports[0].RequestedAt).IsEqualTo(Asked);
    }

    [Test]
    public async Task The_user_is_warned_before_the_action_runs_not_after() {
        // The shim raises an admin-password dialog. Warned afterwards, it has already appeared.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Progress.Performing).IsEquivalentTo(PathShimOnly);

        var entries = h.Log.Entries.ToList();

        await Assert.That(entries.IndexOf("warn")).IsGreaterThanOrEqualTo(0);
        await Assert.That(entries.IndexOf("perform")).IsGreaterThan(entries.IndexOf("warn"));
        await Assert.That(entries.IndexOf("report")).IsGreaterThan(entries.IndexOf("perform"));
    }

    [Test]
    public async Task A_capability_this_host_does_not_advertise_is_left_alone_rather_than_failed() {
        // Reporting it would tell the screen the fix was tried. It was not, and the request stays
        // outstanding so a newer CLI can still answer it.
        var h = Build(capabilities: []);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed).IsEmpty();
        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }

    [Test]
    public async Task A_capability_this_build_cannot_name_is_never_performed() {
        // Dropped at the mapping boundary, so a host that happens to advertise the same string still
        // never sees it: the closed set is what keeps "a named capability" from meaning "whatever
        // the server said".
        var h = Build(capabilities: ["reboot_the_laptop"]);
        h.Channel.Polls.Enqueue(new(200, Asking("reboot_the_laptop")));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed).IsEmpty();
    }

    [Test]
    public async Task The_same_request_seen_twice_performs_once() {
        // The poll returns the request until the report lands, and the report lands after the action.
        // Without the guard the second sighting raises a second admin prompt for a fix already made.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_report_that_did_not_land_is_retried_without_performing_again() {
        var h = Build(capabilities: PathShimOnly);
        h.Channel.ReportStatuses.Enqueue(500);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_fresh_request_performs_again() {
        // A second press after an outcome is a retry, and the timestamp is what says so — the
        // capability alone cannot tell a retry from the request already answered.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Polls.Enqueue(new(200, Asking(requestedAt: Asked.AddMinutes(1))));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(2);
        await Assert.That(h.Channel.ActionReports.Select(r => r.RequestedAt).ToList())
                    .IsEquivalentTo(new[] { Asked, Asked.AddMinutes(1) });
    }

    [Test]
    public async Task An_action_that_throws_is_reported_as_failed() {
        // A screen waiting on an outcome that never comes is the state this lane exists to avoid.
        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Throws = new InvalidOperationException("osascript went missing");
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        await Run(h);

        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports[0].Outcome).IsEqualTo(FirstRunMachineActionOutcomes.Failed);
        await Assert.That(h.Channel.ActionReports[0].Reason).IsNull();
    }

    [Test]
    public async Task A_request_riding_the_poll_that_finishes_the_flow_is_still_performed() {
        // The user presses the button and the browser settles the last step before the next tick. The
        // request was made, so it is owed an attempt.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_host_with_no_actions_performs_nothing_and_still_finishes() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Asking()));
        h.Channel.Tail = new(200, Done());

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }

    [Test]
    public async Task An_outcome_that_did_not_land_on_the_finishing_tick_is_flushed() {
        // The per-tick retry needs a next tick and this tick has none, so without the flush a single blip
        // loses an outcome for a fix that really happened.
        var h = Build(capabilities: PathShimOnly);
        h.Channel.ReportStatuses.Enqueue(500);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Actions!.Performed.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_finished_flow_is_not_held_open_by_a_report_that_never_lands() {
        // The request stays outstanding server-side, which is the honest reading; what must not happen is
        // reporting a finished flow as abandoned because a report kept failing.
        var h = Build(capabilities: PathShimOnly);

        for (var i = 0; i < 10; i++) h.Channel.ReportStatuses.Enqueue(500);

        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();

        // The tick's own attempt plus its two retries, and then it stops.
        await Assert.That(h.Channel.ActionReports.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_cancel_during_the_action_ends_the_leg_rather_than_finishing_it() {
        // Every other await here lets the caller's cancel out. Swallowing it would let a cancelled setup
        // resolve as Finished, reporting a flow as complete that the caller stopped.
        using var cts = new CancellationTokenSource();

        var h = Build(capabilities: PathShimOnly);
        h.Actions!.Throws = new OperationCanceledException(cts.Token);
        h.Channel.Tail = new(200, Done() with {
            MachineActions = [new FirstRunMachineActionResponse {
                Capability = FirstRunMachineCapabilities.PathShim, RequestedAt = Asked
            }]
        });

        await cts.CancelAsync();

        await Assert.That(async () => await Drive(h.Flow.RunAsync(Server, Report, cts.Token), h.Clock))
            .Throws<OperationCanceledException>();

        await Assert.That(h.Channel.ActionReports).IsEmpty();
    }

    // =====================================================================
    // The Import lane: a scan gated on the Agents answer, a report retried
    // until it lands, and a decision run once per distinct answer.
    // =====================================================================

    static readonly DateTimeOffset Decided = new(2026, 8, 21, 12, 5, 0, TimeSpan.Zero);

    /// <summary>A view whose Agents step has settled, carrying <paramref name="records"/> as the
    /// vendors something was turned on for.</summary>
    static FirstRunFlowResponse AgentsAnswered(params string[] records) => new() {
        FlowId          = "",
        Step            = "Import",
        CanFinish       = true,
        Steps           = new() {
            ["SignIn"] = "Completed", ["Agents"] = "Completed", ["Import"] = "Active", ["Done"] = "Pending"
        },
        Agents          = [.. records.Select(v => new FirstRunAgentChoiceResponse { Vendor = v, Record = true, Tools = true })],
        AgentsDecidedAt = ClockBase
    };

    static FirstRunFlowResponse ImportAnswered(
            string?         window   = null,
            string          titles   = "Server",
            string          level    = "Shared",
            DateTimeOffset? decidedAt = null,
            bool            noRepos  = false) {
        var view = AgentsAnswered("claude");

        return view with {
            Steps = new() {
                ["SignIn"] = "Completed", ["Agents"] = "Completed", ["Import"] = "Completed", ["Done"] = "Pending"
            },
            Import = new FirstRunImportDecisionResponse {
                Window = window ?? FirstRunImportWindows.Last90,
                Titles = titles,
                Repos  = noRepos
                    ? []
                    : [new FirstRunImportRepoChoiceResponse { Owner = "kurrent-io", Name = "kcap-server", Level = level }]
            },
            ImportDecidedAt = decidedAt ?? Decided
        };
    }

    [Test]
    public async Task Waits_for_the_Agents_answer_before_scanning_because_it_is_the_vendor_filter() {
        // Scanning on the first poll would find no answer to filter by and so scan everything —
        // reporting figures for agents the user was about to decline. The screen's whole job is to
        // state what a selection will import, so the count and the selection have to agree.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Scans.Single()).DoesNotContain(HarnessId.Claude);
    }

    [Test]
    public async Task Scans_every_vendor_except_one_this_machine_offered_and_the_user_declined() {
        // Only an EXPLICIT refusal drops a vendor. The report detects claude alone, so declining
        // everything refuses claude and nothing else — a vendor with history on disk but nothing
        // installed now was never offered, and its absence is not a refusal.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var scanned = h.Importing!.Scans.Single()!;

        await Assert.That(scanned).DoesNotContain(HarnessId.Claude);
        await Assert.That(scanned).Contains(HarnessId.Gemini).Because("never reported, so never offered");
        await Assert.That(scanned).Contains(HarnessId.Cursor).Because("declined locally is not offered, so not refused here");
    }

    [Test]
    public async Task A_vendor_this_machine_reported_but_did_not_find_was_never_offered_to_refuse() {
        // History on disk with nothing installed now: the screen had no row for it, so its absence
        // from the answer says nothing, and dropping it would silently discard that history.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Scans.Single()!).Contains(HarnessId.Codex);
    }

    [Test]
    public async Task Keeps_a_vendor_the_user_did_turn_on() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Scans.Single()!).Contains(HarnessId.Claude);
    }

    [Test]
    public async Task Retries_the_report_until_it_lands_without_scanning_again() {
        // The scan costs minutes; the POST costs a round trip. Only one of them is worth repeating.
        var h = Build(importing: true);
        h.Channel.ImportReportStatuses.Enqueue(500);
        h.Channel.ImportReportStatuses.Enqueue(200);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Scans.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ImportReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Stops_reporting_once_the_server_has_taken_it() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.ImportReports.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_scan_that_throws_leaves_the_screen_waiting_rather_than_claiming_an_empty_disk() {
        var h = Build(importing: true);
        h.Importing!.ScanThrows = new IOException("disk went away");
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(h.Channel.ImportReports).IsEmpty();
        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>()
                    .Because("a failed scan is not a failed setup");
    }

    [Test]
    public async Task A_scan_that_finds_nothing_usable_reports_nothing_and_is_not_retried() {
        // Distinct from a throw only in how it got there; both mean nothing was learned.
        var h = Build(importing: true);
        h.Importing!.Found = null;
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Scans.Count).IsEqualTo(1);
        await Assert.That(h.Channel.ImportReports).IsEmpty();
    }

    [Test]
    public async Task Runs_the_import_once_the_decision_lands() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var ran = h.Importing!.Imports.Single();

        await Assert.That(ran.Window).IsEqualTo(FirstRunImportWindows.Last90);
        await Assert.That(ran.Choices.Single().Slug).IsEqualTo("kurrent-io/kcap-server");
        await Assert.That(ran.Choices.Single().Level).IsEqualTo(FirstRunImportLevel.Shared);
    }

    [Test]
    public async Task Does_not_run_the_same_answer_twice() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Imports.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Runs_again_when_the_answer_changes() {
        // Going Back and widening the window has real work in it, and the server moves the stamp only
        // when the answer changed — so the stamp is the cursor rather than a "done" flag.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(FirstRunImportWindows.Last30)));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(FirstRunImportWindows.Everything, decidedAt: Decided.AddMinutes(1))));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Imports.Select(i => i.Window))
                    .IsEquivalentTo([FirstRunImportWindows.Last30, FirstRunImportWindows.Everything]);
    }

    [Test]
    public async Task Import_nothing_runs_nothing_but_still_counts_as_answered() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(noRepos: true)));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(noRepos: true)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Imports).IsEmpty();
        await Assert.That(h.Progress.Imports).IsEmpty().Because("there is nothing to announce");
    }

    [Test]
    public async Task Runs_nothing_when_it_could_read_no_vendor_the_decision_named() {
        // Scanning nothing would import nothing while the summary claimed a successful import.
        var h = Build(importing: true);
        var view = ImportAnswered();
        h.Channel.Polls.Enqueue(new(200, view with {
            Import = view.Import! with { Vendors = ["telepathy"] }
        }));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Imports).IsEmpty();
        await Assert.That(h.Progress.Imports).IsEmpty();
    }

    [Test]
    public async Task Reports_what_the_run_moved_against_the_decision_that_ran() {
        var h = Build(importing: true);
        h.Importing!.Moved = new FirstRunImportTotals(7, 2, 1);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var sent = h.Channel.OutcomeReports.Single();

        await Assert.That((sent.Imported, sent.Skipped, sent.Failed)).IsEqualTo((7, 2, 1));
        await Assert.That(sent.Reason).IsNull();
        await Assert.That(sent.DecidedAt).IsEqualTo(Decided)
                    .Because("the answer that ran, not whichever is standing when the run ends");
    }

    [Test]
    public async Task An_undelivered_outcome_keeps_its_own_stamp_when_the_decision_moves_under_it() {
        // The report is held across ticks, so this is the one place the stamp CAN go wrong: re-stamping
        // the retry with whatever is standing would attach the first run's counts to an answer it never
        // ran, and the server would then discard both — the earlier one as superseded, the later one as
        // already answered.
        var h     = Build(importing: true);
        var later = Decided.AddMinutes(3);

        h.Channel.OutcomeStatuses.Enqueue(503);   // the first run's report is refused...
        h.Channel.OutcomeStatuses.Enqueue(200);   // ...and taken on the next tick, still as its own
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(FirstRunImportWindows.Last30)));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(FirstRunImportWindows.Everything, decidedAt: later)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.OutcomeReports.Select(r => r.DecidedAt))
                    .IsEquivalentTo([Decided, Decided, later]);
    }

    [Test]
    public async Task Reports_a_decision_it_cannot_read_as_a_refusal_rather_than_polling_it_forever() {
        // A window this build cannot map never becomes readable by asking again, so the cursor moves and
        // the screen is told why. Without the stamp this re-evaluated the same answer on every tick and
        // reported nothing at all.
        var h    = Build(importing: true);
        var view = ImportAnswered();
        var stuck = view with { Import = view.Import! with { Window = "since_the_dawn_of_time" } };

        h.Channel.Polls.Enqueue(new(200, stuck));
        h.Channel.Polls.Enqueue(new(200, stuck));
        h.Channel.Polls.Enqueue(new(200, stuck));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var sent = h.Channel.OutcomeReports.Single();

        await Assert.That(sent.Reason).IsEqualTo(FirstRunImportOutcomeReasons.DecisionUnreadable);
        await Assert.That((sent.Imported, sent.Skipped, sent.Failed)).IsEqualTo((0, 0, 0));
        await Assert.That(sent.DecidedAt).IsEqualTo(Decided);
        await Assert.That(h.Importing!.Imports).IsEmpty();
    }

    [Test]
    public async Task Reports_no_readable_vendor_as_a_refusal_and_not_as_three_zeroes() {
        // Three zeroes are also a clean run over an already-loaded history. The token is what stops the
        // screen calling "nothing could be read" a successful import.
        var h    = Build(importing: true);
        var view = ImportAnswered();

        h.Channel.Polls.Enqueue(new(200, view with { Import = view.Import! with { Vendors = ["telepathy"] } }));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.OutcomeReports.Single().Reason)
                    .IsEqualTo(FirstRunImportOutcomeReasons.NoReadableAgents);
    }

    [Test]
    public async Task Reports_a_clean_zero_when_the_user_chose_to_import_nothing() {
        // Answered, with nothing to do — but the outcome is also how the screen learns the machine has
        // finished, so silence here leaves it unable to tell this from a stalled run.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(noRepos: true)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var sent = h.Channel.OutcomeReports.Single();

        await Assert.That((sent.Imported, sent.Skipped, sent.Failed)).IsEqualTo((0, 0, 0));
        await Assert.That(sent.Reason).IsNull().Because("nothing was refused; nothing was asked for");
    }

    /// <summary>Pins the wire literal, not the constant: a mistyped token would keep a constant-to-constant
    /// compare green while the server rejected every report.</summary>
    [Test]
    public async Task Reports_a_token_and_no_figures_for_a_run_that_lost_a_pass() {
        var h = Build(importing: true);
        h.Importing!.Moved = null;
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing.Imports).HasSingleItem().Because("the run still happened");

        var sent = h.Channel.OutcomeReports.Single();

        await Assert.That(sent.Reason).IsEqualTo("run_failed");
        await Assert.That((sent.Imported, sent.Skipped, sent.Failed)).IsEqualTo((0, 0, 0))
                    .Because("a partial tally would put a measured-looking zero where nobody measured");
    }

    [Test]
    public async Task Retries_the_outcome_until_it_lands_without_importing_again() {
        var h = Build(importing: true);
        h.Channel.OutcomeStatuses.Enqueue(500);
        h.Channel.OutcomeStatuses.Enqueue(500);
        h.Channel.OutcomeStatuses.Enqueue(200);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Imports).HasSingleItem();
        await Assert.That(h.Channel.OutcomeReports.Count).IsEqualTo(3)
                    .Because("retried until taken, then never again");
    }

    [Test]
    public async Task Flushes_an_owed_outcome_when_the_flow_finishes_in_the_same_tick() {
        // The poll returns on a finished flow, so the tick that reports is also the last one that
        // exists. Without the flush an outcome refused once would never be sent at all.
        var h = Build(importing: true);
        h.Channel.OutcomeStatuses.Enqueue(503);
        h.Channel.OutcomeStatuses.Enqueue(200);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered() with {
            Steps = new() {
                ["SignIn"] = "Completed", ["Agents"] = "Completed",
                ["Import"] = "Completed", ["Done"] = "Completed"
            }
        }));

        await Run(h);

        await Assert.That(h.Channel.OutcomeReports.Count).IsEqualTo(2);
    }

    [Test]
    public async Task An_answer_left_empty_because_its_levels_were_unreadable_is_not_a_decline() {
        // Choices.Count == 0 has two causes and only one of them is "the user chose nothing". A newer
        // server's level empties the list too, and reporting THAT as a clean zero tells the screen the
        // user declined an import they actually asked for.
        var h    = Build(importing: true);
        var view = ImportAnswered();

        h.Channel.Polls.Enqueue(new(200, view with {
            Import = view.Import! with {
                Repos = [new FirstRunImportRepoChoiceResponse {
                    Owner = "kurrent-io", Name = "kcap-server", Level = "EveryoneOnTheInternet"
                }]
            }
        }));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        var sent = h.Channel.OutcomeReports.Single();

        await Assert.That(sent.Reason).IsEqualTo(FirstRunImportOutcomeReasons.DecisionUnreadable);
        await Assert.That((sent.Imported, sent.Skipped, sent.Failed)).IsEqualTo((0, 0, 0));
        await Assert.That(h.Importing!.Imports).IsEmpty();
    }

    [Test]
    public async Task A_stalled_outcome_report_does_not_extend_the_flows_own_backstop() {
        // The report is retried on every tick for as long as it is owed, so crediting its time back to
        // the poll budget — the way the import and the scan are credited — would let a server that never
        // accepts it stretch a 30-minute backstop into hours. A refused report is a blip, not progress.
        var h = Build(importing: true);

        h.Channel.Tail            = new(200, ImportAnswered());
        h.Channel.OnOutcomeReport = () => h.Clock.Advance(TimeSpan.FromSeconds(15));

        // Never taken, so it stays owed for the life of the flow.
        for (var i = 0; i < 5_000; i++) h.Channel.OutcomeStatuses.Enqueue(503);

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Clock.GetUtcNow() - ClockBase).IsLessThanOrEqualTo(TimeSpan.FromMinutes(31));
    }

    [Test]
    public async Task Resolves_the_since_boundary_from_the_flows_own_clock() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Importing!.Dates.Single())
                    .IsEqualTo(DateOnly.FromDateTime(ClockBase.UtcDateTime));
    }

    [Test]
    public async Task Imports_against_the_date_the_reported_counts_were_built_from() {
        // A user reading the screen across UTC midnight would otherwise be shown a figure for one
        // boundary and handed an import against the next, silently missing the day between.
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        // The scan lands on one day; the decision arrives on the next.
        h.Importing!.Advance = () => h.Clock.Advance(TimeSpan.FromHours(14));

        await Run(h);

        var scannedOn = DateOnly.FromDateTime(h.Importing.ScanStamps.Single().UtcDateTime);

        await Assert.That(h.Importing.Dates.Single())
                    .IsEqualTo(scannedOn)
                    .Because("the import has to select the history the counts promised");
    }

    [Test]
    public async Task Falls_back_to_now_when_no_scan_ran_to_agree_with() {
        // Reachable only where the Import step settled while Agents did not, so the scan was never
        // gated in. Defensive rather than expected — but the alternative to a fallback is a null date.
        var h = Build(importing: true);
        var answered = ImportAnswered();
        h.Channel.Polls.Enqueue(new(200, answered with {
            Steps = new() {
                ["SignIn"] = "Completed", ["Agents"] = "Active", ["Import"] = "Completed", ["Done"] = "Pending"
            }
        }));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        // The import ran on the tick before the scan was gated in, so there was no stamp to reuse.
        var order = h.Log.Entries.ToList();

        await Assert.That(order.IndexOf("import")).IsLessThan(order.IndexOf("scan"));
        await Assert.That(h.Importing!.Dates.Single())
                    .IsEqualTo(DateOnly.FromDateTime(ClockBase.UtcDateTime));
    }

    [Test]
    public async Task An_import_that_throws_does_not_end_the_leg() {
        var h = Build(importing: true);
        h.Importing!.ImportThrows = new HttpRequestException("server went away");
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Progress.ImportEnds).IsEqualTo(1).Because("the wait has to reopen either way");

        // A throw is as unaccounted as a lost pass, and just as unavailable to report as silence.
        await Assert.That(h.Channel.OutcomeReports.Single().Reason)
                    .IsEqualTo(FirstRunImportOutcomeReasons.RunFailed);
    }

    [Test]
    public async Task Announces_the_import_with_the_sessions_the_report_counted_for_that_window() {
        var h = Build(importing: true);
        h.Importing!.Found = FakeImportLane.Report(sessions: 41);
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Imports.Single()).IsEqualTo((1, (int?)41));
    }

    [Test]
    public async Task Announces_no_session_count_when_a_chosen_repo_reported_none_for_that_window() {
        // A total that quietly omitted a repository would be the wrong number stated confidently.
        var h = Build(importing: true);
        h.Importing!.Found = new ReportFirstRunImportRequest {
            Repos     = [new FirstRunImportRepoReport {
                Owner    = "kurrent-io",
                Name     = "kcap-server",
                Sessions = new Dictionary<string, int> { [FirstRunImportWindows.Last30] = 3 }
            }],
            Unmatched = new Dictionary<string, int>(),
            RepoTotal = 1
        };
        h.Channel.Polls.Enqueue(new(200, AgentsAnswered("claude")));
        h.Channel.Polls.Enqueue(new(200, ImportAnswered(FirstRunImportWindows.Last90)));
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Progress.Imports.Single().Sessions).IsNull();
    }

    [Test]
    public async Task Neither_lane_spends_the_backstop_it_was_not_waiting_through() {
        // The budget catches a terminal nobody is sitting at. A disk scan and an upload are work, and
        // letting them eat it would abandon a flow that is progressing.
        var h = Build(importing: true);
        var slow = TimeSpan.FromMinutes(20);

        h.Importing!.Found = FakeImportLane.Report();
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        // Both lanes run inside one tick, and the clock only moves from outside, so the advance is
        // driven from the lane itself.
        h.Importing.Advance = () => h.Clock.Advance(slow);

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
    }

    // ---- saying the machine is still here ----

    /// <summary>
    /// Why the beat is not driven by the poll. The import blocks the loop for its whole duration —
    /// deliberately, because two live renderables cannot share a terminal — and the loop credits that
    /// time back to its own deadline, so liveness read from polling would call the machine gone during
    /// the one stretch it is working hardest.
    ///
    /// <para>The lane parks in an <c>await</c> rather than blocking a thread, which is what makes this
    /// deterministic: <c>Drive</c> goes on advancing the fake clock while the poll loop sits inside the
    /// lane, so the beat's timer fires from the test's own pumping rather than from wall time.</para>
    /// </summary>
    [Test]
    public async Task The_beat_goes_on_while_the_import_holds_the_poll() {
        var h = Build(importing: true);
        h.Channel.Polls.Enqueue(new(200, ImportAnswered()));
        h.Channel.Polls.Enqueue(new(200, Done()));

        var before = 0;
        var during = 0;

        // Runs with the poll loop parked inside the lane, which is the only moment this claim is about.
        h.Importing!.Waits = async () => {
            before = h.Channel.Beats;

            // Bounded so a beat that never comes fails the assertion below instead of hanging.
            for (var i = 0; i < YieldBudget && h.Channel.Beats <= before; i++) await Task.Yield();

            during = h.Channel.Beats;
        };

        await Run(h);

        await Assert.That(during).IsGreaterThan(before)
                    .Because("the machine fell silent for the whole import, which is what a death looks like");
    }

    /// <summary>Generous: it is only ever exhausted by a beat that is not coming, and each yield is a
    /// scheduler turn rather than a wait.</summary>
    const int YieldBudget = 200_000;

    // ---- saying the machine has gone ----

    [Test]
    public async Task A_finished_leg_relinquishes_nothing() {
        // The load-bearing guard: the flow is over on its own terms and the browser is rendering the
        // payoff, so telling it the machine has gone would replace that with a dead end.
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        await Assert.That(h.Channel.Relinquished).IsEmpty();
    }

    [Test]
    public async Task An_expired_leg_relinquishes_nothing() {
        // Already terminal server-side, and the store refuses every write past the lifetime — so the
        // POST would be refused and the page already says the right thing.
        var h = Build();
        h.Channel.Polls.Enqueue(new(410, null));

        await Run(h);

        await Assert.That(h.Channel.Relinquished).IsEmpty();
    }

    /// <summary>The one exit where the terminal carries on, so the one where the page must not send
    /// anybody back to <c>kcap setup</c>: the run they would restart is in flight.</summary>
    [Test]
    public async Task A_dismissed_leg_says_the_terminal_is_taking_over() {
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 2));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Channel.Relinquished.Single()).IsEqualTo(FirstRunRelinquishReasons.Handover);
    }

    [Test]
    public async Task An_abandoned_leg_says_nothing_is_left_running() {
        var h = Build();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
        await Assert.That(h.Channel.Relinquished.Single()).IsEqualTo(FirstRunRelinquishReasons.Stopped);
    }

    [Test]
    public async Task A_failed_leg_says_nothing_is_left_running() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(401, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
        await Assert.That(h.Channel.Relinquished.Single()).IsEqualTo(FirstRunRelinquishReasons.Stopped);
    }

    /// <summary>A leg that never reached a flow has nothing to relinquish, and no id to name.</summary>
    [Test]
    public async Task A_tenant_that_does_not_serve_the_flow_is_told_nothing() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(404, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Unavailable>();
        await Assert.That(h.Channel.Relinquished).IsEmpty();
    }

    /// <summary>A refused best-effort relinquish leaves the leg's result unchanged, and the browser then
    /// waits until the flow's own lifetime ends it.</summary>
    [Test]
    public async Task A_relinquish_the_server_refuses_does_not_change_how_the_leg_ended() {
        var h = Build();
        h.Channel.RelinquishStatuses.Enqueue(500);
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(404, null));

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Failed>();
    }

    [Test]
    public async Task A_relinquish_that_throws_does_not_escape_the_leg() {
        var h = Build();
        h.Channel.RelinquishThrows = new InvalidOperationException("boom");

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Abandoned>();
    }

    // ---- the interrupt path ----

    /// <summary>
    /// Ctrl+C reaches <c>Environment.Exit</c> from a signal handler, which runs no <c>finally</c> — so the
    /// leg leaves a callback where the handler can find it.
    /// </summary>
    [Test]
    public async Task An_interrupt_during_the_leg_says_the_machine_stopped() {
        var h = Build();

        // Fired from inside a poll, because that is when a real signal would arrive.
        h.Channel.OnPoll = () => h.Interrupts.Interrupt();
        h.Channel.Polls.Enqueue(new(200, Running()));
        h.Channel.Polls.Enqueue(new(404, null));

        await Run(h);

        await Assert.That(h.Channel.Relinquished[0]).IsEqualTo(FirstRunRelinquishReasons.Stopped);
    }

    /// <summary>
    /// The claim is what stops the two paths contradicting each other. Ending a scope before the leg's own
    /// send does NOT close it: an interrupt handler that read the callback first still runs it afterwards,
    /// and the browser then shows whichever of two opposite remedies landed last.
    ///
    /// <para><b>The interrupt wins, and that is the honest outcome:</b> the process is being killed, so
    /// nothing is carrying on however the poll happened to end.</para>
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_interrupt_racing_the_legs_own_send_produces_exactly_one(bool dismissed) {
        var h = Build(dismissed ? new FakeKeys(canWatch: true, pressAfter: 2) : null);

        // Fires at the moment the leg's own reason is being sent, which is the window a scope-based fix
        // leaves open. ONCE, or the second send re-enters this hook and recurses until the stack goes.
        var fired = false;

        h.Channel.OnRelinquish = () => {
            if (fired) return;

            fired = true;

            h.Interrupts.Interrupt();
        };

        if (!dismissed) h.Channel.Polls.Enqueue(new(200, Done()));

        await Run(h);

        // Exactly one, and it is the reason the result named — never that reason plus its opposite.
        string[] expected = dismissed ? [FirstRunRelinquishReasons.Handover] : [];

        await Assert.That(h.Channel.Relinquished).IsEquivalentTo(expected);
    }

    /// <summary>
    /// The other ordering: an interrupt gets there FIRST, so its reason is the one that stands and the
    /// leg's own send is suppressed rather than appended.
    /// </summary>
    [Test]
    public async Task An_interrupt_that_wins_the_claim_is_the_only_send() {
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 2));

        // On a poll, before the leg has a result.
        h.Channel.OnPoll = () => h.Interrupts.Interrupt();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Channel.Relinquished).IsEquivalentTo([FirstRunRelinquishReasons.Stopped]);
    }

    /// <summary>
    /// An interrupt winning the claim AFTER the leg published a dismissal must still say <c>stopped</c>.
    /// Borrowing the leg's reason here tells someone their terminal took over at the moment that terminal
    /// is killed — a dead end with no remedy stated, and the whole reason the two claimants carry their own
    /// reasons rather than reading one shared field.
    /// </summary>
    [Test]
    public async Task An_interrupt_after_a_dismissal_still_says_the_machine_stopped() {
        var h = Build(new FakeKeys(canWatch: true, pressAfter: 2));

        // Fires once the result is published and before the leg's own send — the window a shared reason
        // leaves open.
        h.Progress.OnWaitEnded = () => h.Interrupts.Interrupt();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Dismissed>();
        await Assert.That(h.Channel.Relinquished).IsEquivalentTo([FirstRunRelinquishReasons.Stopped]);
    }

    /// <summary>And a flow that reached its payoff keeps it: the published result decides whether there is
    /// anything to say at all, so a finished leg stays silent under an interrupt too.</summary>
    [Test]
    public async Task An_interrupt_after_a_finish_says_nothing() {
        var h = Build();
        h.Channel.Polls.Enqueue(new(200, Done()));

        h.Progress.OnWaitEnded = () => h.Interrupts.Interrupt();

        var result = await Run(h);

        await Assert.That(result).IsTypeOf<FirstRunFlowResult.Finished>();
        await Assert.That(h.Channel.Relinquished).IsEmpty();
    }

    /// <summary>A leg that never reached a flow arms nothing: there is no id to name.</summary>
    [Test]
    public async Task A_leg_that_never_reached_a_flow_arms_nothing() {
        var h = Build();
        h.Channel.Creates.Enqueue(new(404, null));

        await Run(h);

        await Assert.That(h.Interrupts.Arms).IsEqualTo(0);
    }
}
