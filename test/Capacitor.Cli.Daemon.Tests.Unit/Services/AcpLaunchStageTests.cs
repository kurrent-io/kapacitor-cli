using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Task 13 (liveness-supervision spec §1 "ACP launch handshake" / "Immediate stage-advance
/// reports"): before this, <c>AcpHostedAgentRuntimeFactory.StartAsync</c>'s spawn + <c>initialize</c>
/// / <c>session/new</c> / model-selection handshake had NO launch-level timeout at all —
/// <c>AcpConnection.RequestAsync</c> has no timer of its own, so only daemon shutdown could ever
/// cancel a wedged call, and a wedged handshake never reaches <c>PublishAgent</c>, making it
/// invisible to both the startup reaper and the reviewer reaper (the "inverse defect" this task
/// closes). This suite drives the REAL <see cref="AcpHostedAgentRuntimeFactory.StartAsync"/> — the
/// exact production wiring path, including the Task-13 fix that assigns
/// <c>AcpHostedAgentRuntime.ActivityClock</c> BEFORE calling the runtime's own <c>StartAsync</c> —
/// against a <see cref="FakeAcpAgent"/>-backed <c>connectionSource</c>, with a
/// <see cref="FakeTimeProvider"/> standing in for the daemon's monotonic clock so a 90s per-stage
/// cap fires deterministically without a real 90-second wait (proven against a real
/// <see cref="CancellationTokenSource"/>/<see cref="TimeProvider"/> race during development — see
/// the task report).
/// </summary>
public class AcpLaunchStageTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>Records every <c>TerminateAsync</c> call so a test can assert the kill was actually
    /// INVOKED (containment-style — evidence, not just the thrown error) rather than merely that the
    /// launch failed.</summary>
    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid            { get; init; } = 4242;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }

        /// <summary>Makes the kill FAIL — the case that makes the timeout message's wording
        /// load-bearing (termination is best-effort, so the message may not claim the child died).</summary>
        public bool TerminateThrows { get; set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            if (TerminateThrows) throw new InvalidOperationException("kill refused by the OS");
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Minimal, non-review, non-borrowed context — mirrors
    /// <c>AcpHostedAgentRuntimeFactoryTests.MakeContext</c>, extended with an
    /// <see cref="AgentActivityClock"/> so a test can observe the handshake's stage stamps.</summary>
    static RuntimeStartContext MakeContext(string agentId, AgentActivityClock clock, string? model = null) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "",
        Model: model, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        ActivityClock: clock);

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent                  Fake    { get; }
        public FakeAcpProcess                Process { get; }
        public FakeTimeProvider              Time    { get; } = new();
        public AgentActivityClock            Clock   { get; }
        public AcpHostedAgentRuntimeFactory  Factory { get; }
        public CancellationTokenSource       Cts     { get; } = new();

        /// <summary>Every <see cref="AgentActivityClock.LaunchStage"/> value observed via
        /// <see cref="AgentActivityClock.OnLaunchStageChanged"/>, in transition order — mirrors how
        /// Task 11's real production hook (<c>AgentOrchestrator.CreateActivityClock</c>) observes
        /// stage advances, without needing the full orchestrator.</summary>
        public List<string?> ObservedStages { get; } = [];

        Task _fakeRunTask = Task.CompletedTask;

        public Harness(ConfigRoot configRoot) {
            Fake    = new FakeAcpAgent();
            Process = new FakeAcpProcess();
            Clock   = new AgentActivityClock(Time);
            Clock.OnLaunchStageChanged = () => ObservedStages.Add(Clock.LaunchStage);

            var connection = new ServerConnection(
                new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
                AuthFixtures.NewTokenStore(configRoot),
                NullLoggerFactory.Instance,
                NullLogger<ServerConnection>.Instance);

            Factory = new AcpHostedAgentRuntimeFactory(
                descriptor: AcpVendorDescriptors.Cursor,
                config: new DaemonConfig { CursorPath = "cursor-agent" }, // never spawned — connectionSource bypasses Process.Start
                loggerFactory: NullLoggerFactory.Instance,
                connection: connection,
                connectionSource: _ => (Fake.ClientWriteStream, Fake.ClientReadStream, Process),
                timeProvider: Time);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        /// <summary>Polls (real, small wall-clock increments — this is process synchronization, not
        /// the thing under test) until the fake has recorded the given method, proving the
        /// corresponding <c>RunHandshakeStageAsync</c> call has genuinely started (its stage CTS
        /// already exists) before the test advances the fake clock.</summary>
        public async Task WaitForCallAsync(string method) {
            var deadline = DateTime.UtcNow + HangGuard;
            while (!Fake.ReceivedCalls.Any(c => c.Method == method) && DateTime.UtcNow < deadline)
                await Task.Delay(5);

            if (!Fake.ReceivedCalls.Any(c => c.Method == method))
                throw new TimeoutException($"'{method}' was never received.");
        }

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try { await _fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    // ── The wedge: a stage that never responds fails at 90s, names itself, and kills the child ────

    [Test]
    public async Task Wedged_initialize_fails_at_90s_naming_the_initialized_stage_and_terminates_the_child() {
        await using var h = new Harness(Config.Root);
        h.Fake.HoldInitializeResponse = new TaskCompletionSource(); // never completed
        h.StartFakeAgentLoop();

        var startTask = h.Factory.StartAsync(MakeContext("agent-wedge", h.Clock), h.Cts.Token);

        await h.WaitForCallAsync("initialize");

        // 91s > the 90s cap — fires deterministically off the fake clock, no real wait.
        h.Time.Advance(TimeSpan.FromSeconds(91));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => startTask.WaitAsync(HangGuard));

        // Pins WHICH stage's cap fired — not merely "the launch failed" (two-guards-one-input trap:
        // a generic handshake failure would also throw InvalidOperationException).
        await Assert.That(ex!.Message).StartsWith("acp_launch_stage_timeout:initialized");

        // Evidence the kill was actually INVOKED, not just that an error was thrown.
        await Assert.That(h.Process.TerminateCalls).IsGreaterThanOrEqualTo(1);
        await Assert.That(h.Process.HasExited).IsTrue();

        // The "spawned" stamp still fired (it precedes any cap) — "initialized" never did. Seq
        // starts at 1 (Task 10) and SetLaunchStage unconditionally advances it (Task 11), so >= 2
        // is only reachable if the "spawned" SetLaunchStage call actually ran against THIS clock —
        // i.e. proves the factory wired ActivityClock onto the runtime before the handshake began.
        await Assert.That(h.Clock.ActivitySeq).IsGreaterThanOrEqualTo(2UL);
        await Assert.That(h.Clock.LaunchStage).IsEqualTo("spawned");
    }

    /// <summary>
    /// Termination is BEST-EFFORT: when the kill fails, the launch must still fail with the coded
    /// stage reason, and the message must not claim a kill that did not happen — an incident
    /// responder reading "the child process was terminated" would be steered away from an orphan
    /// that is still running.
    ///
    /// <para>Two guards, two distinct anchors: restoring the old "The child process was terminated."
    /// wording fails the wording assertions, and dropping the try/catch around the kill lets the
    /// terminate exception escape instead of the coded one, failing the prefix assertion.</para>
    /// </summary>
    [Test]
    public async Task Failed_termination_still_reports_the_coded_stage_and_never_claims_the_child_died() {
        await using var h = new Harness(Config.Root);
        h.Process.TerminateThrows        = true;
        h.Fake.HoldInitializeResponse    = new TaskCompletionSource(); // never completed
        h.StartFakeAgentLoop();

        var startTask = h.Factory.StartAsync(MakeContext("agent-kill-fails", h.Clock), h.Cts.Token);

        await h.WaitForCallAsync("initialize");
        h.Time.Advance(TimeSpan.FromSeconds(91));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => startTask.WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("acp_launch_stage_timeout:initialized");
        await Assert.That(h.Process.TerminateCalls).IsGreaterThanOrEqualTo(1);
        await Assert.That(h.Process.HasExited).IsFalse(); // the kill genuinely failed

        // Non-vacuity: the message really is the stage-timeout text before the negative below runs.
        await Assert.That(ex.Message).Contains("Termination of the child process was requested");
        await Assert.That(ex.Message).DoesNotContain("was terminated");
    }

    // ── Positive control: slow-but-progressing stages all succeed ───────────────────────────────

    /// <summary>
    /// Four stages, three of them (the only three with a real await inside
    /// <c>AcpHostedAgentRuntime.StartAsync</c> — "spawned" is stamped immediately since the child
    /// process already exists synchronously by the time <c>StartAsync</c> runs, so it has nothing to
    /// wait on and nothing to cap) each held ~80s of the SAME fake clock before releasing —
    /// cumulatively ~240s, several multiples of a single 90s bound, proving the cap is PER-STAGE and
    /// does not kill a slow-but-progressing handshake.
    /// </summary>
    [Test]
    public async Task Four_stages_each_taking_about_80_seconds_all_complete_successfully() {
        await using var h = new Harness(Config.Root);
        h.Fake.HoldInitializeResponse      = new TaskCompletionSource();
        h.Fake.HoldSessionNewResponse      = new TaskCompletionSource();
        h.Fake.HoldSetConfigOptionResponse = new TaskCompletionSource();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, currentModelId: "model-a", availableModels: [("model-b", "Model B")]));
        h.StartFakeAgentLoop();

        // A model must be requested and resolvable for the model_set stage to touch the wire at all
        // (a no-match/no-request selector returns near-instantly — see RunHandshakeStageAsync's own
        // remarks) — otherwise this test would only exercise two of the three real stages.
        var startTask = h.Factory.StartAsync(MakeContext("agent-slow", h.Clock, model: "model-b"), h.Cts.Token);

        await h.WaitForCallAsync("initialize");
        h.Time.Advance(TimeSpan.FromSeconds(80));
        h.Fake.HoldInitializeResponse.SetResult();

        await h.WaitForCallAsync("session/new");
        h.Time.Advance(TimeSpan.FromSeconds(80));
        h.Fake.HoldSessionNewResponse.SetResult();

        await h.WaitForCallAsync("session/set_config_option");
        h.Time.Advance(TimeSpan.FromSeconds(80));
        h.Fake.HoldSetConfigOptionResponse.SetResult();

        var started = await startTask.WaitAsync(HangGuard);

        // Order-sensitive by construction (TUnit's collection IsEqualTo is reference equality, not
        // structural — string.Join sidesteps that and pins the SEQUENCE, not just membership).
        await Assert.That(string.Join(",", h.ObservedStages)).IsEqualTo("spawned,initialized,session_created,model_set");
        await Assert.That(h.Process.TerminateCalls).IsEqualTo(0); // never killed — this handshake was healthy throughout

        await started.Runtime.DisposeAsync();
    }

    // ── Independence: a slow-but-completing earlier stage does not shrink a later stage's budget ──

    /// <summary>
    /// A POSITIVE proof, deliberately not a wedge/exception assertion: "initialized" burns 88s of its
    /// own 90s cap before completing, then "session_created" ALSO burns 88s of ITS OWN cap before
    /// completing — 176s cumulative, which no single shared 90s budget could survive. A wedge-based
    /// assertion here would be too weak to catch a shared-CTS regression (verified during
    /// development: a mutation that reuses one CTS across every stage still labels its exception with
    /// whichever stage happens to be in flight when the shared deadline crosses, so the CODED REASON
    /// alone stays correct even under that bug) — only a scenario that must SUCCEED despite exceeding
    /// any single 90s window can distinguish "fresh per stage" from "one shared budget".
    /// </summary>
    [Test]
    public async Task Session_created_stage_gets_its_own_fresh_budget_after_a_slow_but_completing_initialize() {
        await using var h = new Harness(Config.Root);
        h.Fake.HoldInitializeResponse = new TaskCompletionSource();
        h.Fake.HoldSessionNewResponse = new TaskCompletionSource();
        h.StartFakeAgentLoop();

        var startTask = h.Factory.StartAsync(MakeContext("agent-independent", h.Clock), h.Cts.Token);

        await h.WaitForCallAsync("initialize");
        h.Time.Advance(TimeSpan.FromSeconds(88));
        h.Fake.HoldInitializeResponse.SetResult();

        await h.WaitForCallAsync("session/new");
        // Cumulative 176s since launch start — only survivable if session_created counts from its
        // OWN entry, not from whatever's left of initialized's budget.
        h.Time.Advance(TimeSpan.FromSeconds(88));
        h.Fake.HoldSessionNewResponse.SetResult();

        var started = await startTask.WaitAsync(HangGuard);

        await Assert.That(string.Join(",", h.ObservedStages)).IsEqualTo("spawned,initialized,session_created,model_set");
        await Assert.That(h.Process.TerminateCalls).IsEqualTo(0);

        await started.Runtime.DisposeAsync();
    }

    // ── Monotonic, not wall-clock ────────────────────────────────────────────────────────────────

    /// <summary>A <see cref="TimeProvider"/> whose <see cref="CreateTimer"/> delegates to a real
    /// <see cref="FakeTimeProvider"/> (so <c>CancellationTokenSource(TimeSpan, TimeProvider)</c>
    /// schedules deterministically off it) while <see cref="GetUtcNow"/> is an INDEPENDENT, separately
    /// jumpable axis — <see cref="FakeTimeProvider"/> alone ties both to one simulated instant, which
    /// would make a genuine wall-clock/monotonic split structurally untestable (the same reason
    /// <c>AgentActivityClockTests.DecoupledTimeProvider</c> exists — see its remarks). Unlike that
    /// type, this one also supports <c>CreateTimer</c>, which a plain <see cref="TimeProvider"/>
    /// override does NOT get for free: verified empirically that a provider overriding only
    /// <c>GetTimestamp</c>/<c>GetUtcNow</c> still schedules its timers off REAL wall-clock time via
    /// the base class's default <c>CreateTimer</c>, so <c>DecoupledTimeProvider</c> could not drive a
    /// fast, deterministic <c>CancellationTokenSource</c> test — this type exists to close that gap.</summary>
    sealed class WallClockJumpTimeProvider : TimeProvider {
        readonly FakeTimeProvider _monotonic = new();
        DateTimeOffset            _wallClock = DateTimeOffset.UnixEpoch;

        public override long           GetTimestamp()        => _monotonic.GetTimestamp();
        public override long           TimestampFrequency    => _monotonic.TimestampFrequency;
        public override DateTimeOffset GetUtcNow()            => _wallClock;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _monotonic.CreateTimer(callback, state, dueTime, period);

        public void AdvanceMonotonic(TimeSpan by) => _monotonic.Advance(by);
        public void JumpWallClock(TimeSpan    by) => _wallClock += by;
    }

    [Test]
    public async Task Wall_clock_jump_does_not_fail_a_healthy_handshake() {
        var time    = new WallClockJumpTimeProvider();
        var clock   = new AgentActivityClock(time);
        var fake    = new FakeAcpAgent();
        var process = new FakeAcpProcess();

        fake.HoldInitializeResponse = new TaskCompletionSource();

        var connection = new ServerConnection(
            new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            AuthFixtures.NewTokenStore(Config.Root),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, process),
            timeProvider: time);

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var startTask = factory.StartAsync(MakeContext("agent-jump", clock), cts.Token);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "initialize") && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        // A wall-clock jump alone (NO monotonic advance) must never fail a healthy, still-in-progress
        // handshake — a stale/NTP-corrected/DST-shifted system clock is not evidence of a wedge.
        time.JumpWallClock(TimeSpan.FromDays(400));
        await Task.Delay(50); // give any (incorrect) wall-clock-driven cancellation a chance to fire

        await Assert.That(startTask.IsCompleted).IsFalse();

        fake.HoldInitializeResponse.SetResult();
        var started = await startTask.WaitAsync(HangGuard);

        await Assert.That(process.TerminateCalls).IsEqualTo(0);

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}

