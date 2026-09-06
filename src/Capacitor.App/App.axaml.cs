using System.ComponentModel;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App;

public partial class App : Application {
    // spec §3.6: app shutdown WAITS (does not cancel) for an in-flight lifecycle mutation, but
    // only up to this cap — an internally-triggered mutation (startup matrix, skew, txn-requery)
    // has no other shutdown-token wiring, so an uncapped wait could hang shutdown forever.
    static readonly TimeSpan QuiesceShutdownCap = TimeSpan.FromSeconds(60);

    // One socket dial's bound inside DaemonMutationLane's own confirmation polling (its
    // DetachedPollInterval is 1s) — short enough that a handful of polls still fit inside the
    // lane's 10s DetachedConfirmWindow.
    static readonly TimeSpan OneShotProbeTimeout = TimeSpan.FromSeconds(2);

    // Linked to the app's shutdown sequence below; the token StartDaemonCommand's WAIT is built
    // against — never CancellationToken.None, or an unbounded wait would survive app exit.
    readonly CancellationTokenSource _shutdown = new();

    // The app's one read of KCAP_DAEMONS_DIR.
    readonly DaemonStore _daemonStore = DaemonStore.FromEnvironment();

    // And its one read of KCAP_CONFIG_DIR.
    readonly ConfigRoot _config = ConfigRoot.FromEnvironment();
    readonly UserHome   _userHome = UserHome.FromEnvironment();

    /// Only the foreign-host clients: a workspace is signed up for before there is a server to
    /// authenticate against. Process-lifetime rather than per wizard run — a provisioning poll can
    /// outlive the window that started it, and this client degrades a transport failure but not a
    /// disposed handler.
    ///
    /// The token store rides here for its pooled clients. Its WorkOS lane is one of the foreign
    /// ones; its refresh against our own server names a lane only <c>AddCapacitorHttp</c>
    /// registers, and an unregistered name yields a plain pooled client rather than an error —
    /// which is what this process wants, since that lane's handlers need a configured server.
    readonly ServiceProvider _foreignHttp;

    public App() =>
        _foreignHttp = new ServiceCollection()
            .AddSingleton(_config)
            .AddCapacitorForeignClients()
            .AddSingleton<TokenStore>()
            .BuildServiceProvider();

    // And its one read of KCAP_APP_PTY_DUMP: a file every terminal feed frame is appended to as
    // received, for seeing what the emulator was given.
    static readonly string? PtyDumpPath = Environment.GetEnvironmentVariable("KCAP_APP_PTY_DUMP");

