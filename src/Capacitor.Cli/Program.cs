using System.Reflection;
using Capacitor.Cli;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using ReviewCommand = Capacitor.Cli.Commands.ReviewCommand;
using WatchCommand = Capacitor.Cli.Commands.WatchCommand;

if (args.Length < 1) {
    await PrintUsage();

    return 1;
}

var command = args[0];

// Daemon-only borrowed-review context mode. This exact invocation is dispatched before server URL
// resolution and update checks so the sidecar reader has no backend, auth, Git, or config authority.
if (args is ["mcp", "review"] &&
    Environment.GetEnvironmentVariable(McpReviewContextServer.ModeEnvVar) == "1")
    return await McpReviewContextServer.RunAsync(
        Environment.GetEnvironmentVariable(McpReviewContextServer.UrlEnvVar));

// Interactive commands block on synchronous Spectre.Console prompts. Install a
// signal + parent-liveness safety net so an abandoned prompt (closed terminal,
// killed launching agent, detached pseudo-console) can't orphan this process
// alive — the reported stray `kcap.exe` after `kcap setup` is exited partway.
//
// Armed here, before ResolveForRepo/update-check, on purpose: those can block
// for seconds (git remote lookup, npm registry), and StartParentWatchdog refuses
// to arm if the parent is already gone. Installing later leaves a window where
// the launching agent can exit during that startup work — the watchdog then
// never starts and the very first prompt still orphans us.
if (InteractiveLifetime.IsInteractiveCommand(command)) {
    InteractiveLifetime.Install();
}

// Hook short-circuit: when spawned inside a kcap-launched headless agent
// invocation (e.g., title generation, the eval judge) we don't forward the
// nested session's hook events back into kcap. Scoped to `hook` because
// non-hook commands — notably `kcap mcp judge` running as an MCP server
// child of the eval judge claude process — must actually execute despite
// inheriting KCAP_SKIP=1 from the parent.
//
// Vendor-aware: only Claude and Cursor get the early exit. Codex's hook
// parser rejects empty stdout on SessionStart / Stop ("invalid hook JSON
// output") and requires {"continue":true}, which CodexHookCommand emits.
// Returning 0 with no body would break kcap-launched headless Codex flows
// (CodexCliRunner.cs sets KCAP_SKIP=1) whenever ~/.codex/hooks.json is
// populated, so `kcap hook --codex` runs its handler regardless of
// KCAP_SKIP and the handler owns the output contract.
//
// Runs before ResolveForRepo/update-check so a skipped hook does no work:
// ResolveForRepo can shell out to `git remote -v` and emit warnings, and
// the update-check task hits the npm registry — both pure noise inside a
// nested headless invocation.
if (Environment.GetEnvironmentVariable("KCAP_SKIP") is "1"
 && command == "hook"
 && (args.Contains("--claude") || args.Contains("--cursor") || args.Contains("--copilot") || args.Contains("--gemini") || args.Contains("--kiro") || args.Contains("--pi") || args.Contains("--opencode") || args.Contains("--antigravity"))) {
    return 0;
}

// Anchored here, before dispatch: every hook ceiling is relative to it, and anchoring inside a
// handler would inflate the budget by the pre-dispatch work (config load, spool drain) and overshoot
// the true hook ceiling.
var clock = new HookClock(TimeProvider.System);
var isHook = command == "hook";

// Resolved once here and passed onward; nothing downstream resolves a root for itself.
var config = ConfigRoot.FromEnvironment();
var home   = UserHome.FromEnvironment();

// Claude kills a SessionEnd hook after 1.5 s (ClaudeSessionEndHandoff), so the hand-off sits
// ahead of ResolveServerUrl's git probes and the global spool drain, each of which can spend it.
string? claudeHookBody = null;
if (isHook && args.Contains("--claude")) {
    try { claudeHookBody = await Console.In.ReadToEndAsync(); } catch { claudeHookBody = ""; }

    if (ClaudeSessionEndHandoff.IsDetached(args)) {
        ClaudeSessionEndHandoff.EnterDetached(claudeHookBody, config);
    } else if (ClaudeSessionEndHandoff.ShouldHandOff(args, claudeHookBody) && ClaudeSessionEndHandoff.TrySpawn(args, claudeHookBody, config)) {
        return 0;
    }
}

// KCAP_DAEMONS_DIR is dead to the process from this line on.
var daemonPaths = DaemonStore.FromEnvironment();

var profiles = await AppConfig.ResolveForRepo(args, config, gitTimeoutMs: isHook ? 1000 : 5000);
var baseUrl  = profiles.Resolution.ServerUrl;

// Composition root. Every context resolved above is registered once here; the dispatch switch below
// asks for a command rather than handing each one its arguments.
var services = new ServiceCollection()
    .AddCapacitorContext(config, home, daemonPaths, profiles)
    .AddCapacitorCommands();