/// <summary>
/// Orchestrator-level integration for Task 13: reuses the shared
/// <see cref="AgentOrchestratorHarness"/> (<c>BuildOrchestrator</c>, <c>CreateGitRepo</c>,
/// <c>CaptureServerConnection</c>) to prove two things the factory-level <see cref="AcpLaunchStageTests"/>
/// suite cannot reach on its own: (1) a stage timeout surfaces through
/// <c>AgentOrchestrator.HandleLaunchAgentCore</c>'s ordinary catch-all as a real
/// <c>LaunchFailedAsync</c> call — an ordinary launch failure the server already understands, never
/// a silent hang or an unhandled fault — and (2) <see cref="AgentActivityClock.ClearLaunchStage"/>
/// runs at the exact instant the orchestrator flips a Starting ACP agent to Running.
/// </summary>
public class AcpLaunchStageOrchestratorTests {
    /// <summary>Minimal <see cref="IAcpProcess"/> — records terminate calls, mirroring
    /// <see cref="AcpLaunchStageTests"/>'s own fake.</summary>
    sealed class StageTimeoutFakeAcpProcess : IAcpProcess {
        public int  Pid            { get; init; } = 4343;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            HasExited = true;
            ExitCode  = 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task Wedged_initialize_reaches_LaunchFailed_through_the_orchestrator_not_a_silent_hang() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server  = new CaptureServerConnection();
        var fake    = new FakeAcpAgent();
        var process = new StageTimeoutFakeAcpProcess();
        var time    = new FakeTimeProvider();

