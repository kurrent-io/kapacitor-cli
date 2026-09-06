using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;
using Capacitor.Cli.Daemon.Pty.Windows;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Capacitor.Cli.Daemon.Harness.Pi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon;

public static partial class DaemonRunner {
    // Reflection-resolved once: the assembly attribute is fixed for the process lifetime, and
    // DaemonStatusIpc's Snapshot() now calls ResolveDaemonVersion() on every status push.
    static string? _cachedDaemonVersion;

    /// <summary>
    /// Daemon binary version from <c>[AssemblyInformationalVersion]</c>,
    /// baked at build time by MSBuild's git-info integration. Surfaces on
    /// <c>DaemonConnect</c> so the server's <c>Daemon connected:</c> log
    /// line and <c>DaemonInfo</c> can show "v0.4.11+sha.abc1234".
    /// </summary>
    public static string ResolveDaemonVersion() =>
        _cachedDaemonVersion ??= typeof(DaemonRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(string[] args) {
        string?    logFile     = null;
        string?    stderrFile  = null;
        LogLevel?  logLevelArg = null;
        // The one place this process reads KCAP_DAEMONS_DIR. From here the directory travels as an
        // explicit dependency — on the config, and as a DI singleton — never re-read from the
        // environment, so a descendant process can't be handed a different one by accident.
        var paths = DaemonStore.FromEnvironment();

        // Same for KCAP_CONFIG_DIR: read once here, then passed.
        var configRoot = ConfigRoot.FromEnvironment();

        // And the same for the home directory, which the two above fall back to.
        var userHome = UserHome.FromEnvironment();

        // OriginalArgs is captured for self-respawn (detached restart-after-update) and to detect
        // the successor's --await-lock handoff flag. Paths is set here, in the initializer, so the
        // config is never observably path-less.
        var config = new DaemonConfig {
            Store        = paths,
            ConfigRoot   = configRoot,
            Home         = userHome,
            WorktreeRoot = Path.Combine(userHome.Path, ".capacitor", "worktrees"),
            OriginalArgs = args,
        };
        var awaitLock = args.Contains("--await-lock");

        // Boot-local carriers: read off ambient env and IMMEDIATELY remove them, before anything
        // else reads env or the host builder exists, so no descendant process (PTY-spawned agent,
        // ACP child, a self-respawned successor's own inheritance from OUR ambient env) can ever
        // observe them except through the explicit re-injection paths that need them.
        CaptureBootCarriers(config, Environment.GetEnvironmentVariable, k => Environment.SetEnvironmentVariable(k, null));

        // Resolve server URL + active profile. The CLI does this in its own
        // Program.cs, but the daemon is a separate process so its statics start
        // empty. Skips repo discovery (the daemon isn't bound to a working dir);
        // honors --server-url, KCAP_URL, KCAP_PROFILE.
        var profiles = await AppConfig.ResolveActiveProfile(args, configRoot);
        config.Profiles  = profiles;
        config.ServerUrl = profiles.Resolution.ServerUrl ?? "";

        // CLI arg overrides for daemon-specific settings — parse before host builder.
        // --name is consumed below by DaemonNameResolver (shared with the CLI
        // supervisor), so we don't parse it here.
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--log-file": logFile = args[++i]; break;
                case "--stderr-file": stderrFile = args[++i]; break;
                case "--log-level": logLevelArg = ParseLogLevel(args[++i]); break;
                case "--max-agents" when int.TryParse(args[i + 1], out var n) && n >= 1:
                    config.MaxConcurrentAgents = n;
                    i++;

                    break;
                case "--max-agents":
                    await Console.Error.WriteLineAsync($"Invalid --max-agents value: {args[i + 1]} (must be a positive integer)");

                    return 1;
            }
        }

        // Phase B (D3): reviewer lifetime/idle backstop overrides from env (seconds; 0 disables).
        config.ReviewerMaxLifetime = ParseSecondsEnv("KCAP_REVIEWER_MAX_LIFETIME", config.ReviewerMaxLifetime);
        config.ReviewerIdleTimeout = ParseSecondsEnv("KCAP_REVIEWER_IDLE_TIMEOUT", config.ReviewerIdleTimeout);
        config.ReviewerResumableIdleTimeout = ParseSecondsEnv("KCAP_REVIEWER_RESUMABLE_IDLE_TIMEOUT", config.ReviewerResumableIdleTimeout);
        // Task 12: daemon-local held-turn wedge ceiling override (seconds; 0 disables), independent
        // of the server's own Flows:TurnWedgeCeilingSeconds — see DaemonConfig.ReviewerTurnWedgeCeiling.
        config.ReviewerTurnWedgeCeiling = ParseSecondsEnv("KCAP_REVIEWER_TURN_WEDGE_CEILING", config.ReviewerTurnWedgeCeiling);

        // reopen fds 1/2 onto the capture file BEFORE building the host,
        // so even a crash during construction lands somewhere. On the detached
        // launch path the CLI closed our std pipes; without this a runtime/native
        // fatal message would go to a broken pipe and vanish. No-op under launchd
        // (StandardErrorPath) and foreground (no --stderr-file passed).
        if (StdErrCapture.ResolveTarget(stderrFile) is { } capturePath) {
            StdErrCapture.Apply(capturePath);
        }

        // Strip our custom args before passing to host builder
        var hostArgs = Array.Empty<string>();
        var builder  = Host.CreateApplicationBuilder(hostArgs);

        // Configure logging: file when detached, console when foreground.
        // Minimum level defaults to Information; raise verbosity for transport
        // diagnostics (e.g. per-tick DaemonPing RTT, which logs at Debug) via
        // --log-level or KCAP_DAEMON_LOG_LEVEL=debug. The --log-level arg wins
        // over the env var when both are set.
        var minLevel = logLevelArg
                    ?? ParseLogLevel(Environment.GetEnvironmentVariable("KCAP_DAEMON_LOG_LEVEL"))
                    ?? LogLevel.Information;

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(minLevel);