services.AddSingleton(clock);
services.AddSingleton<IBrowserLauncher>(SystemBrowser.Instance);

// Both probe the filesystem, and only a handful of commands take either, so they stay factories:
// resolving a command that wants neither must not pay for them.
services.AddSingleton(sp => HarnessRegistry.FromEnvironment(sp.GetRequiredService<UserHome>()));
services.AddSingleton(sp => PluginEnvironment.FromProcess(
        sp.GetRequiredService<ProfileContext>().Snapshot,
        sp.GetRequiredService<UserHome>()));

// A factory, so a command that never speaks HTTP does not pay for a server URL it may not even have:
// `baseUrl` is null until a profile resolves one.
services.AddSingleton(_ => new CapacitorServer(baseUrl, config, profiles));
services.AddCapacitorHttp();

await using var sp = services.BuildServiceProvider();

TCommand Run<TCommand>() where TCommand : notnull => sp.GetRequiredService<TCommand>();

ISessionsApi Api() => sp.GetRequiredService<ISessionsApi>();

// Telemetry: initialised once the server URL is known (it decides the `organization` group) and
// torn down from ProcessExit, which observes the exit code returned by top-level Main. Every
// call swallows, so nothing here can fail a command.
//
// Deliberately ahead of the update-notice try/finally below: this is process setup, and the
// ProcessExit handler outlives that block anyway.
var commandStart = System.Diagnostics.Stopwatch.GetTimestamp();

// TokenStore.LoadAsync() is the LOCAL read (src/Capacitor.Cli.Core/Auth/TokenStore.cs:211) —
// deliberately not GetValidTokensForProfileAsync(), which can refresh over the network. `logged_in` is a
// cheap fact about disk, never a reason to make a request on the command path.
//
// Gated on IsReportable: denylisted commands (chiefly `hook`, thousands of invocations/day on
// the agent's critical path) never send `logged_in` — CliTelemetry.Initialize below disables
// itself for them regardless — so the disk read has no consumer and is worth skipping outright.
var loggedIn = false;
if (CommandEvents.IsReportable(command)) {
    try { loggedIn = await sp.GetRequiredService<TokenStore>().LoadForProfileAsync(profiles.Name) is not null; } catch { }
}

// `kcap config set telemetry off` must never activate telemetry for the very invocation that
// opts out: without this, Initialize below resolves Enabled from the not-yet-updated persisted
// flag, mints a device id, shows the first-run notice, and queues cli_first_run — all before
// ConfigCommand ever runs. Pre-apply the "off" to disk here so Initialize sees it already
// persisted. Value recognition only (no throw on garbage — an invalid value is reported
// normally once ConfigCommand actually dispatches); KCAP_TELEMETRY=1 still overrides a persisted
// "off" exactly as it does everywhere else, since Resolve checks the env var first regardless of
// what's on disk. ConfigCommand.TryApplyTelemetry re-applies the same (idempotent) change and
// covers that override case with its own DiscardAndDisable.
if (args.Length >= 4 && command == "config" && args[1] == "set" && args[2] == "telemetry"
 && ConfigCommand.TryParseTelemetryToggle(args[3]) == false) {
    TelemetryState.SetEnabled(false, config);
}

// spec decision 9: an app-spawned CLI child must not emit CLI-labeled telemetry nor consume
// the one-time privacy notice on an invisible stderr. Consume-and-REMOVE before dispatch so
// no grandchild (detached daemon, hosted agents) can observe the marker.
var telemetrySuppressed = CliTelemetry.ConsumeSpawnMarker(
    Environment.GetEnvironmentVariable,
    k => Environment.SetEnvironmentVariable(k, null));

CliTelemetry.Initialize(command, baseUrl, loggedIn, config, telemetrySuppressed);

AppDomain.CurrentDomain.ProcessExit += (_, _) => {
    // Environment.Exit runs no `finally` and no `using` disposal, so a leg that ends that way has no
    // other chance to tell a waiting browser it has stopped. Ahead of the telemetry flush because both
    // spend the runtime's one exit budget and only this one is holding a page open; the notice claims
    // once, so a leg that already sent stays silent and a process with none armed is a no-op.
    FirstRunInterruptRelinquish.RunBeforeExit(InteractiveLifetime.ExitNoticeBudget);

    CliTelemetry.RecordCommand(command, args, Environment.ExitCode, CommandTiming.ElapsedMs(commandStart));
    CliTelemetry.FlushAndClose().GetAwaiter().GetResult();
};