        fake.HoldInitializeResponse = new TaskCompletionSource();

        var cursorFactory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: server,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, process),
            timeProvider: time);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
        );

        using var fakeCts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(fakeCts.Token);

        var cmd = AgentOrchestratorHarness.NewCursorLaunch("agent-orch-wedge", repoPath);

        // Fired without awaiting: HandleLaunchAgentForTest awaits the whole launch — including the
        // wedged initialize — to completion, so it must not be awaited until the clock advances.
        var launchTask = orch.HandleLaunchAgentForTest(cmd);

        var deadline = DateTime.UtcNow + WaitHarness.AcpHangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "initialize") && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        time.Advance(TimeSpan.FromSeconds(91));

        await launchTask.WaitAsync(WaitHarness.AcpHangGuard);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-orch-wedge");
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("acp_launch_stage_timeout:initialized");
        await Assert.That(process.TerminateCalls).IsGreaterThanOrEqualTo(1);

        // Never reached PublishAgent — the defect this task fixes is exactly that a wedged
        // handshake used to be invisible to every reaper because it never got this far.
        await Assert.That(orch.GetAgentForTest("agent-orch-wedge")).IsNull();

        fakeCts.Cancel();
        try { await fakeRunTask.WaitAsync(WaitHarness.AcpHangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();

    }

    /// <summary><see cref="IHostedAgentRuntimeFactory"/> test double that — unlike
    /// <c>SpyAcpHostedAgentRuntimeFactory</c> (<c>AgentOrchestratorHarness.cs</c>) — stamps a launch
    /// stage on the context's
    /// clock before returning, and records BOTH the clock instance it wrote to and the value read
    /// back immediately after the write. This is what makes the test's "cleared once Running"
    /// assertion non-vacuous: <c>AgentActivityClock.ClearLaunchStage</c> unconditionally bumps
    /// <c>ActivitySeq</c> too (exactly like a genuine <c>SetLaunchStage</c> transition does), so a seq
    /// check alone can never distinguish "SetLaunchStage really ran, then got cleared" from "nothing
    /// was ever wired, and only the Running-flip's own Clear bumped the seq" — verified by mutation
    /// below. Object-identity plus a same-instance read-back closes that gap.</summary>
    sealed class StageStampingAcpRuntimeFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
    public string CliPath => "unused-by-this-double";
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;

        public AgentActivityClock? ObservedClock       { get; private set; }
        public string?             StageRightAfterSet  { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ObservedClock = ctx.ActivityClock;
            ctx.ActivityClock?.SetLaunchStage("model_set"); // simulates a completed handshake
            StageRightAfterSet = ctx.ActivityClock?.LaunchStage;

            var runtime = new FakeAcpRuntime();
            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
        }
    }

    [Test]
    public async Task ClearLaunchStage_runs_once_the_agent_flips_to_Running() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server        = new CaptureServerConnection();
        var ptyFactory    = new SpyPtyProcessFactory();
        var cursorFactory = new StageStampingAcpRuntimeFactory();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
        );

        var cmd = AgentOrchestratorHarness.NewCursorLaunch("agent-clear-stage", repoPath);

        await orch.HandleLaunchAgentForTest(cmd);

        var agent = orch.GetAgentForTest("agent-clear-stage");
        await Assert.That(agent).IsNotNull();
        await Assert.That(agent!.Status).IsEqualTo("Running");

        // The factory's write really reached a real clock, and read back "model_set" right after
        // — proving SetLaunchStage genuinely ran (not merely "some clock existed somewhere").
        await Assert.That(cursorFactory.ObservedClock).IsNotNull();
        await Assert.That(cursorFactory.StageRightAfterSet).IsEqualTo("model_set");

        // The SAME instance the factory wrote to is the one the orchestrator owns on AgentInstance
        // — the Task 13 wiring-order fix (RuntimeStartContext.ActivityClock threaded to the
        // factory) is what makes this a single shared object, not two independent clocks.
        await Assert.That(ReferenceEquals(cursorFactory.ObservedClock, agent!.ActivityClock)).IsTrue();

        // Only now is "null after Running" a real assertion about ClearLaunchStage, rather than
        // the vacuous "it was always null" the seq-only check above could not rule out.
        await Assert.That(agent.ActivityClock.LaunchStage).IsNull();

        await orch.HandleStopAgentForTest("agent-clear-stage");

    }
}
