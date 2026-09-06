using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Tests.Unit;

/// Shared fixtures for the wizard-first startup composition (decision 2). Kept out of the test
/// classes so the graph harness, the §10 abandon rows and the rendering tests all compose the
/// wizard the way App.RunWizardModeAsync does.
static class WizardFixtures {
    internal sealed class NoopProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            throw new NotSupportedException("the wizard composition must not spawn a process");

        public Task<StreamingResult> RunStreamingAsync(
                string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotSupportedException("the wizard composition must not spawn a process");
    }

    internal sealed class NoopAppStateStore : IAppStateStore {
        public Task<AppState> LoadAsync() => Task.FromResult(new AppState());
        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) => Task.FromResult(true);
    }

    internal sealed class NoopUrlOpener : IUrlOpener {
        public readonly List<string> Opened = [];
        public void Open(string url) => Opened.Add(url);
    }

    internal sealed class NeverObservation : IDaemonObservation {
        public int Calls;

        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
            Calls++;
            return Task.FromResult<ObservedEvidence?>(null);
        }
    }

    /// A fixed-answer observation, for driving the daemon step past ClassifyLiveDaemonAsync's
    /// owned+matched branch (the only path that actually dials the ops factory).
    internal sealed class FixedObservation(ObservedEvidence? evidence) : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) =>
            Task.FromResult(evidence);
    }

    /// Records every request the wizard routes through the lane — the "zero service mutation"
    /// assertions read this, never a status snapshot.
    internal sealed class RecordingLane {
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, CancellationToken, Task<MutationOutcome>> Behavior =
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

        public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Behavior(request, ct);
        }
    }

    internal static Task<MutationOutcome> NeverRunMutation(MutationRequest request, CancellationToken ct) =>
        throw new InvalidOperationException("runMutation must not be called");

    internal static Func<CancellationToken, Task<string?>> FixedTerminalPath(string? path) => _ => Task.FromResult(path);

    internal static OutcomeEnvelope Envelope(string token) =>
        new(new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a"),
            new MutationOutcome.Failed(1, token, RecoverySurface.Attention));

    internal static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    /// The whole wizard graph over fakes: every daemon touchpoint is a counting factory, so "the
    /// wizard built no daemon graph" and "the daemon step's ops/CLI are late-bound" are both
    /// directly observable.
    internal sealed class GraphHarness : IDisposable {
        readonly TempConfigRoot _config = new();
        public ConfigRoot ClaimsRoot => _config.Root;

        // Only the claims store is isolated into ClaimsRoot; the wizard's config reads go where WriteConfig writes.
        public readonly ConfigRoot Root;
        public readonly RecordingLane Lane = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakeKcapCli Cli = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly NeverObservation Observation = new();
        public readonly NoopUrlOpener UrlOpener = new();
        public readonly ConsentFlipClaims Claims;
        public readonly WizardBridges Bridges;
        public readonly WizardLifecycleSurface Surface;
        public readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
        public readonly WorkOSClient Workos = new(new PlainHttpClientFactory());
        public readonly IHttpClientFactory HttpFactory = new PlainHttpClientFactory();
        public readonly IAuthProxyClient Proxy = new AuthProxyClient(new HttpClient());
        public readonly List<LifecyclePrompt> Prompts = [];

        public int CliFactoryCalls;
        public int OpsFactoryCalls;
        public int DetectionFactoryCalls;
        public int DetectCalls;

        public WizardFacadeSpec? Spec;
        public Func<ConnectIntent, CancellationToken, Task<AuthResult>> Operation =
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());

        // The profile a wizard sign-in targets. Separate from Identity's, which is the daemon-facing
        // tuple; a test that writes a config naming another profile sets this too.
        public string Profile = ProfileConfig.DefaultName;

        public (string Profile, string Server, string DaemonName)? Identity = ("default", "https://acme.example", "daemon-a");
        // The claims/TryConsume path's identity (ResolveConsentFlipIdentity in production), kept
        // separate from Identity above — same default tuple, so tests that never diverge them agree.
        public (string Profile, string Server, string DaemonName) ConsentFlipIdentity = ("default", "https://acme.example", "daemon-a");
        public readonly List<string> OpsFactoryNames = [];
        public bool ShimApplicable = true;
        public string? CliPath = "/opt/kcap/bin/kcap";
        public string? ShimTarget = "/opt/kcap/bin/kcap";
        public IReadOnlySet<HarnessId> Detected = VendorDetection.Build("claude");

        public GraphHarness(ConfigRoot root) {
            Root    = root;
            Claims  = new ConsentFlipClaims(_config.Root);
            Bridges = WizardComposition.BuildBridges(action => action(), new(new HttpClient()));
            Surface = new WizardLifecycleSurface((prompt, _) => {
                Prompts.Add(prompt);
                return Task.FromResult(false);
            }, action => action());
        }

        public WizardGraphOptions Options() => new(
            Root,
            AuthFixtures.NewTokenStore(Root),
            HttpFactory,
            Proxy,
            Github,
            Workos,
            Profile,
            Claims,
            Bridges,
            spec => {
                Spec = spec;
                return Operation;
            },
            Surface,
            ResolveCli: () => {
                CliFactoryCalls++;
                return Cli;
            },
            ResolveOps: name => {
                OpsFactoryCalls++;
                OpsFactoryNames.Add(name);
                return Ops;
            },
            ResolveIdentity: () => Identity,
            ResolveConsentFlipIdentity: () => ConsentFlipIdentity,
            RunMutation: Lane.RunAsync,
            Observation: Observation,
            AppState: new NoopAppStateStore(),
            ShimInstaller: new PathShimInstaller(new NoopProcessRunner(), Probe),
            UrlOpener: UrlOpener,
            Probe: Probe,
            // A NEW feed per call: a composition that built one per step is caught by the factory
            // count below, and the shared instance's own call count proves both steps used it.
            DetectionFeed: _ => {
                DetectionFactoryCalls++;
                return _ => {
                    Interlocked.Increment(ref DetectCalls);
                    return Task.FromResult(Detected);
                };
            },
            CliPath: CliPath,
            ShimApplicable: ShimApplicable,
            ShimTarget: ShimTarget,
            DefaultDaemonName: "daemon-a",
            Time: TimeProvider.System,
            ShutdownToken: CancellationToken.None);

        public void Dispose() => _config.Dispose();
    }
}