// Everything from here to the end of command dispatch — including the --help,
// per-command-help, and no-server-configured early exits below — runs inside this
// try/finally so the deterministic exit-time update notice (UpdateNotice.FlushAsync)
// fires on every path out of the command, not just the ones that fall through the
// switch. UpdateNotice.IsHumanFacing is the suppression predicate (hooks, mcp, watch,
// the foreground daemon, update/uninstall themselves, --no-update-check) — it decides
// per-invocation whether FlushAsync does anything at all.
try {

if (command is "--help" or "-h" or "help") {
    await PrintUsage();

    return 0;
}

// Per-command help: kcap <command> --help / -h
if (args.Skip(1).Any(a => a is "--help" or "-h")) {
    return await PrintCommandHelp(command);
}

// Commands that don't need a server URL
// report-version: a no-server host must still hit ReportVersionCommand.HandleAsync's own
// fail-open logic and return 0 silently, per its doc comment — never the generic
// "No server configured" exit 1 this gate would otherwise produce.
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "daemon", "setup", "status", "harness", "update", "plugin", "profile", "use", "repos", "login", "ignore", "remap", "uninstall", "cursor-verify-appendonly", "agent", "report-version"];

// `import --discover` reads local transcripts and never calls the server, so it belongs with the
// offline commands — and it is most useful before setup has run, which is exactly when there is no
// server configured. Only that form: a real import obviously needs one.
var offlineDiscover = command == "import" && args.Contains("--discover");

if (baseUrl is null && !offlineCommands.Contains(command) && !offlineDiscover) {
    Console.Error.WriteLine("No server configured. Run `kcap setup` or set KCAP_URL.");

    return 1;
}