    // Both are app-lifetime and BOTH exist before any graph does: OnShutdownRequested latches and
    // drains them whether or not StartAsync ever got as far as building a window (spec §3). The gate
    // is shared by every MainWindowViewModel the coordinator builds — including one built between
    // the two shutdown passes, which is the case a per-window latch cannot cover.
    readonly NavigationGate _navigation = new();
    readonly WorkspaceTeardownTracker _workspaceTeardown = new(
        TimeProvider.System,
        (context, ex) => Console.Error.WriteLine($"kcap app: {context} failed: {ex}"));
    // Constructed FIRST (before any other graph object) in StartAsync and disposed LAST — every
    // daemon mutation in the app runs through it, so nothing that might still call RunAsync can outlive it.
    DaemonMutationLane? _lane;
    DaemonClientService? _service; // concrete type: IAsyncDisposable is not on the interface
    // spec: subscribed and Start()'d BEFORE _service.Start() begins pumping (subscribe-before-
    // pump — DaemonLifecycleController.Start's own doc comment). Disposed before _service in every
    // teardown path below: it's the dependent (subscribes to _service's streams), so it goes first.
    DaemonLifecycleController? _lifecycle;
    // spec: no disposal needed — it holds no subscription of its own, only a one-shot
    // await chain against BuildLifecycleController's cliPath/probe/store/surface and _shutdown.Token,
    // so cancelling _shutdown (every teardown path below already does) is what stops it.
    ShimOfferCoordinator? _shimOffer;
    // No disposal needed — its Status subscription dies with _service's own subject disposal below.
    ConsentFlipCoordinator? _consentFlip;
    // Assigned by StartAsync's success path only; every one is still null on a startup failure
    // (and cleared again by the catch, which disposes whatever had been built). Teardown —
    // shutdown and startup-failure alike — disposes them in reverse creation order, tray icon
    // first, so a quit never strands a dead icon in the menu bar (spec §9).
    MainWindowCoordinator? _coordinator;
    PauseController? _pause;
    ConsentService? _consent;
    PermissionService? _permissions;
    ConsentPromptCoordinator? _promptCoordinator;
    // Disposed with the other UI services below: it holds a constructor-scoped subscription to
    // the shared ticker, which is RefCount'd — an undisposed subscriber keeps the Interval (and
    // this object) running past teardown. Held as a field so it survives StartAsync's own stack
    // frame: the prompt window factory and BuildAndShowMainWindow both close over the SAME
    // instance.
    ActivityViewModel? _activity;
    // Constructed INSIDE BuildAndShowMainWindow, over the same `service`
    // instance MainWindowViewModel itself uses — retrieved back off the built window's own
    // DataContext right below, so this field (and therefore disposal) never needs a second
    // construction path or a signature change to BuildAndShowMainWindow (AppStartupTests calls
    // that method directly, with no Home argument).
    HomeViewModel? _home;
    // Same reasoning as _home just above: constructed INSIDE BuildAndShowMainWindow (over the
    // SAME `service` instance) and read back off the built window's DataContext right after, so
    // this field never needs a second construction path or a signature change to
    // BuildAndShowMainWindow either.
    SessionRailViewModel? _rail;
    // The server-side clients that outlive a window rebuild (MainWindowCoordinator can build a
    // second window over the same launch client) and own live transports. Torn down through one
    // holder on both teardown paths, after _home — never before, or a launch still in flight would
    // lose its transport mid-invoke.
    ServerClients? _serverClients;
    ServerLaunchClient? _launch;
    TrayViewModel? _trayVm;
    TrayIconManager? _tray;
    // No disposal needed — RefCount tears its Interval down with its last subscriber, and every
    // subscriber above IS disposed. Held so the consent prompt and the activity feed share the
    // same 1 Hz heartbeat.
    UiTicker? _ticker;
    // Wizard-first mode only (spec decision 2): the sign-in driver shutdown cancels and awaits
    // before anything is disposed, the Import step whose in-flight run shutdown must also kill
    // (spec §7 — closing the window never navigates through ImportStepViewModel.CanLeaveAsync), and
    // the window that owns dialogs while no main window exists. All three are cleared again by the
    // handoff at the end of RunWizardModeAsync.
    WizardAuthService? _wizardAuth;
    ImportStepViewModel? _wizardImport;
    Window? _wizardWindow;
    // Steady-state re-auth dialog, one at a time — a second Sign in click focuses it. The settle
    // task is FinishSignInAsync for the most recent close; shutdown awaits it so a live attempt is
    // cancelled (or a commit past the boundary finishes) before the process exits — the dialog's
    // counterpart of the wizard sign-in quiesce.
    SignInWindow? _signInWindow;
    Task? _reauthSettle;
    bool _shutdownStarted;
    bool _shutdownConfirmed;
    // 0 = normal shutdown. Set to 1 on a startup failure so the DEFERRED shutdown path (Cmd+Q /
    // platform shutdown while the error window is showing — OnShutdownRequested ->
    // DisposeAndShutdownAsync) still reports failure, instead of TryShutdown()'s platform
    // default of 0 silently overwriting it.
    int _exitCode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // The steady-state mode (spec §9): closing the main window hides it to the tray, so
            // the app must never exit on last-window-close. Set here, before StartAsync fires, so
            // it holds from the very first window onward; ShowStartupError pins the same value
            // again on the failure path, where it is now redundant but self-documenting (its own
            // comment explains the exit-code bug that pin fixes).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // This continuation is the ONLY path to a visible window: OnFrameworkInitializationCompleted
    // fires it fire-and-forget and returns immediately, so an exception escaping here would
    // otherwise leave a live process with an empty dispatcher loop, no window, and no error
    // surface (stderr is invisible for a GUI-launched WinExe) — it must fail loudly instead.
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        try {
            // The lane is constructed first — every daemon mutation routes through this one instance, and its dependencies need neither a resolved profile nor a live service.
            var laneRunner = new ProcessRunner();
            var laneProbe  = new LoginShellProbe(laneRunner, Environment.GetEnvironmentVariable);
            var channel    = new OutcomeChannel();
            var lane = new DaemonMutationLane(
                _daemonStore, laneProbe, channel, ResolveCliOverride,
                (request, pinnedPath) => new KcapCli(
                    laneRunner, pinnedPath, request.DaemonName, request.Profile, laneProbe.TerminalPathAsync,
                    canonicalServer: request.CanonicalServer),
                _ => new OneShotObservation(_daemonStore, OneShotProbeTimeout),
                TimeProvider.System);
            _lane = lane;

            var (gate, profiles) = await ResolveAndEvaluateGateAsync(_config, _foreignHttp.GetRequiredService<TokenStore>(), _shutdown.Token);
            // A graph built while the lane still owns a live action must not also drive automatic
            // ones (spec §6a) — only the wizard's own handoff can answer this with anything but true.
            var laneQuiesced = true;

            // spec decision 2: an incomplete gate builds NO daemon graph at all — the wizard owns
            // the app until it closes, and the graph is then built against a FRESH resolution,
            // because the wizard is exactly what may have changed the answer.
            if (gate is GateResult.Incomplete) {
                laneQuiesced = await RunWizardModeAsync(desktop, lane, channel, laneRunner, laneProbe, profiles);
                if (_shutdown.IsCancellationRequested) return; // quit during onboarding — nothing left to build
                (gate, profiles) = await ResolveAndEvaluateGateAsync(_config, _foreignHttp.GetRequiredService<TokenStore>(), _shutdown.Token);
            }

            BuildDaemonGraph(desktop, lane, channel, gate, profiles, laneQuiesced);
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
            // A quit landing mid-startup (gate evaluation, the wizard's PATH probe, the post-wizard
            // re-resolve) is not a startup failure: the shutdown path already owns teardown, and an
            // error window here would both lie and outlive the quit.
            _wizardWindow = null;
            _wizardAuth = null;
            _wizardImport = null;
        } catch (Exception ex) {
            // BEFORE any await: a shutdown request can arrive while cleanup below is still
            // awaiting (or if the helper itself throws), and the deferred path reads this.
            _exitCode = 1;
            // Also before any await, and for the same reason: if the main window was already up
            // when the failure hit, no tray will ever exist to bring it back, so hide-on-close
            // must not intercept anything from here on — every close on this path is a real one.
            if (_coordinator is not null) _coordinator.QuitInProgress = true;
            // Also before any await, and the same reasoning as the shutdown path's: a workspace the
            // user opened before the failure landed must release its attach here, not survive into
            // the error window.
            LatchNavigation();
            // No orphan wizard beside the error window: it is a dead shell once startup has failed,
            // and its own close path (the handoff) is exactly what did not run.
            if (_wizardWindow is { IsVisible: true } wizard) wizard.Close();
            Console.Error.WriteLine($"kcap app failed to start: {ex}");
            await _workspaceTeardown.DrainAsync();
            await HandleStartupFailureAsync(
                desktop, ex, _service, _shutdown, [_tray, _trayVm, _promptCoordinator, _consent, _permissions, _activity, _home, _rail, _pause], _lifecycle, _lane);
            await DisposeServerClientsAsync(); // after _home above
            // all already disposed above — never let a later OnShutdownRequested (e.g. Cmd+Q
            // while the error window is up) dispose any of them a second time
            _service = null;
            _lifecycle = null;
            _lane = null;
            _shimOffer = null; // no disposal of its own (see field comment) — just drop the reference
            _consentFlip = null; // same — no disposal of its own
            _tray = null;
            _trayVm = null;
            _promptCoordinator = null;
            _consent = null;
            _permissions = null;
            _pause = null;
            _activity = null;
            _home = null;
            _rail = null;
            _wizardAuth = null; // its attempt, if any, already settled through the wizard's own close path
            _wizardImport = null; // same — any in-flight run already settled through the wizard's own close path
            _wizardWindow = null;
        }
    }

    // The ONE resolve+evaluate composition (OnboardingGate.EvaluateAsync), wrapped in the
    // never-brick degrade: the verdict and the resolution come back together, so the daemon identity
    // cannot be read off a second one, and the post-wizard build re-runs this rather than reusing a
    // startup value the wizard may have invalidated.
    internal static Task<(GateResult Gate, ProfileContext? Profiles)> ResolveAndEvaluateGateAsync(
            ConfigRoot config, TokenStore tokenStore, CancellationToken ct) =>
        EvaluateGateSafelyAsync(new OnboardingGate(config, tokenStore).EvaluateAsync, ct);

    // The steady-state graph, over the resolution the gate was evaluated on (never a second resolve).
    void BuildDaemonGraph(
            IClassicDesktopStyleApplicationLifetime desktop, DaemonMutationLane lane, OutcomeChannel channel,
            GateResult gate, ProfileContext? profiles, bool laneQuiesced) {
        var service = DaemonClientService.CreateResolved(_daemonStore, profiles?.Resolution, lane.RunAsync);

        // Incomplete after the wizard (abandoned, or sign-in skipped) is the carve-out arm, and so
        // is a lane that outran the handoff cap: the graph comes up with every lifecycle
        // auto-action closed and the shim auto-offer suppressed.
        var autoActionsPermanentlyClosed = AutoActionsPermanentlyClosed(gate, laneQuiesced);

        // One LocalControlOps and one AppNotifier for the whole app: the tray menu and the
        // window rows share a single stop/open-in-web code path (spec §7) and a single
        // toast/stderr channel (spec §11). notifier is built here (not after service.Start()
        // below) because PauseController/AgentActionService, constructed further down, need it.
        var ops      = new LocalControlOps(_daemonStore, service.DaemonName);
        var notifier = new AppNotifier();

        // spec: BehaviorSubjects, not plain Subjects — MainWindowViewModel and
        // TrayViewModel don't exist yet at this point in StartAsync (built further down), so a
        // BehaviorSubject replays its latest value to whichever one subscribes later, meaning a
        // Status/Attention call this early (the startup-phase reconciliation, e.g.) is never
        // silently dropped for having no subscriber yet.
        var lifecycleStatus    = new BehaviorSubject<string?>(null);
        var lifecycleAttention = new BehaviorSubject<string?>(null);

        // spec subscribe-before-pump: the controller's attach subscription must be live
        // BEFORE service.Start() begins pumping, or the startup phase could miss the very
        // first terminal outcome it hinges on (DaemonLifecycleController.Start's own comment).
        var (lifecycle, shimOffer, consentFlip, lifecycleSurface, lifecycleProbe) = BuildLifecycleController(
            service, ops, autoActionsPermanentlyClosed, lifecycleStatus.OnNext, lifecycleAttention.OnNext,
            lane.RunAsync, profiles?.Resolution);
        lifecycle.Start();
        _lifecycle = lifecycle;
        // Subscribe-before-run doesn't matter here (Offerable replays); always started so manual install keeps working in Incomplete mode — autoOfferSuppressed skips only the dialog.
        shimOffer.Start();
        _shimOffer = shimOffer;

        consentFlip.Start();
        _consentFlip = consentFlip;

        // Said once, here, because nothing else in the app would: the graph is up but deliberately degraded.
        AnnounceUnquiescedLane(lifecycleSurface, laneQuiesced);

        // Composition-root outcome consumer: shares lifecycle's own surface/probe/CliVersion and the lane itself, so a Takeover accept re-mutates through the same gate.
        _ = ConsumeMutationOutcomesAsync(
            channel, lifecycleSurface, lane.RunAsync, lifecycleProbe.TerminalPathAsync, () => lifecycle.CliVersion, _shutdown.Token);

        service.Start();
        _service = service;

        var ticker = new UiTicker();
        _ticker = ticker;
        _pause = new PauseController(ops, notifier.Notify, _shutdown.Token);
        // ConfirmForceStopAsync reads _coordinator at INVOCATION time (a captured field, not
        // a captured value) — safe even though _coordinator is still null right here, because
        // nothing can trigger a protected-kind stop before ShowMainWindow below assigns it.
        var opener = new ShellUrlOpener();
        var actions = new AgentActionService(ops, notifier, opener, service.Snapshots, _shutdown.Token, ConfirmForceStopAsync);

        // Constructed once here, like the ticker and consent service (spec §7): the prompt
        // window factory below and MainWindowViewModel both need the SAME instance — the
        // former to nudge it on every conclusive ack, the latter to render it.
        var activity = new ActivityViewModel(
            () => ConsentDecisionLogReader.ReadTail(_daemonStore, service.DaemonName, 200),
            () => ActivityStatKey(_daemonStore, service.DaemonName), ticker);
        _activity = activity;

        // The prompt window is built per raise, never here: the coordinator owns its lifetime
        // and each window gets its own ViewModel over the one shared service (spec §6).
        var consent = new ConsentService(
            service, ops, ticker, ct => ConsentSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
            TimeProvider.System, _shutdown.Token);
        _consent = consent;

        var permissions = new PermissionService(
            service, ops, ct => PermissionSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
            TimeProvider.System, _shutdown.Token);
        _permissions = permissions;

        _promptCoordinator = new ConsentPromptCoordinator(consent, () => new ConsentPromptWindow {
            DataContext = new ConsentPromptViewModel(
                consent, notifier, ticker, TimeProvider.System, _shutdown.Token, activity.RequestRefresh),
            Notifier = notifier,
        });

        // One launch client and one work-context source for the app, not one per window the
        // coordinator builds — each owns a live transport, and only a held instance can be
        // disposed at teardown.
        var launch = new ServerLaunchClient(profiles, _foreignHttp.GetRequiredService<TokenStore>());
        var workContext = new ServerWorkContextSource(_config, profiles);
        var serverClients = new ServerClients(launch, workContext);
        _launch = launch;
        _serverClients = serverClients;

        // One attach client per attempt, dialed at the daemon's own control socket; 80x24 is a
        // placeholder only — TerminalControl resizes its model to the real pane the moment it is
        // attached to the visual tree (WorkspaceView's own header comment).
        var attachFactory = CoreTerminalAttachClient.Factory(() => _daemonStore.SocketPath(service.DaemonName));
        Action requestSignIn = () => OpenSignInDialog(profiles, notifier);
        WorkspaceViewModel BuildWorkspace(string agentId) => new(
            agentId, service, actions, attachFactory, () => new XtermTerminalSurface(80, 24, PtyDumpPath), TimeProvider.System, opener, permissions,
            workContext, requestSignIn: requestSignIn, signInCompleted: serverClients.SignInCompleted);

        _coordinator = new MainWindowCoordinator(
            () => BuildAndShowMainWindow(
                service, _config, _foreignHttp.GetRequiredService<TokenStore>(), actions, notifier, ticker, _shutdown.Token, activity, lifecycle.StartActionAsync,
                lifecycleStatus, launch, _navigation, _workspaceTeardown.Track, BuildWorkspace,
                // The tenant slug the rail footer shows — profiles are named after it at sign-in.
                tenantName: profiles?.Resolution?.ProfileName, agentsWithPending: permissions.AgentsWithPending,
                requestSignIn: requestSignIn,
                lifecycleAttention: lifecycleAttention),
            // Both close paths release the workspace: hide-to-tray keeps the window (and its
            // attach) alive, a real close discards the window the next Show() would rebuild.
            releaseWorkspace: window => (window.DataContext as MainWindowViewModel)?.CloseWorkspace());
        // A shutdown that started before this continuation resumed already ran its first
        // pass against a null coordinator, so a window built now must never be
        // close-protected (BeginShutdownPass's rule 1 is the general defense; this is the
        // by-construction one, and it is why the window below cannot even briefly intercept).
        _coordinator.QuitInProgress = _shutdownStarted;
        _coordinator.ShowMainWindow();
        desktop.MainWindow = _coordinator.Window;
        // BuildAndShowMainWindow constructs Home itself (over the same `service`) — read back off
        // the window's own DataContext rather than threading a new parameter through, so
        // AppStartupTests' existing direct call to that method needs no change.
        _home = (_coordinator.Window?.DataContext as MainWindowViewModel)?.Home;
        _rail = (_coordinator.Window?.DataContext as MainWindowViewModel)?.Rail;

        // LAST, deliberately (spec §9): anything above throwing lands in the catch with no
        // tray icon ever created, leaving the error window as the only surface.
        _trayVm = new TrayViewModel(
            service, _pause, actions, consent, openMainWindow: _coordinator.ShowMainWindow,
            quit: () => desktop.TryShutdown(), openReviewPrompts: _promptCoordinator.ShowPromptWindow,
            lifecycleAttention: lifecycleAttention, shimOfferable: shimOffer.Offerable,
            installShim: shimOffer.RunManualInstallAsync, permissions: permissions);
        _tray = new TrayIconManager(this, _trayVm);
    }

    /// Home's Sign in action: the re-auth dialog over a fresh ReauthComposition graph, pinned to
    /// the resolved server. A graph is built per open — a settled attempt's rendered state must
    /// never leak into the next sign-in.
    void OpenSignInDialog(ProfileContext? profiles, IAppNotifier notifier) {
        if (_signInWindow is { } open) {
            open.Activate();
            return;
        }

        // Reachable in the carve-out arm (gate Incomplete after an abandoned wizard), where the
        // rail can show disconnected with no server to re-auth against.
        if (profiles?.Resolution.ServerUrl is not { } serverUrl || !OnboardingGate.ValidServerUrl(serverUrl)) {
            notifier.Notify("No server is configured. Run kcap setup first.");
            return;
        }

        var graph = ReauthComposition.Build(
            _config,
            _foreignHttp.GetRequiredService<TokenStore>(),
            _foreignHttp.GetRequiredService<IHttpClientFactory>(),
            _foreignHttp.GetRequiredService<IAuthProxyClient>(),
            _foreignHttp.GetRequiredService<GitHubOAuthClient>(),
            _foreignHttp.GetRequiredService<WorkOSClient>(),
            profiles.Name, serverUrl,
            WizardComposition.BuildBridges(
                action => Dispatcher.UIThread.Post(action),
                _foreignHttp.GetRequiredService<TenantProvisioningClient>()),
            new ConsentFlipClaims(_config),
            new AppStateStore(_config.Path("app-state.json")),
            new ShellUrlOpener(),
            WizardComposition.NewOperation);
        var window = new SignInWindow { DataContext = graph.SignIn };

        // Hold the dialog on success so "Signed in…" is readable, refresh app state in parallel,
        // then close. Closed below is still the ONE finish path for cancel/quiesce.
        void OnSignInChanged(object? _, PropertyChangedEventArgs e) {
            if (e.PropertyName == nameof(SignInStepViewModel.Satisfied) && graph.SignIn.Satisfied)
                _ = CloseSignInAfterSuccessAsync(window);
        }

        graph.SignIn.PropertyChanged += OnSignInChanged;
        window.Closed += (_, _) => {
            graph.SignIn.PropertyChanged -= OnSignInChanged;
            _signInWindow = null;
            _reauthSettle = FinishSignInAsync(graph);
        };

        _signInWindow = window;
        window.Show();
    }

    /// How long a successful re-auth keeps the dialog open so the success line is not a flash.
    internal static readonly TimeSpan SignInSuccessHold = TimeSpan.FromMilliseconds(1600);

    async Task CloseSignInAfterSuccessAsync(Window window) {
        var refresh = RefreshAfterReauthAsync();
        try {
            await Task.WhenAll(refresh, Task.Delay(SignInSuccessHold)).ConfigureAwait(true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: post-sign-in refresh failed: {ex.Message}");
        }

        if (ReferenceEquals(_signInWindow, window)) window.Close();
    }

    /// Clears the launcher's expired/disconnected sign-in banner path, drops a stale launch hub,
    /// nudges work-context refresh, and kicks attach so snapshots catch up sooner after new tokens land.
    async Task RefreshAfterReauthAsync() {
        _home?.NotifySignInCompleted();
        _serverClients?.NotifySignInCompleted();
        if (_launch is not null) await _launch.InvalidateAsync().ConfigureAwait(true);
        if (_service is not null) await _service.RestartLoopAsync().ConfigureAwait(true);
    }

    /// Fire-and-forget from the dialog's Closed handler — nothing may block the UI close. The
    /// quiesce is what stops a still-running attempt from committing after the window is gone.
    async Task FinishSignInAsync(ReauthGraph graph) {
        try {
            await graph.CloseAsync(CancellationToken.None);
            // Success path already refreshed before close; call again so a manual close after
            // Satisfied still clears the banner if the hold was interrupted.
            if (graph.SignIn.Satisfied) await RefreshAfterReauthAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: sign-in dialog teardown failed: {ex.Message}");
        }
    }

    // Wizard-first mode (spec decision 2): no service, no tray, no lifecycle controller, no
    // coordinators — just the wizard, the app-lifetime lane, and the SAME outcome consumer over a
    // wizard-local surface. Returns once the wizard has closed AND the channel has been handed on;
    // false means the lane outran the handoff cap and the graph must close its auto-actions.
    async Task<bool> RunWizardModeAsync(
            IClassicDesktopStyleApplicationLifetime desktop, DaemonMutationLane lane, OutcomeChannel channel,
            IProcessRunner runner, ILoginShellProbe probe, ProfileContext? profiles) {
        var cliPath = CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists);
        // Same rule as the shim coordinator's: only a resolved ABSOLUTE path is linkable.
        var shimTarget = cliPath is not null && Path.IsPathRooted(cliPath) ? cliPath : null;
        var shimApplicable = await ResolveShimApplicableAsync(
            OperatingSystem.IsMacOS(), shimTarget, ct => probe.KcapOnPathAsync(ct), _shutdown.Token);
        var bridges = WizardComposition.BuildBridges(
            action => Dispatcher.UIThread.Post(action),
            _foreignHttp.GetRequiredService<TenantProvisioningClient>());
        var surface = new WizardLifecycleSurface(ConfirmLifecyclePromptAsync, action => Dispatcher.UIThread.Post(action));

        var graph = WizardComposition.BuildGraph(new WizardGraphOptions(
            _config,
            _foreignHttp.GetRequiredService<TokenStore>(),
            _foreignHttp.GetRequiredService<IHttpClientFactory>(),
            _foreignHttp.GetRequiredService<IAuthProxyClient>(),
            _foreignHttp.GetRequiredService<GitHubOAuthClient>(),
            _foreignHttp.GetRequiredService<WorkOSClient>(),
            // Nothing resolved means the gate's evaluation threw; sign-in then targets the name every
            // fallback lands on, which is what an unresolved config would have answered anyway.
            profiles?.Name ?? ProfileConfig.DefaultName,
            new ConsentFlipClaims(_config),
            bridges,
            WizardComposition.NewOperation,
            surface,
            ResolveCli: () => NewWizardCli(_config, runner, cliPath, probe),
            ResolveOps: name => new LocalControlOps(_daemonStore, name),
            ResolveIdentity: () => ResolveWizardIdentity(_config),
            ResolveConsentFlipIdentity: () => ResolveConsentFlipIdentity(_config),
            RunMutation: lane.RunAsync,
            Observation: new OneShotObservation(_daemonStore, OneShotProbeTimeout),
            AppState: new AppStateStore(_config.Path("app-state.json")),
            ShimInstaller: new PathShimInstaller(runner, probe),
            UrlOpener: new ShellUrlOpener(),
            Probe: probe,
            DetectionFeed: probe => AgentsStepViewModel.BuildDetectionFeed(probe, _userHome),
            CliPath: cliPath,
            ShimApplicable: shimApplicable,
            ShimTarget: shimTarget,
            DefaultDaemonName: ResolveWizardIdentity(_config)?.DaemonName,
            Time: TimeProvider.System,
            ShutdownToken: _shutdown.Token));

        _wizardAuth = graph.Auth;
        _wizardImport = graph.Import;
        var window = ShowWizardWindow(desktop, graph.ViewModel);
        _wizardWindow = window;

        // The SAME consumer function, over the wizard's own surface: outcome presentation stays
        // single-owner while no tray or main window exists. No CLI version to disclose here —
        // wizard mode builds no lifecycle controller to have probed one.
        _ = ConsumeMutationOutcomesAsync(
            channel, surface, lane.RunAsync, probe.TerminalPathAsync, () => null, _shutdown.Token);

        await WaitForWizardCloseAsync(graph.ViewModel, _shutdown.Token);
        var quiesced = await HandoffAfterWizardAsync(
            graph.Auth, () => lane.QuiescedAsync(CancellationToken.None), QuiesceShutdownCap, channel, graph.Import);

        _wizardAuth = null;
        _wizardImport = null;
        _wizardWindow = null;
        if (window.IsVisible) window.Close(); // shutdown ended the wait with the window still up

        return quiesced;
    }

    /// The shim step's applicability, without paying for an answer that cannot matter: the probe
    /// costs a login-shell spawn, and on every non-macOS machine (and every machine with no
    /// resolved absolute CLI) <see cref="ShimStepViewModel.ComputeApplicable"/> is already false.
    internal static async Task<bool> ResolveShimApplicableAsync(
            bool isMacOs, string? shimTarget, Func<CancellationToken, Task<bool?>> probeKcapOnPath, CancellationToken ct) {
        if (!isMacOs || shimTarget is null) return false;

        return ShimStepViewModel.ComputeApplicable(
            isMacOs, shimTarget, await ProbeKcapOnPathSafelyAsync(probeKcapOnPath, ct).ConfigureAwait(true));
    }

    // A probe failure reads as unknown, never as "offer it anyway" — ComputeApplicable's own null
    // rule. A cancellation matching the caller's token is a quit, not an unknown: it propagates, so
    // no wizard window is built after the app has started shutting down (EvaluateGateSafelyAsync's rule).
    static async Task<bool?> ProbeKcapOnPathSafelyAsync(Func<CancellationToken, Task<bool?>> probe, CancellationToken ct) {
        try {
            return await probe(ct).ConfigureAwait(true);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: PATH probe failed — skipping the command-line tool step: {ex.Message}");
            return null;
        }
    }

    // Rebuilt per call (LateBoundKcapCli): the wizard writes the profile, server and daemon name
    // while its steps run, so a binding pinned at composition time would query the wrong service.
    static IKcapCli NewWizardCli(
            ConfigRoot config, IProcessRunner runner, string? cliPath, ILoginShellProbe probe) {
        var (profile, server, daemonName) = ResolveConsentFlipIdentity(config);

        return new KcapCli(
            runner, cliPath, daemonName, string.IsNullOrEmpty(profile) ? "default" : profile,
            probe.TerminalPathAsync, canonicalServer: ServerIdentity.Canonicalize(server));
    }

    // ShutdownMode is deliberately untouched (OnExplicitShutdown, pinned in
    // OnFrameworkInitializationCompleted): closing the wizard hands over to the normal graph,
    // it never exits the app.
    internal static OnboardingWindow ShowWizardWindow(
            IClassicDesktopStyleApplicationLifetime desktop, OnboardingViewModel wizard) {
        var window = new OnboardingWindow { DataContext = wizard };
        desktop.MainWindow = window;
        window.Show();

        return window;
    }

    // One logical close (the Done step's finish and the window's own Closing both route through
    // RequestClose, which is idempotent). Shutdown ends the wait too — a quit must never sit
    // behind a window the user is no longer looking at.
    internal static async Task WaitForWizardCloseAsync(OnboardingViewModel wizard, CancellationToken ct) {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCloseRequested() => closed.TrySetResult();

        wizard.CloseRequested += OnCloseRequested;
        await using var registration = ct.Register(() => closed.TrySetResult());
        try {
            await closed.Task.ConfigureAwait(true);
        } finally {
            wizard.CloseRequested -= OnCloseRequested;
        }
    }

    /// <summary>Close boundary: settle sign-in, cancel any import, quiesce the lane under the cap, then transfer the channel.</summary>
    internal static async Task<bool> HandoffAfterWizardAsync(
            WizardAuthService? auth, Func<Task> laneQuiescedAsync, TimeSpan cap, OutcomeChannel channel,
            ImportStepViewModel? import = null) {
        await CancelAndAwaitAuthAsync(auth).ConfigureAwait(false);
        if (import is not null) await import.CancelActiveRunAsync().ConfigureAwait(false);
        var quiesced = await AwaitQuiescedAsync(laneQuiescedAsync, cap).ConfigureAwait(false);
        channel.TransferConsumer();

        return quiesced;
    }

    // AuthAttempt.Result never faults (an operation that throws arrives as Failed), so this is a
    // pattern-match, not a try/catch: only a failure has anything left to say at this point.
    internal static async Task<AuthResult?> CancelAndAwaitAuthAsync(WizardAuthService? auth) {
        if (auth?.Current is not { } attempt) return null;

        attempt.Cancel();
        var result = await attempt.Result.ConfigureAwait(false);

        switch (result) {
            // Sign-in succeeded and the workspace is on its way; closing the window mid-poll is not a
            // failure, and stderr saying so would be untrue. The guidance is spelled out here rather
            // than carried on the message: in the wizard it arrives through the progress sink, and
            // repeating it there would say the same thing twice — but by now that view is gone, so
            // this line is the only thing left telling the user how to resume.
            case AuthResult.Failed { Reason: AuthFailureReason.ProvisioningInProgress } pending:
                Console.Error.WriteLine(
                    $"kcap: {pending.Message} Join it from the Connect step once it is ready.");
                break;
            case AuthResult.Failed failed:
                Console.Error.WriteLine($"kcap: onboarding sign-in ended with a failure: {failed.Message}");
                break;
            case AuthResult.Committed or AuthResult.Cancelled or AuthResult.Retarget:
                // Committed is already durable and the fresh resolution below reads it; the other
                // two wrote nothing. Nothing to surface either way.
                break;
        }

        return result;
    }

    // Split out of the catch so a test can drive "dispose-then-show-error" against a real
    // DaemonClientService (constructed with fakes, disposal observable) and the same fake
    // IClassicDesktopStyleApplicationLifetime AppStartupTests already uses for ShowStartupError.
    // Ordering matters: dispose WHILE WE STILL CAN. `service` may already be live (Start()
    // called, socket/IPC pump running) if the failure happened later in StartAsync (e.g.
    // BuildAndShowMainWindow throwing) — and the error window's own close handler force-shuts-
    // down via desktop.Shutdown(1), which bypasses OnShutdownRequested/DisposeAndShutdownAsync
    // entirely, so nothing else would ever run this cleanup.
    internal static async Task HandleStartupFailureAsync(
            IClassicDesktopStyleApplicationLifetime desktop, Exception ex, DaemonClientService? service,
            CancellationTokenSource shutdown, IReadOnlyList<IDisposable?> uiDisposables,
            DaemonLifecycleController? lifecycle = null, DaemonMutationLane? lane = null) {
        // The dependent goes first (it subscribes to service's streams) — same ordering rule as
        // the normal shutdown path below. Its own DisposeAsync cancels its independent lifetime
        // token and waits out any mutation it started; that wait is unbounded here on purpose — a
        // startup failure has no live UI to defer against, so there is nothing to keep responsive.
        if (lifecycle is not null) {
            try {
                await lifecycle.DisposeAsync();
            } catch (Exception disposeEx) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during startup-failure cleanup: {disposeEx}");
            }
        }
        if (service is not null) {
            shutdown.Cancel();
            try {
                await service.DisposeAsync();
            } catch (Exception disposeEx) {
                // The ORIGINAL startup exception (ex, already captured and about to be shown
                // below) must never be masked by a secondary dispose failure — append it to the
                // same Console.Error channel instead of letting it propagate.
                Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during startup-failure cleanup: {disposeEx}");
            }
        }
        // The lane goes LAST: both lifecycle and service can still be calling its RunAsync until their own disposal above completes.
        if (lane is not null) {
            try {
                await lane.DisposeAsync();
            } catch (Exception disposeEx) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon mutation lane during startup-failure cleanup: {disposeEx}");
            }
        }
        // Same rule, same reason, for whatever the success path had already built when it threw
        // (tray icon first): the error window's close handler force-shuts-down, so this is their
        // only cleanup too. Entries are null when that step was never reached.
        DisposeAll(uiDisposables, "startup-failure cleanup");
        ShowStartupError(desktop, ex);
    }

    // Split out of the catch so a test can drive it against a fake
    // IClassicDesktopStyleApplicationLifetime (no real windowing/desktop lifetime needed) and
    // assert the ShutdownMode pin, the MainWindow assignment, and the deferred Shutdown(1) all
    // happen in the right order.
    internal static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex) {
        // Redundant since OnFrameworkInitializationCompleted pins the same mode for the whole
        // app (spec §9) — kept because it is what makes THIS path's exit code correct on its own
        // terms, and because the reasoning below is the record of the P1 bug it fixed. It was
        // decompiler-verified against the mode this path used to run under, OnLastWindowClose
        // (the framework default, which the app then set nowhere): Window.HandleClosed raises
        // the CLR Closed event (our handler below, which calls Shutdown(1)) BEFORE the routed
        // WindowClosedEvent that OnLastWindowClose listens for. So closing the error window used
        // to run: our Shutdown(1) (sets _exitCode=1) -> THEN the routed event -> _windows hits 0
        // -> an OnLastWindowClose-driven TryShutdown() with its default exit code 0 ->
        // App.OnShutdownRequested's deferred dance -> a second TryShutdown() whose DoShutdown
        // unconditionally overwrites _exitCode with 0. Net effect: the most common startup
        // failure exited 0. Pinning OnExplicitShutdown disarms that whole OnLastWindowClose
        // branch, so our explicit Shutdown(1) below is the only shutdown and nothing overwrites
        // its exit code.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Showing a window here is legal before Avalonia's main loop starts — it's exactly what
        // StartWithClassicDesktopLifetime's own ShowMainWindow() does right after Start. Calling
        // desktop.Shutdown(1) directly, as this catch used to, is what previously threw when
        // startup faulted synchronously (before the main loop began) — so this shape resolves
        // that pre-main-loop edge case rather than worsening it.
        var errorWindow = BuildStartupErrorWindow(ex);
        if (desktop.MainWindow is null) desktop.MainWindow = errorWindow;
        errorWindow.Closed += (_, _) => desktop.Shutdown(1);
        errorWindow.Show();
    }

    // Last-resort UI for a startup failure: Console.Error above is invisible on a normal GUI
    // launch (OutputType=WinExe has no console), so this window is the only channel that
    // actually reaches the user. SelectableTextBlock (not TextBlock) keeps the stack trace
    // copyable for a bug report.
    internal static Window BuildStartupErrorWindow(Exception ex) =>
        new() {
            Title = "Kurrent Capacitor — startup failed",
            Icon = ProductIcon.WindowIcon,
            Width = 640,
            Height = 400,
            Content = new ScrollViewer {
                Content = new SelectableTextBlock {
                    Text = $"The app failed to start. Details:\n{ex}",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

    // The MainWindowCoordinator's window factory, split out of StartAsync so a test can drive
    // "build VM+window, assign, and Show()" against a fake service without needing a real
    // daemon/profile (the profile resolution behind the graph does real config I/O). This is also
    // the actual bug fix: Avalonia's StartWithClassicDesktopLifetime calls
    // ShowMainWindow() exactly ONCE, synchronously, right after Start — and at that moment
    // desktop.MainWindow is still null, because startup genuinely awaits (the config.json read
    // behind the gate, and in wizard-first mode the whole wizard). By the time this
    // continuation resumes and assigns desktop.MainWindow, nothing else
    // will ever call .Show() for us, so this method must call it explicitly. Show() on an
    // already-visible window is a no-op, so this stays correct even if a future edit changes the
    // timing such that ShowMainWindow() DOES still see a non-null MainWindow.
    internal static MainWindow BuildAndShowMainWindow(
            IDaemonClientService service, ConfigRoot config, TokenStore tokenStore,
            AgentActionService actions, IAppNotifier notifier, ITicker ticker,
            CancellationToken shutdownToken, ActivityViewModel activity, Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null, ILaunchClient? launch = null,
            NavigationGate? navigation = null, Action<Func<Task>>? trackWorkspaceTeardown = null,
            Func<string, WorkspaceViewModel>? workspaceFactory = null, string? tenantName = null,
            IObservable<IReadOnlySet<string>>? agentsWithPending = null, Action? requestSignIn = null,
            IObservable<string?>? lifecycleAttention = null) {
        // Notifier is set on the WINDOW (spec §11 toast overlay), not the ViewModel — the toast
        // is a View-level concern (WindowNotificationManager lives on MainWindow) independent of
        // the VM's WhenActivated-scoped projections.
        //
        // Home is built here, over the SAME `service` instance MainWindowViewModel
        // itself uses — never a second daemon connection. AppStateStore/ServerLaunchClient are both
        // cheap, self-contained constructions (file-path-gated I/O; a HubConnection that only opens
        // lazily on first StartAsync), the same reasoning BuildLifecycleController's own
        // `new AppStateStore(config.Path("app-state.json"))` already relies on. The
        // composition root passes its held client so teardown can dispose it; a caller that passes
        // none (a test) gets an unheld one, which owns nothing until a launch is actually made.
        //
        // Home's three navigation callbacks close over `vm`, which cannot exist yet (it takes Home
        // itself) — a captured local, assigned right below, is what ties the knot without a
        // settable hook on either ViewModel. Every callback runs on the UI thread, after both
        // objects exist.
        MainWindowViewModel? vm = null;
        var home = new HomeViewModel(
            service, new AppStateStore(config.Path("app-state.json")),
            launch ?? new ServerLaunchClient(null, tokenStore), new RepoPathStore(config).GetSortedPathsAsync, shutdownToken,
            openSession: agentId => vm?.OpenSession(agentId),
            navigationGeneration: () => vm?.NavigationGeneration ?? 0,
            openSessionIfCurrent: (agentId, generation) => vm?.OpenSessionIfCurrent(agentId, generation),
            requestSignIn: requestSignIn);
        // Same knot as home above, over the SAME `service` instance — its own openSession
        // callback closes over `vm`, not a local, so no two-step forward-declaration is needed.
        var rail = new SessionRailViewModel(
            service, openSession: agentId => vm?.OpenSession(agentId), agentsWithPending: agentsWithPending);
        vm = new MainWindowViewModel(
            service, shutdownToken, activity, startAction, lifecycleStatus, home: home,
            navigation: navigation, trackWorkspaceTeardown: trackWorkspaceTeardown, workspaceFactory: workspaceFactory,
            rail: rail, tenantName: tenantName, lifecycleAttention: lifecycleAttention);
        var window = new MainWindow {
            DataContext = vm,
            Notifier = notifier,
        };
        window.Show();
        return window;
    }

    // Wires the CLI facade, PATH probe, and decline-memory store (a broken override is "no CLI"); also builds the shim/consent-flip coordinators, sharing rather than re-resolving them.
    (DaemonLifecycleController Lifecycle, ShimOfferCoordinator ShimOffer, ConsentFlipCoordinator ConsentFlip,
            ILifecycleSurface Surface, ILoginShellProbe Probe) BuildLifecycleController(
            DaemonClientService service, ILocalControlOps ops, bool autoActionsPermanentlyClosed,
            Action<string> setLifecycleStatus, Action<string> setLifecycleAttention,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            ResolvedProfile? profile) {
        var cliPath = CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists);
        var runner  = new ProcessRunner();
        var probe   = new LoginShellProbe(runner, Environment.GetEnvironmentVariable);
        var canonicalServer = ServerIdentity.Canonicalize(profile?.ServerUrl);
        // Shared with the probe above (not re-resolved) — decision 7's PATH overlay on `install`
        // must reflect the SAME probe outcome that the controller's preconditions/PathDegraded see.
        var cli     = new KcapCli(runner, cliPath, service.DaemonName, profile?.ProfileName ?? "default", probe.TerminalPathAsync,
            canonicalServer: canonicalServer);
        var store   = new AppStateStore(_config.Path("app-state.json"));
        var surface = new LifecycleSurface(setLifecycleStatus, setLifecycleAttention, ConfirmLifecyclePromptAsync);

        var lifecycle = new DaemonLifecycleController(
            service, cli, probe, store, surface, () => Task.FromResult(ValidProfileName(profile)), TimeProvider.System,
            canonicalServer, runMutation, autoActionsPermanentlyClosed);

        // The shim links to the RESOLVED ABSOLUTE path only — CliResolver's bare "kcap" fallback
        // (no override set, or the not-yet-landed bundle-relative arm) means there is
        // nothing to link, so the offer and the menu item both stay off for the whole run.
        var shimTarget = cliPath is not null && Path.IsPathRooted(cliPath) ? cliPath : null;
        // autoOfferSuppressed: Start() always runs — Offerable/manual install must keep working in Incomplete mode; only the once-ever auto-offer dialog is skipped.
        var shimOffer = new ShimOfferCoordinator(
            lifecycle.PhaseClosed, probe, new PathShimInstaller(runner, probe), store, surface, shimTarget,
            _shutdown.Token, autoActionsPermanentlyClosed);

        // The delegate below and the claims store must share one root: TryConsume takes the config
        // lock this delegate then reads under.
        var consentFlip = new ConsentFlipCoordinator(
            service, ops, new ConsentFlipClaims(_config),
            () => ResolveConsentFlipIdentity(_config), surface, store, _shutdown.Token);

        return (lifecycle, shimOffer, consentFlip, surface, probe);
    }

    // Pure read only — TryConsume already holds this same config lock when this delegate runs, so
    // unreadable config fails closed to an identity that matches nothing (the claim stays pending)
    // rather than throwing inside the two-lock section.
    // Deliberately literal ActiveProfile (no KCAP_PROFILE layering) — a divergence there is fail-safe
    // via the daemon's own identity-conditional ack (task-13-report).
    internal static (string Profile, string Server, string DaemonName) ResolveConsentFlipIdentity(ConfigRoot root) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var config)) return ("", "", "");

        var profileName = config.ActiveProfile;
        var profile     = config.Profiles.GetValueOrDefault(profileName);
        var server      = ServerIdentity.Canonicalize(profile?.ServerUrl) ?? profile?.ServerUrl ?? "";
        var daemonName  = DaemonNameResolver.Resolve([], profile?.Daemon?.Name);
        return (profileName, server, daemonName);
    }

    /// <summary>
    /// A FRESH, env-aware identity for the wizard's own daemon-facing calls: the same
    /// <see cref="ProfileResolver"/> precedence <see cref="OnboardingGate.EvaluateAsync"/> uses, never
    /// the resolution the gate was evaluated on, and side-effect-free. Null — never
    /// an empty-string sentinel — when nothing resolves, which is what keeps its factories fail-closed.
    /// </summary>
    internal static (string Profile, string Server, string DaemonName)? ResolveWizardIdentity(ConfigRoot root) {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(root), out var config)) return null;

        var envUrl     = Environment.GetEnvironmentVariable("KCAP_URL");
        var envProfile = Environment.GetEnvironmentVariable("KCAP_PROFILE");
        var resolved = new ProfileResolver(
            config, cliServerUrl: null, envUrl, envProfile,
            repoConfig: null, repoRemoteUrls: [], repoPath: null).Resolve();

        if (resolved.ProfileName is not { Length: > 0 } profileName) return null;

        var canonicalServer = ServerIdentity.Canonicalize(resolved.ServerUrl);
        if (string.IsNullOrEmpty(canonicalServer)) return null;

        var daemonName = DaemonNameResolver.Resolve([], resolved.Profile?.Daemon?.Name);

        return (profileName, canonicalServer, daemonName);
    }

    // Delegates to the ONE shared validator: must agree with OnboardingGate on what counts as a
    // valid server_url (e.g. both reject file://), or a gate-incomplete machine could still pass
    // this precondition into the normal daemon graph.
    internal static string? ValidProfileName(ResolvedProfile? profile) =>
        OnboardingGate.ValidServerUrl(profile?.ServerUrl)
            ? profile!.ProfileName
            : null;

    // Decision 2's carve-out switch: Incomplete is the only gate outcome that closes auto-actions.
    internal static bool AutoActionsPermanentlyClosed(GateResult gate) => gate is GateResult.Incomplete;

    // spec §6a's post-cap rule: a lane that outran the handoff cap still owns a live child, so the
    // graph comes up degraded whatever the gate said — a second automatic mutation would race it.
    internal static bool AutoActionsPermanentlyClosed(GateResult gate, bool laneQuiesced) =>
        AutoActionsPermanentlyClosed(gate) || !laneQuiesced;

    internal const string LaneStillBusyAttention =
        "A daemon operation is still finishing — automatic actions are disabled.";

    // The other half of the post-cap rule: closing auto-actions silently would leave a degraded app
    // that never says so. Exactly one line, and only when the cap actually fired.
    internal static void AnnounceUnquiescedLane(ILifecycleSurface surface, bool laneQuiesced) {
        if (!laneQuiesced) surface.Attention(LaneStillBusyAttention);
    }

    // A gate-evaluation exception must never brick startup — degrades to Incomplete (fail-safe: the app still launches, with auto-actions closed) instead of throwing.
    internal static async Task<(GateResult Gate, ProfileContext? Profiles)> EvaluateGateSafelyAsync(
            Func<CancellationToken, Task<(GateResult Result, ProfileContext Profiles)>> evaluate, CancellationToken ct) {
        try {
            return await evaluate(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // shutdown mid-evaluation — not a gate failure, let the caller's own catch handle it
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: onboarding gate evaluation failed unexpectedly — degrading to Incomplete: {ex.Message}");
            // Only a throw from the resolve itself reaches here — the gate keeps its resolution
            // across a failed evaluation — so there genuinely is nothing to hand back.
            return (new GateResult.Incomplete(GateReason.EvaluationFailed), null);
        }
    }

    // Reads KCAP_APP_CLI_PATH directly, not CliResolver.ResolvePath (its no-override "kcap" answer is indistinguishable from a real override): set+exists → absolute pin; set+missing or absent → null.
    static string? ResolveCliOverride() =>
        ResolveCliOverrideCore(Environment.GetEnvironmentVariable("KCAP_APP_CLI_PATH"), File.Exists, Path.GetFullPath);

    // Split out so a test can drive it without touching the real environment.
    internal static string? ResolveCliOverrideCore(string? overrideEnv, Func<string, bool> fileExists, Func<string, string> getFullPath) {
        if (string.IsNullOrEmpty(overrideEnv)) return null;
        return fileExists(overrideEnv) ? getFullPath(overrideEnv) : null;
    }

    // Drains every non-success outcome: pre-presentation failures/cancellations requeue once, post-presentation ones still ack. Owns the per-run (never persisted) Takeover decline memory.
    internal static async Task ConsumeMutationOutcomesAsync(
            OutcomeChannel channel, ILifecycleSurface surface,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<CancellationToken, Task<string?>> terminalPathAsync, Func<string?> cliVersion, CancellationToken ct) {
        var declinedTakeoverPairs = new HashSet<(MutationRequest Request, string Token)>();
        try {
            await foreach (var lease in channel.ConsumeAsync(ct)) {
                var presented = false;
                try {
                    await PresentOutcomeAsync(
                            surface, lease.Envelope, runMutation, terminalPathAsync, cliVersion, ct, declinedTakeoverPairs,
                            () => presented = true)
                        .ConfigureAwait(false);
                    lease.Ack(); // ran to completion — whether or not anything needed showing
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    if (presented) lease.Ack(); else lease.CancelLease(); // requeue-once only for a pre-presentation cancellation
                    throw; // let the outer catch end the whole loop, not just this envelope
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap app failed to present a mutation outcome: {ex}");
                    if (presented) lease.Ack(); else lease.CancelLease(); // requeue-once only for a pre-presentation failure
                }
            }
        } catch (OperationCanceledException) {
            // shutdown — draining stops; anything still queued is simply not presented
        }
    }

    // Presents one classified outcome exactly once (success never reaches here). `markPresented` marks the UI boundary reached; `declinedTakeoverPairs` defaults fresh per caller.
    internal static async Task PresentOutcomeAsync(
            ILifecycleSurface surface, OutcomeEnvelope envelope,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<CancellationToken, Task<string?>> terminalPathAsync, Func<string?> cliVersion, CancellationToken ct,
            HashSet<(MutationRequest Request, string Token)>? declinedTakeoverPairs = null, Action? markPresented = null) {
        if (envelope.Outcome is MutationOutcome.UnconfirmedNoAttach) {
            surface.Attention($"The daemon {VerbDisplay(envelope.Request.Verb)} is not yet confirmed — check status.");
            markPresented?.Invoke();
            return;
        }

        var (recoverySurface, token) = ClassifyForPresentation(envelope.Outcome);
        if (recoverySurface == RecoverySurface.None) return; // success cases only — never enqueued anyway

        // Refused/Failed always resolve a non-null token (Failed falls back to the exit-code
        // token) and AttentionSkew/AttentionRepair's own detail is never null either — every
        // branch below that reaches this point has a real token to name.
        var named = token!;
        switch (recoverySurface) {
            case RecoverySurface.Takeover: {
                var declined = declinedTakeoverPairs ?? [];
                var pairKey = (envelope.Request, named);
                if (declined.Contains(pairKey)) {
                    // A pair already declined this run downgrades to a one-line attention presentation — still exactly-once, just never a re-dialog for the SAME pair.
                    surface.Attention($"kcap needs to replace the daemon service to continue ({named}).");
                    markPresented?.Invoke();
                    break;
                }

                var pathDegraded = await terminalPathAsync(ct).ConfigureAwait(false) is null;
                var prompt = new LifecyclePrompt(
                    LifecyclePrompt.KindTakeover, null, cliVersion(), pathDegraded, DaemonLifecycleController.TakeoverDisclosure);
                var accepted = await surface.TryConfirmAsync(prompt, ct).ConfigureAwait(false);
                if (accepted is null) { ct.ThrowIfCancellationRequested(); throw new OperationCanceledException(ct); } // cancelled before the dialog ever ran — never presented
                markPresented?.Invoke(); // the dialog was shown and answered — everything after this is post-presentation
                if (accepted.Value) {
                    // No app-side evidence revalidation on accept: a stale Accept only fails coded (28/29, under the CLI's transaction lock) and re-arrives as a fresh outcome.
                    _ = await runMutation(envelope.Request with { Verb = MutationVerb.Replace }, ct).ConfigureAwait(false);
                } else {
                    declined.Add(pairKey); // Accept never records here — an accepting user wants the retry loop
                    surface.Status($"kcap needs to replace the daemon service to continue ({named}) — declined.");
                }
                break;
            }
            case RecoverySurface.Reinstall:
                // Token stays in the log for support; the banner names the action, not the wire code.
                Console.Error.WriteLine($"kcap: daemon package inconsistent ({named}) — reinstall needed");
                surface.Attention("This kcap install looks broken. Reinstall kcap, then try again.");
                markPresented?.Invoke();
                break;
            case RecoverySurface.Attention:
            case RecoverySurface.Storage: {
                if (AttentionCopyFor(named) is { } copy) {
                    surface.Attention(copy);
                } else {
                    // Opaque tokens are for the log — a bare "needs attention (token)" banner helps nobody.
                    Console.Error.WriteLine($"kcap: daemon mutation needs attention ({named}) — not shown in the UI");
                }
                markPresented?.Invoke();
                break;
            }
        }
    }

    // A small display map instead of MutationVerb.ToString() for user-facing copy.
    static string VerbDisplay(MutationVerb verb) => verb switch {
        MutationVerb.Install       => "install",
        MutationVerb.Replace       => "replace",
        MutationVerb.StartVerified => "verified start",
        MutationVerb.DetachedStart => "daemon start",
        _                          => verb.ToString(),
    };

    /// User-facing line for Attention/Storage outcomes. Null means log-only — never surface a
    /// machine token the operator cannot act on.
    internal static string? AttentionCopyFor(string token) => token switch {
        "cli_not_found"            => "kcap CLI not found. Can't manage the daemon from this app.",
        // App↔CLI floor — never "for the daemon"; this gate runs before any daemon contact.
        "cli_below_floor"          => "This kcap is too old for this app. Update kcap, then press Start daemon.",
        "no_server_configured"     => "No server is configured. Sign in or run setup first.",
        "consent_seed_unwritable"  => "Couldn't write consent data. Check permissions on the kcap config directory.",
        "internal_error"           => "Couldn't finish daemon setup. Press Start daemon to try again.",

        "verify_viability" or "verify_hello_validation" or "verify_start_gate" or "verify_start_gate_drift"
            or "daemon_start_gate"
            => "The daemon didn't come up cleanly. Press Start daemon to try again.",

        "verify_readiness_timeout" or "verify_contended" or "verify_rollback_budget"
            or "verify_restore_verification" or "verify_stop_unconfirmed" or "verify_bootout_unknown"
            => "Daemon setup didn't finish in time. Press Start daemon to try again.",

        "stale_txn_marker"
            => "A previous daemon setup didn't finish. Press Start daemon to try again.",

        "running_without_daemon_pid"
            => "The daemon service is loaded but no process is attached. Press Start daemon to repair.",

        "daemon_running_outside_service"
            => "A daemon is running outside the service. Press Start daemon to repair.",

        "ownership_mismatch" or "ownership_unknown" or "instance_pid_mismatch"
            or "instance_changed_during_classification" or "unreachable_with_recorded_owner"
            => "Another daemon may already be running for this name. Stop it, then press Start daemon.",

        "server_or_name_mismatch" or "identity_inconsistent"
            => "This daemon doesn't match the configured server or name. Check setup, then press Start daemon.",

        _ => null,
    };

    // spec §10 invariant: only these AttentionSkew tokens route to Takeover; every other AttentionSkew/AttentionRepair stays Attention.
    static readonly HashSet<string> TakeoverRoutedSkewTokens = ["missing_capability_consent_3", "daemon_below_floor", "pre_slice_evidence"];

    internal static (RecoverySurface Surface, string? Token) ClassifyForPresentation(MutationOutcome outcome) => outcome switch {
        MutationOutcome.Refused(var reason, var surface)              => (surface, reason),
        MutationOutcome.Failed(var exitCode, var reason, var surface) => (surface, reason ?? VerifyExitCodes.Token(exitCode)),
        MutationOutcome.AttentionSkew(var detail) =>
            (TakeoverRoutedSkewTokens.Contains(detail) ? RecoverySurface.Takeover : RecoverySurface.Attention, detail),
        MutationOutcome.AttentionRepair(var detail) => (RecoverySurface.Attention, detail),
        _ => (RecoverySurface.None, null),
    };

    Task<bool> ConfirmLifecyclePromptAsync(LifecyclePrompt prompt, CancellationToken ct) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowLifecyclePromptDialogAsync(DialogOwner(), prompt, ct));

    // The wizard owns dialogs while wizard-first mode is up — no main window exists yet.
    Window? DialogOwner() =>
        _wizardWindow is { IsVisible: true } wizard ? wizard
        : _coordinator?.Window is { IsVisible: true } main ? main
        : null;

    internal static Task<bool> ShowLifecyclePromptDialogAsync(Window? owner, LifecyclePrompt prompt, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new LifecyclePromptWindow { DataContext = new LifecyclePromptViewModel(prompt, tcs) };
        // Closing via the titlebar/Esc without Accept/Decline also resolves false —
        // BuildConfirmForceStopWindow's same rule below. TrySetResult is idempotent, so this is a
        // no-op once AcceptCommand/DeclineCommand already resolved it.
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        WireDialogCancellation(dialog, tcs, ct);

        if (owner is not null) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // ConfirmAndTakeoverAsync holds the operation gate across the whole ConfirmAsync await — a
    // dialog left open through a lifetime-cancel (app shutdown or DisposeAsync) must not leave the
    // gate (and therefore QuiescedAsync) blocked on a human who may never come back. Cancellation
    // can arrive on any thread, so the close is posted rather than called inline; the registration
    // is disposed once the dialog resolves on its own so it doesn't outlive the window. Extracted
    // (internal, static) so a test can drive it directly against a real headless window.
    internal static void WireDialogCancellation(Window dialog, TaskCompletionSource<bool> tcs, CancellationToken ct) {
        var registration = ct.Register(() =>
            Dispatcher.UIThread.Post(() => { if (!tcs.Task.IsCompleted) dialog.Close(); }));
        tcs.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static string ActivityStatKey(DaemonStore store, string daemonName) {
        var path = store.ConsentLogPath(daemonName);
        return ActivityStatKey(path + ".1", path);
    }

    // Combines both log files' (LastWriteTimeUtc, Length) into one comparison key for
    // ActivityViewModel's stat poll (spec §7). Each file gets its OWN try/catch: `.1` is absent
    // on every fresh install until the first 1MB rotation, and a single shared catch around both
    // files would collapse the WHOLE joined key to the "absent" constant whenever `.1` throws —
    // appends to the live file would then never change the key, and the Activity tab would go
    // stale until the tab is reselected. FileInfo.Length throws FileNotFoundException on a
    // missing file (unlike File.GetLastWriteTimeUtc, which returns a sentinel instead) — that
    // throw is what carries a clean per-file absence into that file's own "absent" branch. Takes
    // both paths directly (rather than a daemon name) so a test can point it at a temp directory
    // without redirecting any real daemon-dir resolution.
    internal static string ActivityStatKey(string p1Path, string livePath) => $"{StatOf(p1Path)}|{StatOf(livePath)}";

    static string StatOf(string path) {
        try {
            return $"{File.GetLastWriteTimeUtc(path).Ticks}:{new FileInfo(path).Length}";
        } catch {
            return "absent";
        }
    }

    // Composed here (not inside AgentActionService, spec decision 5): the service only awaits the
    // seam; every UI concern — the dialog itself, choosing an owner, marshaling onto the UI
    // thread — lives at this composition root, same as ShellUrlOpener/LocalControlOps above.
    Task<bool> ConfirmForceStopAsync(string label) =>
        Dispatcher.UIThread.InvokeAsync(() => ShowConfirmForceStopDialogAsync(label));

    // Runs ON the UI thread (guaranteed by the InvokeAsync call above — never call this directly
    // from a background thread). Owner = the main window only while it's actually VISIBLE
    // (IsVisible, decompile-verified: Window.Show()/Hide() toggle exactly this) — a hide-to-tray
    // stop must still surface the prompt, so it shows standalone and pulls itself forward instead
    // of silently attaching to a window nobody can see.
    Task<bool> ShowConfirmForceStopDialogAsync(string label) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = BuildConfirmForceStopWindow(label, tcs);

        if (DialogOwner() is { } owner) {
            dialog.Show(owner);
        } else {
            dialog.Show();
            dialog.Activate();
        }

        return tcs.Task;
    }

    // Plain code-built Window (same style as BuildStartupErrorWindow above) rather than a XAML
    // view — this dialog has no ViewModel, no data binding, and exists only to resolve `tcs`.
    // "Stop anyway" is IsDefault (Enter-triggered, styled as the destructive default per spec);
    // "Cancel" is IsCancel (Esc-triggered). Closing via the titlebar/Esc without clicking either
    // button also resolves false — TrySetResult is idempotent, so whichever path runs first wins
    // and the other is a no-op.
    internal static Window BuildConfirmForceStopWindow(string label, TaskCompletionSource<bool> tcs) {
        var cancelButton = new Button { Content = "Cancel", IsCancel = true };
        var stopButton = new Button {
            Content = "Stop anyway",
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#D32F2F")),
            Foreground = Brushes.White,
        };

        var window = new Window {
            Title = "Stop review participant?",
            Icon = ProductIcon.WindowIcon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel {
                Margin = new Thickness(20),
                Spacing = 16,
                Children = {
                    new TextBlock {
                        Text = $"{label} is a review participant. Stopping it will strand its flow. Stop anyway?",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancelButton, stopButton },
                    },
                },
            },
        };

        stopButton.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        cancelButton.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
        window.Closed += (_, _) => tcs.TrySetResult(false);

        return window;
    }

    // Async-safe shutdown: ShutdownRequested fires on the UI thread and can be cancelled, so the
    // FIRST pass defers it (e.Cancel = true), cancels the shutdown token (abandoning any
    // in-flight StartDaemonAsync WAIT — never the spawned daemon), and disposes the service in
    // the background (no live socket read/child-process wait may survive app exit, spec §5).
    // Once that completes, TryShutdown() re-raises this same event; the SECOND pass is let
    // through. This never blocks the UI thread on the async disposal.
    void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) {
        // Navigation's twin of BeginShutdownPass rule 1, and for the same reason: applied on EVERY
        // pass, before the confirmed-pass early return, so a window the coordinator builds BETWEEN
        // the passes can neither open a workspace nor keep one attached.
        LatchNavigation();
        if (!BeginShutdownPass(_coordinator, _shutdownConfirmed)) return;

        e.Cancel = true;
        _shutdown.Cancel();
        if (_shutdownStarted) return; // e.g. a rapid double Cmd+Q — disposal is already in flight
        _shutdownStarted = true;
        _ = DisposeAndShutdownAsync();
    }

    // Split out of OnShutdownRequested so a test can drive BOTH passes (the event itself needs a
    // live App and a real lifetime, over a composition that needs a real daemon). Two rules, in
    // this order:
    //
    // 1. QuitInProgress is flagged on EVERY pass — including the confirmed one, which is why this
    //    runs before the guard below. A coordinator that only comes into existence BETWEEN the
    //    passes (quit or an OS logout arriving while startup is still resolving — or still showing
    //    the wizard — with StartAsync's continuation then building the window during the deferred
    //    disposal's await)
    //    would otherwise still have hide-on-close armed when the second pass closes the windows:
    //    the window cancels its own close, DoShutdown aborts with windows still open, and every
    //    later quit early-returns on _shutdownConfirmed — an app that can only be force-quit.
    //    Setting it again on a pass that already set it is a no-op.
    // 2. The confirmed (second) pass is let through untouched — no e.Cancel — which is what the
    //    caller's early return preserves.
    internal static bool BeginShutdownPass(MainWindowCoordinator? coordinator, bool shutdownConfirmed) {
        if (coordinator is not null) coordinator.QuitInProgress = true;
        return !shutdownConfirmed;
    }

    // Synchronous, on the ShutdownRequested (UI) thread: the live workspace is unhooked and its
    // teardown REGISTERED here, so the drain below can only ever seal a set that already contains
    // it. The gate is latched even with no window ever built — a window built later still sees it.
    void LatchNavigation() {
        (_coordinator?.Window?.DataContext as MainWindowViewModel)?.LatchShutdown();
        _navigation.Latch();
    }

    async Task DisposeAndShutdownAsync() {
        // FIRST, before quiesce (which can wait a full minute) and before any disposal: a live
        // workspace holds a terminal attach on the daemon socket, and the clamp it implies must be
        // released for every other viewer as early as possible. Bounded at 5s and never throws.
        //
        // Deliberately NOT ConfigureAwait(false), unlike its neighbours: this is the one await here
        // that routinely suspends (a live workspace's teardown), and its continuation belongs back on
        // the UI thread this was invoked from. That is all it buys — the quiesce below still resumes
        // wherever ConfigureAwait(false) leaves it whenever IT suspends.
        await _workspaceTeardown.DrainAsync();

        // Still on the UI thread (required by Close), and before the quiesce below: the dialog's
        // own close path cancels a live re-auth attempt — or lets a commit already past the
        // boundary finish — and the await stops the process exiting under it. Closed assigns
        // _reauthSettle synchronously, so reading it after Close observes this close's task.
        if (_signInWindow is { } reauthDialog) reauthDialog.Close();
        if (_reauthSettle is { } reauthSettle) await reauthSettle.ConfigureAwait(false);

        // spec §3.6 + decision 2: an in-flight sign-in always settles, mutations get a bounded chance
        // to — both while the UI is still up, before teardown.
        if (_wizardAuth is not null || _wizardImport is not null || _lifecycle is not null || _lane is not null)
            await QuiesceAppAsync(_wizardAuth, _wizardImport, _lifecycle, _lane, QuiesceShutdownCap).ConfigureAwait(false);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            // Prompt coordinator BEFORE the consent service (spec §5): the window and its
            // ViewModel are gone before the service they resolve against, so no click can reach a
            // disposed one. A resolve already in flight was cancelled by _shutdown at the top of
            // OnShutdownRequested and settles on the ViewModel's silent-abort path.
            await DisposeUiThenConfirmShutdownAsync(
                [_tray, _trayVm, _promptCoordinator, _consent, _permissions, _activity, _home, _rail, _pause],
                DisposeLifecycleAndServiceAsync, () => _shutdownConfirmed = true, desktop, _exitCode);
        } else {
            await DisposeLifecycleAndServiceAsync();
            _shutdownConfirmed = true;
        }
    }

    // Runs after the UI disposables (DisposeUiThenConfirmShutdownAsync), so _home is already gone
    // when its server clients are torn down here. _lifecycle then goes first (guarded, so a throw
    // never skips _service's disposal); the lane goes LAST — its substrate must outlive any caller
    // still awaiting RunAsync.
    async ValueTask DisposeLifecycleAndServiceAsync() {
        await DisposeServerClientsAsync().ConfigureAwait(false);
        if (_lifecycle is not null) {
            try {
                await _lifecycle.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon lifecycle controller during shutdown: {ex}");
            }
        }
        if (_service is not null) await _service.DisposeAsync().ConfigureAwait(false);
        if (_lane is not null) {
            try {
                await _lane.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose the daemon mutation lane during shutdown: {ex}");
            }
        }
    }

    // Reached from both teardown paths; the holder memoizes the cleanup, so a second call awaits
    // the first rather than disposing anything again.
    async ValueTask DisposeServerClientsAsync() {
        if (_serverClients is null) return;
        await _serverClients.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Quiesces shutdown in two phases: sign-in and import finish uncapped so an in-progress commit isn't torn down, then lifecycle/lane quiesce under the cap.</summary>
    internal static async Task QuiesceAppAsync(
            WizardAuthService? auth, ImportStepViewModel? import,
            DaemonLifecycleController? lifecycle, DaemonMutationLane? lane, TimeSpan cap) {
        var authTerminal = CancelAndAwaitAuthAsync(auth);
        if (import is not null) await import.CancelActiveRunAsync().ConfigureAwait(false);
        await authTerminal.ConfigureAwait(false);
        await AwaitQuiescedAsync(() => QuiesceLifecycleAndLaneAsync(lifecycle, lane), cap).ConfigureAwait(false);
    }

    // Composes the controller's QuiescedAsync with the lane's (covers main-window Start/Retry too); CancellationToken.None — the bound is AwaitQuiescedAsync's own race against Task.Delay(cap).
    internal static async Task QuiesceLifecycleAndLaneAsync(DaemonLifecycleController? lifecycle, DaemonMutationLane? lane) {
        var waits = new List<Task>(2);
        if (lifecycle is not null) waits.Add(lifecycle.QuiescedAsync());
        if (lane is not null) waits.Add(lane.QuiescedAsync(CancellationToken.None));
        if (waits.Count > 0) await Task.WhenAll(waits).ConfigureAwait(false);
    }

    // §3.6's cap: QuiescedAsync itself is unbounded (it just waits for the gate), so this is what
    // keeps a stuck internal mutation from hanging shutdown forever — DisposeAsync's own eventual
    // lifetime-cancel is still the backstop if the cap is reached. Static + delegate-shaped so a
    // test can drive it without a live controller. Returns which arm won: false = the cap fired
    // with work still live, which the wizard handoff turns into a degraded (auto-actions closed)
    // graph. A dead heat reads as false — never claim quiescence that wasn't observed.
    internal static async Task<bool> AwaitQuiescedAsync(Func<Task> quiescedAsync, TimeSpan cap) {
        var quiesced = quiescedAsync();

        return await Task.WhenAny(quiesced, Task.Delay(cap)).ConfigureAwait(false) == quiesced;
    }

    // Split out of DisposeAndShutdownAsync so a test can pin the ordering with a recording list.
    // The UI-thread-owned disposables go first, synchronously on the UI thread this runs on (the
    // ShutdownRequested thread), so the menu-bar icon is gone before TryShutdown (spec §9) — then
    // the deferred pass below proceeds exactly as it did before the tray existed.
    internal static Task DisposeUiThenConfirmShutdownAsync(
            IReadOnlyList<IDisposable?> uiDisposables, Func<ValueTask>? disposeAsync, Action markConfirmed,
            IClassicDesktopStyleApplicationLifetime desktop, int exitCode) {
        DisposeAll(uiDisposables, "shutdown");
        return DisposeAndConfirmShutdownAsync(disposeAsync, markConfirmed, desktop, exitCode);
    }

    // Per-entry guard for the same reason DisposeAndConfirmShutdownAsync wraps its disposeAsync: a
    // throw here must never skip the remaining disposables, markConfirmed or TryShutdown —
    // _shutdownConfirmed would stay false while _shutdownStarted stayed true, cancelling every
    // later quit forever. Null entries are the "that step never ran" case.
    static void DisposeAll(IReadOnlyList<IDisposable?> disposables, string phase) {
        foreach (var disposable in disposables) {
            try {
                disposable?.Dispose();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to dispose a UI service during {phase}: {ex}");
            }
        }
    }

    // Split out of DisposeAndShutdownAsync so a test can drive the full deferred-shutdown pass —
    // dispose, THEN mark confirmed, THEN shut down carrying an exit code — against a fake
    // IClassicDesktopStyleApplicationLifetime, without needing a live App instance.
    // `disposeAsync` is a delegate (not the concrete DaemonClientService) so a test can inject a
    // throwing disposal without depending on how DaemonClientService itself might fail.
    // Regression coverage for a P2 bug found in re-review: TryShutdown() used to be called with
    // no exit code (defaulting to 0), so Cmd+Q/platform shutdown while the startup-error window
    // was still showing silently overwrote the startup-failure exit code with success. Ordering
    // is preserved exactly from the original inline body: `markConfirmed` MUST run before
    // `TryShutdown`, because TryShutdown can re-raise ShutdownRequested synchronously and
    // OnShutdownRequested's early-return guard (`if (_shutdownConfirmed) return;`) depends on
    // that happening first.
    internal static async Task DisposeAndConfirmShutdownAsync(
            Func<ValueTask>? disposeAsync, Action markConfirmed, IClassicDesktopStyleApplicationLifetime desktop,
            int exitCode) {
        // A throwing disposeAsync must never skip markConfirmed/TryShutdown — otherwise
        // _shutdownConfirmed is never set while _shutdownStarted stays true, and every later
        // quit is cancelled forever.
        try {
            if (disposeAsync is not null) await disposeAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during shutdown: {ex}");
        } finally {
            markConfirmed();
            desktop.TryShutdown(exitCode);
        }
    }
}