        if (logFile is not null) {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(logFile, minLevel: minLevel));
        } else {
            builder.Logging.AddSimpleConsole(opts => {
                    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                    opts.UseUtcTimestamp = false;
                }
            );
        }

        // Daemon settings from the active profile, with env overrides
        var profileDaemon = profiles.Resolution.Profile?.Daemon;

        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;

        if (!string.IsNullOrEmpty(profileDaemon?.ClaudePath))
            config.ClaudePath = profileDaemon.ClaudePath;

        if (!string.IsNullOrEmpty(profileDaemon?.CodexPath))
            config.CodexPath = profileDaemon.CodexPath;

        if (Environment.GetEnvironmentVariable("KCAP_MAX_AGENTS") is { } maxAgents) {
            if (int.TryParse(maxAgents, out var n) && n >= 1)
                config.MaxConcurrentAgents = n;
            else
                await Console.Error.WriteLineAsync($"Warning: ignoring invalid KCAP_MAX_AGENTS={maxAgents}");
        }

        if (Environment.GetEnvironmentVariable("KCAP_CLAUDE_PATH") is { Length: > 0 } envClaudePath)
            config.ClaudePath = envClaudePath;

        if (Environment.GetEnvironmentVariable("KCAP_CODEX_PATH") is { Length: > 0 } envCodexPath)
            config.CodexPath = envCodexPath;

        if (Environment.GetEnvironmentVariable("KCAP_CODEX_APPSERVER_APPROVAL_TIMEOUT_SECONDS") is { Length: > 0 } envApprovalTimeout
         && int.TryParse(envApprovalTimeout, out var approvalTimeoutSeconds) && approvalTimeoutSeconds > 0)
            config.CodexAppServerApprovalTimeoutSeconds = approvalTimeoutSeconds;

        Harness.Codex.CodexTransportDecision.BindFromEnvironment(config, Environment.GetEnvironmentVariable);

        // One resolved fact for both the launch router and the certification advertisement.
        config.CodexAppServerActive = Harness.Codex.CodexTransportDecision.ResolveActive(
            config.CodexTransport, () => ProbeCliVersion(config.CodexPath));

        if (Environment.GetEnvironmentVariable("KCAP_CURSOR_PATH") is { Length: > 0 } envCursorPath)
            config.CursorPath = envCursorPath;

        if (Environment.GetEnvironmentVariable("KCAP_CURSOR_MODEL") is { Length: > 0 } envCursorModel)
            config.CursorModel = envCursorModel;

        if (Environment.GetEnvironmentVariable("KCAP_COPILOT_PATH") is { Length: > 0 } envCopilotPath)
            config.CopilotPath = envCopilotPath;

        if (Environment.GetEnvironmentVariable("KCAP_KIRO_PATH") is { Length: > 0 } envKiroPath)
            config.KiroPath = envKiroPath;

        if (Environment.GetEnvironmentVariable("KCAP_KIRO_MODEL") is { Length: > 0 } envKiroModel)
            config.KiroModel = envKiroModel;

        if (Environment.GetEnvironmentVariable("KCAP_OPENCODE_PATH") is { Length: > 0 } envOpenCodePath)
            config.OpenCodePath = envOpenCodePath;

        if (Environment.GetEnvironmentVariable("KCAP_OPENCODE_MODEL") is { Length: > 0 } envOpenCodeModel)
            config.OpenCodeModel = envOpenCodeModel;

        if (Environment.GetEnvironmentVariable("KCAP_PI_PATH") is { Length: > 0 } envPiPath)
            config.PiPath = envPiPath;

        if (Environment.GetEnvironmentVariable("KCAP_PI_MODEL") is { Length: > 0 } envPiModel)
            config.PiModel = envPiModel;

        if (Environment.GetEnvironmentVariable("KCAP_GEMINI_PATH") is { Length: > 0 } envGeminiPath)
            config.GeminiPath = envGeminiPath;

        if (Environment.GetEnvironmentVariable("KCAP_ANTIGRAVITY_PATH") is { Length: > 0 } agyPath)
            config.AntigravityPath = agyPath;
        if (Environment.GetEnvironmentVariable("KCAP_ANTIGRAVITY_MODEL") is { Length: > 0 } agyModel)
            config.AntigravityModel = agyModel;
        // The per-vendor reviewer switches. These are OPT-OUT now: unset means enabled, matching
        // Claude/Codex/Cursor/Copilot, which have never been gated. See ParseConsentFlag for the full
        // argument and its three precisions — in short, the reviewer vendor is a caller-chosen
        // parameter, so wherever an ungated vendor is also advertised, gating these four did not widen
        // the capability class a requester could reach, and only taxed the honest path.
        // Iterates the SHARED registry, not a local list. A vendor listed here but missing from
        // GatedReviewers.All would be absent from every service unit (ServiceEnvironment derives the
        // allowlist from the same rows), leaving a reviewer that cannot be turned off on the supported
        // install path — so there is one list, and ConsentApplier below fails the boot rather than
        // silently skipping a row it has no accessor for.
        foreach (var reviewer in GatedReviewers.All) {
            var variable = reviewer.EnableEnvVar;
            var apply    = ConsentApplier(config, reviewer.Vendor);
            var raw      = Environment.GetEnvironmentVariable(variable);
            apply(ParseConsentFlag(raw));

            // Warned for EVERY variable, from the same loop that applies it, so a mangled value can
            // never be silently swallowed on one vendor because a hand-maintained warning list missed
            // it. A value we cannot read DISABLES (see ParseConsentFlag) — the operator's evident
            // intent, since unset already enables — and the symptom of the opposite mistake is a
            // reviewer that simply stops being advertised, which is why it says so out loud.
            if (DescribeUnparseableConsent(variable, raw) is { } warning)
                await Console.Error.WriteLineAsync(warning);
        }

        config.DebugFrames = ParseDebugFramesFlag(Environment.GetEnvironmentVariable("KCAP_ACP_DEBUG_FRAMES"));

        config.AcpReconnectEnabled = ParseAcpReconnectFlag(Environment.GetEnvironmentVariable("KCAP_ACP_RECONNECT"));

        // Shared name resolution with the CLI supervisor — the CLI's
        // DaemonCommands and the daemon binary must agree on the name so
        // the per-name PID file the CLI inspects is the one the daemon
        // writes via DaemonLock. Resolve throws on `--name <missing value>`
        // / `--name <next-is-flag>`; refuse to start in that case rather
        // than silently defaulting to the OS username.
        try {
            config.Name = DaemonNameResolver.Resolve(args, profileDaemon?.Name);
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var errors = config.Validate();

        if (errors.Count > 0) {
            await Console.Error.WriteLineAsync("Configuration errors:");

            foreach (var e in errors) {
                await Console.Error.WriteLineAsync($"  - {e}");
            }

            return 1;
        }

        // Resolve our version before acquiring the lock so DaemonLock can stamp
        // it into the freely-readable <name>.version marker that `kcap daemon
        // status` reads to report the running daemon's version.
        config.Version = ResolveDaemonVersion();

        // Acquire the per-name flock that prevents another daemon from
        // running under the same name on this machine. The lock content is
        // a fresh instance id that we'll also send over DaemonConnect so
        // the server can refuse a second daemon claiming the same
        // (owner, name) slot.
        var daemonLock = awaitLock
            ? DaemonLock.TryAcquire(config.Store, config.Name, TimeSpan.FromSeconds(5), config.Version)
            : DaemonLock.TryAcquire(config.Store, config.Name, config.Version);

        if (daemonLock is null) {
            await Console.Error.WriteLineAsync(
                $"Another kcap-daemon is already running under the name '{config.Name}' on this machine. "
                + $"Either stop it (`kcap daemon stop --name {config.Name}`) or start this one with a different `--name`."
            );

            return LockRefusalExit(IsSupervised(config.Name));
        }

        config.InstanceId = daemonLock.InstanceId;

        // Phase B2-b (sequenced-settlement design §4.2.3): the durable per-daemon state root — used by
        // the pre-host boot-check block immediately below AND (further down) by the coverage journal /
        // reviewer-home sweeps. Computed once here so every consumer agrees on the exact same directory.
        var coverageStateDir = config.Store.StateDirectory(config.Name);

        // Best-effort, never throws: on a brand-new daemon name nothing has created this directory yet
        // (LaunchConsentStore's ctor — the previous sole creator — only runs below, and not at all on
        // the expectation-mismatch arm), so without this an expectation-mismatch refusal on a fresh
        // name would have nowhere to write its marker. Same fail-soft posture as BootRefusalMarker.TryWrite
        // itself — a failure here just means that call's own containment swallows the write too.
        try { Directory.CreateDirectory(coverageStateDir); } catch { /* best-effort */ }

        // Pre-host boot checks — see RunBootChecksAsync. Both refusal arms return BEFORE the host is
        // built, before any ServerConnection/token use of any kind, so a misdirected or un-consented
        // daemon never gets far enough to touch the network or spawn anything.
        if (await RunBootChecksAsync(config) is { } bootCheckExit) return bootCheckExit;

        // Phase B2-b (sequenced-settlement design): pin the per-boot epoch here, before any service is
        // built, so the epoch advertised on DaemonConnect and the orchestrator's own _daemonEpoch (which
        // falls back to config.DaemonEpoch) are provably the same value.
        config.DaemonEpoch ??= Guid.NewGuid().ToString("N");

        // Phase B2-b (sequenced-settlement design §4.2.3): fold the durable coverage boot-chain BEFORE any
        // Connect/spawn. this_epoch_contained is true only where OS containment leaves NO recordless
        // survivor class (the Windows Job Object). Fail-closed inside RecordBoot. NullLogger is acceptable
        // this early — the host's logging pipeline isn't built yet.
        SeedReviewerFloors(coverageStateDir, config);

        // Recovers reviewer homes left by a SIGKILLed predecessor. Runs unconditionally: a daemon
        // whose operator has since disabled the reviewer still owns whatever its last incarnation
        // left behind, and those directories hold review context.
        // A real logger, not NullLogger: Delete warns precisely so a retained transcript-bearing home
        // is never silent, and passing NullLogger would defeat the diagnostic this cleanup exists to
        // emit. The host's logging is not built yet at this point, so this writes to stderr like the
        // seeding block above.
        KiroReviewerHome.SweepStale(
            coverageStateDir, config.DaemonEpoch ?? "unpinned", new ConsoleErrorLogger());

        // Same contract for OpenCode's isolated config dir: unconditional, because a daemon whose
        // operator has since disabled the reviewer still owns what its last incarnation left behind.
        OpenCodeReviewerConfigDir.SweepStale(
            coverageStateDir, config.DaemonEpoch ?? "unpinned", new ConsoleErrorLogger());

        // The Antigravity reviewer disposes its own home on the normal path (the runtime's onDisposed
        // hook), so this sweep covers exactly ONE case: a predecessor that was SIGKILLed and never ran
        // it. Unconditional for the same reason as the line above — a daemon whose operator has since
        // disabled the reviewer still owns the transcript-bearing directories its last incarnation
        // left behind.
        AntigravityReviewerHome.SweepStale(
            coverageStateDir, config.DaemonEpoch ?? "unpinned", new ConsoleErrorLogger());

        config.RecordlessSurvivorsImpossible = new CoverageJournal(coverageStateDir, NullLogger.Instance)
            .RecordBoot(daemonLock.InstanceId, daemonLock.PriorInstanceId,
                priorLockReadFailed: daemonLock.PriorLockIndeterminate, thisEpochContained: OperatingSystem.IsWindows());

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(configRoot);
        builder.Services.AddSingleton(config.Profiles);
        builder.Services.AddSingleton(userHome);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(daemonLock);
        builder.Services.AddDaemonHttp(configRoot, config);
        builder.Services.AddSingleton<ServerConnection>();

        // The owner consent gate — policy store + append-only decision log share the
        // per-daemon state root with the coverage journal above; the prompter is null until
        // the broker below registers itself (a Prompt-default policy then denies with "prompt_no_ui").
        // TimeProvider.System drives the gate's monotonic deadline discipline (spec §3.2) — a
        // real singleton in production, swapped for a FakeTimeProvider in tests.
        builder.Services.AddSingleton(TimeProvider.System);
        // A FRESH instance with the real ILogger<LaunchConsentStore> — deliberately NOT the throwaway
        // NullLogger store the boot-check block above used for classification. Reusing that instance
        // here would silence LaunchConsentStore's diagnostics (Load()-time corruption warnings, and the
        // Persist() failure path LaunchConsentIpc doesn't independently log) for the daemon's entire
        // lifetime. The extra file read at boot is cheap; a permanently silenced diagnostic is not.
        builder.Services.AddSingleton(sp => new LaunchConsentStore(
            coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentStore>>()));
        builder.Services.AddSingleton(sp => new LaunchConsentDecisionLog(
            coverageStateDir, sp.GetRequiredService<ILogger<LaunchConsentDecisionLog>>()));
        builder.Services.AddSingleton(sp => new LaunchConsentGate(
            sp.GetRequiredService<LaunchConsentStore>(),
            sp.GetRequiredService<LaunchConsentDecisionLog>(),
            sp.GetService<ILaunchConsentPrompter>(),   // null until the broker below registers itself
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LaunchConsentGate>>()));
        builder.Services.AddSingleton<LaunchConsentBroker>();
        builder.Services.AddSingleton<ILaunchConsentPrompter>(sp => sp.GetRequiredService<LaunchConsentBroker>());
        // Local-socket consent frames — the same broker instance the gate above prompts through, so
        // a subscriber connected via ConsentSubscribe sees the gate's own pending requests.
        builder.Services.AddSingleton<LaunchConsentIpc>();

        builder.Services.AddSingleton<PermissionPromptBroker>();
        builder.Services.AddSingleton<PermissionIpc>();
        builder.Services.AddSingleton(sp => new PermissionDecisionLog(
            coverageStateDir, sp.GetRequiredService<ILogger<PermissionDecisionLog>>()));
        builder.Services.AddSingleton<PolicySnapshotProvider>();

        // The DaemonStatus push: ONE notifier singleton shared by ServerConnection (pulses on hub
        // state transitions) and AgentOrchestrator (pulses on agent mutation) via their optional
        // ctor params, so a StatusSubscribe waiter sees both kinds of change. This depends on the
        // ServerConnection/AgentOrchestrator registrations below/above staying bare AddSingleton<T>()
        // (no factory delegate) — DI only injects a registered service into an optional trailing
        // parameter when it resolves the constructor itself. A factory delegate that constructs
        // either type without passing this notifier would silently sever status pushes with no
        // failing test; DaemonStatusWiringTests pins this mechanism.
        builder.Services.AddSingleton<DaemonStatusNotifier>();
        builder.Services.AddSingleton<DaemonStatusIpc>();

        // Local HTTP bridge that fronts the server's permission flow. Registered as a
        // singleton so AgentOrchestrator can read its bound URL at agent-spawn time, AND
        // as a hosted service so its IHostedService lifecycle starts the listener before
        // any agent is spawned.
        builder.Services.AddSingleton<LocalPermissionBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPermissionBridge>());

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, WinPtyProcessFactory>();
        } else {
            // L1-managed(a)/(b): one dedicated, daemon-lifetime native thread runs EVERY Unix
            // pty_spawn call (never a thread-pool thread — see UnixSpawnerThread's own doc
            // comment for why). Registered with no factory delegate so DI resolves it via its
            // parameterless constructor, which already starts the thread. host.StopAsync() only
            // stops registered IHostedServices — it does NOT dispose the ServiceProvider, so a
            // plain AddSingleton<T>() IDisposable like this one is NOT disposed by StopAsync
            // alone. Disposal (and therefore the thread's retirement, via UnixSpawnerThread.Dispose
            // completing its queue) happens when disposing the host disposes the ServiceProvider —
            // which is why the shutdown sequence below awaits host.StopAsync() and THEN
            // DisposeHostAsync(host) on every exit path, after every hosted agent is already
            // stopped, so normal shutdown retires the thread only once nothing needs it.
            builder.Services.AddSingleton<UnixSpawnerThread>();
            builder.Services.AddSingleton<IPtyProcessFactory, UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();
        builder.Services.AddSingleton<RepoMatcher>();

        builder.Services.AddHttpClient("Attachments", client => client.BaseAddress = new Uri(config.ServerUrl));

        builder.Services.AddSingleton<IHostedAgentLauncher, ClaudeLauncher>();
        builder.Services.AddSingleton<IHostedAgentLauncher, CodexLauncher>();

        builder.Services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(sp =>
            sp.GetServices<IHostedAgentLauncher>().ToDictionary(l => l.Vendor)
        );

        // Runtime-selection seam: one IHostedAgentRuntimeFactory per vendor.
        // AgentOrchestrator selects by vendor from the resulting dictionary instead of driving
        // Prepare/BuildArgs/Spawn inline. PtyHostedAgentRuntimeFactory wraps each registered PTY
        // launcher (Claude, Codex); AcpHostedAgentRuntimeFactory speaks ACP JSON-RPC over stdio for
        // Cursor (no IHostedAgentLauncher — Cursor never went through the PTY launcher contract).
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new PtyHostedAgentRuntimeFactory(
                sp.GetServices<IHostedAgentLauncher>().SingleOrDefault(l => l.Vendor == "claude")
                    ?? throw new InvalidOperationException("No IHostedAgentLauncher registered for vendor 'claude'"),
                sp.GetRequiredService<IPtyProcessFactory>(),
                sp.GetRequiredService<ILogger<PtyHostedAgentRuntimeFactory>>()
            )
        );
        // Codex is the ONE vendor with two transports. The composite factory routes review-flow
        // launches to codex app-server when this daemon resolved it active (config.CodexAppServerActive),
        // and delegates everything else — every interactive launch, and all launches under the PTY
        // default — to the wrapped PTY factory, byte-identically to before.
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp => {
            var codexLauncher = sp.GetServices<IHostedAgentLauncher>().SingleOrDefault(l => l.Vendor == "codex")
                    ?? throw new InvalidOperationException("No IHostedAgentLauncher registered for vendor 'codex'");
            var pty = new PtyHostedAgentRuntimeFactory(
                codexLauncher,
                sp.GetRequiredService<IPtyProcessFactory>(),
                sp.GetRequiredService<ILogger<PtyHostedAgentRuntimeFactory>>());
            return new Harness.Codex.CodexHostedAgentRuntimeFactory(
                (Harness.Codex.CodexLauncher) codexLauncher, pty,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                connection: sp.GetRequiredService<ServerConnection>()); // §2.3 interactive approvals
        });
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Cursor,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>() // spec-review Finding 4 — real production wiring
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Copilot,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Kiro,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.Gemini,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AcpHostedAgentRuntimeFactory(
                AcpVendorDescriptors.OpenCode,
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<ServerConnection>()
            )
        );

        // Not an ACP factory: agy speaks NDJSON over one child process PER TURN, so it carries its own
        // runtime rather than AcpHostedAgentRuntimeFactory's persistent-child shape. Takes the logger
        // FACTORY, like the ACP registrations beside it — it builds a logger for the runtime and one
        // per turn process, not a single typed logger.
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new AntigravityHostedAgentRuntimeFactory(
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>()
            )
        );

        // Not an ACP factory either: pi speaks its own LF-framed JSONL-RPC over one LONG-LIVED
        // process for the whole hosted session (see IPiRpcProcess), unlike Antigravity's
        // exec-per-turn shape above. PR-1 only — interactive hosting; the reviewer lane
        // (SupportsUnattended) is not implemented yet.
        builder.Services.AddSingleton<IHostedAgentRuntimeFactory>(sp =>
            new PiRpcHostedAgentRuntimeFactory(
                sp.GetRequiredService<DaemonConfig>(),
                sp.GetRequiredService<ILoggerFactory>()
            )
        );

        builder.Services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>>(sp =>
            sp.GetServices<IHostedAgentRuntimeFactory>().ToDictionary(f => f.Vendor)
        );

        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<EvalContextCache>();
        builder.Services.AddSingleton<EvalRunner>();

        // Restart-after-update: a coordinator polls the on-disk binary and, when idle,
        // applies a queued restart via the strategy chosen by supervision detection.
        // Strategies are concrete singletons (AOT-safe; same pattern as the services
        // above) and only the selected one is ever constructed.
        builder.Services.AddSingleton<RestartState>();
        builder.Services.AddSingleton<SupervisedExitStrategy>();
        builder.Services.AddSingleton<DetachedRespawnStrategy>();
        builder.Services.AddSingleton<ForegroundNoopStrategy>();
        builder.Services.AddSingleton<IRestartStrategy>(sp => {
            var cfg        = sp.GetRequiredService<DaemonConfig>();
            var hasLogFile = cfg.OriginalArgs.Contains("--log-file");
            var mode       = SupervisionDetector.DetectCurrent(DaemonStore.Sanitize(cfg.Name), hasLogFile);

            return mode switch {
                SupervisionMode.Supervised => sp.GetRequiredService<SupervisedExitStrategy>(),
                SupervisionMode.Detached   => sp.GetRequiredService<DetachedRespawnStrategy>(),
                _                          => sp.GetRequiredService<ForegroundNoopStrategy>(),
            };
        });
        builder.Services.AddSingleton<RestartCoordinator>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RestartCoordinator>());

        // Local control socket: lets `kcap agent start`/`attach`/`ls`/`stop` drive daemon-hosted
        // agents from the user's own terminal (AI local-attach Phase 1).
        builder.Services.AddSingleton<LocalControlServer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalControlServer>());

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kcap.Daemon");

        // Set by the supervised restart strategy so we exit non-zero for a supervisor relaunch.
        var restartState = host.Services.GetRequiredService<RestartState>();

        // probe each registered runtime factory's CLI binary
        // so the DaemonConnect payload only advertises vendors this daemon can actually spawn —
        // now via IHostedAgentRuntimeFactory.IsAvailable() rather than IHostedAgentLauncher, so
        // Cursor (which has no IHostedAgentLauncher) is advertised once cursor-agent is installed.
        // The launch dialog filters its vendor selector by this list. Ordered alphabetically so the
        // wire format is stable across restarts.
        var runtimeFactories = host.Services.GetServices<IHostedAgentRuntimeFactory>().ToArray();

        config.SupportedVendors = runtimeFactories
            .Where(f => f.IsAvailable())
            .Select(f => f.Vendor)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        // ACP permission-preset support: the installed hostable vendors that route permissions
        // through the ACP bridge (Acp.AcpPermissionPresets.RoutedVendors), computed INDEPENDENTLY of
        // the unattended classification below — a preset is an interactive-launch feature, not a
        // reviewer one. The server refuses a preset toward a daemon that does not advertise the vendor.
        config.AcpPresetVendors = config.SupportedVendors
            .Where(Acp.AcpPermissionPresets.RoutedVendors.Contains)
            .ToArray();

        config.PermissionModeVendors = Harness.Claude.ClaudePermissionModePolicy.AdvertisedVendors(config.SupportedVendors);

        // Reviewer vendor override support: a strict subset of SupportedVendors — only vendors that
        // can also run fully unattended without routing an interaction to a human. The server gates
        // a review-flow vendor override on this list rather than SupportedVendors alone, so a vendor
        // that's merely installed but has no unattended launcher is never offered as an override
        // target.
        //
        // Classified ONCE and reused below: a gated reviewer's classification spawns the vendor binary
        // to read its version, so recomputing per consumer would probe it three times per startup.
        var unattendedStatuses = ClassifyUnattendedVendors(runtimeFactories);

        config.UnattendedVendors = AdvertisedUnattendedVendors(unattendedStatuses);
        config.UnattendedVendorCapabilities =
            ComputeUnattendedVendorCapabilities(runtimeFactories, config, config.UnattendedVendors);

        // Which build of each unattended vendor was installed when this daemon started. Recorded at
        // startup (like the Cursor-unavailable warning below) rather than per launch, and reported
        // without any comparison against a validated-build record.
        LogUnattendedVendorIdentities(logger, config.UnattendedVendorCapabilities);

        // The counterpart of the line above. A withheld vendor used to vanish from advertisement in
        // silence: the refusal text existed, but only the launch path threw it — and advertisement is
        // what stops that launch being attempted, so the explanation could never be produced.
        foreach (var withheld in unattendedStatuses.Where(s => s.WithheldReason is not null))
            LogUnattendedVendorWithheld(logger, withheld.Vendor, withheld.WithheldReason!);

        // IsAvailable()==false silently omits cursor from SupportedVendors above — correct
        // behavior (the launch dialog just won't offer Cursor), but gave operators no clue WHY. One
        // Warning at startup (not per-launch) so a missing/misconfigured cursor-agent install is
        // visible in the daemon's own logs instead of only showing up as an absent vendor downstream.
        if (ShouldWarnCursorUnavailable(runtimeFactories))
            LogCursorUnavailable(logger, config.CursorPath);

        // KCAP_ACP_DEBUG_FRAMES is a static, daemon-wide setting read once above — warn once here,
        // at the point it takes effect, rather than lazily the first time some ACP call site actually
        // logs full content (which could fire dozens of times across one busy session).
        if (config.DebugFrames)
            LogAcpDebugFramesEnabled(logger);

        LogDaemonStarting(logger, config.Name, config.ServerUrl);

        // if the previous daemon under this name vanished without
        // releasing its lock, it was SIGKILLed (macOS jetsam/OOM, `kill -9`),
        // lost power, or crashed hard — none of which the dying process can
        // log. Emit a breadcrumb now so the otherwise-silent death is on the
        // record. This is safe even for a `--await-lock` restart-after-update
        // handoff: DaemonLock.Dispose now deletes the outgoing daemon's PID
        // file *before* releasing the flock, so a clean handoff leaves nothing
        // for us to find — a leftover PID file here is a real hard death (e.g.
        // the outgoing daemon was OOM-killed mid-handoff) and worth recording.
        if (daemonLock.PriorExitWasUnclean) {
            LogPriorUncleanExit(logger, config.Name, daemonLock.PriorHolderPid?.ToString() ?? "unknown");
        }

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();

        // Death-rattle instrumentation: without these, a daemon dying mid-run
        // (signal, OOM, unobserved-task FailFast, native crash) leaves no trace
        // in the log and we can't tell a clean shutdown from a kill. Each hook
        // best-effort logs *why* the process is going away before the runtime
        // tears down the logging pipeline. Lifetime is captured so SIGHUP
        // (terminal closed) can be turned into a cooperative StopApplication
        // — without that, the host's finally-block cleanup never runs.
        RegisterDeathRattle(logger, lifetime);

        // Lifetime-driven log lines — pair with the AppDomain/signal hooks so
        // we can distinguish a cooperative StopApplication (e.g. NameInUse,
        // Ctrl+C consumed by ConsoleLifetime) from an outside-the-runtime kill.
        lifetime.ApplicationStopping.Register(() => {
            DeathRattle("Lifetime: ApplicationStopping fired");
            LogLifetimeStopping(logger);
        });
        lifetime.ApplicationStopped.Register(() => {
            DeathRattle("Lifetime: ApplicationStopped fired");
            LogLifetimeStopped(logger);
        });

        // if the server rejects our DaemonConnect because another
        // live daemon owns the (owner, name) slot, signal host shutdown
        // and remember to return exit code 3 instead of 0. Subscribe
        // before ConnectAsync so the initial-connect path is covered.
        var nameInUse = false;

        connection.OnNameInUse += _ => {
            nameInUse = true;
            lifetime.StopApplication();
        };

        // Resolved INSIDE the guarded startup below (after host start); nullable so the teardown
        // can run null-safely when SIGTERM lands before the orchestrator was ever resolved.
        AgentOrchestrator? orchestrator = null;

        // EVERY exit path from host start onward — success, the nameInUse early-exit, cooperative
        // shutdown cancelling an in-flight startup await (including host.StartAsync itself: a
        // hosted service still starting when SIGTERM lands surfaces the cancellation as
        // TaskCanceledException out of StartAsync — dotnet/runtime#111013 behavior), OR any
        // startup exception — MUST run the unified teardown so DisposeHostAsync(host) fires and
        // the process can actually exit instead of hanging or aborting: once the orchestrator is
        // resolved, the Unix IPtyProcessFactory has pulled in UnixSpawnerThread, whose foreground
        // (non-background) OS thread parks forever until the ServiceProvider is disposed.
        // Structural fix; backed by DaemonHostDisposalTests.
        var startupExit = await RunGuardedStartupAsync(
            logger,
            lifetime.ApplicationStopping,
            startupAndWait: async () => {
                // Start hosted services (LocalPermissionBridge in particular) BEFORE the SignalR
                // connection comes up. Otherwise an early LaunchAgent message can arrive while
                // BaseUrl is still null and the spawned Claude falls back to the HTTPS path —
                // exactly what this bridge is meant to avoid.
                await host.StartAsync(lifetime.ApplicationStopping);

                // Phase B (D4 §6.4(3)): resolve the orchestrator (which wires OnLaunchAgent +
                // GetLiveAgents in its ctor) and reap any hosted-agent children that outlived a
                // PRIOR daemon run — all BEFORE ConnectAsync advertises this daemon and the server
                // can dispatch launches. Doing it after connect would let new work be admitted
                // while old capacity is still being reclaimed (those survivors aren't yet in
                // EffectiveCount), and would leave a window where a launch races an unwired
                // handler. Under the daemon lock; best-effort (swallows its own faults).
                orchestrator = host.Services.GetRequiredService<AgentOrchestrator>();
                await orchestrator.ReapOrphansOnceAsync();

                try {
                    await connection.ConnectAsync(lifetime.ApplicationStopping);
                } catch (Exception ex) when (nameInUse) {
                    // ConnectAsync's initial-connect path threw because of NameInUse. OnNameInUse
                    // already fired and set our flag; the host hasn't started its main loop yet, so
                    // just exit — the unified teardown below disposes daemonLock, orchestrator,
                    // connection, and the host (retiring the spawner thread). Supervised → 0: same
                    // launchd-respin rationale as the lock-refusal exit above (a manual daemon keeps 3).
                    _ = ex;

                    return NameInUseExit(IsSupervised(config.Name));
                }

                var worktreeManager = host.Services.GetRequiredService<WorktreeManager>();
                await worktreeManager.CleanupOrphanedAsync();

                // Instantiate EvalRunner so it wires the per-phase eval handlers
                // (PrepareEval / RunQuestion / FinalizeEval / CancelEval) on the
                // ServerConnection. It's stateless beyond the handler assignment —
                // cached context lives in EvalContextCache — so no disposal dance.
                _ = host.Services.GetRequiredService<EvalRunner>();

                // Wait without passing the lifetime token: WaitForShutdownAsync(token) treats
                // token cancellation as a fault, so a normal Ctrl+C / lifetime.StopApplication()
                // would surface as OperationCanceledException. The no-arg overload listens
                // internally for ApplicationStopping and returns cleanly.
                await host.WaitForShutdownAsync();
                LogWaitForShutdownReturned(logger);

                return null;
            },
            teardown: () => RunDaemonTeardownAsync(logger, daemonLock, orchestrator, connection, host));

        if (startupExit is { } earlyExit) return earlyExit;

        // Restart-after-update (supervised): exit non-zero so the unit's
        // failure-restart policy relaunches the now-updated binary.
        if (restartState.SupervisedRestart) return ExitCodes.RestartRequested;

        // if the daemon was shut down because the server told us
        // our (owner, name) slot is contested mid-run (heartbeat-triggered
        // path), exit with code 3 so wrappers (systemd, npm, CI) can tell
        // this apart from a normal Ctrl+C exit. Deliberately NOT decision-6-aware (unlike the
        // initial-connect NameInUseExit above): this is a mid-run contest, not the initial connect,
        // and the one resulting respawn's own fresh initial connect is what settles it — bounding
        // the loop either way without needing this exit to also be supervised-conditional.
        return nameInUse ? 3 : 0;
    }

    /// <summary>
    /// Pre-host boot checks, callable without a live host. Order matters: the server-expectation
    /// check runs FIRST (a server the operator didn't expect must never even reach consent
    /// classification), then the consent-seed directive. A directive is ABSENT only when
    /// <paramref name="config"/>'s
    /// <see cref="DaemonConfig.ConsentSeedDirective"/> is null — an empty string is a deliberate
    /// refusal under the exact-value contract, and <c>BootSeed("")</c> already classifies it
    /// <see cref="SeedOutcome.RefusedInvalidDirective"/>, so activating on "is not null" reaches
    /// that path rather than silently treating an empty directive as no directive at all.
    /// <see cref="LaunchConsentStore"/>'s own constructor can throw (e.g. a file sitting where the
    /// state directory belongs) — that must land as the same coded refusal as any other
    /// unwritable-state-dir condition, never an uncoded crash that respins under KeepAlive. A
    /// passing boot (both checks green, or no directive at all) clears any refusal marker a PRIOR
    /// failed attempt left behind — otherwise `kcap daemon status` (or a desktop supervisor) would
    /// keep reporting a refusal that no longer applies. Returns the process exit code on refusal,
    /// or null to proceed to host construction.
    /// </summary>
    /// <summary>The config's view of a refusal — the only place DaemonConfig meets the marker.</summary>
    static void WriteRefusal(DaemonStore store, DaemonConfig config, string token) =>
        BootRefusalMarker.TryWrite(
            store, config.Name, token, config.ExpectedServerUrl, config.ServerUrl,
            config.InstanceId, config.BootAttemptId);

    internal static async Task<int?> RunBootChecksAsync(DaemonConfig config) {
        var store      = config.Store;
        var daemonName = config.Name;
        if (!ExpectationSatisfied(config.ExpectedServerUrl, config.ServerUrl)) {
            WriteRefusal(store, config, "server_expectation_mismatch");
            await Console.Error.WriteLineAsync("kcap-daemon: refusing to start: server_expectation_mismatch");

            return 0;
        }

        if (config.ConsentSeedDirective is not null) {
            // A THROWAWAY store, used only for this boot-time classification — deliberately NOT the
            // instance handed to DI below (see the DI registration's own comment for why: reusing it
            // would silence LaunchConsentStore's diagnostics, via a NullLogger, for the daemon's
            // entire lifetime).
            SeedResult seed;
            try {
                seed = new LaunchConsentStore(store.StateDirectory(daemonName), NullLogger.Instance).BootSeed(config.ConsentSeedDirective);
            } catch {
                WriteRefusal(store, config, "consent_seed_unwritable");
                await Console.Error.WriteLineAsync("kcap-daemon: refusing to start: consent_seed_unwritable");

                return 0;
            }

            if (seed.Outcome is SeedOutcome.RefusedInvalidDirective or SeedOutcome.RefusedUnwritable) {
                WriteRefusal(store, config, seed.RefusalToken!);
                await Console.Error.WriteLineAsync($"kcap-daemon: refusing to start: {seed.RefusalToken}");

                return 0;
            }
        }

        BootRefusalMarker.TryDelete(store, daemonName);   // passing boot clears leftovers (hygiene)

        return null;
    }

    /// <summary>
    /// Disposes the host so its ServiceProvider — and therefore every registered
    /// <see cref="IDisposable"/> singleton (e.g. <see cref="UnixSpawnerThread"/>) — is released.
    /// <see cref="IHost"/> only surfaces <see cref="IDisposable"/>, but the concrete host built by
    /// <see cref="HostApplicationBuilder"/> also implements <see cref="IAsyncDisposable"/>; prefer
    /// that when present so any <c>IAsyncDisposable</c> singletons get their async path too, and
    /// fall back to the synchronous <see cref="IDisposable.Dispose"/> otherwise. Must be called
    /// AFTER <c>host.StopAsync()</c> on every shutdown path — <c>StopAsync()</c> only stops
    /// <see cref="IHostedService"/>s, it never disposes the ServiceProvider. <c>internal</c> (not
    /// <c>private</c>) so the regression test can drive the exact StopAsync-then-dispose sequence
    /// against a minimal host and prove the ordering is what actually retires a DI-owned
    /// <see cref="UnixSpawnerThread"/>.
    /// </summary>
    internal static async Task DisposeHostAsync(IHost host) {
        if (host is IAsyncDisposable asyncDisposableHost) {
            await asyncDisposableHost.DisposeAsync();
        } else {
            host.Dispose();
        }
    }

    /// <summary>
    /// The guarded startup skeleton around the daemon's whole start→wait lifecycle: runs
    /// <paramref name="startupAndWait"/>, converts a cancellation observed while cooperative
    /// shutdown was already requested into a clean exit (SIGTERM/Ctrl+C during host start or the
    /// initial connect-retry loop otherwise escapes <c>Main</c> and aborts the NativeAOT process
    /// — SIGABRT + an .ips crash report), and ALWAYS runs <paramref name="teardown"/>, logging a
    /// summary line on partial teardown failure. A cancellation with no shutdown requested still
    /// propagates (fail-loud preserved). Returns <paramref name="startupAndWait"/>'s early-exit
    /// code, or null to fall through to the caller's normal exit-code selection — deliberately
    /// unchanged by teardown failures: the process was already exiting (normally 0), and turning
    /// a contained cleanup fault into a non-zero exit would make supervisors (systemd/launchd
    /// restart-on-failure) relaunch a daemon the user just stopped. <c>internal</c> so
    /// <c>DaemonHostDisposalTests</c> can pin the cancellation-during-host-start contract.
    /// </summary>
    internal static async Task<int?> RunGuardedStartupAsync(
            ILogger           logger,
            CancellationToken stopping,
            Func<Task<int?>>  startupAndWait,
            Func<Task<bool>>  teardown) {
        try {
            return await startupAndWait();
        } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
            LogStartupCancelledByShutdown(logger);

            return null;
        } finally {
            LogEnteringCleanup(logger);

            if (!await teardown()) LogTeardownPartialFailure(logger);

            LogCleanupCompleted(logger);
        }
    }

    /// <summary>
    /// The daemon's production teardown: the six real steps, in order, run through
    /// <see cref="RunTeardownAsync"/>. One shared step list (see
    /// <see cref="BuildDaemonTeardownSteps"/>) so the sequencing tests drive EXACTLY what
    /// <c>RunAsync</c> executes — a divergence (reordered/removed step) fails the tests.
    /// </summary>
    internal static Task<bool> RunDaemonTeardownAsync(
            ILogger           logger,
            IDisposable       daemonLock,
            IAsyncDisposable? orchestrator,
            IAsyncDisposable  connection,
            IHost             host)
        => RunTeardownAsync(logger, BuildDaemonTeardownSteps(daemonLock, orchestrator, connection, host));

    /// <summary>
    /// Builds the production teardown step list. Do NOT reorder: the explicit-dispose-before-
    /// host-stop sequence is load-bearing (spawner-thread retirement, pinned by
    /// <c>DaemonHostDisposalTests</c>). <paramref name="orchestrator"/> is nullable because
    /// cooperative shutdown can land before it was ever resolved (mid <c>host.StartAsync</c>);
    /// host disposal still releases whatever WAS partially resolved.
    ///
    /// LIMITATION: <see cref="RunTeardownAsync"/> contains a throw from each STEP, but it cannot
    /// make the DI container's own dispose walk (inside <see cref="DisposeHostAsync"/>) resilient
    /// — a tracked singleton whose Dispose/DisposeAsync throws still aborts the container's
    /// INTERNAL walk partway, silently skipping the remaining singletons. The helper prevents the
    /// process abort, not partial DI disposal; each disposable must still contain its own
    /// failures (ServerConnection and AgentOrchestrator now do). The one skip that could strand
    /// the process — <see cref="UnixSpawnerThread"/>, whose Dispose retires the FOREGROUND thread
    /// that otherwise keeps a "shut down" daemon alive forever — is closed by the dedicated
    /// spawner-retire step below, which runs BEFORE host disposal.
    ///
    /// Structural double-teardown sweep (explicit dispose here + a framework-driven dispose of
    /// the SAME instance) and why each shape is safe today:
    ///   • ServerConnection / AgentOrchestrator / UnixSpawnerThread: run-once dispose guards.
    ///   • DaemonLock: disposed explicitly here AND DI-tracked; its Dispose is idempotent
    ///     (releases the handle once, subsequent calls no-op).
    ///   • RestartCoordinator / LocalControlServer / LocalPermissionBridge: registered through
    ///     TWO singleton descriptors each (<c>AddSingleton&lt;T&gt;()</c> + an
    ///     <c>AddHostedService</c> factory resolving the same instance). Microsoft DI tracks
    ///     disposables per DESCRIPTOR and does not de-duplicate by reference, so the container's
    ///     dispose walk visits each such instance TWICE (LocalPermissionBridge's own
    ///     <c>_disposed</c> doc documents exactly this). LocalPermissionBridge carries its own
    ///     run-once guard; RestartCoordinator and LocalControlServer inherit
    ///     <c>BackgroundService.Dispose</c>, which is safe to re-enter under the current
    ///     Microsoft.Extensions.Hosting behavior (it only cancels its stopping CTS) — a
    ///     framework-version assumption.
    ///
    /// host-stop/host-dispose stay split because <c>host.StopAsync()</c> only STOPS
    /// IHostedServices — it does NOT dispose the ServiceProvider, so a plain
    /// <c>AddSingleton&lt;T&gt;()</c> IDisposable is never released by StopAsync alone; disposing
    /// the host is what disposes the ServiceProvider and therefore every registered singleton.
    /// </summary>
    internal static (string Name, Func<ValueTask> Action)[] BuildDaemonTeardownSteps(
            IDisposable       daemonLock,
            IAsyncDisposable? orchestrator,
            IAsyncDisposable  connection,
            IHost             host) => [
        ("daemon-lock",       () => { daemonLock.Dispose(); return ValueTask.CompletedTask; }),
        ("orchestrator",      () => orchestrator?.DisposeAsync() ?? ValueTask.CompletedTask),
        ("server-connection", connection.DisposeAsync),
        // Retire the spawner thread EXPLICITLY, before host disposal, so even a DI walk aborted
        // partway by some other singleton's throwing Dispose cannot strand its foreground thread
        // (and with it the whole process). Idempotent — the host-dispose walk disposing the same
        // instance again is a no-op. GetService instantiates a never-yet-created singleton
        // (thread start + immediate join — cheap and deterministic) rather than probing; returns
        // null on Windows, where the type is not registered.
        ("spawner-retire",    () => {
            host.Services.GetService<UnixSpawnerThread>()?.Dispose();

            return ValueTask.CompletedTask;
        }),
        ("host-stop",         async () => await host.StopAsync()),
        ("host-dispose",      async () => await DisposeHostAsync(host))
    ];

    /// <summary>
    /// Teardown coordinator for the shutdown finally-block: runs each named step in the given
    /// order, logging and containing any throw so every later step still runs and nothing can
    /// escape into the NativeAOT unhandled path (which calls <c>abort()</c>). Returns whether
    /// ALL steps succeeded so the caller can log a summary on partial failure. <c>internal</c>
    /// so <c>DaemonHostDisposalTests</c> can pin the order/containment contract directly.
    /// </summary>
    internal static async Task<bool> RunTeardownAsync(
            ILogger logger, IReadOnlyList<(string Name, Func<ValueTask> Action)> steps) {
        var allSucceeded = true;

        foreach (var (name, action) in steps) {
            try {
                await action();
            } catch (Exception ex) {
                allSucceeded = false;
                LogTeardownStepFailed(logger, ex, name);
            }
        }

        return allSucceeded;
    }

    /// <summary>
    /// Parses a daemon log-level string (case-insensitive, e.g. "debug",
    /// "trace", "warning") to a <see cref="LogLevel"/>. Returns null for a
    /// null/blank/unrecognised value so callers can fall through to the next
    /// source or the Information default rather than silently logging nothing.
    /// </summary>
    internal static LogLevel? ParseLogLevel(string? value) => value?.Trim().ToLowerInvariant() switch {
        "trace" or "trce"              => LogLevel.Trace,
        "debug" or "dbug"              => LogLevel.Debug,
        "information" or "info"        => LogLevel.Information,
        "warning" or "warn"            => LogLevel.Warning,
        "error" or "fail"              => LogLevel.Error,
        "critical" or "crit"           => LogLevel.Critical,
        "none"                         => LogLevel.None,
        _                              => null
    };

    /// <summary>
    /// The three boot-local carrier env vars: set by whatever spawned this daemon
    /// (`kcap daemon start`, a service unit, or a self-respawned predecessor), read exactly once at
    /// boot by <see cref="CaptureBootCarriers"/>, and never left in the ambient process environment
    /// afterward. Names are shared with <c>DetachedRespawnStrategy</c>'s re-injection and the PTY/ACP
    /// scrub lists so every consumer agrees on the literal strings.
    /// </summary>
    internal static class BootCarriers {
        public const string Seed    = "KCAP_CONSENT_SEED_DEFAULT";
        public const string Expect  = "KCAP_EXPECT_SERVER_URL";
        public const string Attempt = "KCAP_BOOT_ATTEMPT";
        public static readonly string[] All = [Seed, Expect, Attempt];
    }

    /// <summary>
    /// Reads <see cref="BootCarriers.Seed"/>/<see cref="BootCarriers.Expect"/>/<see cref="BootCarriers.Attempt"/>
    /// off ambient env into <paramref name="config"/> and immediately clears them via
    /// <paramref name="clear"/> — called from <see cref="RunAsync"/> right after
    /// <c>config.OriginalArgs = args;</c>, BEFORE anything else reads env or the host builder
    /// exists, so no descendant process can observe them by inheritance. <paramref name="get"/>/
    /// <paramref name="clear"/> are injected so this is testable without touching real process env.
    /// </summary>
    internal static void CaptureBootCarriers(DaemonConfig config, Func<string, string?> get, Action<string> clear) {
        config.ConsentSeedDirective = get(BootCarriers.Seed);
        config.ExpectedServerUrl    = get(BootCarriers.Expect);
        config.BootAttemptId        = get(BootCarriers.Attempt);
        clear(BootCarriers.Seed);
        clear(BootCarriers.Expect);
        clear(BootCarriers.Attempt);
    }

    /// <summary>
    /// Does the resolved <see cref="DaemonConfig.ServerUrl"/> match what the
    /// launcher told this boot to expect (<see cref="DaemonConfig.ExpectedServerUrl"/>, carried in
    /// via <c>KCAP_EXPECT_SERVER_URL</c>)? Only a genuinely NULL expectation is absence and
    /// trivially satisfied — this check exists to catch a daemon that resolved a DIFFERENT server
    /// than the one it was launched to point at, not to require an expectation be set. A
    /// present-but-empty (or otherwise non-canonicalizable) expectation is a deliberate value, same
    /// exact-value contract as the consent-seed directive, so it must MISMATCH rather than be
    /// silently skipped. Otherwise compared through <see cref="ServerIdentity.Matches"/> —
    /// scheme/host normalized, default ports converged, path case preserved.
    /// </summary>
    internal static bool ExpectationSatisfied(string? expected, string resolved) =>
        expected is null || (!string.IsNullOrEmpty(expected) && ServerIdentity.Matches(expected, resolved));

    /// <summary>Phase B (D3): parse a seconds-valued env var into a <see cref="TimeSpan"/>
    /// (<c>0</c> → <see cref="TimeSpan.Zero"/>, which disables the bound). Unset/blank/invalid/negative
    /// → the supplied <paramref name="fallback"/>.</summary>
    internal static TimeSpan ParseSecondsEnv(string name, TimeSpan fallback) {
        var raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, out var secs) && secs >= 0 ? TimeSpan.FromSeconds(secs) : fallback;
    }

    /// <summary>
    /// Parses <c>KCAP_ACP_DEBUG_FRAMES</c> ("1"/"true", case-insensitive, are On; anything else —
    /// including unset/blank — is Off) into <see cref="DaemonConfig.DebugFrames"/>. Pulled out as a
    /// pure predicate (mirroring <see cref="ParseLogLevel"/>) so it's testable without an env var.
    /// </summary>
    internal static bool ParseDebugFramesFlag(string? value) =>
        value?.Trim() is { } v && (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the <c>KCAP_ACP_RECONNECT</c> kill switch into
    /// <see cref="DaemonConfig.AcpReconnectEnabled"/>. Opposite default polarity from
    /// <see cref="ParseDebugFramesFlag"/>, deliberately: reconnect is ON unless explicitly
    /// disabled — only <c>0</c>/<c>false</c> (case-insensitive) turn it off; anything else,
    /// including unset/blank, leaves it on.
    /// </summary>
    internal static bool ParseAcpReconnectFlag(string? value) =>
        value?.Trim() is not { } v || !(v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether this process is <see cref="SupervisionMode.Supervised"/> for <paramref name="resolvedName"/>
    /// — delegates to <see cref="SupervisionDetector.DetectCurrent"/> rather than re-comparing
    /// <c>KCAP_DAEMON_SUPERVISED</c> itself, so systemd/launchd detection stays in lockstep with the one
    /// place that owns it. <c>hasLogFile</c> only distinguishes Detached from Foreground in the
    /// NOT-supervised fallback, so its value here is irrelevant. Reads the environment directly (no DI),
    /// so it's callable before the host builds — needed at the lock-acquire exit below, which returns
    /// long before <c>Host.CreateApplicationBuilder</c> finishes.
    /// </summary>
    internal static bool IsSupervised(string resolvedName) =>
        SupervisionDetector.DetectCurrent(DaemonStore.Sanitize(resolvedName), hasLogFile: false)
            == SupervisionMode.Supervised;

    /// <summary>
    /// Exit code for the local name-lock refusal (another kcap-daemon already holds
    /// <paramref name="supervised"/>'s name). Supervised → 0: under launchd <c>KeepAlive
    /// SuccessfulExit=false</c> a non-zero exit respins the unit forever against a name it can never
    /// win, and a deliberate refusal isn't a crash. A manual daemon keeps 2 for scripts.
    /// </summary>
    internal static int LockRefusalExit(bool supervised) => supervised ? 0 : 2;

    /// <summary>Same rationale as <see cref="LockRefusalExit"/>, for the server's <c>NameInUse</c>
    /// rejection (manual daemons keep 3).</summary>
    internal static int NameInUseExit(bool supervised) => supervised ? 0 : 3;

    /// <summary>
    /// Whether a reviewer vendor is permitted on this daemon. **Absent means ENABLED** — this is an
    /// opt-OUT, and it used to be an opt-in. Same polarity as
    /// <see cref="ParseAcpReconnectFlag"/> now, where it used to be deliberately the opposite.
    ///
    /// <para><b>Why the default flipped.</b> The reviewer vendor is a caller-chosen parameter, and
    /// Claude, Codex, Cursor and Copilot have never been gated at all — each running with FULL tool
    /// access (shell and write) in the same worktree. <b>On any daemon that also ADVERTISES one of
    /// those</b> — the overwhelmingly common case — the gate did not widen the CAPABILITY CLASS a
    /// requester could reach: one it blocked from a gated vendor simply asked for an ungated one with
    /// more capability, while the honest path paid a service-unit edit and a restart. Two of the four
    /// gated vendors (Kiro, OpenCode) also run READ-ONLY reviewers, so the strictest policy sat on the
    /// least dangerous configurations.</para>
    ///
    /// <para><b>Three precisions, because earlier revisions of this comment overstated all three</b> —
    /// review caught each. (1) The predicate is ADVERTISED, not installed: a vendor must also be
    /// certified and above its version floor, so a daemon with an uncertified Claude advertises no
    /// ungated vendor either. (2) "Did not widen the capability class" is the true claim, not "excluded
    /// nobody": even where an ungated vendor exists, this flip adds execution paths with different
    /// binaries, different vendor-side permission models and different CREDENTIALS — a Gemini reviewer
    /// burns the operator's Gemini credentials, which is not a subset of "Claude was already
    /// available". (3) On a daemon that advertises ONLY a gated vendor — installed for hosted work, the
    /// ordinary reason — these variables were the sole separation between the hosted role and the
    /// unattended-reviewer role, and the flip genuinely widens what a non-operator requester can cause
    /// to run with no human in the loop.</para>
    ///
    /// <para><b>What actually compensates, stated without inflation.</b> Exactly two things, and the
    /// floor in the DEFAULT configuration is worth saying plainly: on a fresh daemon that has not been
    /// configured further, the only thing between a non-operator requester and an unattended gated-vendor
    /// launch is that the vendor be installed, certified and above its version floor.
    /// <list type="number">
    /// <item>The operator keeps an explicit opt-out — and it is REACHABLE on the supported install path:
    /// <c>ServiceEnvironment</c> carries these four variables into a service unit on
    /// every platform, DERIVED from the affirmable-reviewer registry so a new vendor cannot be omitted.
    /// The caveat is timing, not reachability: a supervised daemon's environment is FROZEN at
    /// <c>kcap daemon service install</c>, so an opt-out set afterwards needs a reinstall to take
    /// effect.</item>
    /// <item><c>kcap daemon consent</c> is the only control that scopes unattended launches as such — but
    /// it DEFAULTS TO ALLOW, so it compensates only for an operator who has configured it, not for the
    /// average daemon.</item>
    /// </list></para>
    ///
    /// <para><b>The version floor is deliberately NOT a third entry</b>, though it is easy to count as
    /// one. It constrains which BUILD runs, never whether a non-operator can cause an unattended launch;
    /// it is seeded automatically at first boot precisely so it never blocks a first launch. It is real
    /// protection against a different failure — several of these vendors' containment is environment-based
    /// and fails SILENTLY when a build stops honouring it, which is what
    /// <c>kcap daemon reviewer affirm</c> exists to remediate. Remediation, not permission.</para>
    ///
    /// <para><b>Unset means enabled; a value we cannot read means DISABLED.</b></para>
    ///
    /// <para>Those two are not in tension, and the asymmetry is the point. Since unset already enables,
    /// the only reason to set one of these variables at all is to turn a reviewer OFF — enabling needs no
    /// variable. So an unrecognised value is not an ambiguous input, it is a FAILED ATTEMPT TO SAY OFF,
    /// and honouring the evident intent means failing closed. Review caught this: an earlier revision
    /// failed open here, reasoning from the general "a typo must not take a feature offline" rule, which
    /// does not transfer to a setting whose only use is to disable.</para>
    ///
    /// <para>The direction is also cheap in the wrong case: a typo'd ENABLE attempt (<c>=y</c>,
    /// <c>=enabled</c>) lands on the pre-change behaviour, which is the safe side, and the operator still
    /// gets the warning either way.</para>
    ///
    /// <para><b>That justification does not generalise, so do not reuse this from anywhere else.</b> It
    /// rests on TWO facts specific to the four reviewer variables: unset already means enabled, and
    /// disabling is therefore the only possible intent behind setting one. A setting where unset means
    /// disabled, or where fail-open was chosen deliberately, needs its own parse and its own reasoning —
    /// inheriting this one by name would silently import an argument that does not hold. The four
    /// <c>KCAP_*_UNATTENDED_REVIEWER</c> variables in <see cref="RunAsync"/>'s apply loop are the only
    /// production callers.</para>
    /// </summary>
    internal static bool ParseConsentFlag(string? value) => ReviewerConsent.IsEnabled(value);

    /// <summary>
    /// Where a gated reviewer's opt-out lands on <see cref="DaemonConfig"/>.
    ///
    /// <para><b>Throws for an unknown vendor rather than returning null or a no-op.</b> The one caller
    /// iterates <see cref="GatedReviewers.All"/>, so an unmapped row means someone added a reviewer to
    /// the registry — which puts its variable into every service unit and into
    /// <c>daemon reviewer affirm</c>'s usage text — without wiring the daemon to actually read it. The
    /// operator would then set the documented variable and watch it do nothing: an opt-out that appears
    /// to exist and does not. Refusing to boot turns that into a failure the author hits immediately,
    /// and <c>Consent_applier_covers_every_gated_reviewer</c> hits it in CI first.</para>
    ///
    /// <para>A string switch has no compiler exhaustiveness, which is exactly why the throw is here and
    /// not a discard arm.</para>
    /// </summary>
    internal static Action<bool> ConsentApplier(DaemonConfig config, string vendor) => vendor switch {
        "gemini"      => v => config.GeminiUnattendedReviewerEnabled      = v,
        "kiro"        => v => config.KiroUnattendedReviewerEnabled        = v,
        "opencode"    => v => config.OpenCodeUnattendedReviewerEnabled    = v,
        "antigravity" => v => config.AntigravityUnattendedReviewerEnabled = v,
        _ => throw new NotSupportedException(
            $"Gated reviewer '{vendor}' is in GatedReviewers.All but no DaemonConfig flag is wired to "
          + $"its opt-out switch, so setting {GatedReviewers.Resolve(vendor)?.EnableEnvVar ?? "it"} "
          + "would silently do nothing. Add the accessor here.")
    };

    /// <summary>Thin forwarders to <see cref="ReviewerConsent"/> in Core, kept so the daemon's own call
    /// sites and tests read the parser under a daemon-local name. The rules — and the reason unset must
    /// never reach the <c>?? false</c> — live on the Core type, which the CLI's install-output notice
    /// reads too so the two cannot describe a captured value differently.</summary>
    internal static bool? RecogniseConsent(string? value) => ReviewerConsent.Recognise(value);

    internal static string? DescribeUnparseableConsent(string variable, string? value) =>
        ReviewerConsent.DescribeUnparseable(variable, value);

    /// <summary>
    /// True when a "cursor" <see cref="IHostedAgentRuntimeFactory"/> is registered but
    /// reports itself unavailable — the signal for <see cref="RunAsync"/>'s one-time startup
    /// Warning. Pulled out as a pure predicate over the factory list (rather than inlined in
    /// <see cref="RunAsync"/>) so it's testable without spinning up the whole DI host that method
    /// builds.
    /// </summary>
    internal static bool ShouldWarnCursorUnavailable(IEnumerable<IHostedAgentRuntimeFactory> factories) =>
        factories.FirstOrDefault(f => f.Vendor == "cursor") is { } cursorFactory && !cursorFactory.IsAvailable();

    /// <summary>
    /// Every gated vendor's version floor, seeded in one place — the whole block <see cref="RunAsync"/>
    /// runs before anything else touches a reviewer record.
    ///
    /// <para><b>The asymmetry between the three is the point, and it is why this is a method rather
    /// than three lines inline.</b> Nothing invokes <see cref="RunAsync"/> from a test — it builds and
    /// runs the entire DI host — so seeding stated only there is asserted by nothing, and a test that
    /// calls <see cref="SeedVersionFloor"/> by hand pins the directory shape while leaving the
    /// CONDITION each vendor is seeded under unpinned. Driving this method instead makes the
    /// difference between the vendors the thing under test.</para>
    ///
    /// <para>Kiro, Gemini and OpenCode seed whenever the vendor is not explicitly DISABLED — which,
    /// since the switch defaults to enabled, means on essentially every boot. That is what keeps the
    /// floor from becoming the opt-in gate under a new name: with no record the ladder answers
    /// <c>version_no_minimum</c>, a refusal only <c>kcap daemon reviewer affirm</c> can clear. Skipping
    /// a disabled vendor is the one remaining condition, and it exists so an installed-but-wedged
    /// binary cannot stall a boot for a feature the operator switched off.</para>
    ///
    /// <para>Antigravity seeds with NO condition at all, because its floor is NOT reviewer-only: it
    /// gates hosted <c>agy</c> launches too (the isolated <c>HOME</c> they rely on is the containment it
    /// protects), and those have always shipped on. Conditioning it on the reviewer switch would leave a
    /// daemon that disabled the REVIEWER refusing hosted launches as <c>version_no_minimum</c> forever.
    /// Installing <c>agy</c> IS the event here; the resolver's
    /// null-for-a-missing-binary answer is what keeps this a no-op otherwise, at the cost of one
    /// bounded <c>agy --version</c> on the first boot that finds no record, never again.</para>
    /// </summary>
    internal static void SeedReviewerFloors(string stateDir, DaemonConfig config) {
        SeedReviewerAffirmation(
            stateDir, AcpVendorDescriptors.Kiro.Vendor,
            config.KiroUnattendedReviewerEnabled, config.KiroPath);

        SeedReviewerAffirmation(
            stateDir, AcpVendorDescriptors.Gemini.Vendor,
            config.GeminiUnattendedReviewerEnabled, config.GeminiPath);

        SeedReviewerAffirmation(
            stateDir, AcpVendorDescriptors.OpenCode.Vendor,
            config.OpenCodeUnattendedReviewerEnabled, config.OpenCodePath);

        SeedVersionFloor(stateDir, AntigravityVendor, config.AntigravityPath);
    }

    /// <summary>
    /// Records the installed build as affirmed the first time a vendor's reviewer is enabled.
    ///
    /// <para>Keyed on the record's ABSENCE AS A FILE, not on "Affirmed is null". The store reports null
    /// for a corrupt or unreadable record too, and seeding on that would (a) re-affirm whatever is
    /// installed after the record was removed post-upgrade, silently clearing the gate, and (b) attempt
    /// a write that a directory at the pathname makes throw — bricking a boot on a file that is
    /// supposed to fail closed, never fatally.</para>
    /// </summary>
    /// <param name="enabled">The vendor's opt-out state. Only skips the probe for a vendor the operator
    /// has EXPLICITLY disabled — which is now the rare case, since the switch defaults to enabled.
    /// Seeding on the ordinary path is what keeps the version floor from blocking a first launch: with
    /// no record, the gate answers <c>version_no_minimum</c>, and that is a refusal only
    /// <c>kcap daemon reviewer affirm</c> can clear. A floor is meant to exclude a build found to be
    /// bad, not to be an opt-in gate wearing a different hat.</param>
    internal static void SeedReviewerAffirmation(
            string stateDir, string vendor, bool enabled, string binaryPath) {
        if (!enabled) return;

        SeedVersionFloor(stateDir, vendor, binaryPath);
    }

    /// <summary>
    /// The same seeding with NO consent condition — for a vendor whose recorded floor gates more than
    /// its reviewer.
    ///
    /// <para>Deliberately a separate name rather than <c>SeedReviewerAffirmation(…, enabled: true)</c>:
    /// a literal <c>true</c> sitting between two <c>config.XUnattendedReviewerEnabled</c> siblings
    /// reads as an oversight, and "tidying" it back to the flag would silently reinstate the very gate
    /// the caller exists to avoid. With no boolean to flip, the asymmetry has to be read.</para>
    /// </summary>
    internal static void SeedVersionFloor(string stateDir, string vendor, string binaryPath) {
        try {
            if (!ReviewerVersionStore.RecordExists(stateDir, vendor)
             && VendorVersionResolver.Resolve(binaryPath) is { Length: > 0 } installed) {
                new ReviewerVersionStore(stateDir, vendor).Affirm(installed);

                // Printed because a floor is affirmed ONCE and never re-probed, so a wrong number is
                // pinned permanently and gates silently in both directions — too high refuses a working
                // reviewer, too low under-gates. Resolve validates SHAPE (a dotted-numeric token), which
                // rules out banners, `unknown` and localised errors, but NOT a version-shaped token that
                // is not the installed build: an update nag ("0.11.14 -> 0.12.0"), a runtime line
                // ("Node.js v22.1.0") or a date stamp ("2026.08.08") all qualify and can precede the
                // real version. This line is what makes such a floor diagnosable at all.
                //
                // All four vendors' `--version` output was measured on 2026-08-08 and every one yields
                // the right token under first-qualifying-token extraction:
                //     kiro-cli 2.16.0   ("kiro-cli" has no dot, so the version wins)
                //     gemini   0.54.0   (bare)
                //     opencode 1.18.9   (bare)
                //     agy      1.1.11   (bare)
                // That is an observation of four builds on one host, not a guarantee: any of them may
                // add a nag line or a runtime banner in a later release, which is precisely the drift
                // this log line exists to make visible.
                Console.Error.WriteLine(
                    $"{vendor} reviewer version floor seeded at {installed} (from '{binaryPath} --version'). "
                  + $"Correct it with `kcap daemon reviewer affirm --vendor {vendor}` if that is not the "
                  + "installed build.");
            }
        } catch (Exception ex) {
            // The gate fails closed on its own if this never ran; a boot must not die for it.
            Console.Error.WriteLine($"{vendor} reviewer version seeding skipped: {ex.Message}");
        }
    }

    /// <summary>One installed vendor's unattended classification at daemon startup.</summary>
    /// <param name="Vendor">The vendor token.</param>
    /// <param name="Advertised">Whether it is offered as an unattended reviewer host.</param>
    /// <param name="WithheldReason">Why it is not offered, when a daemon-local gate is what refuses
    /// it. Null for an advertised vendor AND for one that never offered unattended hosting — only a
    /// refusal an operator can act on is worth a Warning.</param>
    internal readonly record struct UnattendedVendorStatus(
        string Vendor, bool Advertised, string? WithheldReason);

    /// <summary>
    /// Classifies every INSTALLED factory's unattended support, asking each exactly once (see
    /// <see cref="IHostedAgentRuntimeFactory.DescribeUnattendedSupport"/> — the gated reviewers spawn
    /// their vendor binary to answer). Pure over the factory list, same reasoning as
    /// <see cref="ShouldWarnCursorUnavailable"/>, so both the advertisement and its diagnostic are
    /// testable without spinning up the whole DI host <see cref="RunAsync"/> builds.
    /// </summary>
    internal static IReadOnlyList<UnattendedVendorStatus> ClassifyUnattendedVendors(
            IEnumerable<IHostedAgentRuntimeFactory> factories) =>
        factories
            .Where(f => f.IsAvailable())
            .Select(f => {
                var support = f.DescribeUnattendedSupport();

                return new UnattendedVendorStatus(f.Vendor, support.Supported, support.WithheldReason);
            })
            .OrderBy(s => s.Vendor, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The advertised subset of a <see cref="ClassifyUnattendedVendors"/> result.</summary>
    internal static string[] AdvertisedUnattendedVendors(IEnumerable<UnattendedVendorStatus> statuses) =>
        statuses.Where(s => s.Advertised).Select(s => s.Vendor).ToArray();

    /// <summary>
    /// Vendor tokens this daemon can run fully unattended — a strict subset of
    /// <c>SupportedVendors</c> (installed) further filtered by
    /// <see cref="IHostedAgentRuntimeFactory.SupportsUnattended"/>. Kept as the convenience shape for
    /// callers that need only the list, and expressed THROUGH
    /// <see cref="ClassifyUnattendedVendors"/> so there is one rule rather than two that have to
    /// agree. Prefer classifying once where the reasons are also wanted — this overload re-probes.
    /// </summary>
    /// <param name="config">Accepted, and required rather than defaulted, so a caller cannot silently
    /// classify against a different daemon's configuration than the one whose factories it passed —
    /// the gate ladders themselves read it through the factories.</param>
    internal static string[] ComputeUnattendedVendors(
            IEnumerable<IHostedAgentRuntimeFactory> factories, DaemonConfig config) =>
        AdvertisedUnattendedVendors(ClassifyUnattendedVendors(factories));

    internal const string ClaudeLauncherPolicyVersion = "claude-unattended-v1";
    internal const string CursorLauncherPolicyVersion = "cursor-unattended-v4";
    internal const string CodexLauncherPolicyVersion = "codex-unattended-v1";
    internal const string CodexAppServerLauncherPolicyVersion = "codex-appserver-unattended-v2";
    internal const string CopilotLauncherPolicyVersion = "copilot-unattended-v1";
    internal const string AntigravityLauncherPolicyVersion = "antigravity-unattended-v1";
    internal const string OpenCodeLauncherPolicyVersion = "opencode-unattended-v1";

    /// <summary>The one vendor token this daemon knows agy by. Never <c>agy</c> — that is a binary
    /// name, and the server routes on the vendor.</summary>
    internal const string AntigravityVendor = "antigravity";

    /// <param name="advertised">The already-classified advertised vendors, when the caller has them.
    /// Passing them avoids re-running a classification that spawns vendor binaries; omitting them
    /// recomputes, which is what the tests and any other caller want.</param>
    internal static IReadOnlyList<UnattendedVendorCapability> ComputeUnattendedVendorCapabilities(
            IEnumerable<IHostedAgentRuntimeFactory> factories, DaemonConfig config,
            IEnumerable<string>? advertised = null) {
        var unattended = advertised?.ToArray() ?? ComputeUnattendedVendors(factories, config);
        var capabilities = new List<UnattendedVendorCapability>();
        foreach (var vendor in unattended) {
            var factory = factories.First(f => string.Equals(f.Vendor, vendor, StringComparison.Ordinal));
            // The binary comes from the factory that would launch it, never from a vendor-keyed map
            // here: a map is a second answer about one build, and the vendor it forgets is advertised
            // as "CLI version unknown" while the admission gate resolves it fine — the wrong answer
            // reaching the operator's log and the server. Only the policy version is genuinely per
            // vendor, and a missing arm there is a wrong string rather than a silent nothing.
            var cliPath = factory.CliPath;
            var policyVersion = vendor switch {
                "claude"  => ClaudeLauncherPolicyVersion,
                "cursor"  => CursorLauncherPolicyVersion,
                // Chosen from the SAME resolved field the launch router uses
                // (config.CodexAppServerActive), so the certified policy and the transport actually
                // launched can never diverge.
                "codex"   => config.CodexAppServerActive ? CodexAppServerLauncherPolicyVersion : CodexLauncherPolicyVersion,
                "copilot" => CopilotLauncherPolicyVersion,
                AntigravityVendor => AntigravityLauncherPolicyVersion,
                "opencode" => OpenCodeLauncherPolicyVersion,
                _         => $"{vendor}-unattended-v1"
            };
            // Trust-by-default: a vendor's borrowed-review capability is a property of its FACTORY,
            // never of the installed build's identity. Gating this on an exact-build match made an
            // ordinary vendor auto-update silently withdraw the capability (and the server then
            // resolved workspace_mode=fallback, reviewing a stale committed base with nobody told).
            // See docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
            var borrowedSupported = factory.SupportsBorrowedReviewFlow;
            // Reviewer MODEL override support: advertised true ONLY when this vendor — already known
            // installed + unattended-certified by ComputeUnattendedVendors above — also carries a
            // runtime-owned resolver. Vendor-neutral: we read the factory's resolver + its policy
            // version, never a vendor→model table. A vendor with no resolver advertises false/null,
            // keeping its vendor-only unattended support intact.
            var modelResolver = factory.ReviewerModelResolver;
            capabilities.Add(new(
                vendor,
                string.IsNullOrEmpty(cliPath) ? null : ProbeCliVersion(cliPath),
                policyVersion,
                borrowedSupported,
                borrowedSupported ? factory.BorrowedReviewContainment : null,
                SupportsReviewerModelResolution: modelResolver is not null,
                ReviewerModelPolicyVersion: modelResolver?.PolicyVersion,
                // Caller-selected launch posture, advertised per vendor rather than per platform —
                // the seam is platform-neutral, and only the Codex launcher honours a posture block.
                SupportsLaunchPosture: string.Equals(vendor, "codex", StringComparison.Ordinal)));
        }
        return capabilities;
    }

    /// <summary>Rendered in place of a <c>CliVersion</c> the probe could not determine (spawn
    /// failure, timeout, empty output, unparseable output). It is a reporting placeholder ONLY — an
    /// unidentifiable build is still a trusted build and its capabilities are unaffected.</summary>
    internal const string UnknownCliVersion = "unknown";

    /// <summary>
    /// One Information line per unattended vendor recording the CLI version probed at daemon
    /// startup, so an operator reading the daemon's own log can tell which build was installed when
    /// this daemon came up. Deliberately reports the version and NOTHING else: it does not compare
    /// the installed build against any validated-build record (that would be exactly the automated
    /// version-drift detection this design rejects), and no equivalent line is emitted per launch.
    /// Pure over the computed capabilities — same reasoning as
    /// <see cref="ShouldWarnCursorUnavailable"/> — so it is testable without booting the DI host.
    /// See docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md.
    /// </summary>
    internal static void LogUnattendedVendorIdentities(
            ILogger logger, IEnumerable<UnattendedVendorCapability> capabilities) {
        foreach (var capability in capabilities)
            LogUnattendedVendorIdentity(
                logger,
                capability.Vendor,
                string.IsNullOrWhiteSpace(capability.CliVersion) ? UnknownCliVersion : capability.CliVersion);
    }

    /// <summary>Registration probe budget. Generous and retried because this result is cached for the
    /// daemon's lifetime — one transient miss durably disables the vendor.</summary>
    const int VersionProbeTimeoutMs = 10_000;
    const int VersionProbeAttempts  = 3;

    /// <summary>Launch probe budget, deliberately the ORIGINAL 3s and a single attempt: this runs on
    /// the sequenced command lane, whose single serial consumer holds every later launch and stop
    /// behind it. A miss here is transient and retryable, so it does not need the registration
    /// budget — and inheriting it would have tripled the lane stall this change was meant to
    /// improve.</summary>
    const int LaunchVersionProbeTimeoutMs = 3_000;

    internal static string? ProbeCliVersion(string cliPath) =>
        ProbeCliVersion(cliPath, VersionProbeAttempts, VersionProbeTimeoutMs);

    /// <summary>Single attempt on the original short budget — see LaunchVersionProbeTimeoutMs.</summary>
    internal static string? ProbeCliVersionForLaunch(string cliPath) =>
        ProbeCliVersion(cliPath, attempts: 1, LaunchVersionProbeTimeoutMs);

    static string? ProbeCliVersion(string cliPath, int attempts, int timeoutMs) {
        for (var attempt = 1; attempt <= attempts; attempt++) {
            if (ProbeCliVersionOnce(cliPath, timeoutMs) is { } version) return version;
            // A cold Node start under load is the usual cause; pause rather than hammering.
            if (attempt < attempts) Thread.Sleep(250 * attempt);
        }
        return null;
    }

    static string? ProbeCliVersionOnce(string cliPath, int timeoutMs) {
        try {
            using var process = Process.Start(new ProcessStartInfo {
                FileName = cliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" }
            });
            if (process is null || !process.WaitForExit(timeoutMs)) {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (output.Length == 0) output = process.StandardError.ReadToEnd().Trim();
            return ParseProbedVersion(output);
        } catch { return null; }
    }

    /// <summary>
    /// Extracts the version token from a vendor CLI's <c>--version</c> output. Pure (no process
    /// spawn) so every vendor's real output shape is unit-testable.
    ///
    /// <para>Tokenises on ALL whitespace, not just spaces: a vendor whose <c>--version</c> prints more
    /// than one line (GitHub Copilot CLI appends an "update available" line) would otherwise have the
    /// newline and the next line's first word glued onto the version — e.g.
    /// <c>"1.0.75.\nRun"</c> — because a line break is not a token separator. That value is then
    /// advertised verbatim in the daemon's capability payload and fails
    /// <see cref="CliVersionAllowed"/> outright, since it cannot parse as a version.</para>
    ///
    /// <para>Trailing sentence punctuation is stripped for the same reason: Copilot's version sits at
    /// the end of a sentence ("GitHub Copilot CLI 1.0.75."), so the raw token carries a full stop
    /// that no version parser accepts. Only trailing '.' / ',' are removed — a leading dot is never
    /// part of a version, and every other vendor's token is unaffected.</para>
    /// </summary>
    internal static string? ParseProbedVersion(string output) {
        var token = output
            .Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.Any(char.IsDigit));
        return token?.TrimStart('v', 'V').TrimEnd('.', ',');
    }

    static readonly char[] WhitespaceSeparators = [' ', '\t', '\r', '\n'];

    internal static bool CliVersionAllowed(string? rawVersion, string ranges) {
        // Shared with the reviewer minimum-version gate rather than parsed here — see
        // ReviewerVersionAffirmations.TryParseVersion for why the two must classify identically.
        if (ReviewerVersionAffirmations.TryParseVersion(rawVersion) is not { } version) return false;
        if (string.IsNullOrWhiteSpace(ranges)) return false;
        return ranges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(range => range.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => MatchVersionTerm(version, term)));
    }

    static bool MatchVersionTerm(Version version, string term) {
        var op = term.StartsWith(">=") || term.StartsWith("<=") ? term[..2]
            : term.StartsWith('>') || term.StartsWith('<') || term.StartsWith('=') ? term[..1]
            : "=";
        var start = op == "=" && !term.StartsWith('=') ? 0 : op.Length;
        if (!Version.TryParse(term[start..], out var boundary)) return false;
        var comparison = version.CompareTo(boundary);
        return op switch {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            _ => comparison == 0
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kcap daemon '{Name}' starting, connecting to {ServerUrl}")]
    static partial void LogDaemonStarting(ILogger logger, string name, string serverUrl);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unattended vendor '{Vendor}': CLI version {CliVersion}, as observed by probing the configured binary at daemon startup. That is a startup observation, not the build a later reviewer runs — if the vendor updates while this daemon keeps running, launches pick up the new build and this line stays stale until the daemon restarts.")]
    static partial void LogUnattendedVendorIdentity(ILogger logger, string vendor, string cliVersion);

    // Information, not Warning: an installed vendor withheld for a reason the operator chose — an
    // explicit opt-out, or an unsupported platform — is a NORMAL steady state, so Warning would alert on
    // a correct configuration at every restart. Default minimum level is Information, so it is logged
    // either way. (Pre-ungating this said "installed with no opt-in", which was the common case then and
    // is the rare one now; the level is still right for the reasons that remain.)
    // {Reason} ends the message because each reason is itself a sentence ending in a period.
    [LoggerMessage(Level = LogLevel.Information, Message = "Vendor '{Vendor}' is installed and can host an unattended reviewer, but this daemon is NOT offering it, so a review flow requesting this vendor is refused by the server as an unadvertised reviewer — which does not say why. Restart the daemon after changing this. Reason: {Reason}")]
    static partial void LogUnattendedVendorWithheld(ILogger logger, string vendor, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cursor ACP runtime unavailable: cursor-agent CLI not found (looked for '{CursorPath}'). Cursor will not be offered as a hosted-agent vendor until this is fixed. Set KCAP_CURSOR_PATH to the cursor-agent executable, or install the Cursor CLI, then restart the daemon.")]
    static partial void LogCursorUnavailable(ILogger logger, string cursorPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "KCAP_ACP_DEBUG_FRAMES is enabled — ACP Debug logs may now contain full prompts, tool arguments, and file contents from every hosted Cursor session. Disable in any shared or persistently-logged environment.")]
    static partial void LogAcpDebugFramesEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Previous '{Name}' daemon (PID {Pid}) exited WITHOUT a graceful shutdown — its lock was left for the kernel to release. That is the signature of an uncatchable kill (macOS jetsam/OOM, `kill -9`), a power loss, or a hard native crash; an in-process signal handler cannot record it. If this recurs, run the daemon as a supervised service (`kcap daemon service install`) so it auto-restarts.")]
    static partial void LogPriorUncleanExit(ILogger logger, string name, string pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifetime: ApplicationStopping fired")]
    static partial void LogLifetimeStopping(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lifetime: ApplicationStopped fired")]
    static partial void LogLifetimeStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "WaitForShutdownAsync returned — entering cleanup")]
    static partial void LogWaitForShutdownReturned(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup: disposing daemon resources")]
    static partial void LogEnteringCleanup(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup: completed, daemon exiting")]
    static partial void LogCleanupCompleted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Teardown step '{Step}' failed; continuing shutdown")]
    static partial void LogTeardownStepFailed(ILogger logger, Exception ex, string step);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown requested during startup; cancelling the in-flight startup step and exiting cleanly")]
    static partial void LogStartupCancelledByShutdown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup: one or more teardown steps failed (see warnings above); daemon exiting anyway with its normal exit code")]
    static partial void LogTeardownPartialFailure(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "AppDomain.UnhandledException (terminating={IsTerminating})")]
    static partial void LogUnhandledException(ILogger logger, Exception ex, bool isTerminating);

    [LoggerMessage(Level = LogLevel.Error, Message = "TaskScheduler.UnobservedTaskException — observed and swallowed")]
    static partial void LogUnobservedTaskException(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "AppDomain.ProcessExit fired (this is the last log line)")]
    static partial void LogProcessExit(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Received POSIX signal {Signal} — requesting cooperative shutdown")]
    static partial void LogPosixSignal(ILogger logger, PosixSignal signal);

    /// <summary>
    /// Wires AppDomain + TaskScheduler + POSIX-signal hooks so whenever the
    /// daemon process is going away we get a last log line before the runtime
    /// tears down. Without these, the only signal we'd see is "the log ends" —
    /// indistinguishable between SIGTERM, SIGHUP (terminal closed), OOM kill,
    /// an unobserved Task FailFast, or a clean StopApplication. SIGHUP is the
    /// top suspect for "daemon dies silently after a foreground run", since
    /// ConsoleLifetime doesn't register for it and the OS default is SIGTERM
    /// the process. Routing it through <paramref name="lifetime"/> turns the
    /// hard kill into a cooperative shutdown so the cleanup finally-block
    /// gets to run.
    /// </summary>
    static void RegisterDeathRattle(ILogger logger, IHostApplicationLifetime lifetime) {
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception ex) {
                DeathRattle($"AppDomain.UnhandledException (terminating={args.IsTerminating}): {ex.GetType().Name}: {ex.Message}");
                LogUnhandledException(logger, ex, args.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) => {
            DeathRattle($"TaskScheduler.UnobservedTaskException: {args.Exception.GetType().Name}: {args.Exception.Message}");
            LogUnobservedTaskException(logger, args.Exception);
            // Mark observed so the default policy (a no-op in .NET 5+, but a
            // process-killing rethrow on legacy/AOT configurations) can't
            // escalate this past the logging step.
            args.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            DeathRattle("AppDomain.ProcessExit fired (this is the last log line)");
            LogProcessExit(logger);
        };

        // POSIX signal hooks. ConsoleLifetime already wires SIGINT and SIGTERM
        // to lifetime.StopApplication(); our registration is additive (multiple
        // PosixSignalRegistration handlers all run) so it just guarantees a
        // log line lands before the cooperative shutdown begins. SIGHUP and
        // SIGQUIT are NOT caught by ConsoleLifetime, so for those we both log
        // AND call StopApplication ourselves — otherwise the OS would terminate
        // us before the host's finally-block could run.
        //
        // The returned PosixSignalRegistration IS the registration's lifetime
        // anchor — if it's GC'd and finalized the handler unregisters silently
        // and the signal goes back to its default OS action (terminate the
        // process for SIGHUP, etc). Root them in a static list so they live
        // as long as the DaemonRunner type — i.e. the process.
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }) {
            try {
                _signalRegistrations.Add(PosixSignalRegistration.Create(signal, ctx => {
                    DeathRattle($"Received POSIX signal {ctx.Signal} — requesting cooperative shutdown");
                    LogPosixSignal(logger, ctx.Signal);
                    ctx.Cancel = true;
                    lifetime.StopApplication();
                }));
            } catch (PlatformNotSupportedException) {
                // Signal not supported on this OS — skip silently. SIGHUP/SIGQUIT
                // are unsupported on Windows; SIGINT/SIGTERM are supported
                // everywhere.
            }
        }
    }

    /// <summary>
    /// Roots <see cref="PosixSignalRegistration"/> instances for the lifetime
    /// of the process. <see cref="PosixSignalRegistration.Create"/> returns an
    /// <see cref="IDisposable"/> whose finalizer unregisters the handler;
    /// without a strong reference the registration is eligible for GC the
    /// moment <see cref="RegisterDeathRattle"/> returns, which would silently
    /// re-arm the OS default for SIGHUP/SIGQUIT (= terminate the daemon
    /// before the finally-block runs). Static field on a static class = same
    /// lifetime as the process.
    /// </summary>
    static readonly List<PosixSignalRegistration> _signalRegistrations = [];

    /// <summary>
    /// Synchronous stderr backstop for death-rattle messages. The default
    /// <c>AddSimpleConsole</c> provider uses a background-thread queue
    /// (<c>ConsoleLoggerProcessor</c>) that can drop messages enqueued during
    /// runtime teardown — so a <c>ProcessExit</c> or signal-handler log line
    /// may never reach the terminal even though the hook fired. Writing
    /// directly to <see cref="Console.Error"/> bypasses the queue and lands
    /// on stderr immediately. Best-effort: a closed terminal or broken pipe
    /// must not throw out of an exit hook.
    /// </summary>
    static void DeathRattle(string message) {
        try {
            Console.Error.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [death-rattle] {message}");
            Console.Error.Flush();
        } catch {
            // Stderr might be redirected to a closed pipe, the terminal
            // might be gone, etc. Already exiting — nothing useful to do.
        }
    }
}