// last-resort guard around the whole command dispatch. Without it, any
// exception a handler doesn't swallow escapes to the NativeAOT runtime, which
// aborts the process (SIGABRT + a macOS crash report). For a ~1s hook/generator
// the agent spawns, that was happening dozens of times a day. Record the
// exception and exit cleanly instead (fail-open for agent-spawned commands).
try {
switch (command) {
    case "--version" or "-v": {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?
            .InformationalVersion ?? "unknown";
        await Console.Out.WriteLineAsync($"kcap {version}");

        return 0;
    }
    case "errors": {
        var useChain     = args.Contains("--chain");
        var errSessionId = ResolveSessionId(args, skipCount: 1);

        if (errSessionId is null) {
            Console.Error.WriteLine("Usage: kcap errors [--chain] [sessionId]");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

            return 1;
        }

        return await Run<ErrorsCommand>().HandleErrors(errSessionId, useChain);
    }
    case "recap": {
        var useChain   = args.Contains("--chain");
        var useFull    = args.Contains("--full");
        var useRepo    = args.Contains("--repo");
        var usePerTurn = args.Contains("--per-turn");
        var useGetTurn = args.Contains("--get-turn");

        if (useRepo) {
            return await Run<RecapCommand>().HandleRepoRecap();
        }

        // --get-turn takes a value (the turn index), so declare it as a value flag
        // or ResolveSessionId would mistake the index for the sessionId.
        var recapSessionId = ResolveSessionId(args, valueFlags: ["--get-turn"]);

        if (recapSessionId is null) {
            Console.Error.WriteLine("Usage: kcap recap [--chain] [--full] [--repo] [--per-turn] [--get-turn <N>] [sessionId]");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");
            Console.Error.WriteLine("  Use --repo to see recent session summaries for the current repository.");
            Console.Error.WriteLine("  Use --per-turn for a per-turn index, or --get-turn <N> for one turn's transcript.");

            return 1;
        }

        if (usePerTurn) {
            return await Run<RecapCommand>().HandlePerTurnRecap(recapSessionId);
        }

        if (useGetTurn) {
            var getTurnIdx = args
                .SkipWhile(a => a != "--get-turn")
                .Skip(1)
                .FirstOrDefault(a => !a.StartsWith('-'));

            if (getTurnIdx is null || !int.TryParse(getTurnIdx, out var turnIndex)) {
                Console.Error.WriteLine("Usage: kcap recap --get-turn <turnIndex> [sessionId]");

                return 1;
            }

            return await Run<RecapCommand>().HandleGetTurn(recapSessionId, turnIndex);
        }

        return await Run<RecapCommand>().HandleRecap(recapSessionId, useChain, useFull);
    }
    case "sessions":
        return await Run<SessionsCommand>().HandleAsync(args);
    case "validate-plan": {
        var vpSessionId = ResolveSessionId(args);

        if (vpSessionId is null) {
            Console.Error.WriteLine("Usage: kcap validate-plan [sessionId]");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

            return 1;
        }

        return await Run<ValidatePlanCommand>().Handle(vpSessionId);
    }
    case "feedback":
        return await Run<FeedbackCommand>().HandleAsync(args);
    case "eval": {
        // --list-questions is a standalone sub-action; short-circuit.
        if (args.Contains("--list-questions")) {
            return await Run<EvalCommand>().HandleListQuestions();
        }

        var evalSessionId = ResolveSessionId(args, valueFlags: ["--model", "--threshold", "--questions", "--skip"]);

        if (evalSessionId is null) {
            Console.Error.WriteLine("Usage: kcap eval [--model sonnet] [--chain] [--threshold N]");
            Console.Error.WriteLine("                     [--questions <csv> | --skip <csv>] [sessionId]");
            Console.Error.WriteLine("       kcap eval --list-questions");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

            return 1;
        }

        var evalChain     = args.Contains("--chain");
        var evalModel     = GetArg(args, "--model") ?? "sonnet";
        var evalThreshold = GetArg(args, "--threshold") is { } ts && int.TryParse(ts, out var parsed)
            ? parsed
            : (int?)null;
        var evalQuestions = GetArg(args, "--questions");
        var evalSkip      = GetArg(args, "--skip");

        // Guard against the user dropping the flag value — otherwise GetArg
        // silently returns the next token ("--skip", "--chain", …) and the
        // resolver later reports a confusing "unknown token" error.
        foreach (var (flag, value) in new[] { ("--questions", evalQuestions), ("--skip", evalSkip) }) {
            if (value is not null && value.StartsWith("--")) {
                Console.Error.WriteLine($"eval: {flag} requires a value (got '{value}')");
                return 2;
            }
        }

        return await Run<EvalCommand>().HandleEval(
            evalSessionId, evalModel, evalChain, evalThreshold,
            evalQuestions, evalSkip
        );
    }
    case "generate-whats-done" when args.Length < 2:
        Console.Error.WriteLine("Usage: kcap generate-whats-done <sessionId> [--codex]");

        return 1;
    case "generate-whats-done": {
        var wdSessionId = args[1].Replace("-", "");
        var wdVendor    = args.Contains("--codex") ? "codex" : "claude";

        return await Run<WhatsDoneCommand>().HandleGenerateWhatsDone(baseUrl!, wdSessionId, wdVendor);
    }
    case "login":
        return await Run<LoginCommand>().HandleAsync(args, baseUrl);
    case "logout": {
        await sp.GetRequiredService<TokenStore>().DeleteAsync();
        await Console.Out.WriteLineAsync("Logged out.");

        return 0;
    }
    case "whoami":
        return await Run<WhoamiCommand>().HandleAsync();
    case "daemon":
        return await Run<DaemonCommands>().HandleAsync(args);
    case "agent":
        return await Run<AgentCommand>().HandleAsync(args, baseUrl);
    case "setup":
        return await Run<SetupCommand>().HandleAsync(args);
    case "plugin":
        return await Run<PluginCommand>().HandleAsync(args);
    case "profile":
        return await Run<ProfileCommand>().HandleAsync(args);
    case "machine":
        return await Run<MachineCommand>().HandleAsync(args);
    case "use":
        return await Run<UseCommand>().HandleAsync(args);
    case "status":
        return await Run<StatusCommand>().HandleAsync(args);
    case "harness":
        return await Run<HarnessCommand>().HandleAsync(args);
    case "config":
        return await Run<ConfigCommand>().HandleAsync(args);
    case "ignore":
        return await Run<IgnoreCommand>().HandleAsync(args);
    case "remap":
        return await Run<RemapCommand>().HandleAsync(args);
    case "repos":
        return await Run<ReposCommand>().HandleAsync(args);
    case "projects":
        return await Run<ProjectsCommand>().HandleList();
    case "project": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kcap project <slug>");

            return 1;
        }

        return await Run<ProjectsCommand>().HandleDetail(args[1]);
    }
    case "update":
        return await Run<UpdateCommand>().HandleAsync(args);
    case "review": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kcap review <pr-url-or-shorthand>");
            Console.Error.WriteLine("  Example: kcap review https://github.com/owner/repo/pull/123");
            Console.Error.WriteLine("  Example: kcap review owner/repo#123");

            return 1;
        }

        return await Run<ReviewCommand>().HandleReview(args[1]);
    }
    case "mcp": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kcap mcp review|judge|sessions|flows|flow-result|memory|workitems|analytics …");
            Console.Error.WriteLine("  kcap mcp review [--owner <owner> --repo <repo> --pr <number>]");
            Console.Error.WriteLine("  kcap mcp judge --session <sessionId>");
            Console.Error.WriteLine("  kcap mcp sessions");
            Console.Error.WriteLine("  kcap mcp flows");
            Console.Error.WriteLine("  kcap mcp flow-result   (launched by the daemon for hosted reviewers)");
            Console.Error.WriteLine("  kcap mcp memory");
            Console.Error.WriteLine("  kcap mcp workitems");
            Console.Error.WriteLine("  kcap mcp analytics");

            return 1;
        }

        switch (args[1]) {
            case "review": {
                var mcpOwner = GetArg(args, "--owner");
                var mcpRepo  = GetArg(args, "--repo");
                var mcpPr    = GetArg(args, "--pr");

                // Explicit PR args — use directly
                if (mcpOwner is not null && mcpRepo is not null && mcpPr is not null && int.TryParse(mcpPr, out var mcpPrNum)) {
                    return await Run<McpReviewServer>().RunAsync(mcpOwner, mcpRepo, mcpPrNum);
                }

                // No args — auto-detect from git
                return await Run<McpReviewServer>().RunAutoAsync();
            }
            case "judge": {
                var session = GetArg(args, "--session");

                if (string.IsNullOrWhiteSpace(session)) {
                    Console.Error.WriteLine("Usage: kcap mcp judge --session <sessionId>");

                    return 1;
                }

                return await Run<McpJudgeServer>().RunAsync(session);
            }
            case "sessions":
                return await Run<McpSessionsServer>().RunAsync();
            case "flows":
                return await Run<McpFlowsServer>().RunAsync(GetArg(args, "--driver"));
            case "flow-result":
                return await Run<McpFlowResultServer>().RunAsync();
            case "memory":
                return await Run<McpMemoryServer>().RunAsync();
            case "workitems":
                return await Run<McpWorkItemsServer>().RunAsync();
            case "analytics":
                return await Run<McpAnalyticsServer>().RunAsync();
            default:
                Console.Error.WriteLine($"Unknown mcp subcommand: {args[1]}");

                return 1;
        }
    }
    case "skills": {
        if (args.Length < 2 || args[1] != "sync") {
            Console.Error.WriteLine("Usage: kcap skills sync [--dry-run] [--auto]");
            return 1;
        }
        var skillsAuto = args.Contains("--auto");
        if (skillsAuto) {
            // The auto spawn's parent (a hook) exits long before this process does, closing the
            // pipe read ends — a later write would then throw on a dead fd. Null writers never
            // touch an fd, so the background sync can outlive its parent safely.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
        return await Run<SkillsCommand>().HandleSync(args.Contains("--dry-run"), skillsAuto);
    }
    case "curate": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kcap curate apply [--dry-run] [--yes]");
            return 1;
        }
        switch (args[1]) {
            case "apply": {
                var dryRun = args.Contains("--dry-run");
                var yes    = args.Contains("--yes") || args.Contains("-y");
                return await Run<CurateCommand>().HandleApply(dryRun, yes);
            }
            default:
                Console.Error.WriteLine($"Unknown curate subcommand: {args[1]}");
                Console.Error.WriteLine("Usage: kcap curate apply [--dry-run] [--yes]");
                return 1;
        }
    }
    case "cleanup":
        return await Run<CleanupCommand>().HandleCleanup();
    case "uninstall":
        return await Run<UninstallCommand>().HandleAsync(args);
    case "disable": {
        // The sessionId is consumed as a filesystem path component
        // (watcher PID files, disabled marker file). Validate strictly as a
        // GUID to prevent path traversal via crafted positional input.
        var resolved = ResolveSessionId(args);

        if (resolved is null) {
            Console.Error.WriteLine("Usage: kcap disable [sessionId]");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

            return 1;
        }

        if (!ArgParsing.TryNormalizeSessionGuid(resolved, out var sessionId)) {
            Console.Error.WriteLine($"Invalid session ID: '{resolved}'");
            Console.Error.WriteLine("  Session ID must be a UUID. Use `kcap recap --repo` to find recent session IDs.");

            return 1;
        }

        // 1. Kill the watcher (and any subagent watchers)
        var watchers = new WatcherManager(config, profiles, sp.GetRequiredService<ICapacitorHttpClient>());
        await watchers.KillWatcher(sessionId);

        // Also kill subagent watchers — scan PID files matching "{sessionId}-*"
        var watcherDir = watchers.GetWatcherDir();

        if (Directory.Exists(watcherDir)) {
            foreach (var pidFile in Directory.GetFiles(watcherDir, $"{sessionId}-*.pid")) {
                var subKey = Path.GetFileNameWithoutExtension(pidFile);
                await watchers.KillWatcher(subKey);
            }
        }

        // 2. Mark session as disabled (prevents future hook calls from sending data)
        DisabledSessions.Mark(sessionId, config);

        // 3. Tell server to delete session data
        try {
            switch (await Api().DeleteSessionAsync(sessionId)) {
                case DeleteSessionResponse.Deleted:
                    await Console.Out.WriteLineAsync($"Session {sessionId} disabled. Recording stopped and server data deleted.");

                    break;
                case DeleteSessionResponse.NotFound:
                    await Console.Out.WriteLineAsync($"Session {sessionId} disabled. No server data found (may have already been deleted).");

                    break;
            }
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            // Local state has already changed, so a server that was never reached is not a failure.
            if (ex.Status is not null) return 1;

            await Console.Out.WriteLineAsync("Session disabled locally (watcher stopped, hooks silenced). Server data not deleted.");
        }

        return 0;
    }
    case "hide": {
        // The sessionId is forwarded into a server URL path but we keep the
        // same strict GUID validation as `disable` to reject path-traversal
        // characters and slugs uniformly across local-state-mutating commands.
        var resolved = ResolveSessionId(args);

        if (resolved is null) {
            Console.Error.WriteLine("Usage: kcap hide [sessionId]");
            Console.Error.WriteLine("  No session ID provided. Pass one explicitly, or run inside Claude Code / Codex CLI 0.81+.");

            return 1;
        }

        if (!ArgParsing.TryNormalizeSessionGuid(resolved, out var sessionId)) {
            Console.Error.WriteLine($"Invalid session ID: '{resolved}'");
            Console.Error.WriteLine("  Session ID must be a UUID. Use `kcap recap --repo` to find recent session IDs.");

            return 1;
        }

        try {
            await Api().HideSessionAsync(sessionId);
            await Console.Out.WriteLineAsync($"Session {sessionId} hidden (owner-only).");
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        return 0;
    }
    case "import": {
        // Vendor selection first — quick exit on parse errors so we don't do other work.
        var vsel = VendorSelection.Parse(args);
        if (vsel.HasError) {
            Console.Error.WriteLine(vsel.Error);
            return 1;
        }

        string?   filterCwd     = null;
        string?   filterSession = null;
        var       minLines      = 15;
        DateOnly? since         = null;

        var cwdArgIdx = Array.IndexOf(args, "--cwd");
        if (cwdArgIdx >= 0 && cwdArgIdx + 1 < args.Length) {
            filterCwd = args[cwdArgIdx + 1];
        }

        var sessionArgIdx = Array.IndexOf(args, "--session");
        if (sessionArgIdx >= 0 && sessionArgIdx + 1 < args.Length) {
            filterSession = args[sessionArgIdx + 1];
        }

        var minLinesIdx = Array.IndexOf(args, "--min-lines");
        if (minLinesIdx >= 0 && minLinesIdx + 1 < args.Length && int.TryParse(args[minLinesIdx + 1], out var parsed)) {
            minLines = parsed;
        }

        var sinceIdx = Array.IndexOf(args, "--since");
        if (sinceIdx >= 0 && sinceIdx + 1 < args.Length) {
            if (!DateOnly.TryParseExact(args[sinceIdx + 1], "yyyy-MM-dd", out var parsedSince)) {
                Console.Error.WriteLine("--since must be YYYY-MM-DD");
                return 1;
            }

            since = parsedSince;
        }

        var generateSummaries = args.Contains("--generate-summaries");
        var reimport          = args.Contains("--reimport");
        var skipTitle         = args.Contains("--skip-title");
        var discoverOnly      = args.Contains("--discover");
        var discoverJson      = args.Contains("--json");

        // Silently ignoring it would turn "report as JSON" into a real import, which is the one
        // mistake this flag pair can make that costs something.
        if (discoverJson && !discoverOnly) {
            Console.Error.WriteLine("--json only applies to `kcap import --discover`.");

            return 1;
        }

        // Build sources
        var explicitVendorSelection = vsel.Vendors.Count > 0;
        var sources = SetupCommand.BuildImportSources(
            config, HarnessPaths.FromEnvironment(home), explicitVendorSelection ? vsel.Vendors : null);

        // --- Scope resolution ---
        var profileConfig = profiles.Snapshot;
        // The profile a later import would persist the chosen org to, so this reads it back from
        // the same place.
        var activeProfile = profiles.Name;
        var storedOrg     = profileConfig.Profiles.GetValueOrDefault(activeProfile)?.ImportOrg;

        var currentRepoDetected = await RepositoryDetection.DetectRepositoryAsync(config, Environment.CurrentDirectory);
        (string Owner, string Name)? currentRepo = currentRepoDetected is { Owner: { } o, RepoName: { } n }
            ? (o, n)
            : null;

        var flags = ImportScopeArgs.ParseFlags(args);
        var resolveResult = ImportScopeArgs.Resolve(new(
            Flags:         flags,
            ActiveProfile: activeProfile,
            IsInteractive: !Console.IsInputRedirected && !Console.IsOutputRedirected,
            CurrentRepo:   currentRepo,
            StoredOrg:     storedOrg));

        // `--discover` reports what a scope WOULD select, so requiring one first is backwards — it is
        // the answer to "what should I pick", asked before anything is uploaded.
        if (resolveResult.Error is not null && !discoverOnly) {
            Console.Error.WriteLine(resolveResult.Error);
            return 1;
        }

        return await Run<ImportCommand>().HandleImport(
            filterCwd,
            filterSession,
            minLines,
            generateSummaries,
            sources:                 sources,
            explicitVendorSelection: explicitVendorSelection,
            since:                   since,
            scope:                   resolveResult.Scope, // null => HandleImport runs picker
            skipConfirmation:        resolveResult.Yes,
            forcePrivate:            resolveResult.Private,
            currentRepo:             currentRepo,
            needOrgPick:             resolveResult.NeedOrgPick,
            storedOrg:               storedOrg,
            reimport:                reimport,
            skipTitle:               skipTitle,
            discoverOnly:            discoverOnly,
            discoverJson:            discoverJson);
    }
    case "watch" when args.Length < 3:
        Console.Error.WriteLine("Usage: kcap watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>] [--skip-title] [--parent-pid <pid>] [--vendor claude|codex|copilot|gemini|kiro|pi|opencode|antigravity|cursor]");

        return 1;
    case "watch": {
        var     watchSessionId = args[1].Replace("-", "");
        var     watchPath      = args[2];
        string? watchAgentId   = null;
        string? watchCwd       = null;
        var     agentIdIdx     = Array.IndexOf(args, "--agent-id");

        if (agentIdIdx >= 0 && agentIdIdx + 1 < args.Length) {
            watchAgentId = args[agentIdIdx + 1].Replace("-", "");
        }

        var cwdIdx = Array.IndexOf(args, "--cwd");

        if (cwdIdx >= 0 && cwdIdx + 1 < args.Length) {
            watchCwd = args[cwdIdx + 1];
        }

        var watchSkipTitle = Array.IndexOf(args, "--skip-title") >= 0;

        int? parentPid    = null;
        var  parentPidIdx = Array.IndexOf(args, "--parent-pid");

        if (parentPidIdx >= 0 && parentPidIdx + 1 < args.Length && int.TryParse(args[parentPidIdx + 1], out var ppid)) {
            parentPid = ppid;
        }

        var watchVendor = GetArg(args, "--vendor") ?? "claude";

        return await Run<WatchCommand>().RunWatch(
            watchSessionId, watchPath, watchAgentId, watchCwd,
            watchSkipTitle, parentPid, watchVendor
        );
    }
    // Internal: spawned detached by the Copilot sessionEnd hook to deliver the
    // post-hook `session.shutdown` tail Copilot writes after the hook returns
    // Not a user-facing command.
    case "copilot-finalize" when args.Length < 3:
        Console.Error.WriteLine("Usage: kcap copilot-finalize <sessionId> <transcriptPath>");

        return 1;
    case "copilot-finalize": {
        var cfSessionId = args[1].Replace("-", "");
        var cfPath      = args[2];

        return await Run<CopilotFinalizeDrainCommand>().Run(cfSessionId, cfPath);
    }
    case "set-title" when args.Length < 2:
        Console.Error.WriteLine("Usage: kcap set-title <title>");

        return 1;
    case "set-title": {
        var stSessionId = ArgParsing.ResolveSessionIdFromEnv();

        if (stSessionId is null) {
            Console.Error.WriteLine("No session ID found in KCAP_SESSION_ID or CODEX_THREAD_ID.");
            Console.Error.WriteLine("Run set-title inside an active Claude Code / Codex CLI 0.81+ session.");

            return 1;
        }

        // Join all remaining args as the title (supports unquoted multi-word titles)
        var title = string.Join(' ', args.Skip(1)).Trim();

        if (string.IsNullOrWhiteSpace(title)) {
            Console.Error.WriteLine("Title cannot be empty");

            return 1;
        }

        // Limit to 120 chars
        if (title.Length > 120) {
            title = title[..120];
        }

        try {
            await Api().SetSessionTitleAsync(stSessionId, title);
        } catch (CapacitorApiException ex) {
            Console.Error.WriteLine(ex.Message);

            return 1;
        }

        return 0;
    }
    // Hidden, tooling-internal: spawned once by the npm wrapper's `runUpdate` right after
    // `npm install` lands the new binary, so the server observes the new version immediately
    // instead of waiting for whatever the user runs next. Not in PrintUsage — like
    // generate-whats-done/set-title/copilot-finalize, nobody types this by hand. See
    // ReportVersionCommand for why it never surfaces an error.
    case "report-version":
        return await Run<ReportVersionCommand>().HandleAsync();
    case "hook": {
        // Task 12: global, session-agnostic drain pass run early in EVERY non-Codex hook
        // invocation — centralizes the per-vendor AgentHookPoster.DrainSpoolsAsync calls Tasks 4-6
        // added (removed from their Handle methods so this runs exactly once per invocation) and
        // additionally covers Claude/Cursor, which never called it (they only drain their OWN
        // route-agnostic FIFO backlog via HookSpool.DrainAllAsync). Codex is exempt — it runs its
        // own drain in the BACKGROUND, after satisfying its synchronous stdout contract.
        // Cross-process-throttled (~30s) and auth-gated inside DrainSpoolsAsync, so this adds no
        // per-invocation network cost beyond a disk stat on the vast majority of firings.
        // No acceptability conjunct here: DrainSpoolsAsync owns that decision, and it reaps both
        // spools BEFORE returning. Gating the call would mean a config broken for weeks never reaps
        // anything, and the per-session cap does not bound the number of stale files.
        if (!args.Contains("--codex") && baseUrl is not null) {
            await new AgentHookPoster(config, profiles, sp.GetRequiredService<ICapacitorHttpClient>()).DrainSpoolsAsync(
                new HookSpool(config),
                new TranscriptSpool(config),
                sessionId: null); // current session unknown here — reading stdin now would consume it
        }
        if (args.Contains("--claude")) {
            return await Run<ClaudeHookCommand>().Handle(new StringReader(claudeHookBody!));
        }
        if (args.Contains("--codex")) {
            return await Run<CodexHookCommand>().Handle(Console.In);
        }
        if (args.Contains("--cursor")) {
            return await Run<CursorHookCommand>().Handle(Console.In);
        }
        if (args.Contains("--copilot")) {
            return await Run<CopilotHookCommand>().Handle(Console.In, args);
        }
        if (args.Contains("--gemini")) {
            return await Run<GeminiHookCommand>().Handle(Console.In);
        }
        if (args.Contains("--kiro")) {
            return await Run<KiroHookCommand>().Handle(Console.In, args);
        }
        if (args.Contains("--pi")) {
            return await Run<PiHookCommand>().Handle(args, Console.Out);
        }
        if (args.Contains("--opencode")) {
            return await Run<OpenCodeHookCommand>().Handle(args);
        }
        if (args.Contains("--antigravity")) {
            return await Run<AntigravityHookCommand>().Handle(args);
        }
        Console.Error.WriteLine("kcap hook requires a vendor flag (for example --claude)");
        Console.Error.WriteLine("Supported vendors: --claude, --codex, --cursor, --copilot, --gemini, --kiro, --pi, --opencode, --antigravity");
        return 1;
    }
    case "cursor":
        await Console.Error.WriteLineAsync(
            "kcap cursor import has been removed. Use 'kcap import --cursor' instead.");
        return 2;
    // Internal: D0 phase-0 empirical append-only verification harness. Hidden —
    // not in help-usage.txt — run manually against a live Cursor transcript while gathering
    // the D0 evidence; not part of the normal watch/hook/import surface.
    case "cursor-verify-appendonly":
        return await CursorVerifyAppendOnlyCommand.RunAsync(args);
}

