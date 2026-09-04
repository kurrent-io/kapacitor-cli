using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;
using AppUnderTest = Capacitor.App.App;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// Regression coverage for a Critical bug found in review: OnFrameworkInitializationCompleted
/// kicks off App.StartAsync fire-and-forget and returns immediately; Avalonia's
/// StartWithClassicDesktopLifetime calls ShowMainWindow() exactly ONCE, synchronously, right
/// after Start — and at that moment desktop.MainWindow was still null, because
/// startup genuinely awaits real config I/O (and, in wizard-first mode, the whole wizard). By the
/// time the continuation resumed and assigned desktop.MainWindow, nothing else ever called .Show()
/// — the app booted a dispatcher loop showing nothing.
///
/// That composition needs a real profile/daemon and isn't a seam a unit test can drive, so this
/// exercises the closest testable seam: App.BuildAndShowMainWindow (internal, exposed to
/// this assembly via InternalsVisibleTo) is the exact "build VM+window, assign, and Show()"
/// continuation extracted out of StartAsync — this test proves THAT method actually leaves the
/// window visible, against a fake service, without needing a real desktop lifetime or daemon.
public class AppStartupTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task BuildAndShowMainWindow_leaves_the_window_visible() {
        var isVisible = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var window = AppUnderTest.BuildAndShowMainWindow(
                service, Config.Root, AuthFixtures.NewTokenStore(Config.Root), actions,
                notifier, new FakeTicker(), CancellationToken.None, TestActivity.New());
            Dispatcher.UIThread.RunJobs(); // flush the deferred Loaded post (diagnostic parity with the smoke test)

