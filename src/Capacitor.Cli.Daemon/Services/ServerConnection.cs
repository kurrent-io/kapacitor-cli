using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class ServerConnection : IAsyncDisposable, IDaemonHeartbeatPort {
    readonly HubConnection             _hub;
    readonly DaemonConfig              _config;
    readonly TokenStore                _tokens;
    readonly ILogger<ServerConnection> _logger;
    readonly RegistrationGate          _gate                = new();
    readonly PendingPermissionRegistry _pendingPermissions  = new();
    readonly PendingAcpInteractionRegistry _pendingAcpInteractions = new();

    // The change-generation counter behind the DaemonStatus push. Optional so a subclass can
    // construct without naming one, and so DI still resolves the registered singleton when
    // one exists.
    readonly DaemonStatusNotifier _statusNotifier;

    /// <summary>Test seam: exposes which notifier this connection actually pulses into, so a DI
    /// wiring test can pin that the registered singleton — not a private fallback nobody
    /// subscribes to — is the one every hub-state pulse reaches (see DaemonStatusWiringTests).</summary>
    internal DaemonStatusNotifier StatusNotifierForTest => _statusNotifier;

    /// <summary>
    /// Every currently-active ACP session↔agent binding this daemon owns, keyed by agentId.
    /// Populated by <see cref="RegisterAcpBinding"/> (right after the initial
    /// <see cref="AcpSessionStartedAsync"/> bind succeeds) and drained by
    /// <see cref="UnregisterAcpBinding"/> (on agent end).
    /// <see cref="ReBindAcpSessionsAsync"/> replays every entry here idempotently on each reconnect —
    /// see that method's remarks for why it does NOT go through the gated <see cref="AcpSessionStartedAsync"/>.
    /// </summary>
    readonly ConcurrentDictionary<string, AcpBindInfo> _acpBindings = new();

    static readonly TimeSpan PermissionRetryPollInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan EndSessionRetryPollInterval  = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan AcpRetryPollInterval         = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan ParkRetryPollInterval        = TimeSpan.FromMilliseconds(500);

    /// <summary>§2.7 B6 arm-A: an upper bound on the WHOLE park report (readiness gate + retries).
    /// <c>ParkReviewerAsync</c> holds the reap claim across this call, so — unlike the other
    /// <see cref="ConnectionRetry"/> callers, whose waits are bounded only by daemon shutdown — an
    /// unbounded wait for <see cref="IsReady"/> here would pin an idle reviewer (claimed, slot not freed)
    /// for the entire outage. On elapse the attempt folds to <see cref="ParkAck.Ambiguous"/>, releasing
    /// the claim so a later sweep retries the park (park is best-effort; never worth pinning for).
    /// Settable only so a test need not spend the real 30s proving the bound; production never sets it.</summary>
    internal TimeSpan ParkAckBudget { get; init; } = TimeSpan.FromSeconds(30);

    // Events for incoming commands from server
    public event Func<LaunchAgentCommand, Task>?    OnLaunchAgent;
    public event Func<string, Task>?                OnStopAgent; // agentId
    public event Func<SendInputCommand, Task>?      OnSendInput;
    public event Func<string, string, Task>?        OnSendSpecialKey; // agentId, key
    public event Func<ResizeTerminalCommand, Task>? OnResizeTerminal;

    // Per-phase eval handlers (DEV-1463 PR 2). These use SignalR's
    // client-result invocation — the server calls
    // HubConnection.InvokeAsync<T> which expects exactly one handler
    // returning the typed result, so we use settable properties rather
    // than multicast events. The SignalR 10.0.5 On<T1, TResult> overloads
    // don't expose a CancellationToken to the handler (verified against
    // Microsoft.AspNetCore.SignalR.Client reflection), so per-call
    // cancellation has to be driven by the daemon's own shutdown token —
    // the handler implementations link their work to _shutdownToken.
    public Func<PrepareEvalCommand,  Task<PrepareResult>>?  PrepareEvalHandler  { get; set; }
    public Func<RunQuestionCommand,  Task<QuestionResult>>? RunQuestionHandler  { get; set; }
    public Func<FinalizeEvalCommand, Task<FinalizeResult>>? FinalizeEvalHandler { get; set; }
    public Func<CancelEvalCommand,   Task>?                 CancelEvalHandler   { get; set; }

    /// <summary>Task 8: handler for the server's <c>ResolveReviewerModel</c> client-result
    /// invocation — the side-effect-free reviewer-model preflight. Set by <see cref="AgentOrchestrator"/>
    /// at startup; when null (early startup / an old daemon build), the registration below returns a
    /// fail-closed <c>"unavailable"</c> reply so the server never applies an unresolved override.</summary>
    public Func<ReviewerModelResolveRequestV1, Task<ReviewerModelResolveResponseV1>>? ResolveReviewerModelHandler { get; set; }

    /// <summary>
    /// Handler for the server's "do you have a checkout of this repo?" probe.
    /// Receives <c>(owner, repo, candidatePaths)</c> and returns confirmed git
    /// roots. Set by <see cref="AgentOrchestrator"/> at startup.
    /// </summary>
    public Func<FindRepoForRemoteRequest, Task<string[]>>? FindRepoForRemoteHandler { get; set; }

    /// <summary>
    /// Handler for the server's <c>ProbeBorrowSource</c> client-result invocation (Phase A,
    /// task A3): "can you borrow this path?". Receives a filesystem path and returns the
    /// daemon-computed authorization + canonical paths. Set by <see cref="AgentOrchestrator"/> at
    /// startup; when null, returns <c>BorrowProbeResult(false, null, null, "no handler")</c>.
    /// </summary>
    public Func<string, Task<BorrowProbeResult>>? ProbeBorrowSourceHandler { get; set; }

    /// <summary>
    /// Callback invoked at <see cref="RegisterDaemonAsync"/> time to snapshot the
    /// agent IDs currently hosted by this daemon. The server uses this to
    /// reconcile its registry against the daemon's view. Set by
    /// <see cref="AgentOrchestrator"/> at startup; when null, an empty array
    /// is sent (tests don't need to wire the callback).
    /// </summary>
    public Func<string[]>? GetLiveAgentIds { get; set; }

    /// <summary>Phase B (D2): richer live-agent metadata (kind + flow identity) sent alongside
    /// <see cref="GetLiveAgentIds"/> on <c>DaemonConnect</c>. Optional — null when not wired (tests).</summary>
    public Func<LiveAgentInfo[]>? GetLiveAgents { get; set; }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): the server prunes the daemon's durable
    /// resolved-candidates ledger per-entry (sparse, deliver-once). One-way server→daemon receive; the
    /// handler (<see cref="AgentOrchestrator"/>) is SYNCHRONOUS (the ledger Ack is void).</summary>
    public event Action<AckResolvedCandidates>? OnAckResolvedCandidates;

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2/§5.5): the sequenced-command receive seams.
    /// <see cref="OnStopAgentV2"/> is the Seq'd stop primitive a capability daemon receives instead of the
    /// legacy <c>StopAgent</c>; <see cref="OnAckProcessedPrefix"/> is the server's identity-cache retirement
    /// proof (SYNCHRONOUS — the processor's <c>AckPrefix</c> is void); <see cref="OnRequestStatusReport"/> is
    /// a zero-argument server→daemon nudge answered by an immediate out-of-band <c>DaemonStatusReport</c>.</summary>
    public event Func<StopAgentV2, Task>?    OnStopAgentV2;
    public event Action<AckProcessedPrefix>? OnAckProcessedPrefix;
    public event Func<Task>?                 OnRequestStatusReport;
    /// <summary>The correlated variant: the server's <c>RequestStatusReport2</c> hands over a nonce
    /// the answering report must echo (<c>DaemonStatusReport.EchoNonce</c>) — sent only to a daemon
    /// whose connect advertised <c>SupportsCorrelatedStatusReports</c>.</summary>
    public event Func<string, Task>?         OnRequestStatusReport2;

    /// <summary>What the connect payload claims for RequestStatusReport2 — the live handler itself, so
    /// the claim and the routing cannot drift apart.</summary>
    internal bool AdvertisesCorrelatedStatusReports => OnRequestStatusReport2 is not null;

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.4): snapshot of the un-acked resolved-
    /// candidates ledger, re-advertised on <c>DaemonConnect</c> (mirrors <see cref="GetLiveAgents"/>).
    /// Optional — null when not wired (tests / early startup) ⇒ no candidates advertised.</summary>
    public Func<ResolvedStartupCandidate[]>? GetResolvedStartupCandidates { get; set; }

    /// <summary>Phase B2-b (sequenced-settlement design): the per-platform startup-completeness signals,
    /// re-advertised on <c>DaemonConnect</c> alongside the periodic <c>DaemonStatusReport</c>. Set by
    /// <see cref="AgentOrchestrator"/> at startup; optional — null when not wired (tests / early startup)
    /// ⇒ the additive field defaults (StartupReapComplete/StartupDiscovery null, no blocked candidates).</summary>
    public Func<bool>?                         GetStartupReapComplete         { get; set; }
    public Func<UnresolvedStartupCandidate[]>? GetUnresolvedStartupCandidates { get; set; }
    public Func<StartupDiscovery?>?            GetStartupDiscovery            { get; set; }

    /// <summary>Phase B2-b (sequenced-settlement design §5.5): the resolved-candidates ledger's
    /// daemon-lifetime monotonic high-water, re-advertised on <c>DaemonConnect</c> alongside
    /// <see cref="GetResolvedStartupCandidates"/> so that once sparse acks prune entries the server still
    /// knows the generation frontier. Null when unwired (tests / early startup) ⇒ the additive field
    /// stays null, wire-compatible with old servers.</summary>
    public Func<long?>?                        GetHighestResolutionGeneration { get; set; }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): the sequenced-command watermark counters
    /// (the processor's HighestAcceptedSeq / LastProcessedSeq) and the kill-quarantine snapshot, mirrored
    /// onto the enriched <c>DaemonConnect</c> payload alongside the periodic <c>DaemonStatusReport</c>. Set
    /// by <see cref="AgentOrchestrator"/> at startup; null (tests / early startup) ⇒ the additive fields
    /// stay at their defaults, wire-compatible with old servers.</summary>
    public Func<long?>?                  GetHighestAcceptedSeq { get; set; }
    public Func<long?>?                  GetLastProcessedSeq   { get; set; }
    public Func<QuarantinedAgentInfo[]>? GetQuarantined        { get; set; }

    /// <summary>Phase B2-b (sequenced-settlement design): the daemon's per-boot epoch, advertised on
    /// <c>DaemonConnect</c>. Set by <see cref="AgentOrchestrator"/> to return its own <c>_daemonEpoch</c>
    /// so the connect epoch and the orchestrator/processor epoch read ONE source (no test-divergence
    /// footgun). Null when unwired (tests / early startup) ⇒ falls back to <c>_config.DaemonEpoch</c>,
    /// which <c>DaemonRunner</c> pins before services build, so prod behaviour is unchanged.</summary>
    public Func<string?>?                GetDaemonEpoch        { get; set; }

    /// <summary>Phase B (D2): send the periodic daemon self-report ONE-WAY (never
    /// <c>InvokeAsync</c>) — an old server without the <c>DaemonStatusReport</c> handler produces only
    /// a server-side log line, and any send exception is swallowed so the agent loops are untouched.
    /// Virtual so tests can capture the report without a live hub.</summary>
    public virtual async Task DaemonStatusReportAsync(DaemonStatusReport report) {
        if (!IsReady) return;
        try { await _hub.SendAsync("DaemonStatusReport", report, cancellationToken: _ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "DaemonStatusReport send failed (old server or transient)"); }
    }

    /// <summary>Phase B2-b (sequenced-settlement design §4.2.2): answer a sequenced command ONE-WAY (never
    /// <c>InvokeAsync</c>) — an old server without the <c>CommandAck</c>/<c>CommandRejected</c> handler
    /// produces only a server-side log line, and any send exception is swallowed so the agent loops are
    /// untouched. Virtual so tests can capture the sends without a live hub.</summary>
    public virtual async Task CommandAckAsync(CommandAck ack) {
        if (!IsReady) return;
        try { await _hub.SendAsync("CommandAck", ack, cancellationToken: _ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "CommandAck send failed (old server or transient)"); }
    }

    public virtual async Task CommandRejectedAsync(CommandRejected rej) {
        if (!IsReady) return;
        try { await _hub.SendAsync("CommandRejected", rej, cancellationToken: _ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "CommandRejected send failed (old server or transient)"); }
    }

    public ServerConnection(
            DaemonConfig config, TokenStore tokens, ILoggerFactory loggerFactory,
            ILogger<ServerConnection> logger, DaemonStatusNotifier? statusNotifier = null) {
        _config          = config;
        _tokens          = tokens;
        _logger          = logger;
        _statusNotifier  = statusNotifier ?? new();

        _hub = new HubConnectionBuilder()
            .WithUrl(
                $"{config.ServerUrl.TrimEnd('/')}/hubs/sessions",
                options => {
                    options.AccessTokenProvider = async () => {
                        var resolution = await tokens.GetValidTokensForServerAsync(config.Profiles.Name, config.ServerUrl);

                        return resolution.Tokens?.AccessToken;
                    };
                }
            )
            .WithAutomaticReconnect(new RetryPolicy())
            // Forward SignalR client framework logs (HubConnection, JsonHubProtocol,
            // …) to the daemon's logger factory. Without this, the HubConnectionBuilder
            // resolves a NullLoggerFactory internally and protocol-level errors
            // (e.g. "couldn't bind arguments for invocation 'LaunchAgent'" — exactly
            // what DEV-1665 was) silently disappear, leaving the daemon looking
            // healthy while it drops every invocation.
            .ConfigureLogging(b => b.Services.AddSingleton(loggerFactory))
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, CapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Halve KeepAliveInterval (15s → 7s) so the WebSocket stays warm
        // through cloudflared and the server detects a dead transport sooner.
        // ServerTimeout stays at the 30s default to keep a safe 2× margin
        // against mixed-version rollouts where the server may still be on
        // the default 15s server-side KeepAliveInterval.
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(7);

        _hub.On<LaunchAgentCommand>("LaunchAgent", cmd => SafeInvoke("LaunchAgent", () => OnLaunchAgent?.Invoke(cmd)));
        _hub.On<string>("StopAgent", agentId => SafeInvoke("StopAgent", () => OnStopAgent?.Invoke(agentId)));
        _hub.On<SendInputCommand>("SendInput", cmd => SafeInvoke("SendInput", () => OnSendInput?.Invoke(cmd)));
        _hub.On<string, string>("SendSpecialKey", (agentId, key) => SafeInvoke("SendSpecialKey", () => OnSendSpecialKey?.Invoke(agentId, key)));
        // "ResizeTerminalAggregate", not the legacy "ResizeTerminal": the payload is now the
        // server-aggregated min terminal size across web viewers, with (0,0) meaning "clear web
        // dims". A daemon predating web/local resize aggregation only registered "ResizeTerminal",
        // so a newer server's aggregate (including the (0,0) clear) is silently ignored by it
        // instead of resizing the PTY to 0×0 — graceful degradation during a non-atomic CLI/server
        // rollout.
        _hub.On<ResizeTerminalCommand>("ResizeTerminalAggregate", cmd => SafeInvoke("ResizeTerminalAggregate", () => OnResizeTerminal?.Invoke(cmd)));

        // Phase B2-b (sequenced-settlement design §4.2.4): one-way server→daemon receive that prunes the
        // resolved-candidates ledger per-entry. The handler is SYNCHRONOUS (the ledger Ack is void), so
        // invoke it inline and return a completed task — no work is awaited.
        _hub.On<AckResolvedCandidates>("AckResolvedCandidates", ack => { OnAckResolvedCandidates?.Invoke(ack); return Task.CompletedTask; });

        // Phase B2-b (sequenced-settlement design §4.2.2): the sequenced-command receive seams. StopAgentV2
        // and RequestStatusReport route through SafeInvoke like every other command handler (so a throwing
        // handler is logged, not surfaced to the hub); AckProcessedPrefix is a synchronous void state
        // mutation, so it invokes inline and returns a completed task.
        _hub.On<StopAgentV2>("StopAgentV2", cmd => SafeInvoke("StopAgentV2", () => OnStopAgentV2?.Invoke(cmd)));
        _hub.On<AckProcessedPrefix>("AckProcessedPrefix", ack => { OnAckProcessedPrefix?.Invoke(ack); return Task.CompletedTask; });
        // Offloaded via Task.Run, same as the delivery site's report in AgentOrchestrator.HandleSendInput:
        // OnRequestStatusReport ends in the same gated SendDaemonStatusReportOnceAsync, and awaiting it
        // inline here would park this receive loop behind another emission's whole hub send.
        _hub.On("RequestStatusReport", () => { _ = Task.Run(() => SafeInvoke("RequestStatusReport", () => OnRequestStatusReport?.Invoke())); return Task.CompletedTask; });
        // Same offload shape as RequestStatusReport above, for the same reason.
        _hub.On<StatusReportRequest>("RequestStatusReport2", req => { _ = Task.Run(() => SafeInvoke("RequestStatusReport2", () => OnRequestStatusReport2?.Invoke(req.Nonce))); return Task.CompletedTask; });

        // Client-result invocations for per-phase eval dispatch.
        _hub.On<PrepareEvalCommand, PrepareResult>("PrepareEval",
            cmd => PrepareEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new PrepareResult(false, "no handler", null, 0, 0, 0, 0, 0)));

        _hub.On<RunQuestionCommand, QuestionResult>("RunQuestion",
            cmd => RunQuestionHandler?.Invoke(cmd)
                ?? Task.FromResult(new QuestionResult(false, null, "no handler", 0, 0)));

        _hub.On<FinalizeEvalCommand, FinalizeResult>("FinalizeEval",
            cmd => FinalizeEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new FinalizeResult(false, "no handler", null)));

        _hub.On<CancelEvalCommand>("CancelEval",
            cmd => CancelEvalHandler?.Invoke(cmd) ?? Task.CompletedTask);

        // Task 8: side-effect-free reviewer-model preflight (server→daemon client-result
        // invocation). When the orchestrator hasn't wired the handler (early startup), fail closed with
        // an "unavailable" reply that still echoes RequestId/Vendor so the server's correlation guard
        // passes and it treats the model as simply unavailable. PolicyVersion is always THIS daemon's own
        // RPC protocol version (never an echo of the request's expectation) — matching the invariant the
        // real HandleResolveReviewerModel handler upholds, so a protocol drift is detected the same way
        // whether or not the orchestrator has wired a handler yet.
        _hub.On<ReviewerModelResolveRequestV1, ReviewerModelResolveResponseV1>("ResolveReviewerModel",
            req => ResolveReviewerModelHandler?.Invoke(req)
                ?? Task.FromResult(new ReviewerModelResolveResponseV1(
                    req.RequestId, req.Vendor, ReviewerModelResolvers.RpcProtocolVersion, "unavailable")));

        // Server probe used by the "Review this PR" UI to discover which
        // checkouts on this daemon match the PR's owner/repo. Returns an empty
        // array when the orchestrator hasn't wired a handler yet (e.g. during
        // startup) so the server treats this daemon as having no matches.
        _hub.On<FindRepoForRemoteRequest, string[]>("FindRepoForRemote",
            req => FindRepoForRemoteHandler?.Invoke(req) ?? Task.FromResult(Array.Empty<string>()));

        // Client-result invocation: "can you borrow this path?". Lets the
        // server prove co-location before offering a borrow-cwd launch target. "no handler" is
        // returned when the orchestrator hasn't wired the handler yet (e.g. early startup).
        _hub.On<string, BorrowProbeResult>("ProbeBorrowSource",
            path => ProbeBorrowSourceHandler?.Invoke(path)
                ?? Task.FromResult(new BorrowProbeResult(false, null, null, "no handler")));

        // Server→client push carrying the user's decision for a hosted-agent permission request
        // Paired with the RequestPermission2 invocation in RequestPermissionAsync: that
        // invocation returns a requestId immediately (so it can't occupy the connection's single
        // parallel-invocation slot and starve DaemonPing), and the decision arrives later via
        // this message. Resolve() completes the awaiting RequestPermissionAsync call, or buffers
        // the decision if it raced ahead of the await. Single-record payload (arity 1) so the push
        // contract can evolve without breaking mixed-version daemons.
        _hub.On<PermissionResolution>("PermissionResolved",
            res => _pendingPermissions.Resolve(res.RequestId, res.Decision));

        // Server→client push carrying the user's decision for an ACP permission/elicitation
        // interaction. Mirrors the PermissionResolved registration above — a separate
        // registry (AcpInteractionDecision-typed) rather than reusing _pendingPermissions, since
        // the decision payload shape differs (ACP interactions carry SelectedOptionLabel/
        // SelectedIndex/FreeText that Claude Code's PermissionDecision has no equivalent for).
        _hub.On<AcpInteractionResolution>("AcpInteractionResolved",
            res => _pendingAcpInteractions.Resolve(res.RequestId, res.Decision));

        RegisterUiBroadcastSinks();

        _hub.Reconnecting += OnReconnecting;
        _hub.Reconnected  += OnReconnected;
        _hub.Closed       += OnClosed;

        _terminalSender = new TerminalOutputSender(
            (agentId, base64, ct) => _hub.SendAsync("SendTerminalOutput", new TerminalOutput(agentId, base64), ct),
            isConnected: () => _hub.State == HubConnectionState.Connected,
            logger
        );
    }

    /// <summary>
    /// Registers no-op client handlers for the UI-only broadcasts a daemon can
    /// receive on its hub connection. The server adds every
    /// authenticated connection — daemons included — to its <c>org-members</c>
    /// UI group in <c>CapacitorHub.OnConnectedAsync</c>, and only removes the
    /// daemon again inside <c>DaemonConnect</c>. In the window between the
    /// WebSocket connecting and <c>DaemonConnect</c> completing — which recurs on
    /// every (re)connect — the daemon receives these broadcasts with no matching
    /// handler, and SignalR's <c>JsonHubProtocol</c> logs a "Failed to find
    /// handler" warning plus an argument-bind failure for each one ("Invocation
    /// provides N argument(s) but target expects 0"). Registering sinks at the
    /// server's current arities, with <see cref="JsonElement"/> parameters that
    /// bind to any payload shape, silences the flood even against an
    /// already-deployed server. The permanent fix is server-side: never add a
    /// daemon connection to the UI group in the first place.
    /// </summary>
    void RegisterUiBroadcastSinks() {
        static Task Sink() => Task.CompletedTask;

        _hub.On("AgentInstancesChanged", Sink);
        _hub.On("DaemonsChanged",        Sink);
        _hub.On("WelcomeStateChanged",   Sink);

        _hub.On<JsonElement>("ActiveSessionAdded",   _ => Sink());
        _hub.On<JsonElement>("ActiveSessionChanged", _ => Sink());
        _hub.On<JsonElement>("ActiveSessionRemoved", _ => Sink());

        _hub.On<JsonElement, JsonElement>("LaunchFailed",        (_, _) => Sink());
        _hub.On<JsonElement, JsonElement>("PermissionResponded", (_, _) => Sink());

        _hub.On<JsonElement, JsonElement, JsonElement, JsonElement, JsonElement>(
            "PermissionRequested", (_, _, _, _, _) => Sink());
    }

    CancellationToken _ct;
    volatile bool     _disposed;
    Task?             _eventProcessorTask;

    /// <summary>
    /// Guards <see cref="DisposeAsync"/> so its body runs exactly once — the DI container tracks
    /// this singleton AND <c>DaemonRunner</c> disposes it explicitly, so DisposeAsync runs twice
    /// by construction on every shutdown. Distinct from <see cref="_disposed"/>, which is a
    /// live-path flag read by <see cref="OnClosed"/>.
    /// </summary>
    int _disposeOnce;

    /// <summary>Counts entries into the <see cref="DisposeAsync"/> body (post-guard). Test seam:
    /// proves durably that a second dispose did NOT re-enter the body, so removal of the run-once
    /// guard fails a suite test permanently rather than only a one-off mutation check.</summary>
    int _disposeBodyRuns;

    internal int DisposeBodyRuns => Volatile.Read(ref _disposeBodyRuns);

    /// <summary>Test seam: the terminal-sender CTS, so tests can assert it ends cancelled AND
    /// disposed after the first dispose pass (removal of the Dispose call fails the suite).</summary>
    internal CancellationTokenSource? TerminalSenderCtsForTests => _terminalSenderCts;

    /// <summary>Test seam: lets a test swap in a faulting awaited task and assert the failure is
    /// contained + logged while the mandatory resource release still runs.</summary>
    internal Task? EventProcessorTaskForTests {
        get => _eventProcessorTask;
        set => _eventProcessorTask = value;
    }

    readonly TerminalOutputSender    _terminalSender;
    Task?                            _terminalSenderTask;
    CancellationTokenSource?         _terminalSenderCts;

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> taken each time the hub reaches a
    /// connected+registered state. Logged as connection uptime in
    /// <see cref="OnClosed"/> so the daemon log shows how long each connection
    /// survived — the cadence that distinguishes a steady transport from one
    /// flapping every few seconds (diagnostics). Zero until the first
    /// successful connect.
    /// </summary>
    long _connectedTimestamp;

    /// <summary>
    /// Sentinel prefix the server (<c>DaemonRegistry.NameInUseErrorCode</c>)
    /// embeds in the <see cref="Microsoft.AspNetCore.SignalR.HubException"/>
    /// message when <c>DaemonConnect</c> is rejected because another live
    /// daemon already holds the <c>(owner, name)</c> slot. The daemon parses
    /// this prefix and exits with code 3 instead of force-reconnecting in a
    /// loop with the incumbent — see <see cref="OnNameInUse"/>.
    /// </summary>
    public const string NameInUseErrorCode = "DAEMON_NAME_IN_USE";

    /// <summary>
    /// Fires when the server rejected <c>DaemonConnect</c> with the
    /// <see cref="NameInUseErrorCode"/> prefix. <c>DaemonRunner</c>
    /// subscribes and signals host shutdown so the binary exits with
    /// code 3 rather than oscillating with the incumbent daemon.
    /// </summary>
    public event Action<string>? OnNameInUse;

    static bool IsNameInUse(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he
        && he.Message.StartsWith(NameInUseErrorCode, StringComparison.Ordinal);

    public async Task ConnectAsync(CancellationToken ct) {
        _ct                 = ct;
        _eventProcessorTask = ProcessEventQueueAsync(ct);
        // Linked to ct but separately cancellable so DisposeAsync can stop the
        // sender even if the caller's token never fires — otherwise a chunk held
        // through an outage could block disposal.
        _terminalSenderCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _terminalSenderTask = _terminalSender.RunAsync(_terminalSenderCts.Token);
        await ConnectWithRetryAsync(ct);
    }

    /// <summary>
    /// Serializes <see cref="ConnectWithRetryAsync"/> so the initial connect and every
    /// <see cref="OnClosed"/>-triggered reconnect share ONE retry loop. Without this, each
    /// close event spawned another concurrent loop against the same <see cref="HubConnection"/>;
    /// the loser called <c>StartAsync</c> on a hub the winner had already started and got
    /// "cannot be started if it is not in the Disconnected state" — forever, at Warning,
    /// every 30s, against a perfectly healthy connection (issue #374).
    /// </summary>
    readonly SemaphoreSlim _connectLock = new(1, 1);

    /// <summary>
    /// Backoff schedule for <see cref="ConnectWithRetryAsync"/> (stays at the last entry once
    /// exhausted). Settable so tests can drive the retry path without real multi-second waits.
    /// </summary>
    internal TimeSpan[] ConnectRetryDelays { get; set; } = [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    /// <summary>Raw hub state — a seam so the retry loop's state checks are unit-testable
    /// without a live SignalR transport.</summary>
    internal virtual HubConnectionState HubState => _hub.State;

    /// <summary>Raw <see cref="HubConnection.StartAsync"/> — a seam for the same reason.</summary>
    internal virtual Task StartHubAsync(CancellationToken ct) => _hub.StartAsync(ct);

    /// <summary>Raw <see cref="HubConnection.StopAsync"/> — a seam so
    /// <see cref="ForceReconnectAsync"/>'s wedged-transport cap is unit-testable.</summary>
    internal virtual Task StopHubAsync(CancellationToken ct) => _hub.StopAsync(ct);

    /// <summary>How long <see cref="ForceReconnectAsync"/> waits for the hub stop before
    /// abandoning it. Settable so tests don't wait the real 5 s.</summary>
    internal TimeSpan ForceStopCap { get; set; } = TimeSpan.FromSeconds(5);

    internal async Task ConnectWithRetryAsync(CancellationToken ct) {
        await _connectLock.WaitAsync(ct);

        try {
            var attempt = 0;

            while (!ct.IsCancellationRequested) {
                try {
                    // Another path may have healed the connection while this call was queued on
                    // the lock or sleeping in backoff — SignalR's automatic reconnect
                    // (OnReconnected → RegisterDaemonAsync) or the heartbeat's ReRegisterAsync.
                    // A live, registered connection needs nothing from this loop.
                    if (IsReady) return;

                    // Only start a hub that is actually Disconnected. Connected means the
                    // transport is fine and registration is the missing half (e.g. the previous
                    // iteration's RegisterDaemonAsync threw after StartAsync succeeded).
                    // Connecting/Reconnecting means automatic reconnect owns the transport;
                    // RegisterDaemonAsync below fails ("connection is not active"), we back off,
                    // and re-check — auto-reconnect terminally either restores the connection or
                    // exhausts to Disconnected (firing Closed), so this converges.
                    if (HubState == HubConnectionState.Disconnected) {
                        LogConnecting(_config.ServerUrl);
                        await StartHubAsync(ct);
                        // Hub is now Connected (pre-registration) — pulse so a subscriber that
                        // snapshotted while still "connecting" converges without waiting for
                        // RegisterDaemonAsync too.
                        _statusNotifier.Pulse();
                    }

                    await RegisterDaemonAsync();
                    _connectedTimestamp = Stopwatch.GetTimestamp();
                    LogConnected(_config.Name);
                    _statusNotifier.Pulse();

                    return;
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (IsNameInUse(ex)) {
                    // server explicitly rejected this daemon because
                    // another live daemon owns the (owner, name) slot. Don't
                    // retry — retrying would just thrash the incumbent.
                    // RegisterDaemonAsync already fired OnNameInUse before re-throwing
                    // here; we just need to propagate so DaemonRunner exits
                    // with code 3 instead of looping forever.
                    throw;
                } catch (Exception ex) {
                    var delay = ConnectRetryDelays[Math.Min(attempt, ConnectRetryDelays.Length - 1)];
                    LogConnectionAttemptFailed(ex, attempt + 1, delay.TotalSeconds);
                    // The failed attempt has returned the hub to Disconnected — pulse so a
                    // subscriber converges to "disconnected" during the backoff instead of a
                    // stale "connecting" (Codex P2: initial-start failures never fired a pulse).
                    _statusNotifier.Pulse();
                    await Task.Delay(delay, ct);
                    attempt++;
                }
            }

            ct.ThrowIfCancellationRequested();
        } finally {
            _connectLock.Release();
        }
    }

    async Task OnClosed(Exception? ex) {
        _statusNotifier.Pulse();
        _gate.MarkUnregistered();

        if (_disposed || _ct.IsCancellationRequested) {
            return;
        }

        var uptimeSeconds = _connectedTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_connectedTimestamp).TotalSeconds;
        LogConnectionClosed(ex, uptimeSeconds);

        try {
            await ConnectWithRetryAsync(_ct);
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            // Shutting down, ignore
        } catch (Exception ex2) when (IsNameInUse(ex2)) {
            // ConnectWithRetryAsync already fired OnNameInUse via
            // RegisterDaemon and propagated. The host's shutdown handler
            // will tear everything down; swallow here so OnClosed (an
            // unobserved Task) doesn't crash the process.
        }
    }

    /// <summary>
    /// Runs a full (re-)registration through <see cref="RegistrationGate.RunRegistrationAsync"/>:
    /// <c>DaemonConnect</c>, then per-agent re-registration (<see cref="ReRegisterAgentsHook"/>)
    /// followed by the ACP re-bind (<see cref="ReRegisterAgentsAndAcpBindingsAsync"/>), and only THEN
    /// restores readiness. Folding agent re-registration into the readiness bracket closes the
    /// window where a permission invoke could fire after <c>DaemonConnect</c> but before the server
    /// re-established per-session ownership; folding the ACP re-bind in right after it
    /// closes the equivalent window for <see cref="SendAcpEventsAsync"/> — that call is
    /// <see cref="IsReady"/>-gated, so it can never
    /// reach the server before <see cref="ReBindAcpSessionsAsync"/> has re-established every active
    /// binding. The gate clears readiness at the start of the bracket, which is also what drops
    /// readiness on the heartbeat slot-displacement path (DaemonHeartbeatLoop.cs → ReRegisterAsync),
    /// where the transport stays up and no Reconnecting/Closed event fires.
    /// </summary>
    internal virtual Task RegisterDaemonAsync() =>
        _gate.RunRegistrationAsync(
            daemonConnect: DaemonConnectAsync,
            reRegisterAgents: ReRegisterAgentsAndAcpBindingsAsync,
            // Runs AFTER MarkRegistered, i.e. with IsReady == true, so a re-delivery here actually reaches
            // the server — inside reRegisterAgents it would be silently dropped by the CommandAckAsync
            // IsReady gate. Contained so a failing re-delivery never un-registers the daemon.
            postRegister: async () => {
                try { await (OnRegisteredHook?.Invoke() ?? Task.CompletedTask); }
                // Cancellation (shutdown) propagates — never contained as a hook failure.
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) {
                    // The log itself is contained: a throwing ILogger provider is a supported input, so a
                    // bare LogDebug here could re-throw and defeat this very containment.
                    try { _logger.LogDebug(ex, "post-registration hook failed — ignoring"); }
                    catch { /* logger provider threw — swallow so post-register containment holds */ }
                }
            }
        );

    /// <summary>
    /// Composes the existing per-agent re-registration hook with the ACP reconnect re-bind — AFTER
    /// agent re-registration, so per-session agent ownership is restored before an ACP binding tries
    /// to reference its agent. Both steps
    /// run inside <see cref="RegisterDaemonAsync"/>'s <see cref="RegistrationGate.RunRegistrationAsync"/>
    /// bracket, i.e. strictly BEFORE <see cref="IsReady"/> can report true — <c>internal</c> (not
    /// <c>private</c>) so it can be driven directly in tests without a live hub connection.
    /// </summary>
    internal async Task ReRegisterAgentsAndAcpBindingsAsync() {
        await (ReRegisterAgentsHook?.Invoke() ?? Task.CompletedTask);
        await ReBindAcpSessionsAsync();
    }

    /// <summary>Serialises DTO construction AND invocation. Two registrations can otherwise each
    /// capture their own <c>_config</c> snapshot and land in either order: the heartbeat's
    /// slot-displaced re-registration can capture the OLD capabilities, the certification self-heal
    /// can then publish the NEW ones, and if the heartbeat's frame is processed last the server ends
    /// up advertising the stale set while the daemon's local config says otherwise. That silently
    /// undoes the self-heal — which this area now depends on to restore a missing advertisement, so
    /// it is not a harmless duplicate registration.
    /// <para>Held across the hub invoke, not just the construction: releasing early would let a
    /// second DTO built from fresher config overtake an in-flight older one.</para></summary>
    readonly SemaphoreSlim _registerLock = new(1, 1);

    async Task DaemonConnectAsync() {
        await _registerLock.WaitAsync().ConfigureAwait(false);
        try {
            await DaemonConnectCoreAsync().ConfigureAwait(false);
        } finally {
            _registerLock.Release();
        }
    }

    async Task DaemonConnectCoreAsync() {
        var platform  = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
        var repoPaths = await MergeRepoPathsAsync();
        var liveIds   = GetLiveAgentIds?.Invoke() ?? [];
        var liveAgents = GetLiveAgents?.Invoke(); // Phase B (D2): additive; null on an unwired/old path

        try {
            await _hub.InvokeAsync(
                "DaemonConnect",
                new DaemonConnect(
                    _config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds,
                    _config.InstanceId, _config.Version, _config.SupportedVendors, new MachineId(_config.ConfigRoot).Get(), liveAgents,
                    _config.UnattendedVendors,
                    // Phase B2-b (sequenced-settlement design §4.2.3/§4.2.4): advertise the durable
                    // coverage boot-chain verdict plus the un-acked resolved-candidates ledger snapshot,
                    // re-reported on every connect until the server prunes it via AckResolvedCandidates.
                    // The full enriched sequenced-settlement payload lands in a later task; these
                    // additive fields are wire-compatible with old servers (ignored) and inert until the
                    // paired server PR consumes them.
                    RecordlessSurvivorsImpossible: _config.RecordlessSurvivorsImpossible,
                    ResolvedStartupCandidates: GetResolvedStartupCandidates?.Invoke(),
                    // Phase B2-b (sequenced-settlement design): the per-platform startup-completeness
                    // signals. Null getters (tests / early startup) leave the additive fields at their
                    // defaults, wire-compatible with old servers.
                    StartupReapComplete: GetStartupReapComplete?.Invoke(),
                    UnresolvedStartupCandidates: GetUnresolvedStartupCandidates?.Invoke(),
                    StartupDiscovery: GetStartupDiscovery?.Invoke(),
                    // Phase B2-b (sequenced-settlement design §4.2.2): the sequenced-settlement capability
                    // + its epoch/watermark counters + the kill-quarantine snapshot. SupportsSequencedCommands
                    // is THE gate: advertised true here but inert until the paired server PR consumes it. Epoch
                    // is read from the orchestrator's own per-boot _daemonEpoch via GetDaemonEpoch — the SINGLE
                    // source the orchestrator + processor use — so the connect epoch can't diverge from it.
                    // Unwired (tests / early startup) falls back to _config.DaemonEpoch, which DaemonRunner
                    // pins before services build, so prod behaviour is unchanged.
                    Quarantined: GetQuarantined?.Invoke(),
                    Epoch: GetDaemonEpoch?.Invoke() ?? _config.DaemonEpoch,
                    HighestAcceptedSeq: GetHighestAcceptedSeq?.Invoke(),
                    LastProcessedSeq: GetLastProcessedSeq?.Invoke(),
                    SupportsSequencedCommands: true,
                    // Phase B2-b (sequenced-settlement design §5.5): the resolved-candidates ledger's
                    // monotonic high-water alongside the re-advertised snapshot above.
                    HighestResolutionGeneration: GetHighestResolutionGeneration?.Invoke(),
                    UnattendedVendorCapabilities: _config.UnattendedVendorCapabilities,
                    // Launch-time ACP permission-preset advertisement: the supported vendors that route
                    // permissions through the ACP bridge. Null on an unwired/early-startup config;
                    // wire-compatible with old servers (ignored).
                    AcpPresetVendors: _config.AcpPresetVendors,
                    PermissionModeVendors: _config.PermissionModeVendors,
                    // Read off the handler, never asserted: an unwired connection (early startup,
                    // a test, a second ServerConnection) would otherwise invite RequestStatusReport2
                    // frames that its null-conditional invoke answers with silence.
                    SupportsCorrelatedStatusReports: AdvertisesCorrelatedStatusReports
                ),
                cancellationToken: _ct
            );
        } catch (Exception ex) when (IsNameInUse(ex)) {
            // server refused our (owner, name) slot because another
            // live daemon owns it. Surface to DaemonRunner before re-throwing
            // so the host can shut down cleanly; the heartbeat loop's
            // SafeReRegisterAsync filters this exception out so we don't
            // escalate to a pointless force-reconnect. RunRegistrationAsync
            // leaves readiness cleared and skips agent re-registration.
            LogNameInUse(_config.Name, ex.Message);
            OnNameInUse?.Invoke(ex.Message);

            throw;
        }
    }

    /// <summary>
    /// Set by <see cref="AgentOrchestrator"/>: re-registers this daemon's live agents with the
    /// server (AgentRegistered + AgentStatusChanged) so per-session ownership is restored after a
    /// (re-)connect. Invoked inside <see cref="RegisterDaemonAsync"/> BEFORE readiness is restored, so
    /// a permission invoke gated on <see cref="IsReady"/> can't beat session-ownership recovery.
    /// Null until wired (early startup / tests) — treated as a no-op.
    /// </summary>
    internal Func<Task>? ReRegisterAgentsHook { get; set; }

    /// <summary>Invoked inside <see cref="RegisterDaemonAsync"/> AFTER readiness is restored
    /// (post-<c>MarkRegistered</c>), unlike <see cref="ReRegisterAgentsHook"/>. For work that must reach
    /// the server on the freshly ready transport — the settlement lost-ack re-delivery lives here because
    /// <see cref="CommandAckAsync"/> silently drops sends while <see cref="IsReady"/> is still false inside
    /// the registration bracket. Null until wired (early startup / tests) — a no-op; failures are contained
    /// by the caller so they never un-register the daemon.</summary>
    internal Func<Task>? OnRegisteredHook { get; set; }

    async Task<string[]> MergeRepoPathsAsync() {
        var persisted = await new RepoPathStore(_config.ConfigRoot).GetSortedPathsAsync();

        if (_config.AllowedRepoPaths.Length == 0)
            return persisted;

        // Union: persisted paths first (sorted by last_used desc), then config-only paths
        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(persisted, comparer);
        var merged = new List<string>(persisted);
        merged.AddRange(_config.AllowedRepoPaths.Select(p => p.TrimEnd('/', '*')).Where(seen.Add));

        return [..merged];
    }

    /// <summary>
    /// Auto-reconnect started: the transport is no longer Connected and the
    /// server-side registration for this connection is stale. Clear readiness so
    /// nothing invokes a daemon-scoped hub method until <see cref="OnReconnected"/>
    /// re-runs <see cref="RegisterDaemonAsync"/>.
    /// </summary>
    Task OnReconnecting(Exception? error) {
        _gate.MarkUnregistered();
        _statusNotifier.Pulse();

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the hub is Connected AND this connection has completed a full
    /// (re-)registration — <c>DaemonConnect</c> AND per-agent re-registration (see
    /// <see cref="RegisterDaemonAsync"/>). The permission-request retry loop waits on this rather than
    /// raw <see cref="HubConnectionState.Connected"/> so a retry can't race re-registration.
    /// <c>virtual</c> so unit tests can control readiness directly without a live SignalR transport
    /// (see the ACP hub-method tests).
    /// </summary>
    internal virtual bool IsReady => _gate.IsReady(_hub.State);

    /// <summary>The current SignalR connection incarnation. Review-flow launch commands pin this
    /// value server-side and the daemon rechecks it immediately before any spawn side effects.</summary>
    internal virtual string? CurrentConnectionId => _hub.ConnectionId;

    async Task OnReconnected(string? connectionId) {
        _statusNotifier.Pulse();
        LogReconnected();
        await RegisterDaemonAsync();
        _connectedTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Round-trip liveness probe. Calls <c>DaemonPing</c> on the server
    /// and returns whether this connection is still the registered daemon for
    /// its <c>(owner, name)</c> slot. <c>false</c> means the slot was displaced
    /// — usually by an auto-reconnect Register from a different conn id —
    /// and the daemon should re-register so the orchestrator's view is
    /// repaired. <c>virtual</c> so unit tests can override without spinning
    /// up a real SignalR client.
    /// </summary>
    public Task<bool> PingAsync(CancellationToken ct)
        => _hub.InvokeAsync<bool>("DaemonPing", cancellationToken: ct);

    /// <summary>
    /// Re-runs <c>DaemonConnect</c> on the existing hub connection. Used by
    /// the heartbeat loop when the server reports it doesn't recognise this
    /// connection as a daemon (slot displaced or never registered).
    /// </summary>
    public Task ReRegisterAsync() => RegisterDaemonAsync();

    /// <summary>
    /// Stops the underlying hub. <see cref="OnClosed"/> fires and calls
    /// <see cref="ConnectWithRetryAsync"/>, which establishes a fresh
    /// transport and a new server-side conn id, then re-registers via
    /// <see cref="RegisterDaemonAsync"/>. Used when the heartbeat ping times out
    /// or throws — the WebSocket is hung and only a fresh connection
    /// recovers it. The stop is capped at <see cref="ForceStopCap"/> so a wedged
    /// transport can't stall the heartbeat loop indefinitely (Qodo). The cap is
    /// enforced from OUTSIDE via <c>WaitAsync</c>: <c>HubConnection.StopAsync</c>'s
    /// cancellation token is dead in the pinned client (its connection-lock wait and
    /// transport stop run on <c>token: default</c>), so passing the token in would
    /// never actually bound the await. Abandoning the wait is safe — StopAsync
    /// signals its internal stop token synchronously before its first await, so the
    /// teardown is already underway; when it eventually completes, Closed fires and
    /// <see cref="OnClosed"/> reconnects, and until then each heartbeat tick stays
    /// bounded and keeps retrying.
    /// </summary>
    public async Task ForceReconnectAsync() {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(ForceStopCap);

        try {
            await StopHubAsync(CancellationToken.None).WaitAsync(cts.Token);
        } catch (OperationCanceledException) when (!_ct.IsCancellationRequested) {
            // StopAsync didn't return within the cap — transport is wedged. OnClosed
            // may still fire eventually, but we don't want to block the
            // heartbeat loop on it. The next tick will retry.
            _logger.LogWarning("ForceReconnectAsync: StopAsync exceeded {CapSeconds:F0} s — abandoning wait", ForceStopCap.TotalSeconds);
        }
    }

    public virtual async Task UpdateRepoPathsAsync() {
        try {
            var repoPaths = await MergeRepoPathsAsync();
            await _hub.InvokeAsync("DaemonUpdateRepoPaths", repoPaths, cancellationToken: _ct);
        } catch (Exception ex) {
            LogRepoPathUpdateFailed(ex);
        }
    }

    // Outgoing messages to server
    public virtual Task AgentRegisteredAsync(
            string agentId, string? prompt, string? model, string? effort, string? repoPath,
            string? sandboxPolicy = null, string? approvalPolicy = null, string? permissionPreset = null,
            string? runtimeTransport = null)
        => _hub.InvokeAsync(
            "AgentRegistered",
            new AgentRegistered(agentId, prompt, model, effort, repoPath, sandboxPolicy, approvalPolicy, permissionPreset, runtimeTransport),
            cancellationToken: _ct);

    /// <summary>
    /// Reports the hosted agent's fixed PTY dimensions to the server, which stores
    /// them and broadcasts to subscribed read-only viewers so their xterm locks to
    /// the source size instead of auto-fitting its panel (which garbles the TUI).
    /// </summary>
    public virtual Task SendTerminalDimensionsAsync(string agentId, int cols, int rows)
        => _hub.SendAsync("SendTerminalDimensions", agentId, cols, rows, cancellationToken: _ct);

    public virtual Task AgentStatusChangedAsync(string agentId, string status, string? sessionId)
        => _hub.InvokeAsync("AgentStatusChanged", new AgentStatusChanged(agentId, status, sessionId), cancellationToken: _ct);

    /// <summary>
    /// Best-effort: tell the server the model a hosted agent actually resolved to at launch
    /// (e.g. the value Codex read from <c>~/.codex/config.toml</c> when dispatched with the
    /// "default" sentinel) so the UI can display the real model. Fire-and-forget over the
    /// persistent connection; swallowed when the connected server is older and has no
    /// <c>ReportAgentResolvedModel</c> hub method (missing-method / dispatch errors), so a
    /// mixed-version rollout never surfaces this as a failure.
    /// </summary>
    public virtual async Task ReportAgentResolvedModelAsync(string agentId, string model) {
        try {
            await _hub.SendAsync("ReportAgentResolvedModel", agentId, model, cancellationToken: _ct);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, agentId);
        }
    }

    /// <summary>
    /// Task 8: reports the CONCRETE resolved model an explicit-model reviewer actually launched
    /// with (the post-launch counterpart of the preflight RPC), over the persistent connection to the
    /// server's <c>ReportExplicitReviewerModelResolved</c> hub method — a single-record (arity 1)
    /// payload so the wire shape can evolve additively. Distinct from
    /// <see cref="ReportAgentResolvedModelAsync"/> (name/arity/behavior unchanged): the legacy report is
    /// still used for every no-model / vendor-only launch. Fire-and-forget best-effort: swallowed when
    /// the connected server is older and has no such hub method, so a mixed-version rollout never
    /// surfaces this as a failure. Virtual so tests can capture the report without a live hub.
    /// </summary>
    public virtual async Task ReportExplicitReviewerModelResolvedAsync(ExplicitReviewerModelResolvedV1 report) {
        try {
            await _hub.SendAsync("ReportExplicitReviewerModelResolved", report, cancellationToken: _ct);
        } catch (Exception ex) {
            LogReportResolvedModelFailed(ex, report.AgentId);
        }
    }

    /// <summary>
    /// Best-effort: tell the server a launch-time permission preset auto-approved one ACP permission
    /// request without a human, so it can persist an audit record. Fire-and-forget over the persistent
    /// connection; swallowed when the connected server is older and has no <c>NotifyAcpAutoApproval</c>
    /// hub method (missing-method / dispatch errors), so a mixed-version rollout never surfaces this as
    /// a failure. Virtual so tests can capture the notice without a live hub. Never throws — the
    /// caller's discarded task can be safely fire-and-forget.
    /// </summary>
    public virtual async Task NotifyAcpAutoApprovalAsync(AcpAutoApprovalNotice notice) {
        try {
            await _hub.SendAsync("NotifyAcpAutoApproval", notice, cancellationToken: _ct);
        } catch (Exception ex) {
            LogNotifyAcpAutoApprovalFailed(ex, notice.AgentId);
        }
    }

    public virtual Task AgentUnregisteredAsync(string agentId)
        => _hub.InvokeAsync("AgentUnregistered", new AgentUnregistered(agentId), cancellationToken: _ct);

    /// <summary>Tells the server this daemon dropped a dispatched input rather than delivering it.
    /// Without it a drop is visible only in this log, while the sender is shown a message that was
    /// delivered — the send returning proves the transport wrote and nothing more.
    ///
    /// <para>Best-effort by contract, and swallowed at Debug in particular for the server that has no
    /// such method: reporting a refusal must never be able to fail a delivery path, and a daemon
    /// talking to an older server has to behave exactly as it did before this existed.</para></summary>
    public virtual async Task SendInputRejectedAsync(Guid dispatchId, string agentId, string reason) {
        try {
            await _hub.InvokeAsync("SendInputRejected", dispatchId, agentId, reason, cancellationToken: _ct)
                .WaitAsync(SendInputRejectedBudget, _ct);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Could not report the dropped input for agent {AgentId} ({Reason})", agentId, reason);
        }
    }

    /// <summary>Bounds the report so an unresponsive server cannot pin the receive handler that is
    /// waiting on it. The invoke has no timer of its own, and the input this reports on is already
    /// dropped — waiting longer buys nothing anyone is still listening for.</summary>
    static readonly TimeSpan SendInputRejectedBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The one report a shutting-down daemon can still make. Every other method here bakes in
    /// <c>_ct</c>, which is <c>ApplicationStopping</c> and therefore already cancelled by the time
    /// teardown reaches this — so a caller using them at that point sends nothing and cannot tell.
    /// This one takes the caller's own live token and uses it throughout, including for the run event,
    /// which normally rides a background queue whose drain is dead for the same reason.
    ///
    /// <para>Best-effort by contract: every step is contained, and a failure in one does not skip the
    /// rest. Nothing here retries — the caller is holding a shutdown open.</para>
    /// </summary>
    public virtual async Task ReportAgentEndedForShutdownAsync(
            string agentId, string? sessionId, string status, string reason, int? exitCode, CancellationToken ct) {
        await SafeShutdownStepAsync(
            () => _hub.InvokeAsync("AgentStatusChanged", new AgentStatusChanged(agentId, status, sessionId), cancellationToken: ct),
            agentId, "status");

        await SafeShutdownStepAsync(
            () => PostAgentRunEventAsync(agentId, new AgentRunStopped(reason, exitCode), ct), agentId, "run-stopped");

        await SafeShutdownStepAsync(
            () => _hub.InvokeAsync<EndAgentSessionResult>("EndAgentSession", agentId, reason, cancellationToken: ct),
            agentId, "session-end");

        await SafeShutdownStepAsync(
            () => _hub.InvokeAsync("AgentUnregistered", new AgentUnregistered(agentId), cancellationToken: ct),
            agentId, "unregister");
    }

    async Task SafeShutdownStepAsync(Func<Task> step, string agentId, string label) {
        try {
            await step();
        } catch (Exception ex) {
            LogShutdownReportStepFailed(ex, agentId, label);
        }
    }

    /// <summary>The queued run-event POST, sent directly on the caller's token. Same endpoint and
    /// payload shape as the drain, without its retry loop.</summary>
    async Task PostAgentRunEventAsync(string agentId, object evt, CancellationToken ct) {
        var data = JsonSerializer.SerializeToNode(evt, evt.GetType(), CapacitorJsonContext.Default)!.AsObject();
        var payload = new JsonObject { ["event_type"] = evt.GetType().Name, ["data"] = data }.ToJsonString();

        _httpClient ??= new();

        var resolution = await _tokens.GetValidTokensForServerAsync(_config.Profiles.Name, _config.ServerUrl, ct);

        if (resolution.Tokens?.AccessToken is not null)
            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", resolution.Tokens.AccessToken);

        var response = await _httpClient.PostAsync(
            $"{_config.ServerUrl.TrimEnd('/')}/api/agent-runs/{agentId}/events",
            new StringContent(payload, Encoding.UTF8, "application/json"), ct);

        response.EnsureSuccessStatusCode();
    }

    public virtual Task LaunchFailedAsync(string agentId, string reason)
        => _hub.InvokeAsync("LaunchFailed", new LaunchFailed(agentId, reason), cancellationToken: _ct);

    /// <summary>
    /// Tells the server to end the AgentSession for a daemon-hosted agent. Used when
    /// the daemon stops or observes a hosted claude exiting, since claude isn't
    /// guaranteed to fire its own <c>session-end</c> hook on SIGTERM. The server-side
    /// handler is idempotent — if SessionEnded was already written (e.g. claude did
    /// fire session-end first), this call is a no-op.
    ///
    /// The result carries the resolved <c>SessionId</c> (the daemon only knows
    /// agentId; the server resolves the link) plus a <c>GenerateWhatsDone</c> flag.
    /// When the flag is true and SessionId is non-null, the daemon should spawn
    /// <c>kcap generate-whats-done {sessionId}</c> locally — matching the
    /// behaviour of the CLI session-end handler for the local-claude case.
    /// </summary>
    public virtual Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason)
        => ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => _hub.InvokeAsync<EndAgentSessionResult>("EndAgentSession", agentId, reason, cancellationToken: _ct),
            () => IsReady,
            EndSessionRetryPollInterval,
            attempt => LogEndSessionRetry(agentId, attempt),
            _ct
        );

    /// <summary>
    /// Forwards a hosted-agent permission request to the server's <c>RequestPermission</c>
    /// hub method and returns the user's decision. Runs over the persistent SignalR
    /// connection so the long-poll isn't subject to the Cloudflare HTTP-request timeout
    /// that severs the equivalent <c>/hooks/permission-request</c> route at ~120s.
    /// The provided <paramref name="ct"/> typically tracks daemon shutdown — HttpListener
    /// in the bridge doesn't surface a per-request "client disconnected" signal, so a
    /// Claude process exiting mid-wait won't cancel this call. Switching the bridge to
    /// Kestrel + <c>HttpContext.RequestAborted</c> would give us per-request cancellation.
    ///
    /// The bridge knows the vendor ("claude" or "codex") locally to pick the right hook
    /// response shape in <c>LocalPermissionBridge.BuildHookResponseJson</c>, but the
    /// server's permission flow is vendor-agnostic so it is NOT forwarded over the wire.
    /// The wire payload is a single <see cref="HostedPermissionRequest"/> record (arity 1):
    /// SignalR binds hub arguments by count, so a record lets the contract gain fields without
    /// the positional-arity fragility that broke earlier hosted-permission changes.
    /// </summary>
    public virtual async Task<PermissionDecision> RequestPermissionAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default) {
        var requestId = await BeginPermissionRequestAsync(sessionId, toolName, toolInput, suggestions, ct, static () => false);
        return await AwaitPermissionDecisionAsync(requestId, ct);
    }

    public enum RespondOutcomeKind { Applied, NotPending, Failed }
    public readonly record struct RespondOutcome(RespondOutcomeKind Kind, string? Reason);

    /// The RequestPermission2 invoke under ConnectionRetry. `abandoned` is evaluated synchronously
    /// immediately before every hub invoke: a token cancelled from a task continuation is not
    /// synchronous with the settlement that requested it, so the predicate is what keeps a settled
    /// request's invoke off the wire when readiness returns.
    public virtual Task<string> BeginPermissionRequestAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions,
            CancellationToken ct, Func<bool> abandoned) =>
        ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => {
                if (abandoned()) throw new PermissionRequestAbandonedException();
                return _hub.InvokeAsync<string>(
                    "RequestPermission2",
                    new HostedPermissionRequest(sessionId, toolName, toolInput, suggestions),
                    ct
                );
            },
            () => IsReady,
            PermissionRetryPollInterval,
            attempt => LogPermissionRetry(sessionId, attempt),
            ct,
            isRetriableServerError: IsOwnershipNotReady,
            maxServerErrorRetries: OwnershipNotReadyMaxRetries
        );

    public virtual Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct) =>
        _pendingPermissions.AwaitDecisionAsync(serverRequestId, ct);

    /// The hub method the web UI answers through, invoked as the owner so the web card clears
    /// after a local settlement. Never throws; runs on the daemon-lifetime token.
    /// SignalR binds a positional invocation by argument count, so all seven of the server
    /// method's parameters are sent; the last two name an ACP option, which a hook decision
    /// never carries.
    public virtual async Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) {
        try {
            await _hub.InvokeAsync("RespondToPermission", sessionId, serverRequestId, decision.Behavior,
                decision.ApplyPermissions, decision.UpdatedInput, null, null, _ct);
            return new RespondOutcome(RespondOutcomeKind.Applied, null);
        } catch (Exception ex) {
            return ClassifyRespondFailure(ex);
        }
    }

    internal static RespondOutcome ClassifyRespondFailure(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he && he.Message.Contains("no longer pending", StringComparison.Ordinal)
            ? new RespondOutcome(RespondOutcomeKind.NotPending, he.Message)
            : new RespondOutcome(RespondOutcomeKind.Failed, ex.Message);

    /// <summary>
    /// Forwards an ACP permission/elicitation interaction to the server's
    /// <c>AcpRequestInteraction</c> hub method and returns the user's decision. Mirrors
    /// <see cref="RequestPermissionAsync"/>'s non-blocking-invoke-then-await-push pattern exactly —
    /// see that method's remarks for why the invoke returns a requestId immediately rather than
    /// blocking the connection's parallel-invocation slot for the whole interaction wait.
    /// </summary>
    public virtual async Task<AcpInteractionDecision> RequestAcpInteractionAsync(
            AcpInteractionRequest request,
            CancellationToken     ct = default
        ) {
        var requestId = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => _hub.InvokeAsync<string>("AcpRequestInteraction", request, ct),
            () => IsReady,
            PermissionRetryPollInterval,
            attempt => LogPermissionRetry(request.AcpSessionId, attempt),
            ct,
            isRetriableServerError: IsOwnershipNotReady,
            maxServerErrorRetries: OwnershipNotReadyMaxRetries
        );

        return await _pendingAcpInteractions.AwaitDecisionAsync(requestId, ct);
    }

    /// <summary>
    /// Max bounded retries for the post-reconnect "Caller is not the daemon owning session"
    /// HubException. ≈ this × <see cref="PermissionRetryPollInterval"/> of grace for
    /// per-agent re-registration to restore ownership before the request falls through to a deny.
    /// </summary>
    const int OwnershipNotReadyMaxRetries = 6;

    static bool IsOwnershipNotReady(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he
        && he.Message.Contains("owning session", StringComparison.Ordinal);

    // ── ACP wire contract forwarding ────────────────────────────────────────────────────────────
    // Two gated hub-invoke methods mirroring the server's
    // CapacitorHub.AcpSessionStarted/AcpSessionEvents (Capacitor.Server.Sessions.CapacitorHub)
    // exactly — same method names, same argument order/names — plus the
    // reconnect re-bind registry or above (RegisterAcpBinding/UnregisterAcpBinding/ReBindAcpSessionsAsync).
    // AcpTranscriptForwarder (Acp/AcpTranscriptForwarder.cs) is the stateful caller of SendAcpEventsAsync;
    // this class never itself decides seq/gap/retry — it only forwards + gates + re-binds.

    /// <summary>
    /// Registers an ACTIVE ACP session↔agent binding (called right after
    /// its initial <see cref="AcpSessionStartedAsync"/> call succeeds) so a future reconnect's
    /// <see cref="ReBindAcpSessionsAsync"/> can idempotently re-establish it. Overwriting an existing
    /// entry for the same <paramref name="agentId"/> is harmless — the server-side bind is itself
    /// idempotent on a same-agent re-bind (<c>AcpSessionRegistry.TryBindAsync</c>).
    /// </summary>
    public void RegisterAcpBinding(string agentId, AcpBindInfo bindInfo) => _acpBindings[agentId] = bindInfo;

    /// <summary>
    /// Removes an ACP binding (when the agent ends) so a later reconnect no longer tries to
    /// re-invoke <c>AcpSessionStarted</c> for an agent that no longer exists.
    /// </summary>
    public void UnregisterAcpBinding(string agentId) => _acpBindings.TryRemove(agentId, out _);

    /// <summary>
    /// Bounded backoff between re-bind attempts inside <see cref="ReBindAcpSessionsAsync"/> — short
    /// by design since this runs INSIDE the registration bracket, before
    /// <see cref="IsReady"/> flips true, so a slow bound here delays every other inbound/outbound
    /// call on this connection. Settable so tests don't wait for the real value.
    /// </summary>
    internal TimeSpan AcpRebindRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many times <see cref="ReBindAcpSessionsAsync"/> retries a single binding's re-bind before
    /// giving up on it. Bounded so a binding that can never be re-established (the
    /// server has forgotten it, or the agent it belongs to has ended) isn't replayed forever on every
    /// future reconnect.
    /// </summary>
    internal const int AcpRebindMaxAttempts = 3;

    /// <summary>
    /// Re-invokes <c>AcpSessionStarted</c> directly on the hub — bypassing the gated
    /// <see cref="AcpSessionStartedAsync"/>/<see cref="IsReady"/> path on purpose — for every
    /// currently-registered ACP binding. Called from
    /// <see cref="ReRegisterAgentsAndAcpBindingsAsync"/>, itself run inside
    /// <see cref="RegistrationGate.RunRegistrationAsync"/>'s bracket BEFORE
    /// <see cref="RegistrationGate.MarkRegistered"/> — i.e. while <see cref="IsReady"/> is still
    /// FALSE. Gating this call on <see cref="IsReady"/> (the way the public wrapper does) would
    /// therefore deadlock: <see cref="IsReady"/> can only become true once this very method returns.
    /// The transport itself is already <see cref="HubConnectionState.Connected"/> by the time
    /// <see cref="RegisterDaemonAsync"/> runs (that is what triggered it), so invoking the raw hub method
    /// here is safe — exactly the same reasoning that lets <see cref="AgentRegisteredAsync"/>/
    /// <see cref="AgentStatusChangedAsync"/> be called ungated from
    /// <c>AgentOrchestrator.ReRegisterAgentsAsync</c>. Best-effort per binding: one binding's re-bind
    /// failure does not stop the others or withhold daemon readiness for the rest of this connection,
    /// mirroring <c>ReRegisterAgentsAsync</c>'s per-agent isolation.
    ///
    /// <b>Bounded re-bind-miss:</b> a binding that fails to re-establish would
    /// otherwise be silently skipped and left registered — <see cref="IsReady"/> flips true
    /// regardless once this whole pass returns, so the forwarder's <see cref="SendAcpEventsAsync"/>
    /// calls would start retrying the SAME batch against a binding the server never got, forever
    /// (nothing replays the bind itself until another reconnect). Retry each binding up to
    /// <see cref="AcpRebindMaxAttempts"/> times (short <see cref="AcpRebindRetryDelay"/> backoff
    /// between attempts) before giving up on it; on a bound-exhausting permanent failure,
    /// <see cref="UnregisterAcpBinding"/> removes it so a LATER reconnect doesn't replay it either.
    /// </summary>
    internal async Task ReBindAcpSessionsAsync() {
        // belt-and-braces: never replay a binding whose agent the daemon no longer hosts as
        // live. _acpBindings is maintained independently of the live-agent set, so a stale entry to a
        // gone agent is exactly the binding the server now rejects. GetLiveAgentIds is the same source
        // the reconnect survivor set uses; when it isn't wired (tests) we skip the pre-filter.
        var live = GetLiveAgentIds is { } getLive ? new HashSet<string>(getLive(), StringComparer.Ordinal) : null;

        foreach (var (agentId, bind) in _acpBindings) {
            if (live is not null && !live.Contains(agentId)) {
                LogAcpRebindSkippedNotLive(agentId, bind.AcpSessionId);
                UnregisterAcpBinding(agentId);

                continue;
            }

            var settled = false; // the invoke returned an OUTCOME (Bound or Rejected): stop, don't give up

            for (var attempt = 1; attempt <= AcpRebindMaxAttempts; attempt++) {
                try {
                    var outcome = await InvokeAcpSessionStartedRawAsync(agentId, bind.Vendor, bind.AcpSessionId, bind.Cwd, bind.Model, bind.Metadata, _ct);

                    // a Rejected outcome is a terminal stand-down, not a transient failure —
                    // the server declined a stale/foreign/conflicting binding. Drop the binding (so a
                    // later reconnect doesn't replay it) and move on WITHOUT retrying; the agent's
                    // forwarder terminalizes independently on the matching AcpSessionEvents rejection
                    // ack. An old server (void return) decodes as Bound, preserving today's behaviour.
                    if (outcome == AcpBindOutcome.Rejected) {
                        LogAcpRebindRejected(agentId, bind.AcpSessionId);
                        UnregisterAcpBinding(agentId);
                    }

                    settled = true;

                    break;
                } catch (Exception ex) {
                    LogAcpRebindFailed(ex, agentId, bind.AcpSessionId, attempt, AcpRebindMaxAttempts);

                    if (attempt == AcpRebindMaxAttempts) break;

                    try {
                        await Task.Delay(AcpRebindRetryDelay, _ct);
                    } catch (OperationCanceledException) {
                        return; // shutting down mid-retry — nothing more to do here
                    }
                }
            }

            if (!settled) {
                LogAcpRebindGivingUp(agentId, bind.AcpSessionId, AcpRebindMaxAttempts);
                UnregisterAcpBinding(agentId);
            }
        }
    }

    /// <summary>
    /// Binds an ACP canonical session to an agent. Gated
    /// exactly like <see cref="EndAgentSessionAsync"/>/<see cref="RequestPermissionAsync"/> —
    /// <see cref="ConnectionRetry"/> waits for <see cref="IsReady"/> before every attempt, so this
    /// can never fire against a connection the server hasn't (re-)registered. Idempotent server-side
    /// (<c>AcpSessionRegistry</c>'s same-agent re-bind), so callers (the initial bind, and this
    /// class's own <see cref="ReBindAcpSessionsAsync"/> reconnect path) may invoke the underlying hub
    /// method again freely — a redundant re-bind is harmless even if the two race.
    /// </summary>
    public virtual async Task AcpSessionStartedAsync(
            string                               agentId,
            string                               vendor,
            string                               acpSessionId,
            string?                              cwd,
            string?                              model,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken                    ct = default
        ) {
        var outcome = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => InvokeAcpSessionStartedRawAsync(agentId, vendor, acpSessionId, cwd, model, metadata, ct),
            () => IsReady,
            AcpRetryPollInterval,
            attempt => LogAcpSessionStartedRetry(agentId, attempt),
            ct
        ).ConfigureAwait(false);

        // an INITIAL bind that the server declines (stale/foreign/conflict) must NOT register
        // a binding or start a forwarder. Surface it as a local exception (never a HubException) so the
        // launch path's existing "bind threw ⇒ don't register" catch handles it unchanged. Reason-
        // agnostic: any Rejected origin maps here. An old server's void return decodes to Bound.
        if (outcome == AcpBindOutcome.Rejected)
            throw new AcpBindRejectedException(agentId, acpSessionId);
    }

    /// <summary>
    /// Forwards a batch of ACP transcript envelopes to the server's <c>AcpSessionEvents</c> hub
    /// method and returns the ack
    /// <c>AcpTranscriptForwarder</c> uses to drive its seq/gap/terminal state machine. Gated exactly
    /// like <see cref="AcpSessionStartedAsync"/> — a post-reconnect batch blocks on
    /// <see cref="IsReady"/> until <see cref="ReBindAcpSessionsAsync"/> has re-established the
    /// binding, so it can never reach the server ahead of the
    /// re-bind.
    /// </summary>
    public virtual Task<AcpBatchAck> SendAcpEventsAsync(
            string             agentId,
            string             acpSessionId,
            AcpEventEnvelope[] envelopes,
            CancellationToken  ct = default
        ) => ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => InvokeAcpSessionEventsRawAsync(agentId, acpSessionId, envelopes, ct),
            () => IsReady,
            AcpRetryPollInterval,
            attempt => LogAcpEventsRetry(agentId, attempt),
            ct
        );

    /// <summary>
    /// The actual <c>AcpSessionStarted</c> hub invocation, isolated into its own <c>virtual</c>
    /// method so both <see cref="AcpSessionStartedAsync"/> (gated) and
    /// <see cref="ReBindAcpSessionsAsync"/> (ungated, see its remarks) share one call site, and so
    /// unit tests can capture/verify the exact payload without a live hub connection.
    /// </summary>
    /// returns the server's <see cref="AcpBindOutcome"/>. An OLD server whose hub method is
    /// still <c>void</c> completes with no result, which <c>InvokeAsync&lt;AcpBindOutcome&gt;</c> decodes
    /// to <c>default</c> == <see cref="AcpBindOutcome.Bound"/> — legacy success, preserving today's
    /// behaviour against an un-upgraded server.
    internal virtual Task<AcpBindOutcome> InvokeAcpSessionStartedRawAsync(
            string                               agentId,
            string                               vendor,
            string                               acpSessionId,
            string?                              cwd,
            string?                              model,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken                    ct
        ) => _hub.InvokeAsync<AcpBindOutcome>("AcpSessionStarted", agentId, vendor, acpSessionId, cwd, model, metadata, cancellationToken: ct);

    /// <summary>
    /// The actual <c>AcpSessionEvents</c> hub invocation, isolated into its own <c>virtual</c> method
    /// so <see cref="SendAcpEventsAsync"/>'s gating can be tested against a fake payload capture
    /// without a live hub connection.
    /// </summary>
    internal virtual Task<AcpBatchAck> InvokeAcpSessionEventsRawAsync(
            string             agentId,
            string             acpSessionId,
            AcpEventEnvelope[] envelopes,
            CancellationToken  ct
        ) => _hub.InvokeAsync<AcpBatchAck>("AcpSessionEvents", agentId, acpSessionId, envelopes, cancellationToken: ct);

    /// <summary>
    /// The deferred-first-turn SOURCE CLAIM: durably records this hosted session's ownership (the
    /// guard-2 substrate) BEFORE the orchestrator dispatches the first turn. Gated exactly like
    /// <see cref="AcpSessionStartedAsync"/> — <see cref="ConnectionRetry"/> waits for
    /// <see cref="IsReady"/> before every attempt. Returns the server's full outcome (bind result +
    /// ownership token + canonical resume cursor); the caller acts on <see cref="AcpBindOutcome.Rejected"/>
    /// (coded launch failure + teardown) and carries the token to <see cref="ConfirmSessionLaunchAsync"/>.
    /// </summary>
    public virtual Task<AcpSourceClaimOutcome> AcpSessionSourceClaimAsync(
            string agentId, string acpSessionId, CancellationToken ct = default
        ) => ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => InvokeAcpSessionSourceClaimRawAsync(agentId, acpSessionId, ct),
            () => IsReady,
            AcpRetryPollInterval,
            attempt => LogAcpSourceClaimRetry(agentId, attempt),
            ct
        );

    /// <summary>
    /// The token-fenced CONFIRM: clears the ledger row's provisional flag once the first turn has been
    /// dispatched. Gated on <see cref="IsReady"/>. Returns the token-fenced outcome; the caller's
    /// confirm loop treats <see cref="AcpLaunchConfirmOutcome.Confirmed"/>/<see cref="AcpLaunchConfirmOutcome.AlreadyConfirmed"/>
    /// as done, <see cref="AcpLaunchConfirmOutcome.Superseded"/>/<see cref="AcpLaunchConfirmOutcome.NotFound"/>
    /// as terminal-stop, and a transient connection failure as a retry — a confirm failure never tears
    /// down the running agent.
    /// </summary>
    public virtual Task<AcpLaunchConfirmOutcome> ConfirmSessionLaunchAsync(
            string acpSessionId, long ownershipToken, CancellationToken ct = default
        ) => ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => InvokeConfirmSessionLaunchRawAsync(acpSessionId, ownershipToken, ct),
            () => IsReady,
            AcpRetryPollInterval,
            attempt => LogConfirmSessionLaunchRetry(acpSessionId, attempt),
            ct
        );

    /// <summary>
    /// The actual <c>AcpSessionSourceClaim</c> hub invocation, isolated into its own <c>virtual</c>
    /// method so <see cref="AcpSessionSourceClaimAsync"/>'s gating can be tested without a live hub.
    /// A pre-source-claim server has no such method, so <c>InvokeAsync</c> throws (method-not-found) —
    /// which the launch path treats as a coded launch failure (the reverse-skew contract).
    /// </summary>
    internal virtual Task<AcpSourceClaimOutcome> InvokeAcpSessionSourceClaimRawAsync(
            string agentId, string acpSessionId, CancellationToken ct
        ) => _hub.InvokeAsync<AcpSourceClaimOutcome>("AcpSessionSourceClaim", agentId, acpSessionId, cancellationToken: ct);

    /// <summary>
    /// The actual <c>ConfirmSessionLaunch</c> hub invocation, isolated into its own <c>virtual</c>
    /// method so <see cref="ConfirmSessionLaunchAsync"/>'s gating can be tested without a live hub.
    /// </summary>
    internal virtual Task<AcpLaunchConfirmOutcome> InvokeConfirmSessionLaunchRawAsync(
            string acpSessionId, long ownershipToken, CancellationToken ct
        ) => _hub.InvokeAsync<AcpLaunchConfirmOutcome>("ConfirmSessionLaunch", acpSessionId, ownershipToken, cancellationToken: ct);

    /// <summary>
    /// §2.7 B6: reports a settled, resumable hosted reviewer that the daemon is about to
    /// PARK (freeing its slot while keeping its app-server thread alive for a later resume) to the
    /// server's <c>ReportParticipantParked</c> hub method, and folds the reply into a
    /// <see cref="ParkAck"/>. Gated on <see cref="IsReady"/> and wrapped in <see cref="ConnectionRetry"/>
    /// exactly like <see cref="AcpSessionSourceClaimAsync"/>/<see cref="ConfirmSessionLaunchAsync"/> —
    /// a transient disconnect is retried transparently rather than surfacing.
    ///
    /// Unlike those two, this method NEVER throws. The arm-A park state machine
    /// (<c>AgentOrchestrator.ParkReviewerAsync</c>) needs a definite three-way answer rather than a
    /// two-way outcome-or-exception split, because an exception here must NOT be treated as a
    /// rejection by default: <see cref="ParkAck.Ambiguous"/> — covering a transient
    /// <c>HubException</c>, an <see cref="OperationCanceledException"/> (including daemon shutdown),
    /// or any other unmapped exception — leaves the reviewer's local state untouched so the next reap
    /// sweep retries the park, instead of falling back to a destructive end. Only the server's two
    /// definite wire outcomes (<see cref="ParkParticipantOutcome.Parked"/>/
    /// <see cref="ParkParticipantOutcome.Rejected"/>) map to their like-named <see cref="ParkAck"/>
    /// members; any other/unmapped value degrades to <see cref="ParkAck.Ambiguous"/> rather than being
    /// assumed successful.
    ///
    /// One exception IS treated as a definite outcome: <see cref="IsUnknownHubMethod"/> singles out
    /// the <c>HubException</c> a pre-B1 server raises because it has no <c>ReportParticipantParked</c>
    /// handler at all. That is a PERMANENT degrade, not a transient hiccup — folding it into Ambiguous
    /// would have <c>ParkReviewerAsync</c> retry the park forever against a server that will never grow
    /// the method. Mapping it to <see cref="ParkAck.Rejected"/> instead makes the caller fall back to
    /// the normal reap, same as a definite server-side refusal.
    /// </summary>
    public virtual async Task<ParkAck> ReportParticipantParkedAsync(
            string agentId, string canonicalSessionId, string reason, CancellationToken ct = default
        ) {
        // Bound the whole attempt (readiness gate + retries) with ParkAckBudget so a disconnected daemon
        // can never pin the caller: ParkReviewerAsync holds the reap claim across this await, and the
        // readiness loop is otherwise bounded only by the shutdown token. On elapse the linked token
        // cancels, ConnectionRetry surfaces OperationCanceledException, and the generic catch below folds
        // it to Ambiguous — "no definite reply" — releasing the claim for a later sweep to retry. If
        // IsReady never came, no report was sent, so there is nothing to reconcile.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ParkAckBudget);
        try {
            var outcome = await ConnectionRetry.InvokeWithConnectionRetryAsync(
                () => InvokeReportParticipantParkedRawAsync(agentId, canonicalSessionId, reason, timeoutCts.Token),
                () => IsReady,
                ParkRetryPollInterval,
                attempt => LogReportParticipantParkedRetry(agentId, attempt),
                timeoutCts.Token
            );

            return outcome switch {
                ParkParticipantOutcome.Parked   => ParkAck.Parked,
                ParkParticipantOutcome.Rejected => ParkAck.Rejected,
                _                                => ParkAck.Ambiguous, // unmapped/future wire value — never assume success
            };
        } catch (Exception ex) when (IsUnknownHubMethod(ex)) {
            // Pre-B1 server: ReportParticipantParked doesn't exist there and never will until the
            // server is upgraded. Definite degrade, not "no reply" — fall back to the normal reap
            // instead of retrying this park forever (see the doc comment above).
            LogReportParticipantParkedUnknownMethod(agentId);

            return ParkAck.Rejected;
        } catch (Exception ex) {
            LogReportParticipantParkedAmbiguous(ex, agentId);

            return ParkAck.Ambiguous;
        }
    }

    /// <summary>
    /// True when <paramref name="ex"/> is the <see cref="Microsoft.AspNetCore.SignalR.HubException"/>
    /// SignalR raises when the connected server has no handler at all for the invoked hub method
    /// name — the case a pre-B1 server hits for <c>ReportParticipantParked</c>. When a client invokes
    /// a target the server's <c>DefaultHubDispatcher</c> can't resolve, it completes the invocation
    /// with the error <c>Unknown hub method '&lt;target&gt;'</c> (sent regardless of
    /// <c>EnableDetailedErrors</c>), surfaced on the client as a <c>HubException</c> carrying that
    /// message — verified against aspnetcore v10.0.11
    /// (<c>src/SignalR/server/Core/src/Internal/DefaultHubDispatcher.cs</c>). Matched case-insensitively
    /// on the stable <c>"Unknown hub method"</c> substring (robust to the interpolated target name), and
    /// narrowly scoped to <see cref="Microsoft.AspNetCore.SignalR.HubException"/> only: that phrase is
    /// SignalR-internal protocol text no hub handler emits deliberately, so a genuine transient
    /// <c>HubException</c> (e.g. "Caller is not a registered daemon") never contains it and is never
    /// misclassified as a permanent degrade.
    /// </summary>
    static bool IsUnknownHubMethod(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he
        && he.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The actual <c>ReportParticipantParked</c> hub invocation, isolated into its own <c>virtual</c>
    /// method so <see cref="ReportParticipantParkedAsync"/>'s gating/mapping can be tested without a
    /// live hub. A pre-B1 server has no such method, so <c>InvokeAsync</c> throws (method-not-found),
    /// which <see cref="ReportParticipantParkedAsync"/> singles out via <see cref="IsUnknownHubMethod"/>
    /// and maps to <see cref="ParkAck.Rejected"/> rather than treating it like any other exception.
    /// </summary>
    internal virtual Task<ParkParticipantOutcome> InvokeReportParticipantParkedRawAsync(
            string agentId, string canonicalSessionId, string reason, CancellationToken ct
        ) => _hub.InvokeAsync<ParkParticipantOutcome>("ReportParticipantParked", agentId, canonicalSessionId, reason, cancellationToken: ct);

    /// <summary>
    /// Queues a base64 PTY chunk for the hosted-agent terminal mirror:
    /// chunks are drained by <see cref="TerminalOutputSender"/>'s single ordered loop
    /// instead of being fired at <c>SendAsync</c> fire-and-forget, so they reach the
    /// server in PTY order. The enqueue awaits when the queue is full — the caller
    /// (the PTY read loop) awaits this, so a stalled transport back-pressures the PTY
    /// rather than dropping bytes mid-escape-sequence.
    /// </summary>
    /// <param name="ct">
    /// Cancels a blocked (back-pressured) enqueue. The read loop passes a token tied
    /// to BOTH the per-agent stop (<c>ReadCts</c>) and daemon shutdown, so stopping a
    /// single agent releases its read loop even mid-outage — otherwise the loop's
    /// finally-block finalization/cleanup would stall until daemon shutdown.
    /// </param>
    public virtual Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) =>
        _terminalSender.EnqueueAsync(agentId, base64Data, ct).AsTask();

    /// <summary>
    /// Non-blocking terminal-output enqueue for local-first agents (see
    /// <see cref="TerminalOutputSender.TryEnqueue"/>): never back-pressures the caller, so a
    /// registered local agent's PTY read loop and live terminal stay responsive through a tunnel
    /// stall. Returns false if the chunk was dropped (backlog full).
    /// </summary>
    public virtual bool TrySendTerminalOutput(string agentId, string base64Data) =>
        _terminalSender.TryEnqueue(agentId, base64Data);

    // ── Eval progress events (DEV-1440) ────────────────────────────────────

    public Task EvalStartedAsync(string evalRunId, string sessionId, string judgeModel, int totalQuestions)
        => _hub.SendAsync("EvalStarted", new EvalStarted(evalRunId, sessionId, judgeModel, totalQuestions), cancellationToken: _ct);

    public Task EvalQuestionStartedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId)
        => _hub.SendAsync("EvalQuestionStarted", new EvalQuestionStarted(evalRunId, sessionId, index, total, category, questionId), cancellationToken: _ct);

    public Task EvalQuestionCompletedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, int score, string verdict)
        => _hub.SendAsync("EvalQuestionCompleted", new EvalQuestionCompleted(evalRunId, sessionId, index, total, category, questionId, score, verdict), cancellationToken: _ct);

    public Task EvalQuestionFailedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, string reason)
        => _hub.SendAsync("EvalQuestionFailed", new EvalQuestionFailed(evalRunId, sessionId, index, total, category, questionId, reason), cancellationToken: _ct);

    public Task EvalFinishedAsync(string evalRunId, string sessionId, int overallScore, string summary)
        => _hub.SendAsync("EvalFinished", new EvalFinished(evalRunId, sessionId, overallScore, summary), cancellationToken: _ct);

    public Task EvalFailedAsync(string evalRunId, string sessionId, string reason)
        => _hub.SendAsync("EvalFailed", new EvalFailed(evalRunId, sessionId, reason), cancellationToken: _ct);

    // ── Retrospective progress events (DEV-1470) ───────────────────────────

    public Task EvalRetrospectiveStartedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveStarted", new EvalRetrospectiveStarted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveCompletedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveCompleted", new EvalRetrospectiveCompleted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveFailedAsync(string sessionId, string evalRunId, string reason)
        => _hub.SendAsync("EvalRetrospectiveFailed", new EvalRetrospectiveFailed(sessionId, evalRunId, reason), cancellationToken: _ct);

    public virtual Task AppendAgentRunEventAsync(string agentId, object evt) {
        _eventChannel.Writer.TryWrite(new PendingEvent(agentId, evt));

        return Task.CompletedTask;
    }

    readonly Channel<PendingEvent> _eventChannel = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest }
    );

    HttpClient? _httpClient;

    async Task ProcessEventQueueAsync(CancellationToken ct) {
        try {
            await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct)) {
                string payload;

                try {
                    var eventType = evt.Event.GetType().Name;
                    var data      = JsonSerializer.SerializeToNode(evt.Event, evt.Event.GetType(), CapacitorJsonContext.Default)!.AsObject();

                    var payloadObj = new JsonObject {
                        ["event_type"] = eventType,
                        ["data"]       = data
                    };
                    payload = payloadObj.ToJsonString();
                } catch (Exception ex) {
                    LogEventSerializationFailed(ex, evt.Event.GetType().Name, evt.AgentId);

                    continue;
                }

                var url        = $"{_config.ServerUrl.TrimEnd('/')}/api/agent-runs/{evt.AgentId}/events";
                var retryDelay = TimeSpan.FromSeconds(1);

                while (!ct.IsCancellationRequested) {
                    try {
                        _httpClient ??= new();
                        var resolution = await _tokens.GetValidTokensForServerAsync(_config.Profiles.Name, _config.ServerUrl, ct);

                        if (resolution.Tokens?.AccessToken is not null) {
                            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", resolution.Tokens.AccessToken);
                        }

                        var response = await _httpClient.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                        response.EnsureSuccessStatusCode();

                        break;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        LogEventPostFailed(ex, retryDelay.TotalSeconds);

                        try {
                            await Task.Delay(retryDelay, ct);
                        } catch (OperationCanceledException) {
                            return;
                        }

#pragma warning disable IDE0059
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
#pragma warning restore IDE0059
                    }
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Graceful shutdown — channel read cancelled
        }
    }

    record PendingEvent(string AgentId, object Event);

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;

        Interlocked.Increment(ref _disposeBodyRuns);

        _disposed = true; // live-path flag read by OnClosed — separate from the run-once guard

        var cts = _terminalSenderCts;

        try {
            // Faultable awaits — each contained + logged individually so one faulted pipeline
            // task can't skip its sibling or the mandatory resource release in the finally below.
            // A disposal path must never throw into DI teardown (NativeAOT: unhandled → abort()).
            try {
                _eventChannel.Writer.TryComplete();
                _terminalSender.Complete();

                if (_eventProcessorTask is not null) {
                    await _eventProcessorTask;
                }
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "event-processor");
            }

            try {
                // Cancel the sender's own token so a chunk being held through an outage
                // can't block disposal regardless of the caller's token state.
                if (cts is not null) {
                    try {
                        await cts.CancelAsync();
                    } catch (ObjectDisposedException) {
                        // Already torn down elsewhere — nothing left to cancel.
                    }
                }

                if (_terminalSenderTask is not null) {
                    await _terminalSenderTask;
                }
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "terminal-sender");
            }
        } finally {
            // Mandatory release — each step individually guarded so one failure can't skip the
            // rest, and nothing here can throw into DI teardown.
            try {
                cts?.Dispose();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "terminal-sender-cts");
            }

            try {
                _httpClient?.Dispose();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "http-client");
            }

            try {
                await _hub.DisposeAsync();
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "hub");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "ServerConnection dispose step '{Step}' failed; continuing shutdown")]
    partial void LogDisposeStepFailed(Exception ex, string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "Daemon name '{Name}' is already in use by another live daemon on this account. Server rejected DaemonConnect: {Reason}")]
    partial void LogNameInUse(string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {Url}...")]
    partial void LogConnecting(string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected and registered as '{Name}'")]
    partial void LogConnected(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection attempt {Attempt} failed, retrying in {Delay}s")]
    partial void LogConnectionAttemptFailed(Exception ex, int attempt, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SignalR connection closed after {UptimeSeconds:F1}s uptime, will reconnect")]
    partial void LogConnectionClosed(Exception? ex, double uptimeSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to server, re-registering daemon")]
    partial void LogReconnected();

    [LoggerMessage(Level = LogLevel.Information, Message = "RequestPermission for session {SessionId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogPermissionRetry(string sessionId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "EndAgentSession for agent {AgentId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogEndSessionRetry(string agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown report step '{Step}' failed for agent {AgentId}")]
    partial void LogShutdownReportStepFailed(Exception ex, string agentId, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "AcpSessionStarted for agent {AgentId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogAcpSessionStartedRetry(string agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "AcpSessionEvents for agent {AgentId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogAcpEventsRetry(string agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "AcpSessionSourceClaim for agent {AgentId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogAcpSourceClaimRetry(string agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "ConfirmSessionLaunch for session {AcpSessionId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogConfirmSessionLaunchRetry(string acpSessionId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "ReportParticipantParked for agent {AgentId} interrupted by a connection drop (retry {Attempt}); waiting for the daemon connection to recover before retrying")]
    partial void LogReportParticipantParkedRetry(string agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReportParticipantParked for agent {AgentId} got no definite reply — treating as ambiguous so the next reap sweep retries the park")]
    partial void LogReportParticipantParkedAmbiguous(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ReportParticipantParked for agent {AgentId} failed because the connected server has no such hub method (pre-B1) — treating as Rejected so the caller falls back to the normal reap instead of retrying forever")]
    partial void LogReportParticipantParkedUnknownMethod(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect re-bind of ACP session {AcpSessionId} for agent {AgentId} failed (attempt {Attempt}/{MaxAttempts})")]
    partial void LogAcpRebindFailed(Exception ex, string agentId, string acpSessionId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnect re-bind of ACP session {AcpSessionId} for agent {AgentId} failed after {MaxAttempts} attempts — unregistering the binding so it isn't replayed forever")]
    partial void LogAcpRebindGivingUp(string agentId, string acpSessionId, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnect re-bind of ACP session {AcpSessionId} for agent {AgentId} was rejected by the server (stale/foreign/conflicting binding) — standing down and unregistering the binding")]
    partial void LogAcpRebindRejected(string agentId, string acpSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping reconnect re-bind of ACP session {AcpSessionId} for agent {AgentId} — the agent is no longer hosted as live; unregistering the stale binding")]
    partial void LogAcpRebindSkippedNotLive(string agentId, string acpSessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to post agent run event, retrying in {Delay}s")]
    partial void LogEventPostFailed(Exception ex, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to serialize {EventType} for agent {AgentId}, dropping event")]
    partial void LogEventSerializationFailed(Exception ex, string eventType, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update repo paths on server")]
    partial void LogRepoPathUpdateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to report resolved model for agent {AgentId} (server may not support it)")]
    partial void LogReportResolvedModelFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to send ACP auto-approval audit for agent {AgentId} (server may not support it)")]
    partial void LogNotifyAcpAutoApprovalFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hub method '{Method}' handler threw — invocation dropped")]
    partial void LogHandlerThrew(Exception ex, string method);

    /// <summary>
    /// Wraps each typed <c>On(...)</c> handler so an exception inside a handler
    /// is logged with the hub method name instead of bubbling up into SignalR's
    /// generic dispatch error path. Pairs with the <c>ConfigureLogging</c> wiring
    /// above, which surfaces the framework's own binding/parsing errors. Together
    /// they make sure no class of "daemon silently dropped a server invocation"
    /// is invisible in the logs.
    /// </summary>
    async Task SafeInvoke(string method, Func<Task?> handler) {
        try {
            var task = handler();

            if (task is not null) await task;
        } catch (Exception ex) {
            LogHandlerThrew(ex, method);
        }
    }

    class RetryPolicy : IRetryPolicy {
        static readonly TimeSpan[] Delays = [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);

            return Delays[index]; // Keeps retrying at 30s intervals after initial backoff
        }
    }
}

/// <summary>
/// The args needed to re-invoke <c>AcpSessionStarted</c> after a reconnect. Purely in-memory —
/// never (de)serialized — captured once
/// at initial-bind time via <see cref="ServerConnection.RegisterAcpBinding"/> and replayed
/// idempotently by <see cref="ServerConnection.ReBindAcpSessionsAsync"/> on every reconnect until
/// <see cref="ServerConnection.UnregisterAcpBinding"/> removes it. Top-level (not nested in
/// <see cref="ServerConnection"/>) purely so tests can reference it without qualification.
/// </summary>
internal sealed record AcpBindInfo(
    string                               Vendor,
    string                               AcpSessionId,
    string?                              Cwd,
    string?                              Model,
    IReadOnlyDictionary<string, string>? Metadata = null
);

/// <summary>
/// thrown by <see cref="ServerConnection.AcpSessionStartedAsync"/> when the server declines
/// an INITIAL bind (a stale/foreign/conflicting binding, surfaced as <see cref="AcpBindOutcome.Rejected"/>).
/// A LOCAL exception, never a <c>HubException</c>, so the launch path's existing "bind threw ⇒ do not
/// register a binding / do not start a forwarder" catch handles it unchanged. The reconnect path
/// (<see cref="ServerConnection.ReBindAcpSessionsAsync"/>) reads the outcome directly and never throws
/// this.
/// </summary>
internal sealed class AcpBindRejectedException(string agentId, string acpSessionId)
    : Exception($"Server rejected the ACP bind for agent {agentId} (session {acpSessionId})") {
    public string AgentId      { get; } = agentId;
    public string AcpSessionId { get; } = acpSessionId;
}

/// <summary>
/// §2.7 B6: the daemon-local result of <see cref="ServerConnection.ReportParticipantParkedAsync"/> —
/// purely in-memory, never (de)serialized. Widens the server's two-value wire outcome
/// (<see cref="ParkParticipantOutcome"/>) with a third, daemon-only case the wire never encodes:
/// <see cref="Ambiguous"/> covers any transport error, timeout, <c>HubException</c>, or otherwise
/// unmapped result — i.e. no definite reply was received. <see cref="Parked"/> and
/// <see cref="Rejected"/> mirror the like-named <see cref="ParkParticipantOutcome"/> members
/// one-for-one. The arm-A park state machine (<c>AgentOrchestrator.ParkReviewerAsync</c>) branches
/// on this: <c>Parked</c> completes the park teardown (session-end suppressed), <c>Rejected</c>
/// falls back to the normal end path, and <c>Ambiguous</c> leaves everything intact for the next
/// reap sweep to retry — never a destructive teardown on an uncertain reply.
/// </summary>
internal enum ParkAck { Parked, Rejected, Ambiguous }