Console.Error.WriteLine($"Unknown command: {command}");

return 1;
} catch (UnusableServerUrlException ex) {
    // Returned, not exited: every finally, the telemetry flush and each vendor's fail-open catch
    // still run. Not a crash either — the repair is a config edit, so a crash report would file a
    // bug against the user's own settings.
    Console.Error.WriteLine(ex.Message);

    // A fail-open command's exit code is read by the harness as its verdict — Gemini maps anything
    // outside 0 and 1 to a blocked session — so a misconfigured URL must not be what blocks one.
    return CrashReporter.IsFailOpenCommand(command) ? 0 : 2;
} catch (Exception topLevelEx) {
    CrashReporter.Record(config, command, topLevelEx);

    return CrashReporter.ExitCode(command);
}

} finally {
    await UpdateNotice.FlushAsync(command, args, profiles, config, Run<NpmRegistryClient>);
    await HarnessSetupNotice.FlushAsync(command, config, profiles, home);
}

static string? GetArg(string[] arguments, string flag) {
    var idx = Array.IndexOf(arguments, flag);

    return idx >= 0 && idx + 1 < arguments.Length ? arguments[idx + 1] : null;
}

string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) =>
    ArgParsing.ResolveSessionId(args, skipCount, valueFlags);

async Task PrintUsage() {
    var text = EmbeddedResources.Load("help-usage.txt");
    await Console.Out.WriteAsync(text);
}

async Task<int> PrintCommandHelp(string cmd) {
    var text = EmbeddedResources.TryLoad($"help-{cmd}.txt");

    if (text is not null) {
        await Console.Out.WriteAsync(text);
        return 0;
    }

    Console.Error.WriteLine($"Unknown command: {cmd}");
    Console.Error.WriteLine("Run `kcap --help` for a list of commands.");

    return 1;
}