/// <summary>
/// Decision 2's wizard-first startup: the wizard composition (no daemon graph at all) and the close
/// boundary — cancel + await the sign-in's terminal answer, wait the lane out under the cap, then
/// hand the outcome channel over. StartAsync itself needs a real daemon/profile (same reason
/// AppStartupTests drives extracted statics), so this drives the seams it is composed from.
///
/// [NotInParallel]: the step ViewModels are ReactiveObjects whose WhenAnyValue wiring needs the
/// process-global headless session's ReactiveUI initialization, so every test here runs inside it.
/// </summary>
[NotInParallel("AvaloniaSession")]
public class WizardStartupTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static readonly TimeSpan Cap = TimeSpan.FromSeconds(5);

    // ── the close boundary (steps 1-4) ────────────────────────────────────────

    [Test]
    public async Task Handoff_cancels_a_pre_boundary_sign_in_and_ends_it_as_cancelled() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, null, []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Discover(AuthProvider.GitHubApp));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.HandoffAfterWizardAsync(graph.Auth, () => Task.CompletedTask, Cap, new OutcomeChannel())
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();
            // Nothing durable: a pre-boundary cancel never armed a claim and never touched the lane.
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();

            return true;
        });
    }

    [Test]
    public async Task Handoff_waits_for_a_post_boundary_sign_in_to_answer_committed_before_transferring() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            // Past the commit boundary the façade ignores the cancel and still publishes.
            harness.Operation = async (_, _) => {
                entered.TrySetResult();
                await release.Task;
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, "someone", []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var channel = new OutcomeChannel();
            using var wizardCts = new CancellationTokenSource();
            var wizardConsumer = Task.Run(() => AppUnderTest.ConsumeMutationOutcomesAsync(
                channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
                WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, wizardCts.Token));

            var handoff = AppUnderTest.HandoffAfterWizardAsync(graph.Auth, () => Task.CompletedTask, Cap, channel);
            await Task.Delay(50);

            await Assert.That(handoff.IsCompleted).IsFalse(); // the graph build waits on the terminal answer
            // Still owned by the wizard consumer: the transfer is the LAST step of the handoff.
            Assert.Throws<InvalidOperationException>(() => { _ = channel.ConsumeAsync(CancellationToken.None); });

            release.SetResult();
            await handoff.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Committed>();
            _ = channel.ConsumeAsync(CancellationToken.None); // transferred: a fresh consumer is admitted

            await wizardCts.CancelAsync();
            await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        });
    }

    /// spec §6a: past the cap the handoff proceeds, but it must SAY the lane is still live —
    /// otherwise the graph comes up driving automatic actions against a child that is still running.
    [Test]
    public async Task Handoff_past_the_cap_reports_an_unquiesced_lane_and_closes_auto_actions() {
        var never = new TaskCompletionSource();
        var channel = new OutcomeChannel();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var quiesced = await AppUnderTest
            .HandoffAfterWizardAsync(auth: null, () => never.Task, TimeSpan.FromMilliseconds(50), channel)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(quiesced).IsFalse();
        // Even a Complete gate builds the degraded graph while the lane still owns a live action.
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete(), quiesced)).IsTrue();
        _ = channel.ConsumeAsync(CancellationToken.None); // past the cap the channel is still handed over
    }

    [Test]
    public async Task Handoff_within_the_cap_reports_a_quiesced_lane_and_leaves_auto_actions_open() {
        var quiesced = await AppUnderTest
            .HandoffAfterWizardAsync(auth: null, () => Task.CompletedTask, Cap, new OutcomeChannel())
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(quiesced).IsTrue();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete(), quiesced)).IsFalse();
        // An incomplete gate still closes them, quiesced or not.
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Incomplete(GateReason.NoToken), quiesced))
            .IsTrue();
    }

    [Test]
    public async Task An_unquiesced_lane_is_announced_exactly_once_and_a_quiesced_one_says_nothing() {
        var announced = new FakeLifecycleSurface();
        var silent = new FakeLifecycleSurface();

        AppUnderTest.AnnounceUnquiescedLane(announced, laneQuiesced: false);
        AppUnderTest.AnnounceUnquiescedLane(silent, laneQuiesced: true);

        await Assert.That(announced.AttentionMessages).IsEquivalentTo([AppUnderTest.LaneStillBusyAttention]);
        await Assert.That(silent.AttentionMessages).IsEmpty();
        await Assert.That(announced.StatusMessages).IsEmpty();
    }

    /// The mirror of the post-boundary ordering test, for the WITHIN-cap path: the channel stays
    /// wizard-owned until the lane's own quiesce completes — the transfer is the last step.
    [Test]
    public async Task Handoff_transfers_only_after_the_lane_quiesces_within_the_cap() {
        var quiesce = new TaskCompletionSource();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
            WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        var handoff = AppUnderTest.HandoffAfterWizardAsync(
            auth: null, () => quiesce.Task, TimeSpan.FromSeconds(30), channel);
        await Task.Delay(50);

        await Assert.That(handoff.IsCompleted).IsFalse();
        Assert.Throws<InvalidOperationException>(() => { _ = channel.ConsumeAsync(CancellationToken.None); });

        quiesce.SetResult();

        await Assert.That(await handoff.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue();
        _ = channel.ConsumeAsync(CancellationToken.None); // transferred only now

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Handoff_with_no_sign_in_attempt_still_transfers() {
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();
        var first = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
            WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, cts.Token);

        await AppUnderTest.HandoffAfterWizardAsync(auth: null, () => Task.CompletedTask, Cap, channel)
            .WaitAsync(TimeSpan.FromSeconds(5));

        _ = channel.ConsumeAsync(CancellationToken.None);
        await cts.CancelAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── the consumer runs unchanged over the wizard's own surface ─────────────

    [Test]
    public async Task The_outcome_consumer_presents_through_the_wizard_surface() {
        var surface = new WizardLifecycleSurface((_, _) => Task.FromResult(false), action => action());
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var consumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, surface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => surface.AttentionText is not null, what: "the wizard-surface presentation");

        await Assert.That(surface.AttentionText).IsEqualTo(AppUnderTest.AttentionCopyFor("internal_error")!);

        await cts.CancelAsync();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// Blocks its FIRST Attention call on a gate, so the envelope is provably in flight on the
    /// wizard consumer at the moment the handoff runs.
    sealed class GatedAttentionSurface(Task gate) : ILifecycleSurface {
        public int Entered;
        public int Presented;

        public void Status(string message) { }

        public void Attention(string message) {
            Interlocked.Increment(ref Entered);
            gate.GetAwaiter().GetResult();
            Interlocked.Increment(ref Presented);
        }

        public Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => Task.FromResult(false);
        public Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct) => Task.FromResult<bool?>(false);
    }

    [Test]
    public async Task An_envelope_in_flight_across_the_handoff_is_presented_exactly_once() {
        var gate = new TaskCompletionSource();
        var wizardSurface = new GatedAttentionSurface(gate.Task);
        var rootSurface = new FakeLifecycleSurface();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, wizardSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => wizardSurface.Entered == 1, what: "the wizard consumer's presentation");

        await AppUnderTest.HandoffAfterWizardAsync(auth: null, () => Task.CompletedTask, Cap, channel);
        var rootConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, rootSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        gate.SetResult(); // the wizard consumer finishes its presentation and acks
        await WizardFixtures.WaitUntilAsync(() => wizardSurface.Presented == 1, what: "the acked presentation");
        await Task.Delay(150); // give a wrong re-delivery to the root consumer every chance to fire

        await Assert.That(wizardSurface.Presented + rootSurface.AttentionMessages.Count).IsEqualTo(1);

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
        await rootConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task An_envelope_enqueued_after_the_handoff_reaches_the_root_consumer_only() {
        var wizardSurface = new FakeLifecycleSurface();
        var rootSurface = new FakeLifecycleSurface();
        var channel = new OutcomeChannel();
        using var cts = new CancellationTokenSource();

        var wizardConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, wizardSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        await AppUnderTest.HandoffAfterWizardAsync(auth: null, () => Task.CompletedTask, Cap, channel);
        var rootConsumer = AppUnderTest.ConsumeMutationOutcomesAsync(
            channel, rootSurface, WizardFixtures.NeverRunMutation, WizardFixtures.FixedTerminalPath("/usr/bin"),
            () => null, cts.Token);

        channel.Enqueue(WizardFixtures.Envelope("internal_error"));
        await WizardFixtures.WaitUntilAsync(() => rootSurface.AttentionMessages.Count == 1, what: "the root presentation");

        await Assert.That(rootSurface.AttentionMessages[0]).IsEqualTo(AppUnderTest.AttentionCopyFor("internal_error")!);
        await Assert.That(wizardSurface.AttentionMessages).IsEmpty();

        await cts.CancelAsync();
        await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));
        await rootConsumer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── shutdown during wizard mode ───────────────────────────────────────────

    [Test]
    public async Task Shutdown_quiesce_cancels_the_wizard_sign_in_and_waits_for_the_lane() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new AuthResult.Cancelled();
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Create());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.QuiesceAppAsync(graph.Auth, import: null, lifecycle: null, lane: null, Cap)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(attempt.Result.IsCompleted).IsTrue();
            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();

            return true;
        });
    }

    [Test]
    public async Task Shutdown_quiesce_with_no_wizard_is_the_existing_lifecycle_and_lane_wait() {
        await AppUnderTest.QuiesceAppAsync(auth: null, import: null, lifecycle: null, lane: null, Cap)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// decision 2: the cap is the LANE's, never the sign-in's — a post-boundary operation that is
    /// still publishing when the cap window has long expired is awaited to its terminal answer.
    [Test]
    public async Task Shutdown_quiesce_awaits_a_post_boundary_sign_in_long_past_the_cap() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            harness.Operation = async (_, _) => {
                entered.TrySetResult();
                await release.Task;
                return new AuthResult.Committed("acme", "https://acme.example:443", AuthProvider.None, "someone", []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Paste("https://acme.example"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var quiesce = AppUnderTest.QuiesceAppAsync(
                graph.Auth, import: null, lifecycle: null, lane: null, TimeSpan.FromMilliseconds(20));
            await Task.Delay(400); // twenty cap windows

            await Assert.That(quiesce.IsCompleted).IsFalse();

            release.SetResult();
            await quiesce.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Committed>();

            return true;
        });
    }

    // The other half: the lifecycle/lane wait is still the capped one (spec §6a).
    [Test]
    public async Task Shutdown_quiesce_still_caps_an_in_flight_lane_mutation() {
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        await using var lane = new DaemonMutationLane(Daemons.Store,
            new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            new OutcomeChannel(),
            () => "/opt/kcap/bin/kcap",
            (_, _) => cli,
            _ => new WizardFixtures.NeverObservation(),
            TimeProvider.System);

        var runTask = lane.RunAsync(
            new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a"),
            CancellationToken.None);

        await AppUnderTest
            .QuiesceAppAsync(auth: null, import: null, lifecycle: null, lane, TimeSpan.FromMilliseconds(50))
            .WaitAsync(TimeSpan.FromSeconds(5));

        gate.SetResult("9.9.9");
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Spec §7's close contract (KillTree + await exit) must run from the close boundary, not only CanLeaveAsync.
    [Test]
    public async Task Handoff_cancels_an_in_flight_import_and_awaits_it_before_transferring() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var cancelObserved = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            harness.Cli.ImportBehavior = async (_, _, ct) => {
                entered.TrySetResult();
                await using var reg = ct.Register(() => cancelObserved.TrySetResult());
                await cancelObserved.Task.ConfigureAwait(false);
                // Mirrors ProcessRunner.RunStreamingAsync: the killed tree's pumps drain
                // to EOF before the streaming call itself returns/throws.
                await release.Task.ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var import = graph.Import;
            import.Vendors[0].IsSelected = true; // RunCoreAsync no-ops with nothing selected
            _ = import.RunAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(import.Busy).IsTrue();

            var channel = new OutcomeChannel();
            using var wizardCts = new CancellationTokenSource();
            var wizardConsumer = Task.Run(() => AppUnderTest.ConsumeMutationOutcomesAsync(
                channel, new FakeLifecycleSurface(), WizardFixtures.NeverRunMutation,
                WizardFixtures.FixedTerminalPath("/usr/bin"), () => null, wizardCts.Token));

            var handoff = AppUnderTest.HandoffAfterWizardAsync(auth: null, () => Task.CompletedTask, Cap, channel, import);
            await cancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)); // CancelActiveRunAsync reached the CLI's own ct
            await Task.Delay(50);

            await Assert.That(handoff.IsCompleted).IsFalse(); // still draining the killed import
            Assert.Throws<InvalidOperationException>(() => { _ = channel.ConsumeAsync(CancellationToken.None); });

            release.SetResult();
            await handoff.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(import.Busy).IsFalse(); // the run fully finished before the handoff returned
            _ = channel.ConsumeAsync(CancellationToken.None); // transferred only now

            await wizardCts.CancelAsync();
            await wizardConsumer.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        });
    }

    [Test]
    public async Task Shutdown_quiesce_cancels_an_in_flight_import_and_awaits_it() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var entered = new TaskCompletionSource();
            var cancelObserved = new TaskCompletionSource();
            harness.Cli.ImportBehavior = async (_, _, ct) => {
                entered.TrySetResult();
                await using var reg = ct.Register(() => cancelObserved.TrySetResult());
                await cancelObserved.Task.ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var import = graph.Import;
            import.Vendors[0].IsSelected = true;
            _ = import.RunAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await AppUnderTest.QuiesceAppAsync(auth: null, import, lifecycle: null, lane: null, Cap)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(import.Busy).IsFalse();

            return true;
        });
    }

    // ── the wizard graph: composition invariants ──────────────────────────────

    [Test]
    public async Task The_wizard_graph_builds_every_step_and_touches_no_daemon() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());

            await Assert.That(graph.Steps.Select(s => s.Id).ToList()).IsEquivalentTo([
                WizardStepId.Shim, WizardStepId.Connect, WizardStepId.SignIn, WizardStepId.Defaults,
                WizardStepId.Agents, WizardStepId.Import, WizardStepId.Daemon, WizardStepId.Done,
            ]);
            // No mutation, no IPC, no status read: composing the wizard never speaks to a daemon.
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Observation.Calls).IsEqualTo(0);
            // Never composed once: the socket is only bound when a step actually puts to it (the
            // CLI factory IS consulted here, for the pure CliPath the vendor steps gate on).
            await Assert.That(harness.OpsFactoryCalls).IsEqualTo(0);

            return true;
        });
    }

    [Test]
    public async Task An_inapplicable_shim_step_is_dropped_from_the_wizard_but_kept_in_the_summary() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.ShimApplicable = false;

            var graph = WizardComposition.BuildGraph(harness.Options());

            await Assert.That(graph.ViewModel.Steps.Any(s => s.Id == WizardStepId.Shim)).IsFalse();
            await Assert.That(graph.Steps.Any(s => s.Id == WizardStepId.Shim)).IsTrue();

            return true;
        });
    }

    [Test]
    public async Task The_same_detection_feed_is_shared_by_the_agents_and_import_steps() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var agents = graph.Steps.OfType<AgentsStepViewModel>().Single();
            var import = graph.Steps.OfType<ImportStepViewModel>().Single();

            await agents.OnEnterAsync(CancellationToken.None);
            await import.OnEnterAsync(CancellationToken.None);

            // ONE feed instance exists (the factory ran once) and BOTH steps went through it.
            await Assert.That(harness.DetectionFactoryCalls).IsEqualTo(1);
            await Assert.That(harness.DetectCalls).IsEqualTo(2);

            return true;
        });
    }

    [Test]
    public async Task The_daemon_step_reads_status_through_a_freshly_resolved_cli() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));
            harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(null);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5)); // the row's own precondition

            await daemon.RefreshAsync(CancellationToken.None);
            var afterFirst = harness.CliFactoryCalls;
            await daemon.RefreshAsync(CancellationToken.None);

            // Every status read rebinds — a name written by the Defaults step lands on the next one.
            await Assert.That(afterFirst).IsGreaterThan(0);
            await Assert.That(harness.CliFactoryCalls).IsGreaterThan(afterFirst);

            return true;
        });
    }

    [Test]
    public async Task The_daemon_step_requires_a_committed_sign_in_before_it_resolves_an_identity() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));

            var graph = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var connect = graph.Steps.OfType<ConnectStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();

            await daemon.RefreshAsync(CancellationToken.None);
            var beforeSignIn = daemon.Row;

            connect.Choice = ConnectChoice.Paste;
            connect.ServerInputText = "https://acme.example";
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));
            harness.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("daemon-a", false, "not_installed", null, "/opt/kcap/kcapd", null, null, false, false));
            await daemon.RefreshAsync(CancellationToken.None);

            await Assert.That(beforeSignIn).IsEqualTo(DaemonRow.RequiresSignIn);
            await Assert.That(signIn.Satisfied).IsTrue();
            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.NotInstalled);

            return true;
        });
    }

    // ── the composition seam: one façade, provisioner armed, arming hook wired ─

    [Test]
    public async Task The_facade_is_built_from_the_bridges_own_sink_picker_and_provisioner() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            WizardComposition.BuildGraph(harness.Options());

            await Assert.That(harness.Spec).IsNotNull();
            await Assert.That(harness.Spec!.Progress).IsSameReferenceAs(harness.Bridges.Progress);
            await Assert.That(harness.Spec.Picker).IsSameReferenceAs(harness.Bridges.Picker);
            // A provisioner-less façade dead-ends "Create a workspace" at "ask your admin".
            await Assert.That(harness.Spec.Provisioner).IsSameReferenceAs(harness.Bridges.Provisioner);

            return true;
        });
    }

    [Test]
    public async Task The_facades_beforeCommit_hook_arms_a_durable_claim_per_identity() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            WizardComposition.BuildGraph(harness.Options());
            await harness.Spec!.BeforeCommit(
                [new AuthIdentity("acme", "https://acme.example:443"), new AuthIdentity("work", "https://work.example:443")],
                CancellationToken.None);

            await Assert.That(harness.Claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo(["acme", "work"]);

            return true;
        });
    }

    [Test]
    public async Task The_production_bridges_marshal_through_the_avalonia_dispatcher() {
        var (marshalled, hasProvisioner) = await AvaloniaSession.DispatchAsync(async () => {
            var bridges = WizardComposition.BuildBridges(action => Dispatcher.UIThread.Post(action), new(new HttpClient()));
            var posted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Posted from a background thread, exactly as the façade's flows raise their events.
            await Task.Run(() => bridges.Post(() => posted.TrySetResult(Dispatcher.UIThread.CheckAccess())));
            Dispatcher.UIThread.RunJobs();

            // WizardBridges' own contract: the provisioner is built from the bridges' sink.
            return (await posted.Task.WaitAsync(TimeSpan.FromSeconds(10)), bridges.Provisioner is not null);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(marshalled).IsTrue();
        await Assert.That(hasProvisioner).IsTrue();
    }

    // ── the Done step's summary (why-skipped notes) ───────────────────────────

    [Test]
    public async Task A_missing_cli_is_the_summary_note_for_every_step_that_needs_one() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.CliPath = null;
            harness.Cli.CliPath = null;

            var graph = WizardComposition.BuildGraph(harness.Options());
            var summary = graph.Steps.OfType<DoneStepViewModel>().Single().Summary;

            await Assert.That(summary.Count).IsEqualTo(7); // every step but Done itself
            foreach (var title in new[] { "Command-line tool", "Coding agents", "Import past sessions", "Enable the daemon" })
                await Assert.That(summary.Single(e => e.Title == title).Note).IsEqualTo(WizardComposition.CliMissingNote);

            return true;
        });
    }

    [Test]
    public async Task An_unsigned_in_daemon_step_reads_as_requires_sign_in_in_the_summary() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();

            await daemon.RefreshAsync(CancellationToken.None);

            await Assert.That(done.Summary.Single(e => e.Title == "Enable the daemon").Note)
                .IsEqualTo(WizardComposition.RequiresSignInNote);

            return true;
        });
    }

    [Test]
    public async Task A_satisfied_step_carries_no_note() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);

            var graph = WizardComposition.BuildGraph(harness.Options());
            var connect = graph.Steps.OfType<ConnectStepViewModel>().Single();
            var done = graph.Steps.OfType<DoneStepViewModel>().Single();

            connect.Choice = ConnectChoice.Create; // Satisfied without any input

            var entry = done.Summary.Single(e => e.Title == "Connect to Capacitor");
            await Assert.That(entry.Satisfied).IsTrue();
            await Assert.That(entry.Note).IsNull();

            return true;
        });
    }

    // ── the shim step's pre-probe (latency the shipping default must not pay) ─

    [Test]
    [Arguments(false, "/opt/kcap/bin/kcap")] // not macOS: the answer can't change the outcome
    [Arguments(true, null)]                  // no linkable target: same
    public async Task The_path_probe_is_skipped_when_it_cannot_change_the_shim_decision(bool isMacOs, string? target) {
        var probes = 0;

        var applicable = await AppUnderTest.ResolveShimApplicableAsync(isMacOs, target, _ => {
            probes++;
            return Task.FromResult<bool?>(false);
        }, CancellationToken.None);

        await Assert.That(applicable).IsFalse();
        await Assert.That(probes).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, true)]  // kcap is NOT on the terminal PATH — the step has something to offer
    [Arguments(true, false)]  // already on PATH
    [Arguments(null, false)]  // unknown probe: never offer on an inconclusive read
    public async Task On_macos_with_a_target_the_probe_decides(bool? onPath, bool expected) {
        var probes = 0;

        var applicable = await AppUnderTest.ResolveShimApplicableAsync(true, "/opt/kcap/bin/kcap", _ => {
            probes++;
            return Task.FromResult(onPath);
        }, CancellationToken.None);

        await Assert.That(applicable).IsEqualTo(expected);
        await Assert.That(probes).IsEqualTo(1);
    }

    [Test]
    public async Task A_probe_failure_is_unknown_but_a_quit_during_it_propagates() {
        var applicable = await AppUnderTest.ResolveShimApplicableAsync(
            true, "/opt/kcap/bin/kcap", _ => throw new InvalidOperationException("probe boom"), CancellationToken.None);

        await Assert.That(applicable).IsFalse(); // degraded to unknown, never "offer it anyway"

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // A quit mid-probe must not go on to build a wizard window — same rule as the gate's.
        await Assert.ThrowsAsync<OperationCanceledException>(() => AppUnderTest.ResolveShimApplicableAsync(
            true, "/opt/kcap/bin/kcap", ct => Task.FromCanceled<bool?>(ct), cts.Token));
    }

    // ── the sign-in step's retarget answer ────────────────────────────────────

    [Test]
    public async Task A_retarget_prefills_the_connect_step_and_navigates_back_to_it() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(new AuthResult.Retarget("acme"));

            var graph = WizardComposition.BuildGraph(harness.Options());
            var connect = graph.Steps.OfType<ConnectStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await graph.ViewModel.PendingEnterForTesting;

            graph.ViewModel.TryGoTo(WizardStepId.SignIn);
            await WizardFixtures.WaitUntilAsync(
                () => graph.ViewModel.Current.Id == WizardStepId.SignIn, what: "the jump to the sign-in step");

            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await WizardFixtures.WaitUntilAsync(
                () => graph.ViewModel.Current.Id == WizardStepId.Connect, what: "the retarget navigation");

            await Assert.That(connect.ServerInputText).IsEqualTo("acme");
            await Assert.That(connect.Choice).IsEqualTo(ConnectChoice.Paste);

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ── late binding (the adapters the daemon step is composed over) ──────────

    [Test]
    public async Task Late_bound_ops_resolve_a_fresh_socket_per_call() {
        var first = new ScriptedLocalControlOps();
        var second = new ScriptedLocalControlOps();
        var current = first;
        var ops = new LateBoundLocalControlOps(() => current);

        first.QueueGet(new ConsentPolicyDto("allow", 300, []));
        await ops.GetConsentPolicyAsync(CancellationToken.None);

        current = second; // the Defaults step renamed the daemon between calls
        second.QueuePutV2(true, null);
        await ops.PutConsentPolicyV2Async(
            new ConsentPolicyPutV2Dto("renamed", "https://acme.example:443", new ConsentPolicyDto("prompt", 300, [])),
            CancellationToken.None);

        await Assert.That(first.GetCalls).IsEqualTo(1);
        await Assert.That(first.PutV2Calls).IsEqualTo(0);
        await Assert.That(second.PutV2Calls).IsEqualTo(1);
        await Assert.That(second.PutV2Payloads[0].ExpectedName).IsEqualTo("renamed");
    }

    [Test]
    public async Task Late_bound_cli_resolves_a_fresh_binding_per_call() {
        var first = new FakeKcapCli { CliPath = "/one/kcap" };
        var second = new FakeKcapCli { CliPath = "/two/kcap" };
        var current = first;
        var binds = 0;
        var cli = new LateBoundKcapCli(() => { binds++; return current; }, "/opt/kcap/bin/kcap");

        await cli.ServiceStatusAsync(CancellationToken.None);
        current = second;
        await cli.ServiceStatusAsync(CancellationToken.None);

        await Assert.That(first.StatusCallCount).IsEqualTo(1);
        await Assert.That(second.StatusCallCount).IsEqualTo(1);
        // CliPath is the captured binary — a UI binding must not pay a config load per get.
        await Assert.That(cli.CliPath).IsEqualTo("/opt/kcap/bin/kcap");
        _ = cli.CliPath;
        await Assert.That(binds).IsEqualTo(2); // the two status calls only
    }

    // ── window/dialog routing ─────────────────────────────────────────────────

    sealed class StubStep(WizardStepId id, string title) : IWizardStep {
        public WizardStepId Id => id;
        public string Title => title;
        public bool Applicable => true;
        public bool Satisfied => false;
        public Task OnEnterAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) => Task.FromResult(true);
    }

    static (OnboardingViewModel Wizard, WizardLifecycleSurface Surface) NewShell() {
        var surface = new WizardLifecycleSurface((_, _) => Task.FromResult(false), action => action());
        var wizard = new OnboardingViewModel(
            [new StubStep(WizardStepId.Connect, "Connect to Capacitor")], CancellationToken.None, surface);

        return (wizard, surface);
    }

    [Test]
    public async Task The_wizard_window_renders_the_surfaces_status_line() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var (wizard, surface) = NewShell();
            var window = new OnboardingWindow { DataContext = wizard };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            surface.Status("a status line from the consumer");
            Dispatcher.UIThread.RunJobs();

            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "LifecycleStatusText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return text;
        });

        await Assert.That(rendered).IsEqualTo("a status line from the consumer");
    }

    [Test]
    public async Task The_wizard_window_renders_the_surfaces_attention_line() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var (wizard, surface) = NewShell();
            var window = new OnboardingWindow { DataContext = wizard };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            surface.Attention("a daemon mutation needs attention (some_token)");
            Dispatcher.UIThread.RunJobs();

            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "LifecycleAttentionText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return text;
        });

        await Assert.That(rendered).IsEqualTo("a daemon mutation needs attention (some_token)");
    }

    [Test]
    public async Task A_lifecycle_prompt_opens_a_dialog_owned_by_the_wizard_window() {
        var (owned, settled) = await AvaloniaSession.DispatchAsync(() => {
            var (wizard, _) = NewShell();
            var window = new OnboardingWindow { DataContext = wizard };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            using var cts = new CancellationTokenSource();
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindTakeover, null, null, false, "disclosure text");
            var answer = AppUnderTest.ShowLifecyclePromptDialogAsync(window, prompt, cts.Token);
            Dispatcher.UIThread.RunJobs();

            var dialogOpen = window.OwnedWindows.Count > 0;

            cts.Cancel(); // the wizard closing must never strand the dialog
            Dispatcher.UIThread.RunJobs();

            var resolvedFalse = answer.IsCompletedSuccessfully && !answer.Result;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (dialogOpen, resolvedFalse);
        }).WaitAsync(TimeSpan.FromSeconds(20));

        await Assert.That(owned).IsTrue();
        await Assert.That(settled).IsTrue();
    }

    [Test]
    public async Task The_wizard_is_the_main_window_and_closing_it_never_exits_the_app() {
        var (isWizard, visible, mode) = await AvaloniaSession.DispatchAsync(() => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; // pinned by OnFrameworkInitializationCompleted
            var (wizard, _) = NewShell();

            var window = AppUnderTest.ShowWizardWindow(desktop, wizard);
            Dispatcher.UIThread.RunJobs();

            var result = (ReferenceEquals(fake.MainWindow, window), window.IsVisible, fake.ShutdownMode);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return result;
        });

        await Assert.That(isWizard).IsTrue();
        await Assert.That(visible).IsTrue();
        // No Shutdown call and the mode untouched: closing the wizard hands over, it never exits.
        await Assert.That(mode).IsEqualTo(ShutdownMode.OnExplicitShutdown);
    }

    [Test]
    public async Task Closing_the_wizard_window_ends_the_close_wait() {
        await AvaloniaSession.DispatchAsync(async () => {
            var (wizard, _) = NewShell();
            var window = new OnboardingWindow { DataContext = wizard };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var wait = AppUnderTest.WaitForWizardCloseAsync(wizard, CancellationToken.None);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            await wait.WaitAsync(TimeSpan.FromSeconds(10));

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Test]
    public async Task A_shutdown_while_the_wizard_is_open_ends_the_close_wait() {
        await AvaloniaSession.DispatchAsync(async () => {
            var (wizard, _) = NewShell();
            using var cts = new CancellationTokenSource();

            var wait = AppUnderTest.WaitForWizardCloseAsync(wizard, cts.Token);
            await Task.Delay(20);
            await Assert.That(wait.IsCompleted).IsFalse();

            await cts.CancelAsync();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));

            return true;
        }).WaitAsync(TimeSpan.FromSeconds(20));
    }

    // ── the daemon step's row/ops identity derives from ResolveIdentity, never the claims
    // ── identity (ResolveConsentFlipIdentity stays literal, for TryConsume only) ──────

    [Test]
    public async Task The_daemon_steps_row_reflects_ResolveIdentity_not_the_claims_identity() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("row-profile", "https://row.example:443", AuthProvider.None, "someone", []));
            // Deliberately different values: if the row-classification gate ever read the claims
            // identity instead, this server (which the claims identity does NOT name) would still
            // canonicalize fine and the row would read something other than NoServerConfigured.
            harness.Identity            = ("row-profile", "file:///etc/passwd", "row-daemon");
            harness.ConsentFlipIdentity = ("claims-profile", "https://claims.example", "claims-daemon");

            var graph  = WizardComposition.BuildGraph(harness.Options());
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await daemon.RefreshAsync(CancellationToken.None);

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.NoServerConfigured);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0); // refused before any status read

            return true;
        });
    }

    [Test]
    public async Task The_ops_factory_receives_the_row_identitys_daemon_name_not_the_claims_identitys() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("row-profile", "https://row.example:443", AuthProvider.None, "someone", []));
            harness.Identity            = ("row-profile", "https://row.example", "row-daemon");
            harness.ConsentFlipIdentity = ("claims-profile", "https://claims.example", "claims-daemon");
            harness.Cli.StatusBehavior  = _ => Task.FromResult<ServiceSnapshot?>(
                new ServiceSnapshot("row-daemon", true, "running", "/opt/kcap/kcap-daemon", "/opt/kcap/kcap-daemon",
                    111, 111, false, false));

            var canonical = ServerIdentity.Canonicalize("https://row.example")!;
            var evidence = new ObservedEvidence(
                Reachable: true, Capabilities: [DaemonStepViewModel.ConsentV3Capability], DaemonVersion: "1.0.0",
                ServerUrl: canonical, DaemonName: "row-daemon", Pid: 111, InstanceId: "instance-1", IdentityConsistent: true);
            var options = harness.Options() with { Observation = new WizardFixtures.FixedObservation(evidence) };

            harness.Ops.QueueGet(new ConsentPolicyDto("allow", 300, []));
            harness.Ops.QueuePutV2(true, null);
            harness.Claims.Arm(new ConsentFlipClaim("row-profile", canonical));

            var graph  = WizardComposition.BuildGraph(options);
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await daemon.RefreshAsync(CancellationToken.None);
            await WizardFixtures.WaitUntilAsync(() => harness.Ops.PutV2Calls > 0, what: "the claim to apply");

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.AlreadyEnabled);
            await Assert.That(harness.OpsFactoryNames).Contains("row-daemon");
            await Assert.That(harness.OpsFactoryNames).DoesNotContain("claims-daemon");
            // The claim was found and PUT (matched against the mutation request, itself built from
            // the ROW identity) but TryConsume's own re-resolve — resolveIdentityUnderConfigLock,
            // i.e. ResolveConsentFlipIdentity, deliberately the SEPARATE claims identity — disagrees
            // with the claim's key, so the claim stays pending. This is the EXACT proof the claims
            // path stayed decoupled from the row identity, not a bug in this test.
            await Assert.That(harness.Claims.Pending().Select(c => c.Profile)).IsEquivalentTo(["row-profile"]);

            return true;
        });
    }

}