            var visible = window.IsVisible;
            window.Close();
            return visible;
        });

        await Assert.That(isVisible).IsTrue();
    }

    /// The service composition StartAsync builds once and shares between the window, the tray and
    /// the pause controller (spec §7 one code path, §11 one banner/stderr channel).
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        return (new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm), notifier);
    }

    /// Regression coverage for a P2 bug found in review: the startup catch used to write to
    /// Console.Error and call desktop.Shutdown(1) directly — but App is OutputType=WinExe, so a
    /// normal GUI launch has no console, and a startup failure (bad config, window construction
    /// throw) made the app silently vanish with zero actionable error. BuildStartupErrorWindow
    /// is the replacement: a plain, visible window with a copyable (SelectableTextBlock) lead
    /// line plus the exception's full ToString(). This proves the rendered text actually carries
    /// both, the same way MainWindowSmokeTests proves bound VM text actually reaches the screen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task BuildStartupErrorWindow_renders_lead_line_and_exception_details() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var window = AppUnderTest.BuildStartupErrorWindow(new InvalidOperationException("boom-marker"));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = window.GetVisualDescendants()
                .Select(v => v switch {
                    SelectableTextBlock stb => stb.Text,
                    TextBlock tb => tb.Text,
                    _ => null,
                })
                .Where(t => t is not null);
            var joined = string.Join('\n', texts);

            window.Close();
            return joined;
        });

        await Assert.That(rendered).Contains("The app failed to start. Details:");
        await Assert.That(rendered).Contains("boom-marker");
    }

    /// Regression coverage for a P1 bug found in re-review: with the app's default ShutdownMode
    /// (OnLastWindowClose, never set elsewhere), closing the error window used to exit 0 instead
    /// of 1. Window.HandleClosed raises the CLR Closed event (our handler, calling Shutdown(1))
    /// BEFORE the routed WindowClosedEvent that OnLastWindowClose listens for; that routed event
    /// then drives an OnLastWindowClose TryShutdown() with its default exit code 0, which — via
    /// App.OnShutdownRequested's deferred cancel-then-retry dance — unconditionally overwrites
    /// the exit code back to 0. ShowStartupError now pins ShutdownMode to OnExplicitShutdown
    /// before showing the window, which disarms that whole branch. This drives ShowStartupError
    /// against a fake lifetime (no real desktop lifetime needed) and asserts: the mode is pinned
    /// and MainWindow assigned with no Shutdown call yet, then closing the window produces
    /// exactly one Shutdown call, with exit code 1.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ShowStartupError_pins_explicit_shutdown_and_exits_with_code_1_on_close() {
        var (modeAfterShow, mainWindowAssigned, callsBeforeClose, callsAfterClose) =
            await AvaloniaSession.DispatchAsync(() => {
                var (desktop, fake) = FakeClassicDesktopLifetime.Create();

                AppUnderTest.ShowStartupError(desktop, new InvalidOperationException("boom"));
                Dispatcher.UIThread.RunJobs();

                var mode = fake.ShutdownMode;
                var assigned = fake.MainWindow is not null;
                var before = fake.ShutdownCalls.ToArray();

                fake.MainWindow!.Close();
                Dispatcher.UIThread.RunJobs();

                var after = fake.ShutdownCalls.ToArray();
                return (mode, assigned, before, after);
            });

        await Assert.That(modeAfterShow).IsEqualTo(ShutdownMode.OnExplicitShutdown);
        await Assert.That(mainWindowAssigned).IsTrue();
        await Assert.That(callsBeforeClose).IsEmpty();
        await Assert.That(callsAfterClose).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    /// Fix-round-1 regression coverage (Task 21), carried forward against Task 22's REAL
    /// LifecyclePromptWindow/LifecyclePromptViewModel: the interim lifecycle prompt dialog used to
    /// ignore its ConfirmAsync CancellationToken entirely — the tcs only ever resolved on a button
    /// click or the window's own Closed event. Since ConfirmAndTakeoverAsync holds the operation
    /// gate across the whole ConfirmAsync await, a dialog left open through a lifetime-cancel (app
    /// shutdown) would hold that gate forever, and QuiescedAsync (the very backstop shutdown
    /// relies on) would never complete. WireDialogCancellation is the fix: a cancelled token
    /// closes the dialog, which resolves false through the SAME Closed handler a manual
    /// Cancel/titlebar close uses (wired here exactly like App.ShowLifecyclePromptDialogAsync
    /// does, since that wiring — not the window itself — is what owns the fallback). The overall
    /// dispatch is bounded (WaitAsync) so a reintroduced SizeToContent+Wrap headless hang (Task
    /// 21's carried-forward finding) fails this test instead of hanging CI — LifecyclePromptWindow
    /// deliberately uses a fixed Width/Height instead, see its .axaml.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WireDialogCancellation_resolves_false_and_closes_the_dialog() {
        var (resolvedFalse, stillVisible) = await AvaloniaSession.DispatchAsync(() => {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindRepair, null, null, false, "disclosure text");
            var dialog = new LifecyclePromptWindow { DataContext = new LifecyclePromptViewModel(prompt, tcs) };
            dialog.Closed += (_, _) => tcs.TrySetResult(false);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            using var cts = new CancellationTokenSource();
            AppUnderTest.WireDialogCancellation(dialog, tcs, cts.Token);

            cts.Cancel(); // simulates DisposeAsync cancelling _lifetime while the dialog is open
            Dispatcher.UIThread.RunJobs(); // flush the posted Close()

            return (tcs.Task.IsCompletedSuccessfully && tcs.Task.Result == false, dialog.IsVisible);
        }).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(resolvedFalse).IsTrue();
        await Assert.That(stillVisible).IsFalse();
    }

    /// A dialog resolved normally (button click, before any cancellation) must not have its
    /// registration fire later and try to re-close an already-closed window — the registration is
    /// disposed as soon as the tcs completes, so a later Cancel() is a silent no-op.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WireDialogCancellation_after_normal_resolution_ignores_a_later_cancel() {
        var result = await AvaloniaSession.DispatchAsync(() => {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var prompt = new LifecyclePrompt(LifecyclePrompt.KindRepair, null, null, false, "disclosure text");
            var dialog = new LifecyclePromptWindow { DataContext = new LifecyclePromptViewModel(prompt, tcs) };
            dialog.Closed += (_, _) => tcs.TrySetResult(false);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            using var cts = new CancellationTokenSource();
            AppUnderTest.WireDialogCancellation(dialog, tcs, cts.Token);

            // Same resolution path a real Accept/Decline click takes (LifecyclePromptViewModel.
            // Resolve): set the result, then close — Closed re-affirms false via TrySetResult,
            // which is a no-op once already resolved true.
            tcs.TrySetResult(true);
            dialog.Close();
            Dispatcher.UIThread.RunJobs();

            cts.Cancel(); // must not throw or flip the already-resolved result
            Dispatcher.UIThread.RunJobs();

            return tcs.Task.Result;
        }).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(result).IsTrue();
    }

    /// Minimal stand-in for LocalControlClient.RunAsync: yields Connecting once, then sits
    /// forever until its ct is cancelled (RestartLoopAsync/DisposeAsync's normal teardown path)
    /// — enough to prove a DaemonClientService actually has a LIVE loop to dispose.
    sealed class ForeverRunClient {
        public int LiveEnumerations;

        public async IAsyncEnumerable<LocalControlEvent> Run([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref LiveEnumerations);
            try {
                yield return new LocalControlEvent.Connecting();
                await Task.Delay(Timeout.Infinite, ct);
            } finally {
                Interlocked.Decrement(ref LiveEnumerations);
            }
        }
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    /// Regression coverage for a P2 bug found in review: a startup failure that happened AFTER
    /// service.Start()/_service assignment (e.g. BuildAndShowMainWindow throwing) used to go
    /// straight to ShowStartupError, abandoning the live IPC pump/socket — closing the error
    /// window force-shuts-down via desktop.Shutdown(1), which bypasses OnShutdownRequested and
    /// its async DisposeAsync entirely, so nothing else would ever clean it up. Drives the
    /// extracted HandleStartupFailureAsync against a REAL DaemonClientService (constructed with
    /// fakes, so disposal is directly observable) and asserts: the shutdown token is cancelled,
    /// the service's loop actually ends (proving DisposeAsync ran, not just was called), and the
    /// error window is still shown afterward exactly as ShowStartupError already guarantees.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task HandleStartupFailureAsync_disposes_the_live_service_before_showing_the_error_window() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention)));
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var shutdown = new CancellationTokenSource();

        var (modeAfterShow, mainWindowAssigned) = await AvaloniaSession.DispatchAsync(async () => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();
            await AppUnderTest.HandleStartupFailureAsync(
                desktop, new InvalidOperationException("boom"), service, shutdown, []);
            Dispatcher.UIThread.RunJobs();
            return (fake.ShutdownMode, fake.MainWindow is not null);
        });

        await Assert.That(shutdown.IsCancellationRequested).IsTrue();
        await Assert.That(modeAfterShow).IsEqualTo(ShutdownMode.OnExplicitShutdown);
        await Assert.That(mainWindowAssigned).IsTrue();
        await WaitUntilAsync(() => runClient.LiveEnumerations == 0, TimeSpan.FromSeconds(5));

        // A second DisposeAsync (mirroring the real catch path's `_service = null` guard against
        // a later OnShutdownRequested double-dispose) must be a safe no-op, not a throw.
        await service.DisposeAsync();
    }

    sealed class NeverObservation : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) => Task.FromResult<ObservedEvidence?>(null);
    }

    /// Task 10: the lane is disposed too during startup-failure cleanup (last, after
    /// lifecycle/service — see HandleStartupFailureAsync's own ordering comment) — a disposed
    /// lane cancels every subsequent RunAsync immediately, the observable proof this step ran.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task HandleStartupFailureAsync_disposes_the_lane() {
        var lane = new DaemonMutationLane(Daemons.Store,
            new FakeLoginShellProbe(), new OutcomeChannel(), () => null,
            (_, pinnedPath) => new FakeKcapCli { CliPath = pinnedPath },
            _ => new NeverObservation(), TimeProvider.System);

        var (modeAfterShow, mainWindowAssigned) = await AvaloniaSession.DispatchAsync(async () => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();
            await AppUnderTest.HandleStartupFailureAsync(
                desktop, new InvalidOperationException("boom"), service: null, new CancellationTokenSource(), [],
                lifecycle: null, lane: lane);
            Dispatcher.UIThread.RunJobs();
            return (fake.ShutdownMode, fake.MainWindow is not null);
        });

        await Assert.That(modeAfterShow).IsEqualTo(ShutdownMode.OnExplicitShutdown);
        await Assert.That(mainWindowAssigned).IsTrue();

        var request = new MutationRequest(MutationVerb.StartVerified, "default", "https://kcap.example.com:443", "daemon-a");
        await Assert.ThrowsAsync<OperationCanceledException>(() => lane.RunAsync(request, CancellationToken.None));
    }

    /// Regression coverage for a P2 bug found in re-review: TryShutdown() in the DEFERRED
    /// shutdown path (OnShutdownRequested -> DisposeAndShutdownAsync — e.g. Cmd+Q while the
    /// startup-error window is still up) used to be called with no exit code, defaulting to 0 —
    /// silently overwriting the startup failure with an apparent success. Drives the extracted
    /// DisposeAndConfirmShutdownAsync directly: a real DaemonClientService (fakes, disposal
    /// observable) and the same fake IClassicDesktopStyleApplicationLifetime used above, with
    /// exitCode: 1 (what StartAsync's catch now sets on _exitCode before a later
    /// OnShutdownRequested can reach this path). No Avalonia session needed — DispatchProxy and
    /// DaemonClientService are both plain .NET, same as DaemonClientServiceTests.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_disposes_then_confirms_then_carries_the_exit_code() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention)));
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        // Ordering pin: markConfirmed must observably run BEFORE TryShutdown is called — proven
        // by checking fake.ShutdownCalls is still empty at the moment markConfirmed fires.
        var confirmedBeforeShutdownCall = false;

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            service.DisposeAsync,
            markConfirmed: () => confirmedBeforeShutdownCall = fake.ShutdownCalls.Count == 0,
            desktop,
            exitCode: 1);

        await Assert.That(confirmedBeforeShutdownCall).IsTrue();
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
        await WaitUntilAsync(() => runClient.LiveEnumerations == 0, TimeSpan.FromSeconds(5));
    }

    /// Same seam, but the normal (non-failure) exit code: a plain Cmd+Q with no prior startup
    /// failure must still carry 0 through — this fix must not change the happy path.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_normal_shutdown_carries_exit_code_zero() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention)));
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var (desktop, fake) = FakeClassicDesktopLifetime.Create();

        await AppUnderTest.DisposeAndConfirmShutdownAsync(service.DisposeAsync, markConfirmed: () => { }, desktop, exitCode: 0);

        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([0], CollectionOrdering.Matching);
    }

    /// Regression coverage for a Qodo review finding: DisposeAndConfirmShutdownAsync used to call
    /// disposeAsync() with no surrounding try/catch/finally, so a throw left markConfirmed and
    /// TryShutdown never called — _shutdownConfirmed stuck false while _shutdownStarted stayed
    /// true, cancelling every later quit forever. Drives a disposeAsync delegate that throws and
    /// asserts confirm still happens and TryShutdown still carries the exit code.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_confirms_and_shuts_down_when_dispose_throws() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        var confirmed = false;

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            disposeAsync: () => throw new InvalidOperationException("dispose-boom"),
            markConfirmed: () => confirmed = true,
            desktop,
            exitCode: 1);

        await Assert.That(confirmed).IsTrue();
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    // spec §3.6: shutdown awaits DaemonLifecycleController.QuiescedAsync (mutations are never
    // abandoned) but only up to a cap, since an internally-triggered mutation has no shutdown-token
    // wiring of its own. AwaitQuiescedAsync is the extracted seam DisposeAndShutdownAsync wires it
    // through — no live controller/App needed to drive it directly.
    [Test]
    public async Task AwaitQuiescedAsync_returns_once_the_wait_completes() {
        var tcs = new TaskCompletionSource();
        var task = AppUnderTest.AwaitQuiescedAsync(() => tcs.Task, TimeSpan.FromSeconds(30));

        await Task.Delay(20);
        await Assert.That(task.IsCompleted).IsFalse();

        tcs.SetResult();
        await task;
    }

    [Test]
    public async Task AwaitQuiescedAsync_caps_an_otherwise_unbounded_wait() {
        var never = new TaskCompletionSource(); // deliberately never resolves
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await AppUnderTest.AwaitQuiescedAsync(() => never.Task, TimeSpan.FromMilliseconds(50));

        // Generous upper bound — this only needs to prove the cap fired, not measure it precisely.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
    }

    sealed class RecordingDisposable(Action onDispose) : IDisposable {
        public void Dispose() => onDispose();
    }

    /// Ordering pin for spec §9's "quit never strands a menu-bar icon" and spec §5's consent
    /// shutdown order: the UI-thread-owned disposables run, in the order given (tray icon first,
    /// then the prompt coordinator BEFORE the consent service it resolves against), all BEFORE the
    /// service dispose / markConfirmed / TryShutdown pass. Driven through a recording list rather
    /// than the real App fields, which no test can populate (StartAsync's composition needs a real
    /// daemon).
    [Test]
    public async Task DisposeUiThenConfirmShutdownAsync_disposes_ui_services_before_the_confirm_pass() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        var order = new List<string>();

        await AppUnderTest.DisposeUiThenConfirmShutdownAsync(
            [new RecordingDisposable(() => order.Add("tray")),
             new RecordingDisposable(() => order.Add("trayVm")),
             new RecordingDisposable(() => order.Add("promptCoordinator")),
             new RecordingDisposable(() => order.Add("consent")),
             new RecordingDisposable(() => order.Add("pause"))],
            disposeAsync: () => { order.Add("service"); return ValueTask.CompletedTask; },
            markConfirmed: () => order.Add("confirm"),
            desktop,
            exitCode: 0);

        await Assert.That(order).IsEquivalentTo(
            ["tray", "trayVm", "promptCoordinator", "consent", "pause", "service", "confirm"], CollectionOrdering.Matching);
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([0], CollectionOrdering.Matching);
    }

    /// Same class of bug DisposeAndConfirmShutdownAsync's throwing-dispose test pins, one step
    /// earlier: a throwing UI dispose must not skip the remaining disposables, markConfirmed or
    /// TryShutdown — otherwise _shutdownConfirmed stays false while _shutdownStarted stays true
    /// and every later quit is cancelled forever. Nulls are tolerated: App passes its
    /// possibly-unassigned tray/VM/pause fields straight through.
    [Test]
    public async Task DisposeUiThenConfirmShutdownAsync_continues_when_a_ui_dispose_throws() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        var order = new List<string>();

        await AppUnderTest.DisposeUiThenConfirmShutdownAsync(
            [new RecordingDisposable(() => throw new InvalidOperationException("tray-boom")),
             null,
             new RecordingDisposable(() => order.Add("promptCoordinator")),
             new RecordingDisposable(() => order.Add("consent")),
             new RecordingDisposable(() => order.Add("pause"))],
            disposeAsync: null,
            markConfirmed: () => order.Add("confirm"),
            desktop,
            exitCode: 1);

        await Assert.That(order).IsEquivalentTo(["promptCoordinator", "consent", "pause", "confirm"], CollectionOrdering.Matching);
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    // ---- MainWindowCoordinator: hide-to-tray lifecycle (spec §9) ----
    //
    // Real headless MainWindows with no DataContext: these pin window lifecycle, not bindings
    // (MainWindowSmokeTests owns the bound-text coverage), and the coordinator's Closing
    // interception is installed on the window itself, so it is live without a ViewModel.

    static (MainWindowCoordinator Coordinator, Func<int> Builds) NewCoordinator() {
        var builds = 0;
        var coordinator = new MainWindowCoordinator(() => {
            builds++;
            var window = new MainWindow();
            window.Show(); // the production factory (App.BuildAndShowMainWindow) shows too
            return window;
        });
        return (coordinator, () => builds);
    }

    /// Closing the window while no quit is in progress cancels the close and hides instead — the
    /// window instance survives (Closed never fires), which is what lets ShowMainWindow re-show
    /// it: Avalonia refuses to Show() a window that was really closed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Close_hides_window() {
        var (visibleAfterClose, stillTracked, builds) = await AvaloniaSession.DispatchAsync(() => {
            var (coordinator, buildCount) = NewCoordinator();
            coordinator.ShowMainWindow();
            var window = coordinator.Window!;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            var result = (window.IsVisible, ReferenceEquals(coordinator.Window, window), buildCount());
            coordinator.QuitInProgress = true; // let the fixture window actually go away
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(visibleAfterClose).IsFalse();
        await Assert.That(stillTracked).IsTrue();
        await Assert.That(builds).IsEqualTo(1);
    }

    /// The mirror: with QuitInProgress set (App.OnShutdownRequested's first, deferring pass), the
    /// close is NOT intercepted, so the second pass's real window teardown completes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Quit_lets_close_through() {
        var (interceptRequested, visibleAfterClose, stillTracked) = await AvaloniaSession.DispatchAsync(() => {
            var (coordinator, _) = NewCoordinator();
            coordinator.ShowMainWindow();
            var window = coordinator.Window!;

            coordinator.QuitInProgress = true;
            var intercept = coordinator.OnWindowClosing();
            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (intercept, window.IsVisible, coordinator.Window is not null);
        });

        await Assert.That(interceptRequested).IsFalse();
        await Assert.That(visibleAfterClose).IsFalse();
        await Assert.That(stillTracked).IsFalse();
    }

    /// Tray "Open Kurrent Capacitor" on a hidden window re-shows THAT window (no rebuild).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ShowMainWindow_reshows_same_instance() {
        var (same, visible, builds) = await AvaloniaSession.DispatchAsync(() => {
            var (coordinator, buildCount) = NewCoordinator();
            coordinator.ShowMainWindow();
            var window = coordinator.Window!;
            window.Close(); // hidden
            Dispatcher.UIThread.RunJobs();

            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();

            var result = (ReferenceEquals(coordinator.Window, window), window.IsVisible, buildCount());
            coordinator.QuitInProgress = true;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(same).IsTrue();
        await Assert.That(visible).IsTrue();
        await Assert.That(builds).IsEqualTo(1);
    }

    /// After a REAL close (quit path only) the tracked window is gone, so a later ShowMainWindow
    /// must build a fresh one from the factory — Avalonia throws on Show() of a closed window,
    /// and no view state is lost (everything displayed comes from the live service, spec §9).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ShowMainWindow_builds_fresh_after_real_close() {
        var (different, visible, builds) = await AvaloniaSession.DispatchAsync(() => {
            var (coordinator, buildCount) = NewCoordinator();
            coordinator.ShowMainWindow();
            var window = coordinator.Window!;

            coordinator.QuitInProgress = true;
            window.Close();
            Dispatcher.UIThread.RunJobs();

            coordinator.QuitInProgress = false;
            coordinator.ShowMainWindow();
            Dispatcher.UIThread.RunJobs();

            var fresh = coordinator.Window!;
            var result = (!ReferenceEquals(fresh, window), fresh.IsVisible, buildCount());
            coordinator.QuitInProgress = true;
            fresh.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(different).IsTrue();
        await Assert.That(visible).IsTrue();
        await Assert.That(builds).IsEqualTo(2);
    }

    /// Regression coverage for an Important finding in review: QuitInProgress used to be set
    /// AFTER OnShutdownRequested's `_shutdownConfirmed` guard, so a coordinator that came into
    /// existence BETWEEN the two passes was never flagged. Shape: a quit (or an OS logout) lands
    /// while startup is still resolving (or still showing the wizard) — pass 1 sees a null coordinator — and
    /// StartAsync's continuation then builds the window during the deferred disposal's await.
    /// Pass 2 closed the windows with hide-on-close still armed: the window cancelled its own
    /// close, and (decompiler-verified) DoShutdown aborts once a close is cancelled with windows
    /// still open, after which every later quit early-returns on _shutdownConfirmed — an app that
    /// can only be force-quit, over an already-disposed service. Drives the real pass logic in
    /// the real order, with the deferred pass in between.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shutdown_racing_the_startup_composition_still_tears_the_window_down() {
        var (pass1Defers, pass2Defers, flagged, tornDown, shutdownCalls) = await AvaloniaSession.DispatchAsync(async () => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();

            // Pass 1: nothing is composed yet, so there is no coordinator to flag.
            var deferred = AppUnderTest.BeginShutdownPass(coordinator: null, shutdownConfirmed: false);

            // StartAsync's continuation resumes DURING the deferred disposal's await and builds
            // the window. Its own `QuitInProgress = _shutdownStarted` belt-and-braces is
            // deliberately NOT applied here: this test pins the guard-ordering half of the fix,
            // so the coordinator arrives unflagged and only pass 2 can save the shutdown.
            var (coordinator, _) = NewCoordinator();
            coordinator.ShowMainWindow();
            var window = coordinator.Window!;

            // The deferred pass completes: dispose, confirm, TryShutdown(exitCode).
            var confirmed = false;
            await AppUnderTest.DisposeUiThenConfirmShutdownAsync(
                [], disposeAsync: null, markConfirmed: () => confirmed = true, desktop, exitCode: 1);

            // TryShutdown re-raises the event (pass 2), and DoShutdown then closes every window.
            var deferredAgain = AppUnderTest.BeginShutdownPass(coordinator, confirmed);
            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (deferred, deferredAgain, coordinator.QuitInProgress, coordinator.Window is null, fake.ShutdownCalls.ToArray());
        });

        await Assert.That(pass1Defers).IsTrue();
        await Assert.That(pass2Defers).IsFalse();  // the confirmed pass is let through untouched
        await Assert.That(flagged).IsTrue();       // ...but it still flagged the late coordinator
        await Assert.That(tornDown).IsTrue();      // Closed fired ⇒ the close was NOT cancelled
        await Assert.That(shutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    /// Spec §9: on a startup failure the error window is the only surface. Tray creation is
    /// structurally the LAST step of StartAsync's success path, so the failure path cannot have
    /// created one — this pins the other half of that claim, that the failure path itself never
    /// registers a tray icon on the Application.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Startup_failure_creates_no_tray() {
        var (trayIcons, errorWindowShown) = await AvaloniaSession.DispatchAsync(async () => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();

            await AppUnderTest.HandleStartupFailureAsync(
                desktop, new InvalidOperationException("boom"), service: null, new CancellationTokenSource(), []);
            Dispatcher.UIThread.RunJobs();

            var result = (TrayIcon.GetIcons(Application.Current!)?.Count ?? 0, fake.MainWindow is not null);
            fake.MainWindow?.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(trayIcons).IsEqualTo(0);
        await Assert.That(errorWindowShown).IsTrue();
    }

    /// A failure LATER in the success path (e.g. the tray's own construction throwing) leaves the
    /// services built before it live — and the error window's own desktop.Shutdown(1) bypasses
    /// OnShutdownRequested/DisposeAndShutdownAsync entirely, so this is their only cleanup. Same
    /// "dispose WHILE WE STILL CAN" rule the service disposal above already follows: proven here
    /// by a real TrayIconManager (its icon must be deregistered) and by a spy that records
    /// whether the error window was still unshown at the moment it was disposed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Startup_failure_disposes_the_ui_services_created_before_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (trayIcons, disposedBeforeErrorWindow, errorWindowShown) = await AvaloniaSession.DispatchAsync(async () => {
                var (desktop, fake) = FakeClassicDesktopLifetime.Create();
                var daemon = new FakeDaemonClientService();
                var (actions, _) = NewActions(daemon);
                var trayVm = new TrayViewModel(daemon, new FakePauseController(), actions, new FakeConsentService());
                var app = Application.Current!;
                var tray = new TrayIconManager(app, trayVm);

                var beforeErrorWindow = false;
                var spy = new RecordingDisposable(() => beforeErrorWindow = fake.MainWindow is null);

                await AppUnderTest.HandleStartupFailureAsync(
                    desktop, new InvalidOperationException("boom"), service: null, new CancellationTokenSource(),
                    [tray, trayVm, spy]);
                Dispatcher.UIThread.RunJobs();

                var result = (TrayIcon.GetIcons(app)?.Count ?? 0, beforeErrorWindow, fake.MainWindow is not null);
                fake.MainWindow?.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });

            await Assert.That(trayIcons).IsEqualTo(0);
            await Assert.That(disposedBeforeErrorWindow).IsTrue();
            await Assert.That(errorWindowShown).IsTrue();
        });
    }
}