/// <summary>
/// The startup decisions that read real config: the single fresh resolution the graph is built on,
/// and the §10 decision-2 abandon rows (zero service mutation, gate still incomplete afterwards).
///
/// [NotInParallel]: the process-global headless session, since composing the wizard constructs
/// ReactiveUI ViewModels.
/// </summary>
[NotInParallel("AvaloniaSession")]
public class WizardStartupResolutionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
    static readonly WorkOSClient Workos = new(new PlainHttpClientFactory());

    const string ProfileName = "acme";

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));

    /// The post-wizard build must never reuse the startup-cached resolution: a config written WHILE
    /// the wizard was open is what the graph is built against.
    [Test]
    public async Task The_gate_is_re_resolved_from_the_config_on_every_call() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var (before, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), CancellationToken.None);

        // What a committed wizard sign-in leaves behind: a real server plus its provider stamp.
        WriteConfig(SingleProfileConfig(new Profile {
            ServerUrl = "https://acme.example",
            AuthProvider = new AuthProviderStamp("none", "https://acme.example"),
        }));

        var (after, afterProfiles) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), CancellationToken.None);

        await Assert.That(before).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)before).Reason).IsEqualTo(GateReason.InvalidServerUrl);
        await Assert.That(after).IsTypeOf<GateResult.Complete>();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(after)).IsFalse();
        // The graph identity comes from the SAME resolution the verdict was read off.
        await Assert.That(afterProfiles?.Resolution.ServerUrl).IsEqualTo("https://acme.example");
    }

    // ── App.ResolveWizardIdentity's own resolution rules ──────────────────────

    [Test]
    public async Task ResolveWizardIdentity_unreadable_config_is_null() {
        File.WriteAllText(ConfigPath, "{ this is not valid json");

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root);

        await Assert.That(identity).IsNull();
    }

    [Test]
    public async Task ResolveWizardIdentity_a_profile_with_an_invalid_server_is_null() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root);

        await Assert.That(identity).IsNull();
    }

    [Test]
    public async Task ResolveWizardIdentity_a_valid_profile_resolves_the_canonical_tuple() {
        WriteConfig(SingleProfileConfig(new Profile {
            ServerUrl = "https://acme.example",
            Daemon    = new DaemonSettings { Name = "acme-daemon" },
        }));

        var identity = AppUnderTest.ResolveWizardIdentity(Config.Root);

        await Assert.That(identity).IsNotNull();
        await Assert.That(identity!.Value.Profile).IsEqualTo(ProfileName);
        await Assert.That(identity.Value.Server).IsEqualTo(ServerIdentity.Canonicalize("https://acme.example"));
        await Assert.That(identity.Value.DaemonName).IsEqualTo("acme-daemon");
    }

    // An empty-name sentinel would dial the sanitized DEFAULT daemon socket — unreadable config must yield null.
    [Test]
    public async Task Unreadable_config_shows_the_daemon_steps_not_ready_state_and_makes_no_status_or_ops_call() {
        File.WriteAllText(ConfigPath, "{ this is not valid json");

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            harness.Operation = (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.Committed("default", "https://acme.example:443", AuthProvider.None, "someone", []));

            var options = harness.Options() with { ResolveIdentity = () => AppUnderTest.ResolveWizardIdentity(Config.Root) };
            var graph  = WizardComposition.BuildGraph(options);
            var daemon = graph.Steps.OfType<DaemonStepViewModel>().Single();
            var signIn = graph.Steps.OfType<SignInStepViewModel>().Single();
            await signIn.SignInAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(signIn.Satisfied).IsTrue(); // sign-in itself doesn't touch config.json

            await daemon.RefreshAsync(CancellationToken.None);

            await Assert.That(daemon.Row).IsEqualTo(DaemonRow.RequiresSignIn);
            await Assert.That(harness.Cli.StatusCallCount).IsEqualTo(0);
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.OpsFactoryCalls).IsEqualTo(0);
            await Assert.That(harness.Lane.Requests).IsEmpty();

            return true;
        });
    }

    [Test]
    [Arguments("https://acme.example")] // valid URL, no token
    [Arguments("file:///tmp/x")]        // invalid, non-HTTP URL
    public async Task Abandoning_the_wizard_mutates_no_service_and_lands_on_the_carve_out_arm(string serverUrl) {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = serverUrl }));
        var configBefore = await File.ReadAllTextAsync(ConfigPath);

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var graph = WizardComposition.BuildGraph(harness.Options());

            // Abandon: the window closes without a single step being driven.
            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel())
                .WaitAsync(TimeSpan.FromSeconds(5));

            var (gate, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), CancellationToken.None);

            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(harness.Ops.GetCalls).IsEqualTo(0);
            await Assert.That(harness.Ops.PutV2Calls).IsEqualTo(0);
            await Assert.That(harness.Cli.InstallVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Cli.StartVerifiedCallCount).IsEqualTo(0);
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(configBefore);
            await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
            // The carve-out arm: auto-actions closed, which is also what suppresses the shim auto-offer.
            await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();

            return true;
        });
    }

    [Test]
    public async Task Closing_during_an_in_flight_pre_boundary_sign_in_writes_nothing() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "https://acme.example" }));
        var configBefore = await File.ReadAllTextAsync(ConfigPath);

        await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root);
            var started = new TaskCompletionSource();
            harness.Operation = async (_, ct) => {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // still pre-boundary when the close arrives
                return new AuthResult.Committed(ProfileName, "https://acme.example:443", AuthProvider.None, null, []);
            };

            var graph = WizardComposition.BuildGraph(harness.Options());
            var attempt = graph.Auth.Begin(new ConnectIntent.Discover(AuthProvider.WorkOS));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            graph.ViewModel.RequestClose();
            await AppUnderTest.HandoffAfterWizardAsync(
                    graph.Auth, () => Task.CompletedTask, TimeSpan.FromSeconds(5), new OutcomeChannel())
                .WaitAsync(TimeSpan.FromSeconds(5));

            var (gate, _) = await AppUnderTest.ResolveAndEvaluateGateAsync(
                Config.Root, AuthFixtures.NewTokenStore(Config.Root), CancellationToken.None);

            await Assert.That(await attempt.Result).IsTypeOf<AuthResult.Cancelled>();
            await Assert.That(await File.ReadAllTextAsync(ConfigPath)).IsEqualTo(configBefore);
            await Assert.That(harness.Claims.Pending()).IsEmpty();
            await Assert.That(harness.Lane.Requests).IsEmpty();
            await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();

            return true;
        });
    }

    /// The production operation over a scripted /auth/config: a pasted server is ADOPTED (its
    /// server_url is written), which is what carries the gate to Complete — a non-adopting login
    /// would have failed with "profile is not configured for ...".
    [Test]
    public async Task A_pasted_server_is_adopted_and_the_arming_hook_runs_before_the_commit() {
        WriteConfig(new ProfileConfig { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = new Profile() } });

        using var claimsRoot = new TempConfigRoot();
        var claims = new ConsentFlipClaims(claimsRoot.Root);
        var bridges = WizardComposition.BuildBridges(action => action(), new(new HttpClient()));
        using var handler = new StubAuthHandler();

        var operation = WizardComposition.BuildOperation(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), new PlainHttpClientFactory(handler),
            new AuthProxyClient(new HttpClient(handler, disposeHandler: false)), Github, Workos, ProfileName,
            bridges, claims,
            spec => WizardSignInOperation.For(new OnboardingFacade(
                spec.Root, spec.TokenStore, spec.HttpFactory, spec.Proxy, spec.GitHub, spec.WorkOS, spec.Progress,
                new RecordingBrowser(), spec.Picker, spec.Provisioner, spec.BeforeCommit), spec.Profile));

        var result = await operation(new ConnectIntent.Paste("https://acme.example"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var config = await AppConfig.LoadProfileConfig(Config.Root);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(config.Profiles[ProfileName].ServerUrl).IsEqualTo("https://acme.example");
        await Assert.That(config.Profiles[ProfileName].AuthProvider?.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(claims.Pending().Select(c => c.Profile).ToList()).IsEquivalentTo([ProfileName]);
        await Assert.That(handler.Requests.Any(r => r.Contains("/auth/config"))).IsTrue();
    }

    /// Both zero-tenant intents run the DISCOVERY leg (the auth proxy), not a per-server login —
    /// which is where the armed provisioner offers to create a workspace.
    [Test]
    [Arguments("create")]
    [Arguments("discover")]
    public async Task Create_and_workos_discovery_route_through_the_auth_proxy(string intentName) {
        using var claimsRoot = new TempConfigRoot();
        var claims = new ConsentFlipClaims(claimsRoot.Root);
        var bridges = WizardComposition.BuildBridges(action => action(), new(new HttpClient()));
        using var handler = new StubAuthHandler { Status = HttpStatusCode.ServiceUnavailable };
        ConnectIntent intent = intentName == "create"
            ? new ConnectIntent.Create()
            : new ConnectIntent.Discover(AuthProvider.WorkOS);

        WizardFacadeSpec? spec = null;
        var operation = WizardComposition.BuildOperation(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root), new PlainHttpClientFactory(handler),
            new AuthProxyClient(new HttpClient(handler, disposeHandler: false)), Github, Workos, ProfileName,
            bridges, claims,
            s => {
                spec = s;
                return WizardSignInOperation.For(new OnboardingFacade(
                    s.Root, s.TokenStore, s.HttpFactory, s.Proxy, s.GitHub, s.WorkOS, s.Progress,
                    new RecordingBrowser(), s.Picker, s.Provisioner, s.BeforeCommit), s.Profile);
            });

        var result = await operation(intent, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(handler.Requests.Any(r => r.Contains("/config"))).IsTrue();
        await Assert.That(spec!.Provisioner).IsSameReferenceAs(bridges.Provisioner);
    }

    /// Answers every GET with an auth-config body; the discovery rows flip Status so the proxy leg
    /// fails fast instead of opening a browser.
    sealed class StubAuthHandler : HttpMessageHandler {
        public readonly List<string> Requests = [];
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Requests.Add(request.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(Status) {
                Content = new StringContent("""{"provider":"None"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
