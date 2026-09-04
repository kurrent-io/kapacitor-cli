using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;
using Spectre.Console;
using Spectre.Console.Rendering;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Commands;

/// <summary>Setup's step-scoped rendering of façade output: setup owns the guidance tail, while the
/// indent belongs to the renderer underneath — most of the auth copy is composed in there, so shifting
/// it from out here would move the lines this class is handed and leave the rest at column 0.</summary>
sealed class SetupAuthProgress(IAuthProgress inner) : IAuthProgress {
    /// <summary>What a line of setup's own output is shifted by, and what its façade renderer is built
    /// with, so the two halves of a step land on one margin.</summary>
    internal const string StepIndent = "  ";

    internal const string UnreachableGuidance = "Retry later, or pass --server-url <url>.";

    public void Notice(string message) => inner.Notice(message);

    public void Error(string message) => inner.Error(message);

    public void BrowserOpening(string url) => inner.BrowserOpening(url);

    public void DeviceCode(string code, string verificationUri, string? provider, bool prefilled) => inner.DeviceCode(code, verificationUri, provider, prefilled);

    public void PollTick() => inner.PollTick();

    /// <summary>Mapped off <see cref="AuthFailureReason"/>, never off the rendered text.</summary>
    public void ReportFailure(AuthResult result) {
        if (result is AuthResult.Failed { Reason: AuthFailureReason.Unreachable }) inner.Error(UnreachableGuidance);
    }

    // Blank separators and already-indented copy (the device-flow numbered list) pass through as-is.
    internal static string Indent(string message) =>
        string.IsNullOrWhiteSpace(message) || message.StartsWith(' ') ? message : StepIndent + message;
}

/// <summary>The browser leg's rendering, at setup's two-space indent. The URL is printed whether or not
/// a browser opened — a machine with no browser of its own has a human at a different one.</summary>
/// <param name="keys">Only to tell whether there is a keyboard at all: advertising a key that cannot be
/// pressed is worse than not offering the way out.</param>
sealed class SpectreFirstRunFlowProgress(IKeyWatcher? keys = null) : IFirstRunFlowProgress, IDisposable {
    internal static readonly string Offer =
        $"{BrowserFirstRunFlow.HandoverKey} to carry on here  ·  ctrl+c to stop";

    internal const string Unreachable = "Can't reach the server. Still trying…";

    readonly IKeyWatcher      _keys = keys ?? ConsoleKeyWatcher.Instance;
    readonly TerminalWaitLine _wait = new(tty: !Console.IsOutputRedirected);

    FirstRunFlowStep? _step;
    bool              _healthy = true;
    bool              _pickedUp;
    bool              _saidUnreachable;
    bool              _saidOffer;

    public void Opening(string setupUrl) {
        AnsiConsole.MarkupLine(SetupAuthProgress.Indent("Opening your browser to finish setup."));
        AnsiConsole.MarkupLine(SetupAuthProgress.Indent($"[dim]If it didn't open:[/]  {Markup.Escape(setupUrl)}"));

        Refresh();
    }

    public void Waiting(FirstRunFlowStep? flowStep, bool healthy) {
        // Said once per episode rather than per tick, and only where there is no spinner to say it:
        // with one, the line itself changes and repeating it would be a log of the same fact.
        if (!_wait.Pinned && !healthy && !_saidUnreachable)
            AnsiConsole.MarkupLine(SetupAuthProgress.Indent($"[yellow]![/] {Unreachable}"));

        _saidUnreachable = !healthy;

        _step    = flowStep;
        _healthy = healthy;

        Refresh();
    }

    public void Settled(FirstRunFlowStep flowStep, FirstRunStepOutcome outcome, string? detail) {
        // Handover is one-directional: press the key and the terminal spends every step that had
        // settled, but nothing reads the flow again, so later browser clicks go unseen. Once any screen
        // past the gate has an outcome the browser is where the answers are coming from, so we stop
        // advertising a way out that would silently drop the ones still to come. Not sign-in — the CLI
        // held a token before this leg ran, so that step settling is not the user investing anything.
        if (flowStep is not FirstRunFlowStep.SignIn) _pickedUp = true;

        if (StepLine(flowStep, outcome, detail) is { } line) Say(SetupAuthProgress.Indent(line));

        Refresh();
    }

    public void PerformingAction(string capability) {
        // The service ladder writes its own coded lines, so the spinner comes down first for exactly
        // the reason the import takes it down: two live renderables cannot share a console. The shim
        // prompts through osascript and writes nothing, so it keeps the spinner.
        if (capability == FirstRunMachineCapabilities.DaemonService) _wait.Stop();

        Say(SetupAuthProgress.Indent(capability switch {
            FirstRunMachineCapabilities.PathShim =>
                "The browser asked to put kcap on your terminal PATH. "
              + "[dim]Your Mac will ask for your password.[/]",
            FirstRunMachineCapabilities.DaemonService =>
                "The browser asked to run the agent daemon as a service, so this machine stays reachable.",
            _ => "The browser asked this machine to do something this version of kcap does not know."
        }));
    }

    public void ActionEnded() => Refresh();

    public void Discovering() => Say(SetupAuthProgress.Indent("Looking for past sessions on this machine…"));

    public void Importing(int repos, int? sessions) {
        // Takes the spinner down outright: the import renders its own bars, and two live renderables
        // cannot share a console.
        _wait.Stop();

        var what = sessions is { } n
            ? $"{n} session{(n == 1 ? "" : "s")} from {repos} repositor{(repos == 1 ? "y" : "ies")}"
            : $"{repos} repositor{(repos == 1 ? "y" : "ies")}";

        AnsiConsole.MarkupLine(SetupAuthProgress.Indent($"Importing {what}, as chosen in the browser."));
    }

    public void ImportEnded() => Refresh();

    public void WaitEnded() => _wait.Stop();

    /// <summary>A second net under the leg's own <c>finally</c>: nothing may leave a terminal without a
    /// cursor, whatever path the wait ended down.</summary>
    public void Dispose() => _wait.Dispose();

    /// <summary>What one settled step reads as, or null for a step that needs no line: the leg's own
    /// outcome line lands a moment after Done and says the same thing.</summary>
    internal static string? StepLine(FirstRunFlowStep step, FirstRunStepOutcome outcome, string? detail) =>
        step switch {
            FirstRunFlowStep.SignIn => Glyph(outcome, "Signed in"),

            // No detail has two causes and only one is a decline: an answer naming only vendors this
            // build cannot map asks for agents and gets none. Worded to be true of both, since step 4's
            // warning is where the second one's reason and remedy live.
            FirstRunFlowStep.Agents => Glyph(
                outcome,
                detail is null ? "No agents to set up" : $"Agents: {detail}"),

            // Neutral, because a decline settles this step exactly as a selection does and nothing
            // here can tell them apart. What was chosen is the import's own line, or step 6's.
            FirstRunFlowStep.Import => Glyph(outcome, "Chose what to import"),

            _ => null
        };

    static string Glyph(FirstRunStepOutcome outcome, string text) => outcome switch {
        FirstRunStepOutcome.Failed  => $"[yellow]![/] {Markup.Escape(text)}",
        FirstRunStepOutcome.Skipped => $"[dim]·[/] [dim]{Markup.Escape(text)}[/]",
        _                           => $"[green]✓[/] {Markup.Escape(text)}"
    };

    /// <summary>What the spinner says. An unhealthy poll replaces the step's wording outright rather
    /// than decorating it: naming the screen the user is supposedly looking at, while nothing has come
    /// back from the server for minutes, states a fact the CLI does not have.</summary>
    internal static string WaitText(FirstRunFlowStep? step, bool healthy) =>
        !healthy
            ? Unreachable
            : step switch {
                FirstRunFlowStep.SignIn => "Waiting for you to sign in",
                FirstRunFlowStep.Agents => "Choose your harnesses in the browser",
                FirstRunFlowStep.Import => "Choose what to import in the browser",
                FirstRunFlowStep.Done   => "Finishing up in the browser",
                _                       => "Waiting on the browser"
            };

    /// <summary>A line that stays. Goes through the block either way: with one drawn it is written
    /// above it, and with none it is just a line.</summary>
    void Say(string markup) => _wait.WriteAbove(markup);

    /// <summary>Whether the handover offer has to be said as its own line, because there is no pinned
    /// one carrying it. Split out because it is the whole of the decision and nothing else here can be
    /// reached by a test.</summary>
    internal static bool SaysOfferOutright(bool pinned, bool canWatch, bool pickedUp, bool alreadySaid) =>
        !alreadySaid && !pinned && canWatch && !pickedUp;

    /// <summary>The mechanism stays live once the offer is withdrawn — it is also the only way out of a
    /// thirty-minute wait, and a closed tab still needs it. Only the advertisement goes.</summary>
    void Refresh() {
        _wait.Show(WaitText(_step, _healthy), _keys.CanWatch && !_pickedUp ? Offer : null);

        // Once, and only where no pinned line is carrying it: a terminal too narrow to host one, or a
        // wide one that has since narrowed. Latched because a line already scrolled past cannot be
        // taken back, and repeating it every poll would bury the ticks it sits under.
        if (!SaysOfferOutright(_wait.Pinned, _keys.CanWatch, _pickedUp, _saidOffer)) return;

        _saidOffer = true;

        Say(SetupAuthProgress.Indent($"[dim]{Markup.Escape(Offer)}[/]"));
    }
}

/// <summary>
/// The Import step's two halves, over the same <c>kcap import</c> the terminal step runs.
/// </summary>
/// <remarks>
/// Both go through <see cref="ImportCommand.HandleImport"/> rather than reimplementing anything: the
/// screen's job is to choose the arguments a person would otherwise have typed.
/// </remarks>
sealed class SetupImportLane(
        ConfigRoot config,
        ProfileContext profiles,
        UserHome home,
        HarnessPaths paths,
        Func<SetupImportLane.Pass, Task<ImportCommand.ImportRunOutcome?>>? runner = null) : IFirstRunImportLane {
    /// <summary>One invocation's arguments, so a test can assert what each level asked for without
    /// running an import.</summary>
    internal sealed record Pass(
        FirstRunImportLevel                 Level,
        IReadOnlyList<FirstRunImportChoice> Repos,
        DateOnly?                           Since,
        bool                                SkipTitle,
        IReadOnlyList<HarnessId>?           Vendors);

    public async Task<ReportFirstRunImportRequest?> DiscoverAsync(
            IReadOnlyList<HarnessId>? vendors, DateTimeOffset asOf, CancellationToken ct) {
        ImportCommand.ImportDiscoveryResult? found = null;

        // Quiet, because the caller owns the terminal for the duration and the figures go to a screen.
        var exit = await new ImportCommand(config, profiles, home).HandleImport(
            filterCwd:    null,
            sources:      SetupCommand.BuildImportSources(config, paths, vendors),
            discoverOnly: true,
            discoverJson: true,
            windowsAsOf:  asOf,
            onDiscovered: result => found = result,
            // Inert while the discovery call stays quiet, and the reason this is set anyway: un-quieting
            // it would otherwise draw step-weight rules in the middle of the browser leg's own copy.
            nested:       true);

        if (exit != 0 || found is null) return null;

        return Report(found);
    }

    /// <summary>
    /// The summary as the flow's report, capped and disclosed.
    /// </summary>
    /// <remarks>
    /// The cap keeps the newest repositories because the summary already orders them that way. What it
    /// hid is the difference against <c>repo_total</c>, which is what makes the bound disclosable
    /// rather than silent — and an over-long identity is DROPPED rather than truncated, since owner and
    /// name are what resolve back to <c>--repo owner/name</c>.
    /// </remarks>
    internal static ReportFirstRunImportRequest Report(ImportCommand.ImportDiscoveryResult found) {
        var named = found.Summary.Repos
            .Where(r => r.Owner.Length <= ReportFirstRunImportRequest.MaxOwnerLength
                     && r.Name.Length  <= ReportFirstRunImportRequest.MaxNameLength)
            .ToList();

        return new ReportFirstRunImportRequest {
            Repos = [.. named
                .Take(ReportFirstRunImportRequest.MaxRepos)
                .Select(r => new FirstRunImportRepoReport {
                    Owner         = r.Owner,
                    Name          = r.Name,
                    Sessions      = new Dictionary<string, int>(r.SessionsByWindow, StringComparer.Ordinal),
                    LastSessionAt = r.LastSessionAt
                })],
            Unmatched = new Dictionary<string, int>(found.Summary.UnmatchedByWindow, StringComparer.Ordinal),
            RepoTotal = found.Summary.Repos.Count,
            Vendors   = [.. found.ScannedVendors.Select(v => v.VendorId)]
        };
    }

    /// <summary>Whether every pass this lane ran landed. False is what stops the closing summary
    /// reporting a backfill that did not happen.</summary>
    public bool Failed { get; private set; }

    async Task<ImportCommand.ImportRunOutcome?> Run(Pass pass) {
        ImportCommand.ImportRunOutcome? outcome = null;

        await new ImportCommand(config, profiles, home).HandleImport(
            filterCwd:          null,
            sources:            SetupCommand.BuildImportSources(config, paths, pass.Vendors),
            since:              pass.Since,
            scope:              new ImportScope.Repo([.. pass.Repos.Select(c => (c.Owner, c.Name))]),
            skipConfirmation:   true,
            forcePrivate:       pass.Level is FirstRunImportLevel.OnlyMe,
            autoSkipExclusions: true,
            skipTitle:          pass.SkipTitle,
            // What makes the shared stop honest, since the profile default cannot reach the class the
            // visibility predicate admits unconditionally.
            shareWithOrg:       pass.Level is FirstRunImportLevel.Shared,
            onFinished:         o => outcome = o,
            nested:             true);

        return outcome;
    }

    public async Task<FirstRunImportTotals?> ImportAsync(
            FirstRunImportAnswer answer, DateOnly today, CancellationToken ct) {
        var since   = answer.Since(today);
        var totals  = new FirstRunImportTotals(0, 0, 0);
        var counted = true;

        // One pass per level, because --private is per invocation. Ordered narrowest first so a run
        // interrupted between them has uploaded the private history rather than the shared.
        foreach (var level in (FirstRunImportLevel[])[FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared]) {
            if (answer.At(level) is not { Count: > 0 } chosen) continue;

            ImportCommand.ImportRunOutcome? outcome;

            // Per pass, so a throw in the private one does not cancel the shared one, and so a
            // failure that arrived as an exception counts the same as one the run reported.
            try {
                outcome = await (runner ?? Run)(
                    new Pass(level, chosen, since, answer.SkipTitle, answer.Vendors));
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                Failed = true;

                throw;
            } catch (Exception ex) {
                Failed  = true;
                counted = false;

                AnsiConsole.MarkupLine(
                    $"  [yellow]![/] That history did not import: {Markup.Escape(ex.Message)}. "
                  + "Run [cyan]kcap import[/] to retry it.");

                continue;
            }

            // The exit code cannot answer this: an import is best-effort and returns 0 for a run whose
            // sessions failed, so reading it would call a partial or total failure a success. A run
            // that reported nothing at all did not reach its Done grid, which is a failure too.
            if (outcome is null || outcome.AnythingFailed) {
                Failed = true;

                AnsiConsole.MarkupLine(
                    "  [yellow]![/] Some of that history did not import. Run [cyan]kcap import[/] to retry it.");
            }

            if (outcome is null) {
                counted = false;

                continue;
            }

            // A session the visibility preflight held back never reached the upload, so it is in none of
            // the run's three counts — and re-running retries it, which is what `failed` means here.
            totals += new FirstRunImportTotals(
                outcome.Counts.Imported,
                outcome.Counts.Skipped,
                outcome.Counts.Failed + outcome.VisibilityFailures);
        }

        return counted ? totals : null;
    }
}

/// <summary>
/// What this host can do to the machine when the browser asks. Each capability reaches the same ladder
/// its own <c>kcap</c> verb runs, so the outcome the screen renders is the outcome the terminal would
/// have printed.
/// </summary>
/// <summary>
/// What this host can do to the machine <i>while the flow is live</i>. One capability, reaching the same
/// ladder <c>kcap daemon shim ensure</c> runs, so the outcome the screen renders is the outcome the
/// terminal would have printed.
///
/// <para><b>The daemon service is deliberately absent.</b> Its unit bakes settings the steps after this
/// leg have not written yet, so it is performed at the end of setup instead — see
/// <see cref="SetupDaemonService"/>. The loop leaves an unadvertised capability outstanding rather than
/// reporting it, which is the state the screen already renders as having asked.</para>
/// </summary>
sealed class SetupMachineActions : IFirstRunMachineActions {
    public IReadOnlyCollection<string> Capabilities { get; } = [FirstRunMachineCapabilities.PathShim];

    public async Task<FirstRunMachineActionResult> PerformAsync(string capability, CancellationToken ct) {
        if (capability != FirstRunMachineCapabilities.PathShim)
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Not a capability this host advertises.");

        var result = await DaemonShimCommands.EvaluateAsync(ct: ct);

        return new FirstRunMachineActionResult(result.Outcome, result.Reason);
    }
}

public sealed class SetupCommand(ConfigRoot config, ProfileContext profiles, IBrowserLauncher browser, UserHome home) {
    readonly HarnessPaths _paths = HarnessPaths.FromEnvironment(home);

    public async Task<int> HandleAsync(string[] args) {
        var serverUrlArg     = GetArg(args, "--server-url");

        // `kcap setup <tenant>`: a leading positional arg (bare slug or full URL) is treated as the
        // server, equivalent to --server-url. A bare single label expands to {slug}.kcap.ai.
        if (serverUrlArg is null && args.Length > 1 && !args[1].StartsWith('-'))
            serverUrlArg = ServerInput.ResolveTenantArg(args[1]);
        var noPrompt         = args.Contains("--no-prompt");
        // Also when there is no keyboard: a redirected stdin cannot press the escape-hatch key, so a
        // loopback wait there can only end in the listener timeout. Not a headless guess - an
        // interactive SSH session has a keyboard and keeps the browser.
        var forceDevice      = OAuthLoginFlow.DeviceRouteRequired(args.Contains("--device"), ConsoleKeyWatcher.Instance.CanWatch);
        var skipClaudeFlag   = args.Contains("--skip-claude-hooks");
        var skipCodexFlag    = args.Contains("--skip-codex-hooks");
        var skipCodexNetworkFlag = args.Contains("--skip-codex-network-access");
        var skipCursorFlag   = args.Contains("--skip-cursor-hooks");
        var skipCursorMcpFlag = args.Contains("--skip-cursor-mcp");
        var skipCopilotFlag  = args.Contains("--skip-copilot-hooks");
        var skipCopilotMcpFlag = args.Contains("--skip-copilot-mcp");
        var skipCopilotInstructionsFlag = args.Contains("--skip-copilot-instructions");
        var skipGeminiFlag   = args.Contains("--skip-gemini-hooks");
        var skipGeminiMcpFlag = args.Contains("--skip-gemini-mcp");
        var skipGeminiInstructionsFlag = args.Contains("--skip-gemini-instructions");
        var skipKiroFlag     = args.Contains("--skip-kiro-hooks");
        var skipKiroMcpFlag  = args.Contains("--skip-kiro-mcp");
        var skipKiroSkillsFlag = args.Contains("--skip-kiro-skills");
        var skipPiFlag       = args.Contains("--skip-pi-hooks");
        var skipPiMcpFlag    = args.Contains("--skip-pi-mcp");
        var skipPiInstructionsFlag = args.Contains("--skip-pi-instructions");
        var skipOpenCodeFlag = args.Contains("--skip-opencode-hooks");
        var skipOpenCodeMcpFlag = args.Contains("--skip-opencode-mcp");
        var skipOpenCodeInstructionsFlag = args.Contains("--skip-opencode-instructions");
        var skipAntigravityFlag = args.Contains("--skip-antigravity-hooks");
        var skipAntigravityMcpFlag = args.Contains("--skip-antigravity-mcp");
        var skipAntigravityInstructionsFlag = args.Contains("--skip-antigravity-instructions");
        var skipAntigravitySkillsFlag = args.Contains("--skip-antigravity-skills");
        var skipImport       = args.Contains("--skip-import");
        var legacyPluginScope = GetArg(args, "--plugin-scope"); // "user" | "project" | "skip" | null
        var skipClaude       = skipClaudeFlag || legacyPluginScope == "skip";
        var legacyProjectScope = legacyPluginScope == "project";

        // Presence, not value: `--server-url` with nothing after it parses as absent, and pairing that
        // with --org/--slug would read as "create one" and do something irreversible.
        var serverUrlGiven = serverUrlArg is not null
                          || args.Contains("--server-url")
                          || args.Any(a => a.StartsWith("--server-url=", StringComparison.Ordinal));

        var (requestedWorkspace, workspaceArgError) = ParseRequestedWorkspace(args, serverUrlGiven);

        if (workspaceArgError is not null) {
            await Console.Error.WriteLineAsync($"  {workspaceArgError}");
            return 1;
        }

        // The flags settle the creation questions and nothing after them, so on a session that cannot
        // be asked anything they buy a created workspace followed by a throw at the first step that
        // still prompts. Refusing here costs a flag; letting it run costs a workspace.
        if (requestedWorkspace is not null && !noPrompt && !AnsiConsole.Profile.Capabilities.Interactive) {
            await Console.Error.WriteLineAsync(
                "  --org/--slug answer the workspace questions only; the steps after them still prompt, and this session is non-interactive.");
            await Console.Error.WriteLineAsync("  Add --no-prompt.");

            return 1;
        }

        var profile = await AppConfig.LoadProfileConfig(config);

        SetupFunnel.Started(
            hasExistingProfile: AppConfig.HasConfiguredProfile(profile),
            serverUrlProvided:  serverUrlArg is not null,
            noPrompt:           noPrompt);
        // Resolve repo root once and reuse for both the project-scope install path and the
        // non-repo tip at the end. --plugin-scope project writes hooks at <repo>/.claude/...,
        // so it requires a working tree; without one the hooks would land in a directory
        // unrelated to any project, or — worse — under a subdirectory of the repo if we
        // used cwd directly, which means two devs running setup from different subdirs
        // install hooks in different places.
        var gitRoot = GitRepository.FindRoot(Environment.CurrentDirectory);

        if (legacyProjectScope && gitRoot is null) {
            await Console.Error.WriteLineAsync(
                $"--plugin-scope project requires a git working tree, but '{Environment.CurrentDirectory}' is not inside one.");
            await Console.Error.WriteLineAsync(
                "Either re-run `kcap setup` from inside your repo, or drop --plugin-scope project to install user-scope hooks.");
            return 1;
        }
        AnsiConsole.Write(new Rule("[bold green]Welcome to Capacitor[/]").Centered());

        // Check if already configured
        var activeProfile  = profile.ActiveName;
        var existing       = profile.Profiles.GetValueOrDefault(activeProfile);
        var existingTokens = await new TokenStore(config).LoadAsync(activeProfile);

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            var rerun = AnsiConsole.Prompt(
                new ConfirmationPrompt($"Already configured for [cyan]{Markup.Escape(existing.ServerUrl)}[/] as [cyan]{Markup.Escape(existingTokens.GitHubUsername ?? "?")}[/]. Re-run setup?")
                    { DefaultValue = false });

            if (!rerun) {
                AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

                return 0;
            }
        }

        // Step 1: Server
        AnsiConsole.Write(new Rule("[yellow]Step 1/6 — Server[/]").LeftJustified());
        string serverUrl;
        string  provider;
        bool    loginComplete = false; // Discovery authenticates inline; skip the Step-2 login.

        if (serverUrlArg is not null) {
            var resolved = await ResolveServerAndProviderAsync(serverUrlArg);
            if (resolved is null) return 1;

            (serverUrl, provider) = resolved.Value;
        } else if (noPrompt && requestedWorkspace is null) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");
            await Console.Error.WriteLineAsync("  (or --org \"<name>\" --slug <slug> to create a workspace)");
            return 1;
        } else {
            var discovered = await RunDiscoveryAsync(args, forceDevice, requestedWorkspace);
            if (discovered is null) return 1;
            (serverUrl, provider, loginComplete) = discovered.Value;

            // Discovery activates the tenant you picked, so the profile captured before it ran is
            // now stale. Step 2 must save the token under the profile setup will actually
            // configure, or the token lands on the old profile and the new one has none.
            var afterDiscovery = await AppConfig.LoadProfileConfig(config);
            activeProfile = string.IsNullOrWhiteSpace(afterDiscovery.ActiveProfile)
                ? "default"
                : afterDiscovery.ActiveProfile;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        AnsiConsole.Write(new Rule("[yellow]Step 2/6 — Login[/]").LeftJustified());

        var loginStepResult = await RunLoginStepAsync(loginComplete, provider, serverUrl, forceDevice, activeProfile);
        if (loginStepResult != 0) return loginStepResult;

        // The browser leg, where the tenant serves one. Unnumbered because it is not a step: the
        // steps below run either way, and on every tenant that has not turned the flow on this
        // returns without a word. It has to sit after login — both routes are authenticated.
        var browserAnswers = await RunBrowserFlowStepAsync(serverUrl, provider, noPrompt);
        var browserAgents  = browserAnswers.Agents;

        await Console.Out.WriteLineAsync();

        // Step 3: Default session visibility
        AnsiConsole.Write(new Rule("[yellow]Step 3/6 — Default session visibility[/]").LeftJustified());

        string defaultVisibility;

        // The flow's answer, resolved once above the branches so the rule sits in one testable place.
        // The profile is read only where the answer might defer to it — a run with no flow needs none.
        var flowVisibility = noPrompt || browserAgents is null
            ? new VisibilityDecision(null, Kept: false)
            : DecideVisibility(
                  browserAgents,
                  (await AppConfig.LoadProfileConfig(config))
                      .Profiles.GetValueOrDefault(activeProfile)?.DefaultVisibility ?? "org_public");

        if (noPrompt) {
            defaultVisibility = (GetArg(args, "--default-visibility") ?? "org_public").ToLowerInvariant();

            if (!AppConfig.ValidVisibilities.Contains(defaultVisibility)) {
                await Console.Error.WriteLineAsync($"  Invalid default-visibility: {defaultVisibility}. Must be: {string.Join(", ", AppConfig.ValidVisibilities)}");

                return 1;
            }

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        } else if (flowVisibility.Apply is { } fromFlow) {
            // Re-writing the profile's own value is the no-op that keeps the rest of the run — the
            // import stamp, the summary — reading one field rather than two.
            defaultVisibility = fromFlow;

            AnsiConsole.MarkupLine(flowVisibility.Kept
                ? $"  [dim]· Not chosen in the browser - keeping {Markup.Escape(VisibilityLabel(fromFlow))}[/]"
                : $"  [dim]· Chosen in the browser: {Markup.Escape(VisibilityLabel(fromFlow))}[/]");
        } else {
            var visibilityPrompt = new SelectionPrompt<string>()
                .Title("Which of your sessions should be readable by other users in the same Kurrent Capacitor account by default?")
                .AddChoices(AppConfig.ValidVisibilities)
                .UseConverter(VisibilityLabel);

            // Start the cursor on the option we label "(default)" rather than the first choice.
            visibilityPrompt.DefaultValue = "org_public";

            defaultVisibility = AnsiConsole.Prompt(visibilityPrompt);

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Harnesses
        AnsiConsole.Write(new Rule("[yellow]Step 4/6 — Harnesses[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  Capacitor records sessions by installing hooks into your harnesses.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        // Composed once in Core so the probe set is testable without touching the real
        // environment — see Capacitor.Cli.Core.Setup.AgentDetection for the per-vendor
        // rationale (dual PATH + install-marker signals, Cursor's marker-only exception, etc).
        var harnesses  = HarnessRegistry.FromEnvironment(home);
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude:      harnesses.Detected(HarnessId.Claude),
            Codex:       harnesses.Detected(HarnessId.Codex),
            Cursor:      harnesses.Detected(HarnessId.Cursor),
            Copilot:     harnesses.Detected(HarnessId.Copilot),
            Gemini:      harnesses.Detected(HarnessId.Gemini),
            Kiro:        harnesses.Detected(HarnessId.Kiro),
            Pi:          harnesses.Detected(HarnessId.Pi),
            OpenCode:    harnesses.Detected(HarnessId.OpenCode),
            Antigravity: harnesses.Detected(HarnessId.Antigravity));

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        var detectedSummary = SetupDecisions.DetectedAgentsSummary(detected);

        if (detectedSummary is not null)
            await Console.Out.WriteLineAsync($"  Detected harnesses: {detectedSummary}");

        bool installAgents;

        if (browserAgents is { } answered) {
            // Asked and answered in the browser minutes ago, so this step applies rather than
            // re-asks — the flow settles its Agents step on the decision being recorded, not on the
            // install finishing, which is what leaves the work here.
            foreach (var line in BrowserAgentsSummary(answered)) AnsiConsole.MarkupLine(line);

            // Nothing understood is not consent. A decline asks for nothing, and an answer whose every
            // entry named a vendor this build has never heard of asks for nothing this build can do —
            // and the step's own writes are gated on this, not on the per-vendor skips.
            installAgents = answered.Choices.Count > 0;
        } else {
            // The single install-consent decision, replacing the nine per-vendor prompts. Made
            // BEFORE CodingAgentsStep.Options is constructed, so it uses the LOCAL `noPrompt` (there
            // is no `options` object yet). NoPrompt alone would not imply InstallAgents, so this must
            // be set explicitly here or `--no-prompt` would silently stop installing agents.
            installAgents = SetupDecisions.DecideInstallAgents(detected, noPrompt, PromptYesNo);
        }

        // gitRoot is guaranteed non-null here when legacyProjectScope is true (the early
        // guard at the top of HandleAsync returns 1 otherwise).
        var claudeSettingsPath = legacyProjectScope
            ? Path.Combine(gitRoot!, ".claude", "settings.local.json")
            : _paths.Claude.UserSettings;

        var stepOptions = new CodingAgentsStep.Options(
            SkipClaude:  skipClaude,
            SkipCodex:   skipCodexFlag,
            SkipCursor:  skipCursorFlag,
            SkipCopilot: skipCopilotFlag,
            SkipGemini:  skipGeminiFlag,
            SkipKiro:    skipKiroFlag,
            SkipPi:      skipPiFlag,
            SkipOpenCode: skipOpenCodeFlag,
            SkipAntigravity: skipAntigravityFlag,
            NoPrompt:    noPrompt,
            SkipCodexNetworkAccess: skipCodexNetworkFlag,
            SkipCursorMcp: skipCursorMcpFlag,
            SkipCopilotMcp: skipCopilotMcpFlag,
            SkipCopilotInstructions: skipCopilotInstructionsFlag,
            SkipGeminiMcp: skipGeminiMcpFlag,
            SkipGeminiInstructions: skipGeminiInstructionsFlag,
            SkipAntigravityMcp: skipAntigravityMcpFlag,
            SkipAntigravityInstructions: skipAntigravityInstructionsFlag,
            SkipAntigravitySkills: skipAntigravitySkillsFlag,
            SkipOpenCodeMcp: skipOpenCodeMcpFlag,
            SkipOpenCodeInstructions: skipOpenCodeInstructionsFlag,
            SkipKiroMcp: skipKiroMcpFlag,
            SkipKiroSkills: skipKiroSkillsFlag,
            SkipPiMcp: skipPiMcpFlag,
            SkipPiInstructions: skipPiInstructionsFlag,
            InstallAgents: installAgents);

        stepOptions = SetupDecisions.WithBrowserAnswer(stepOptions, browserAgents);

        // allowlist the Capacitor server(s) Codex skills need to reach. A single
        // **.kcap.ai wildcard covers every SaaS tenant (current + future) and the auth
        // proxy; self-hosted servers are added as exact hosts. Derived from the active
        // server URL plus every configured profile so switching profiles still works.
        var profilesForDomains = await AppConfig.LoadProfileConfig(config);

        // Every profile's server, EXCEPT on the browser path. The Agents screen discloses this on the
        // Codex row — "also opens Codex's sandbox network to your server" — and that sentence is about
        // one server, the one they are setting up. Consent to reach it is not consent to reach every
        // tenant this machine has ever been pointed at.
        var codexAllowDomains = CodexConfigToml.BuildAllowDomains(
            browserAgents is null
                ? new[] { serverUrl }.Concat(profilesForDomains.Profiles.Values.Select(p => p.ServerUrl))
                : [serverUrl]);

        var copilot  = _paths.Copilot;
        var pi       = _paths.Pi;
        var kiro     = _paths.Kiro;
        var codex    = _paths.Codex;
        var opencode = _paths.OpenCode;
        var cursor   = _paths.Cursor;
        var gemini   = _paths.Gemini;
        var agy      = _paths.Antigravity;

        var stepPaths = new CodingAgentsStep.Paths(
            ClaudeSettingsPath:   claudeSettingsPath,
            ClaudeScopeLabel:     legacyProjectScope ? "project" : "user",
            PluginDir:            pluginPath,
            CodexHooksPath:       codex.UserHooksJson,
            CursorHooksPath:      cursor.UserHooksJson,
            CopilotHooksPath:     copilot.KcapHooksJson,
            GeminiSettingsPath:   gemini.SettingsJson,
            AgentsSkillsDir:      _paths.Agents.UserSkillsDir,
            LegacyCodexSkillsDir: codex.SkillsDir,
            KiroHooksPath:        kiro.KcapAgentJson,
            PiExtensionPath:      pi.KcapExtension,
            OpenCodeExtensionPath: opencode.KcapPlugin,
            AntigravityHooksPath: agy.GlobalHooksJson,
            CodexConfigTomlPath:  codex.ConfigToml,
            CursorMcpPath:        cursor.UserMcpJson,
            CopilotMcpPath:       copilot.McpConfigJson,
            CopilotInstructionsPath: copilot.InstructionsMd,
            GeminiInstructionsPath: gemini.GeminiMd,
            AntigravityMcpPath:       agy.McpConfigJson,
            AntigravityInstructionsPath: agy.InstructionsMd,
            AntigravitySkillsDir:     agy.SkillsDir,
            OpenCodeMcpPath:      opencode.McpConfigJson,
            OpenCodeInstructionsPath: opencode.AgentsMd,
            KiroMcpPath:          kiro.SettingsMcpJson,
            KiroSkillsDir:        kiro.SkillsDir,
            PiMcpExtensionPath:   pi.KcapMcpExtension,
            PiAgentsMdPath:       pi.AgentsMd);

        var stepInstallers = new CodingAgentsStep.Installers(
            InstallClaudePlugin:    InstallPlugin,
            InstallCodexHooks:      PluginCommand.InstallCodexHooks,
            InstallCursorHooks:     PluginCommand.InstallCursorHooks,
            InstallCopilotHooks:    PluginCommand.InstallCopilotHooks,
            InstallGeminiHooks:     PluginCommand.InstallGeminiHooks,
            CapacitorOnPath:        () => BinaryProbe.OnPath("kcap"),
            InstallAgentSkills:     AgentsSkillsInstaller.Install,
            CleanLegacyCodexSkills: legacyDir => AgentsSkillsInstaller.CleanLegacyCodexSkills(legacyDir).RemovedAny,
            InstallKiroHooks:       PluginCommand.InstallKiroHooks,
            InstallPiExtension:     PiExtensionInstaller.Install,
            InstallOpenCodeExtension: OpenCodeExtensionInstaller.Install,
            InstallAntigravityHooks:  PluginCommand.InstallAntigravityHooks,
            EnableCodexNetworkAccess: () => CodexConfigToml.EnableNetworkAccess(codexAllowDomains, codex.ConfigToml),
            RegisterCodexMcp:         () => CodexConfigToml.RegisterKcapMcpServers(codex.ConfigToml),
            // every non-Claude JSON harness registers the ForCursor subset — the full set,
            // kcap-workitems included (see KcapMcpServers.ForCursor).
            RegisterCursorMcp:        () => HarnessMcpProjections.Cursor.Register(cursor.UserMcpJson, home),
            RegisterCopilotMcp:       () => HarnessMcpProjections.Copilot.Register(copilot.McpConfigJson, home),
            InstallCopilotInstructions: () => AgentInstructionsWriter.Write(
                copilot.InstructionsMd, KcapAgentInstructions.Body),
            // Skills are already current when the on-disk marker matches this build AND
            // every owned kcap-* folder is present; used to skip the prompt + re-copy
            // (mirrors PluginCommand's postinstall fast path). A missing/stale marker — or a
            // deleted skill folder — reads as "not current" → prompt + install (self-heals).
            AgentSkillsCurrent:       AgentsSkillsInstaller.IsCurrent,
            RegisterOpenCodeMcp:      () => HarnessMcpProjections.OpenCode.Register(opencode.McpConfigJson, home),
            InstallOpenCodeInstructions: () => AgentInstructionsWriter.Write(
                opencode.AgentsMd, KcapAgentInstructions.Body),
            RegisterKiroMcp:          () => HarnessMcpProjections.Kiro.Register(kiro.SettingsMcpJson, home),
            RegisterGeminiMcp:        () => HarnessMcpProjections.Gemini.Register(gemini.SettingsJson, home),
            InstallGeminiInstructions: () => AgentInstructionsWriter.Write(
                gemini.GeminiMd, KcapAgentInstructions.Body),
            RegisterAntigravityMcp:   () => HarnessMcpProjections.Antigravity.Register(agy.McpConfigJson, home),
            InstallAntigravityInstructions: () => AgentInstructionsWriter.Write(
                agy.InstructionsMd, KcapAgentInstructions.Body),
            // Pi has no JSON MCP config — the "MCP" is a second extension file (kcap-mcp.ts).
            InstallPiMcp:             PiMcpExtensionInstaller.Install,
            InstallPiInstructions:    () => AgentInstructionsWriter.Write(
                pi.AgentsMd, KcapAgentInstructions.Body));

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var installResult = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

        // Record that setup offered these detected agents, so the new-harness nudge doesn't later
        // re-offer a vendor the user just saw at the Step 4 prompt (whether they said yes or no).
        // A vendor skipped by its own --skip-<vendor> flag was not meaningfully offered, so it is
        // left unstamped and can still nudge later. Never writes/overwrites a dismissal.
        var offeredNow = new List<HarnessId>();
        void OfferedIf(bool wasDetected, bool skipped, HarnessId id) { if (wasDetected && !skipped) offeredNow.Add(id); }
        OfferedIf(detected.Claude,      skipClaude,          HarnessId.Claude);
        OfferedIf(detected.Codex,       skipCodexFlag,       HarnessId.Codex);
        OfferedIf(detected.Cursor,      skipCursorFlag,      HarnessId.Cursor);
        OfferedIf(detected.Copilot,     skipCopilotFlag,     HarnessId.Copilot);
        OfferedIf(detected.Gemini,      skipGeminiFlag,      HarnessId.Gemini);
        OfferedIf(detected.Kiro,        skipKiroFlag,        HarnessId.Kiro);
        OfferedIf(detected.Pi,          skipPiFlag,          HarnessId.Pi);
        OfferedIf(detected.OpenCode,    skipOpenCodeFlag,    HarnessId.OpenCode);
        OfferedIf(detected.Antigravity, skipAntigravityFlag, HarnessId.Antigravity);
        new HarnessOfferStore(config).StampOffered(offeredNow, DateTimeOffset.UtcNow);

        // Provider API key handling. kcap scrubs ANTHROPIC_API_KEY / OPENAI_API_KEY
        // from headless agent CLI spawns by default so subscription auth
        // wins. PAYG users with the keys set in their environment can opt back in
        // here; the rest never see this prompt.
        var anthropicSet     = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var openaiSet        = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var promptApiKey      = (anthropicSet && !skipClaude) || (openaiSet && !skipCodexFlag);
        // Preserve any previous opt-in when no key is in the current env (we just
        // don't have anything to prompt about; the on-disk value is still valid).
        var useProviderApiKey = existing?.UseProviderApiKey ?? false;

        if (promptApiKey) {
            await Console.Out.WriteLineAsync();

            var keys = (anthropicSet, openaiSet) switch {
                (true, true)  => "ANTHROPIC_API_KEY and OPENAI_API_KEY are set",
                (true, false) => "ANTHROPIC_API_KEY is set",
                (false, true) => "OPENAI_API_KEY is set",
                _             => ""
            };

            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(keys)} in your environment.[/]");
            AnsiConsole.MarkupLine("  [dim]By default kcap scrubs these when spawning Claude/Codex for headless calls so your[/]");
            AnsiConsole.MarkupLine("  [dim]subscription login is used. Keep them if you authenticate via API key (PAYG).[/]");

            if (noPrompt) {
                var flagValue = GetArg(args, "--use-provider-api-key");
                if (flagValue is not null) {
                    var parsed = ProviderApiKeyPolicy.TryParseBool(flagValue);
                    if (parsed is null) {
                        await Console.Error.WriteLineAsync(
                            $"  Invalid value for --use-provider-api-key: '{flagValue}'. Must be true/1/yes/on or false/0/no/off.");
                        return 1;
                    }
                    useProviderApiKey = parsed.Value;
                }
                await Console.Out.WriteLineAsync($"  Use provider API key: {useProviderApiKey}");
            } else {
                useProviderApiKey = AnsiConsole.Prompt(
                    new ConfirmationPrompt("  Use these API keys for kcap's headless calls?") { DefaultValue = useProviderApiKey });
            }
        }

        await Console.Out.WriteLineAsync();

        // Step 5: Daemon name + save
        AnsiConsole.Write(new Rule("[yellow]Step 5/6 — Agent Daemon[/]").LeftJustified());

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            await Console.Out.WriteLineAsync($"  Daemon name: {daemonName}");
        } else {
            daemonName = AnsiConsole.Prompt(
                new TextPrompt<string>("Daemon name:")
                    .DefaultValue(defaultName)
                    .ShowDefaultValue());
        }

        await Console.Out.WriteLineAsync();

        // Save config
        var activeName     = "default";
        var defaultProfile = new Profile();

        await ConfigMutator.MutateAsync(config, c => {
            activeName     = c.ActiveName;
            defaultProfile = c.Profiles.GetValueOrDefault(activeName) ?? new Profile();

            defaultProfile = defaultProfile with {
                ServerUrl          = serverUrl,
                DefaultVisibility  = defaultVisibility,
                UseProviderApiKey  = useProviderApiKey,
                Daemon             = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
            };

            return c with {
                Profiles = new Dictionary<string, Profile>(c.Profiles) { [activeName] = defaultProfile }
            };
        });

        // The exact values just saved, for the same-process work below (the import step). Built
        // rather than re-resolved: CLI/env/repo precedence could land on something else than what
        // this run just wrote.
        var saved = new ProfileContext(
            new(serverUrl, activeName, defaultProfile, null), await AppConfig.LoadProfileConfig(config));

        // Here rather than in the browser leg, and after the write above rather than before it: the unit
        // bakes the profile, the expected server and the daemon name, and `saved` is the only context that
        // carries what this run actually chose.
        if (browserAnswers.FlowId is { } browserFlowId) {
            try {
                // Through the same authenticated choke point the leg uses. Every flow route is
                // authenticated, and a poll that 401s answers an empty body — indistinguishable from
                // nothing having been asked — so an unusable client here enables nothing, silently.
                var (deferredHttp, deferredAuth) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                    config, saved, serverUrl, autoRetryUnauthorized: true);

                using var deferred = deferredHttp;

                // The beat holds its lane until a request finishes, so this client's timeout is how long a
                // wedged connection can silence liveness. Left unset it is the 100s default; matched to the
                // browser leg's so both halves of the flow answer for silence on the same scale.
                deferred.Timeout = BrowserFlowHttpTimeout;

                // The status is the point of the factory: it hands back a client either way, and an
                // expired or missing token leaves one that cannot poll. Checked rather than assumed, or
                // the silent path is exactly the one above with a better-looking constructor.
                //
                // NoAuthRequired runs it. That status means the client is usable as it stands, so
                // skipping on it would leave the request outstanding on a server that would have
                // answered — the browser leg skips there only because it has nothing left to do.
                if (deferredAuth is AuthStatus.Ok or AuthStatus.NoAuthRequired) {
                    var channel = new FirstRunFlowClient(deferred);

                    // The browser is holding the request this leg is about to perform, and enabling a
                    // service can outlast any staleness window — so the beat has to span it. Only for a
                    // flow that finished: every other ending already relinquished, and beating again
                    // would take that back.
                    using var beat = browserAnswers.FlowStillLive
                        ? FirstRunHeartbeat.Start(channel, serverUrl, browserFlowId, TimeProvider.System)
                        : null;

                    await SetupDaemonService.RunAsync(
                        channel, serverUrl, browserFlowId, config, saved, home);
                }
                else
                    AnsiConsole.MarkupLine(
                        "  [dim]Did not finish the daemon service request: the stored token is not usable. "
                      + "Run 'kcap login', then 'kcap daemon service ensure'.[/]");
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Best-effort, as the rest of this leg is: the request stays outstanding and the screen
                // goes on saying it asked, which is honest. Setup finishing matters more.
                AnsiConsole.MarkupLine(
                    $"  [yellow]![/] Could not finish the daemon service request: {Markup.Escape(ex.Message)}");
            }
        }

        var finalTokens = await new TokenStore(config).LoadAsync(activeName);

        // tell the server this user has finished CLI setup, so the dashboard
        // can flip the new-tenant welcome modal from "Waiting for CLI to register"
        // to "Registered". Best-effort — never block setup completion on this.
        await PingCliSetupAsync(serverUrl, activeName, provider);

        await Console.Out.WriteLineAsync();

        // Step 6: Import past sessions
        AnsiConsole.Write(new Rule("[yellow]Step 6/6 — Import past sessions[/]").LeftJustified());

        // detectPullRequest:false — Step 6 only needs (owner, name) to scope the repo import;
        // PR/MR detection would run extra provider probes/subprocesses for nothing here.
        var currentRepoDetected = await RepositoryDetection.DetectRepositoryAsync(
            config, Environment.CurrentDirectory, detectPullRequest: false);
        (string Owner, string Name)? currentRepo = currentRepoDetected is { Owner: { } o, RepoName: { } n }
            ? (o, n)
            : null;

        // Auth requirements are satisfied when no login is required at all (provider
        // None), or the login this run just did (or already had) produced a usable,
        // non-expired token — not merely "provider != None" (Decision 9).
        // "usable token" = refresh-aware (mirrors the import path's own auth): an expired but
        // refreshable token still counts. The probe is wrapped so a token I/O / refresh failure
        // degrades to an ineligible (best-effort) skip rather than throwing out of setup — the
        // import path's own errors are caught inside RunImportStepAsync, and this eligibility
        // probe (awaited outside that boundary) must be equally non-fatal.
        // Server-scoped: the import step is only actually authorized if the token both refreshes
        // and belongs to the server we just configured.
        var authSatisfied = await IsAuthSatisfiedAsync(
            provider, async () => (await new TokenStore(config).GetValidTokensForServerAsync(activeName, serverUrl)).Tokens is not null);

        await RunImportStepAsync(
            currentRepo, authSatisfied, skipImport, noPrompt,
            () => AnsiConsole.Prompt(new ConfirmationPrompt("Import past sessions from this repository?") { DefaultValue = true }),
            saved,
            defaultVisibility,
            browserAnswers.Import,
            browserAnswers.ImportFailed);

        await Console.Out.WriteLineAsync();

        AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
        grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
        grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

        if (useProviderApiKey) {
            grid.AddRow("[bold]Provider API key[/]", "kept in headless spawns");
        }

        if (finalTokens is not null) {
            grid.AddRow("[bold]Auth[/]", Markup.Escape(finalTokens.GitHubUsername ?? "?"));
        }

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath(config)));

        AnsiConsole.Write(new Padder(grid).Padding(2, 0, 0, 0));

        // hooks only load at coding-agent session start. The common case is a user
        // running `kcap setup` from inside an already-running session, which won't stream
        // live until it restarts — so tell them, but only when something was actually
        // installed (no point promising recording we never wired up).
        var restartTip = LiveRecordingRestartTip(installResult);
        if (restartTip is not null) AnsiConsole.MarkupLine($"\n  {restartTip}");

        // Setup itself is user-scope and works fine outside a repo, but sessions recorded
        // from non-repo directories have no owner/repo/branch/PR enrichment (see
        // RepositoryDetection.DetectRepositoryAsync), which weakens grouping in the UI.
        if (gitRoot is null) {
            AnsiConsole.MarkupLine(
                $"\n  [yellow]Tip:[/] you ran setup outside a git working tree ([dim]{Markup.Escape(Environment.CurrentDirectory)}[/]).");
            AnsiConsole.MarkupLine(
                "    Hooks fire from any directory, but sessions recorded outside a repo won't include owner/repo/branch context.");
            AnsiConsole.MarkupLine(
                "    [dim]cd[/] into your project before recording to capture full session context.");
        }

        AnsiConsole.MarkupLine("\n  [dim]Optional:[/] start the daemon with [cyan]kcap daemon start -d[/]");
        AnsiConsole.MarkupLine("  [dim]Optional:[/] import past sessions with [cyan]kcap import --org[/]");

        WriteNextSteps(ShouldOfferGuidedTour(detectedSummary is not null, claudeSettingsPath, stepPaths));

        // Same fields as Result.AnyHooksInstalled, counted instead of OR'd: CodingAgentsStep
        // doesn't surface a count directly, so this sums the per-vendor hook-install outcomes
        // already tracked locally in installResult — vendor names themselves are never sent
        // (see SetupFunnel.Succeeded).
        var agentsConfigured = new[] {
            installResult.ClaudeInstalled, installResult.CodexHooksInstalled, installResult.CursorHooksInstalled,
            installResult.CopilotHooksInstalled, installResult.GeminiHooksInstalled, installResult.KiroHooksInstalled,
            installResult.PiExtensionInstalled, installResult.OpenCodeExtensionInstalled, installResult.AntigravityHooksInstalled,
        }.Count(installed => installed);

        SetupFunnel.Succeeded(agentsConfigured);

        return 0;
    }

    /// <summary>The closing "Next steps" box: a question per item, its answer indented beneath.</summary>
    static void WriteNextSteps(bool offerGuidedTour) {
        var rows = new List<IRenderable>();

        // Padder, not a "  " prefix: these lines wrap, and a prefix indents only the first of them.
        foreach (var (question, answer) in NextStepItems(offerGuidedTour)) {
            if (rows.Count > 0) rows.Add(Text.Empty);

            rows.Add(new Markup($"[bold]{Markup.Escape(question)}[/]"));
            rows.Add(Text.Empty);
            rows.Add(new Padder(new Markup(answer), new Padding(2, 0, 0, 0)));
        }

        AnsiConsole.Write(
            new Panel(new Rows(rows))
                .Header("[bold green] Next steps [/]")
                .BorderColor(Color.Green)
                .Padding(1, 0));
    }

    /// <summary>The box's (question, answer-markup) pairs, split from the write so copy is testable.</summary>
    internal static List<(string Question, string Answer)> NextStepItems(bool offerGuidedTour) {
        var items = new List<(string, string)> {
            (ServerSetupQuestion,
             $"{Markup.Escape(ServerSetupAction)}\n[cyan]{Markup.Escape(ServerSetupDocsUrl)}[/]"),
        };

        if (offerGuidedTour) {
            // Markup-safe: the quoted prompt has no [ or ], so escaping leaves it a plain substring.
            items.Add((GuidedTourQuestion,
                       Markup.Escape(GuidedTourAction)
                             .Replace(GuidedTourPromptQuoted,
                                      $"[cyan]{GuidedTourPromptQuoted}[/]", StringComparison.Ordinal)));
        }

        return items;
    }

    /// <summary>
    /// Whether to point the user at the guided tour. Both halves are required: an agent to type
    /// the prompt into, and the skill actually on disk for one of them. Skill presence is read
    /// from the filesystem rather than the install result, because the installers report false
    /// when work was skipped as already-current — a wired-up machine re-running setup, which
    /// must still get the CTA.
    /// </summary>
    internal static bool ShouldOfferGuidedTour(
            bool anyAgentDetected, string claudeSettingsPath, CodingAgentsStep.Paths paths) =>
        anyAgentDetected
     && (ClaudeCarriesGuidedTour(claudeSettingsPath, paths.PluginDir)
      || AgentsSkillsInstaller.HasSkill(paths.AgentsSkillsDir,      GuidedTourSkillName)
      || AgentsSkillsInstaller.HasSkill(paths.KiroSkillsDir,        GuidedTourSkillName)
      || AgentsSkillsInstaller.HasSkill(paths.AntigravitySkillsDir, GuidedTourSkillName));

    /// <summary>
    /// The plugin is registered AND the directory Claude loads it from ships the skill. The
    /// registered marketplace path in settings is the artifact that matters — <paramref
    /// name="pluginDir"/> is only where THIS build would install from, and after an upgrade the
    /// two can differ. Falls back to it when nothing is registered; false when neither resolves,
    /// because an unverifiable skill must not be advertised.
    /// </summary>
    static bool ClaudeCarriesGuidedTour(string claudeSettingsPath, string? pluginDir) {
        if (!ClaudePluginInstaller.IsInstalled(claudeSettingsPath)) return false;

        var dir = ClaudePluginInstaller.RegisteredMarketplacePath(claudeSettingsPath) ?? pluginDir;

        return dir is not null
            && File.Exists(Path.Combine(dir, "skills", GuidedTourSkillName, "SKILL.md"));
    }

    /// <summary>Source folder name under <c>kcap/skills/</c>; <c>kcap-</c>-prefixed once installed.</summary>
    internal const string GuidedTourSkillName = "guided-tour";

    internal const string GuidedTourQuestion = "New to Capacitor?";

    /// <summary>
    /// A prompt, not <c>/kcap:guided-tour</c>: this box prints for every vendor and only Claude Code
    /// has slash commands. Must stay a verbatim trigger in the skill's frontmatter description
    /// (pinned by <c>SetupCommandTests</c>) or it fires nothing.
    /// </summary>
    internal const string GuidedTourPrompt = "Start kcap guided tour";

    /// <summary>Quoted as well as coloured — colour is lost to <c>NO_COLOR</c> and redirected stdout.</summary>
    internal const string GuidedTourPromptQuoted = $"\"{GuidedTourPrompt}\"";

    internal const string GuidedTourAction =
        $"Prompt {GuidedTourPromptQuoted} in your coding agent to see what Capacitor can do for you";

    internal const string GuidedTourCallToAction = $"{GuidedTourQuestion} {GuidedTourAction}";

    /// <summary>
    /// Server setup lives in the dashboard, so this can only be pointed at. Always printed: who owns
    /// the server is not knowable here, so the reader self-selects on the question. Says "server",
    /// never "workspace" — that word means the local tree everywhere else in this CLI.
    /// </summary>
    internal const string ServerSetupQuestion = "Did you create this Capacitor server?";

    internal const string ServerSetupAction = "Complete server setup with instructions here:";

    internal const string ServerSetupDocsUrl =
        "https://capacitor.kurrent.io/docs/getting-started/setup-server/";

    /// <summary>
    /// Whether Step 6's import eligibility auth requirement is met: provider <c>None</c> needs no
    /// token; any other provider needs a usable (valid-or-refreshable) token. The token probe is
    /// injected so it's testable, and any exception it throws is treated as "not satisfied" — this
    /// probe is awaited OUTSIDE <see cref="RunImportStepAsync"/>'s try/catch, so it must never
    /// throw out of setup (the optional import failing must not fail <c>kcap setup</c>).
    /// </summary>
    internal static async Task<bool> IsAuthSatisfiedAsync(string provider, Func<Task<bool>> hasUsableToken) {
        if (provider == AuthProvider.None) return true;

        try {
            return await hasUsableToken();
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Step 6 (import past sessions) decision + best-effort execution, extracted from
    /// <see cref="HandleAsync"/> so it's unit-testable without driving the whole wizard: the
    /// eligibility/policy decision goes through <see cref="SetupDecisions.DecideImport"/>, and the
    /// actual import call goes through <see cref="ImportRunnerOverride"/> (the real
    /// <see cref="ImportCommand.HandleImport"/> when null) so tests can intercept the invocation
    /// instead of running a real import. Import is best-effort: a thrown exception or a non-zero
    /// exit code is reported with a warning and swallowed — this method never throws and never
    /// fails setup.
    /// </summary>
    internal async Task RunImportStepAsync(
            (string Owner, string Name)? currentRepo,
            bool                          authSatisfied,
            bool                          skipImport,
            bool                          noPrompt,
            Func<bool>                    promptYesNo,
            ProfileContext                profiles,
            string                        defaultVisibility,
            FirstRunImportAnswer?         browserImport = null,
            bool                          browserImportFailed = false) {
        // Asked and answered in the browser, over a repository selection this step cannot express —
        // so it reports rather than prompting. Re-prompting would offer to import one repository
        // again, right after a screen that chose several.
        if (browserImport is { } browser) {
            foreach (var line in BrowserImportSummary(browser, browserImportFailed)) AnsiConsole.MarkupLine(line);

            return;
        }

        var decision = SetupDecisions.DecideImport(
            currentRepo is not null, authSatisfied, skipImport, noPrompt, promptYesNo);

        if (decision.Outcome == SetupDecisions.ImportOutcome.Skip) {
            if (decision.SkipReason is not null)
                AnsiConsole.MarkupLine($"  [dim]Skipping import — {Markup.Escape(decision.SkipReason)}.[/]");

            return;
        }

        // Run: DecideImport only returns Run when hasCurrentRepo was true, so currentRepo is
        // guaranteed non-null here.
        var invocation = new ImportInvocation(
            Repo:               currentRepo!.Value,
            DefaultVisibility:  defaultVisibility,
            AutoSkipExclusions: true,
            ForcePrivate:       false,
            Profiles:           profiles);

        try {
            var exitCode = await (ImportRunnerOverride ?? DefaultImportRunner)(invocation);

            if (exitCode != 0) {
                AnsiConsole.MarkupLine(
                    "  [yellow]⚠[/] Import of past sessions did not complete. Run [cyan]kcap import[/] manually to retry.");
            }
        } catch (Exception ex) {
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠[/] Import of past sessions failed: {Markup.Escape(ex.Message)}. Run [cyan]kcap import[/] manually to retry.");
        }
    }

    /// <summary>
    /// The arguments Step 6 pins into its embedded <see cref="ImportCommand.HandleImport"/> call.
    /// A record (not a bare argument list) so tests can capture and assert on it via
    /// <see cref="ImportRunnerOverride"/> without running a real import.
    /// </summary>
    internal sealed record ImportInvocation(
        (string Owner, string Name) Repo,
        string?                      DefaultVisibility,
        bool                         AutoSkipExclusions,
        bool                         ForcePrivate,
        ProfileContext               Profiles);

    /// <summary>
    /// Test seam: when set, replaces the real <see cref="ImportCommand.HandleImport"/> call made
    /// by <see cref="RunImportStepAsync"/>. Process-global static state — tests must reset it to
    /// null (in a finally block) after use.
    /// </summary>
    internal static Func<ImportInvocation, Task<int>>? ImportRunnerOverride;

    Task<int> DefaultImportRunner(ImportInvocation inv) =>
        new ImportCommand(config, inv.Profiles, home).HandleImport(
            filterCwd:               null,
            filterSession:           null,
            minLines:                15,
            generateSummaries:       false,
            sources:                 BuildImportSources(config, _paths),
            explicitVendorSelection: false,
            since:                   null,
            scope:                   new ImportScope.Repo(inv.Repo.Owner, inv.Repo.Name),
            skipConfirmation:        true,
            forcePrivate:            inv.ForcePrivate,
            currentRepo:             inv.Repo,
            needOrgPick:             false,
            storedOrg:               null,
            autoSkipExclusions:      inv.AutoSkipExclusions,
            defaultVisibility:       inv.DefaultVisibility,
            nested:                  true);

    /// <summary>
    /// Every import source, one per catalogue vendor.
    /// </summary>
    /// <param name="vendors">Restricts which are built. <b>Null is no filter</b>, never filter-to-
    /// nothing. Filtering the sources rather than the counts afterwards is what makes a reported figure
    /// already scoped to what the user kept.</param>
    internal static IReadOnlyList<IImportSource> BuildImportSources(
            ConfigRoot config, HarnessPaths paths, IReadOnlyCollection<HarnessId>? vendors = null) {
        IReadOnlyList<IImportSource> all = [
            new ClaudeImportSource(config, paths.Claude.Projects),
            new CodexImportSource(config, paths.Codex.Sessions),
            new CursorImportSource(config, paths.Cursor.ProjectsDir, paths.Cursor.WorkspaceStorageDir),
            new CopilotImportSource(config, paths.Copilot),
            new GeminiImportSource(paths.Gemini.TmpDir),
            new KiroImportSource(config, paths.Kiro.SessionsDir),
            new PiImportSource(config, paths.Pi.SessionsDir),
            new OpenCodeImportSource(
                    Path.Combine(paths.OpenCode.DataDir, "opencode.db"),
                    paths.OpenCode.ImportLedgerJson),
            new AntigravityImportSource(paths.Antigravity)
        ];

        if (vendors is null) return all;

        var wanted = vendors.ToHashSet();

        return [.. all.Where(s => wanted.Contains(s.Vendor))];
    }

    /// <summary>
    /// Normalizes a user-supplied server (a full URL, or a bare slug already expanded by
    /// <see cref="ServerInput.ResolveTenantArg"/>), probes it, and reads the auth provider from the server's
    /// own <c>/auth/config</c>. Returns null after printing the reason. Shared by
    /// `kcap setup &lt;tenant&gt;` / --server-url and by the zero-tenant "I already have a
    /// workspace" path, so provider selection has exactly one implementation.
    /// </summary>
    async Task<(string ServerUrl, string Provider)?> ResolveServerAndProviderAsync(string serverArg) {
        var normalized = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
            async _ => await ServerUrlNormalizer.NormalizeAsync(
                serverArg, skipProbe: false, CancellationToken.None));

        if (!normalized.Reachable) {
            AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(normalized.Warning ?? serverArg)}");
            AnsiConsole.MarkupLine("  [dim]Check the URL is correct and the server is running.[/]");
            return null;
        }

        var serverUrl = normalized.Url;
        await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

        // Reachable, but with an informational warning (e.g. https→http downgrade).
        if (normalized.Warning is not null)
            AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(normalized.Warning)}");

        try {
            var provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl, config, profiles);
            AnsiConsole.MarkupLine("  [green]✓[/] Reachable");

            return (serverUrl, provider);
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    /// <summary>Test seam: overrides façade construction for Step 1/2. Reset to null in a finally block.</summary>
    internal static Func<ITenantProvisioner?, OnboardingFacade>? FacadeOverride;

    internal static readonly SetupAuthProgress StepProgress = new(new ConsoleAuthProgress(SetupAuthProgress.StepIndent));

    OnboardingFacade NewFacade(
            ITenantProvisioner? provisioner, ITenantPicker? picker = null, RequestedWorkspace? requested = null) =>
        FacadeOverride?.Invoke(provisioner)
            ?? new OnboardingFacade(config, StepProgress, browser, picker ?? DefaultPicker(browser, () => true), provisioner,
                WorkspaceGuard(requested)) {
                KeyWatcher = ConsoleKeyWatcher.Instance
            };

    /// <summary>
    /// The workspace pick, in the browser where one is reachable and in the terminal otherwise. The
    /// composite decides per call, since only the completed login knows which channel it used.
    /// </summary>
    static ITenantPicker DefaultPicker(IBrowserLauncher launcher, Func<bool> canPrompt) =>
        new BrowserTenantPicker(
            launcher, new SpectreTenantPicker(canPrompt), StepProgress, ConsoleKeyWatcher.Instance,
            canPrompt: canPrompt);

    /// <summary>
    /// Refuses the commit when discovery is about to publish a workspace other than the one
    /// <c>--org</c>/<c>--slug</c> named. Runs on the boundary's last cancellable step, so the stop
    /// happens before any profile, stamp or token is written rather than after.
    /// </summary>
    internal static Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? WorkspaceGuard(
            RequestedWorkspace? requested) {
        if (requested is null) return null;

        return (identities, _) => {
            // A WorkOS identity's profile IS its tenant slug, which is what makes this a comparison
            // against the workspace itself rather than a URL the server chose the shape of.
            if (identities.Any(i => string.Equals(i.Profile, requested.Slug, StringComparison.Ordinal)))
                return Task.CompletedTask;

            var landed = identities.Count > 0 ? identities[0].CanonicalServer : "a workspace you already belong to";

            throw new InvalidOperationException(
                $"your account already belongs to {landed}, so '{requested.Slug}' was not created. "
              + $"--org/--slug create a workspace only for an account that has none — re-run with "
              + $"--server-url {landed} to configure that one.");
        };
    }

    /// <summary>
    /// Step 2 (Login) as a standalone step: a discovery-completed sign-in just reports what
    /// discovery already published; everything else — including a <c>None</c> provider, which needs
    /// no interactive login but still needs its auth_provider stamp written inside the façade's
    /// commit boundary — goes through the façade, adopting the server onto the active profile,
    /// since setup's whole job is configuring that profile for the chosen server.
    /// </summary>
    internal async Task<int> RunLoginStepAsync(
            bool loginComplete, string provider, string serverUrl, bool forceDevice, string activeProfile) {
        if (loginComplete) {
            // Discovery's WorkOS leg reports the sign-in itself, naming the workspace it picked. Every
            // other provider's discovery reports a tenant count and no identity, so this is its only one.
            if (provider != AuthProvider.WorkOS) {
                var cfgAfter = await AppConfig.LoadProfileConfig(config);
                var tokens   = await new TokenStore(config).LoadAsync(cfgAfter.ActiveProfile);
                AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [cyan]{Markup.Escape(tokens?.GitHubUsername ?? "?")}[/]");
            }

            return 0;
        }

        var result = await NewFacade(provisioner: null)
            .LoginAsync(serverUrl, forceDevice, activeProfile, CancellationToken.None, adoptServer: true);

        if (result is not AuthResult.Committed) {
            await Console.Error.WriteLineAsync("  Login failed.");

            return 1;
        }

        if (provider == AuthProvider.None) {
            // The façade already printed the "no authentication configured" notice through this sink.
            return 0;
        }

        // The façade's notice is the shared report: `kcap login` and the desktop wizard render from it
        // too, so setup restating it is the half to drop rather than suppress.
        return 0;
    }

    /// <summary>
    /// What step 3 does about the default visibility: apply a value, or prompt.
    /// </summary>
    /// <param name="Apply">The value to apply, or null to prompt.</param>
    /// <param name="Kept">The value is the profile's own, carried because the screen was answered and
    /// left unset. Distinguished from a browser choice only so the line can say which happened.</param>
    internal readonly record struct VisibilityDecision(string? Apply, bool Kept);

    /// <summary>
    /// Which default visibility step 3 applies.
    ///
    /// <para><b>An answered screen that set nothing leaves the profile alone</b>, which is the lane's
    /// contract for a null answer — and the reason this cannot simply fall through to the prompt: the
    /// prompt's cursor starts on <c>org_public</c>, so one Return would widen an existing
    /// <c>private</c>. A screen that was never answered has told us nothing and still needs asking.</para>
    /// </summary>
    /// <param name="browser">The Agents answer, or null where that step never settled.</param>
    /// <param name="current">What the profile holds now.</param>
    internal static VisibilityDecision DecideVisibility(FirstRunAgentsAnswer? browser, string current) =>
        browser switch {
            { DefaultVisibility: { } chosen } => new(chosen, Kept: false),
            not null                          => new(current, Kept: true),
            _                                 => new(null, Kept: false)
        };

    /// <summary>What each <c>default_visibility</c> stop is called. Shared by the prompt and by the line
    /// that reports the browser's answer, because two lists that have to correspond are one list.</summary>
    internal static string VisibilityLabel(string visibility) => visibility switch {
        "private"    => "All private — only you can see your sessions",
        "project"    => "Project repos public to fellow project members, others private",
        "org_public" => "Org repos public, others private (default)",
        "public"     => "All public — others can see all your sessions",
        _            => visibility
    };

    /// <summary>Per request, not per leg: the poll below runs for as long as a human takes.</summary>
    static readonly TimeSpan BrowserFlowHttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long the login-shell probe may hold up the create. Two attempts at its own 5s
    /// timeout, plus room to spawn them; past that the machine reports "not probed", which is not the
    /// same as "not found" and draws no alarm on the screen.</summary>
    static readonly TimeSpan LoginShellProbeBudget = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The profile the import lane runs under. <see cref="ImportCommand"/> reads its base URL from the
    /// context it is handed, and <c>profiles</c> is resolved at process start, before setup has asked
    /// for a server — on a first run it names none at all, and the one this run chose is not persisted
    /// until after this leg returns.
    /// </summary>
    internal static ProfileContext ImportContext(ProfileContext profiles, string serverUrl) =>
        new(profiles.Resolution with { ServerUrl = serverUrl }, profiles.Snapshot);

    /// <summary>Creates the first-run flow, opens the browser on it, and polls it as itself, returning
    /// the Agents decision for Step 4 to apply. <b>Nothing is configured here</b> — what crosses is
    /// vendor keys and booleans, and the install runs through the same one place the terminal prompt
    /// does. Every outcome leaves setup running: sign-in has already happened, so nothing in this leg
    /// can strand a machine.</summary>
    async Task<BrowserFlowAnswers> RunBrowserFlowStepAsync(string serverUrl, string provider, bool noPrompt) {
        // --no-prompt is a scripted run and this waits on a human. None has no identity for a flow to
        // be owned by, and its routes are authenticated. Headless is deliberately NOT a skip: a
        // machine with no browser of its own is exactly the one whose user is sitting at another, and
        // the URL is printed for them to carry across — the device path keeps the screens rather than
        // designing that population out of them.
        if (noPrompt || provider == AuthProvider.None) return BrowserFlowAnswers.None;

        await Console.Out.WriteLineAsync();

        FirstRunFlowResult result;
        SetupImportLane?   importing = null;

        // Through the ONE authenticated-client choke point, so the bearer refreshes and a 401 is
        // recovered rather than ending a wait that can outlive a short-lived WorkOS token. The try
        // keeps the leg's "no reachable failure crashes setup" promise whole.
        try {
            var (http, authStatus) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                config, profiles, serverUrl, autoRetryUnauthorized: true);

            using (http) {
                http.Timeout = BrowserFlowHttpTimeout;

                // Ok runs the leg. NoAuthRequired is the None-provider skip again — silent, for the
                // same reason. The rest get one line: the factory's quiet variant prints nothing, and
                // expired / not authenticated / wrong server all share one remedy.
                if (authStatus is not AuthStatus.Ok) {
                    if (authStatus != AuthStatus.NoAuthRequired)
                        AnsiConsole.MarkupLine(
                            "  [dim]Skipped browser setup: the stored token is not usable. Run 'kcap login' to re-authenticate.[/]");

                    return BrowserFlowAnswers.None;
                }

                // Said out loud because it is the one part of this leg that takes noticeable time: the
                // login-shell probe spawns a shell, and a slow profile would otherwise be dead air
                // ahead of the only line that explains what is happening.
                AnsiConsole.MarkupLine("  [dim]Checking this machine for harnesses…[/]");

                var report = FirstRunMachineReport.EvaluateCurrent(
                    config, HarnessRegistry.FromEnvironment(home),
                    Environment.MachineName, await LoginShellFindsCliAsync());

                importing = new SetupImportLane(config, ImportContext(profiles, serverUrl), home, _paths);

                using var progress = new SpectreFirstRunFlowProgress();

                result = await new BrowserFirstRunFlow(
                        new FirstRunFlowClient(http), progress, browser,
                        actions:   new SetupMachineActions(),
                        importing: importing)
                    .RunAsync(serverUrl, report, CancellationToken.None);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            AnsiConsole.MarkupLine($"  [yellow]![/] Could not start browser setup: {Markup.Escape(ex.Message)}");

            return BrowserFlowAnswers.None;
        }

        // Silent: this tenant does not serve the flow, which is every tenant that has not turned it
        // on. Announcing it would report our rollout as though it were the user's problem.
        if (result is FirstRunFlowResult.Unavailable) return BrowserFlowAnswers.None;

        AnsiConsole.MarkupLine(BrowserFlowOutcome(result));

        // Not after a keypress: that outcome's own line already says where setup went, and saying it
        // twice would be the only thing this added.
        if (result is not (FirstRunFlowResult.Finished or FirstRunFlowResult.Dismissed))
            AnsiConsole.MarkupLine("  [dim]Carrying on here.[/]");

        return new BrowserFlowAnswers(
            FirstRunFlowOutcomes.Agents(result),
            FirstRunFlowOutcomes.Import(result),
            importing?.Failed == true,
            FlowId(result),
            result is FirstRunFlowResult.Finished);
    }

    /// <summary>
    /// What the browser leg answered, for the steps below to spend instead of asking again.
    /// </summary>
    /// <param name="Import">The import ran inside the leg, so this is a record of what happened
    /// rather than work to do — Step 6 reports it instead of prompting.</param>
    /// <param name="ImportFailed">A pass returned non-zero, so the closing summary must not report a
    /// backfill that did not happen.</param>
    /// <param name="FlowId">The flow this leg ran, or null where none did. What the steps below need to
    /// finish a capability the browser asked for and this leg deliberately did not perform.</param>
    /// <param name="FlowStillLive">Whether the browser is still waiting on this machine. True only for a
    /// leg that finished: every other ending relinquished, and a relinquish is the machine stating it has
    /// gone. Read before beating again — saying this machine is here, after saying it had gone, is worse
    /// than the silence it would be covering.</param>
    internal sealed record BrowserFlowAnswers(
            FirstRunAgentsAnswer? Agents,
            FirstRunImportAnswer? Import,
            bool                  ImportFailed  = false,
            string?               FlowId        = null,
            bool                  FlowStillLive = false) {
        /// <summary>No browser leg ran, or it ended with nothing to spend.</summary>
        public static BrowserFlowAnswers None { get; } = new(null, null);
    }

    /// <summary>The flow id off whichever result carries a view. A leg that never reached one has no flow
    /// to finish anything against.</summary>
    static string? FlowId(FirstRunFlowResult result) => result switch {
        FirstRunFlowResult.Finished f  => f.View.FlowId,
        FirstRunFlowResult.Abandoned a => a.View?.FlowId,
        FirstRunFlowResult.Dismissed d => d.View?.FlowId,
        _                              => null
    };

    /// <summary>Whether the login shell resolves the CLI — see <see cref="ILoginShellProbe"/> for why
    /// that differs from this process's PATH. Bounded because it spawns a shell: a probe that did not
    /// finish reports unknown rather than as a hazard, since only an explicit false draws the alarm.</summary>
    static async Task<bool?> LoginShellFindsCliAsync() {
        try {
            using var cts = new CancellationTokenSource(LoginShellProbeBudget);

            return await new LoginShellProbe(new ProcessRunner(), Environment.GetEnvironmentVariable)
                .KcapOnPathAsync(cts.Token);
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>What Step 6 says instead of prompting, when the browser already answered it. Split
    /// from the write so the copy is testable.</summary>
    /// <param name="failed">A pass returned non-zero. The line then says so rather than showing a
    /// tick, or the closing summary contradicts the warning the import itself already printed.</param>
    internal static IReadOnlyList<string> BrowserImportSummary(FirstRunImportAnswer answer, bool failed = false) {
        if (answer.IsDecline) return ["  [dim]· You chose not to import past sessions in the browser.[/]"];

        if (answer.NoReadableVendors)
            return [
                "  [yellow]![/] Nothing was imported: those sessions come from agents this version of kcap "
              + "does not know. Run 'kcap update', then 'kcap import' to bring them in."
            ];

        var lines = new List<string>();

        if (answer.Choices.Count > 0) {
            var repos  = answer.Choices.Count;
            var subject = $"{repos} repositor{(repos == 1 ? "y" : "ies")} as chosen in the browser "
                        + $"[dim]({Markup.Escape(FirstRunImportWindows.Label(answer.Window))})[/]";

            lines.Add(failed
                ? $"  [yellow]![/] Partly imported {subject}. Run [cyan]kcap import[/] to finish it."
                : $"  [green]✓[/] Imported {subject}");
        }

        if (answer.Unreadable > 0)
            lines.Add(
                $"  [yellow]![/] {answer.Unreadable} of those repositories asked for something this version of "
              + "kcap does not know, and were left alone. Run 'kcap update' and import them with 'kcap import'.");

        return lines;
    }

    /// <summary>
    /// What Step 4 says instead of prompting, when the browser already answered it. Split from the
    /// write so the copy is testable.
    ///
    /// <para><b>The choice itself is not restated here.</b> The leg says it live, as the step settles,
    /// and naming the same harnesses again a screen later is the same fact landing twice. What is left
    /// is the warning, which is not a restatement and whose remedy is a command to run here.</para>
    /// </summary>
    internal static IReadOnlyList<string> BrowserAgentsSummary(FirstRunAgentsAnswer answer) {
        var lines = new List<string>();

        if (answer.Unrecognised > 0)
            lines.Add(
                $"  [yellow]![/] {answer.Unrecognised} of your choices name an agent this version of kcap "
              + "does not know. Run 'kcap update' and setup again to finish them.");

        return lines;
    }

    /// <summary>The leg's one line about how it ended, split from the write so the copy is testable.
    /// <see cref="FirstRunFlowResult.Unavailable"/> has none — it is not reported at all.</summary>
    internal static string BrowserFlowOutcome(FirstRunFlowResult result) => result switch {
        FirstRunFlowResult.Finished => "  [green]✓[/] Browser setup finished.",

        FirstRunFlowResult.Expired =>
            "  [yellow]![/] That setup link expired before the browser finished with it.",

        FirstRunFlowResult.Abandoned => "  [yellow]![/] The browser didn't finish setup.",

        // Not a warning. The user chose this, and dressing a choice up as something gone wrong is how
        // a CLI teaches people to read past its warnings.
        FirstRunFlowResult.Dismissed => "  [dim]Left the browser to it.[/]",

        // The server asks for ten minutes, which is not a wait an interactive setup can offer, so the
        // number is reported rather than slept through.
        FirstRunFlowResult.RateLimited limited =>
            $"  [yellow]![/] Too many setup links created recently. Browser setup is available again in {Math.Max(1, (int)Math.Ceiling(limited.RetryAfter.TotalMinutes))} min.",

        FirstRunFlowResult.Failed failed => $"  [yellow]![/] {Markup.Escape(failed.Message)}",

        _ => "  [yellow]![/] Browser setup did not finish."
    };

    /// <summary>
    /// A flag's value, and whether the flag was there at all. <see cref="GetArg"/> returns the next
    /// token whatever it is, so <c>--org --slug acme</c> reads "--slug" as the organization name —
    /// survivable for a flag that resolves to a URL, but here it would name a real workspace.
    /// </summary>
    static (bool Present, string? Value) ValuedFlag(string[] args, string name) {
        // The equals form is not this CLI's spelling, so an exact-token search would not see it at all
        // and the flags would go unread — the silent drop every other arm of this parse refuses.
        if (args.Any(a => a.StartsWith($"{name}=", StringComparison.Ordinal))) return (true, null);

        var idx = Array.IndexOf(args, name);

        if (idx < 0) return (false, null);

        var next = idx + 1 < args.Length ? args[idx + 1] : null;

        return (true, next is null || next.StartsWith('-') || string.IsNullOrWhiteSpace(next) ? null : next);
    }

    /// <summary>
    /// Reads <c>--org</c>/<c>--slug</c> into the answers the create-a-workspace prompts would have
    /// collected, or the error that stops the run; (null, null) when neither was passed. Every
    /// rejection is a combination that would otherwise take the flags and never act on them.
    /// </summary>
    internal static (RequestedWorkspace? Workspace, string? Error) ParseRequestedWorkspace(
            string[] args, bool haveServerUrl) {
        const string Usage = "kcap setup --org \"<name>\" --slug <slug> --no-prompt";

        var (orgGiven, orgName) = ValuedFlag(args, "--org");
        var (slugGiven, slug)   = ValuedFlag(args, "--slug");

        if (!orgGiven && !slugGiven) return (null, null);

        // Present-but-empty is rejected rather than read as absent: a script whose $ORG expanded to
        // nothing still asked for a workspace.
        if (orgGiven && orgName is null)   return (null, $"--org needs a value: {Usage}");
        if (slugGiven && slug is null)     return (null, $"--slug needs a value: {Usage}");

        // Both or neither. The slug becomes a permanent public hostname, so deriving one from the
        // name would pick it on the user's behalf in the one run nobody is watching.
        if (orgGiven != slugGiven)
            return (null, $"{(orgGiven ? "--org needs --slug" : "--slug needs --org")}: {Usage}");

        if (haveServerUrl)
            return (null, "--org/--slug create a workspace; --server-url (or `kcap setup <tenant>`) points at one that exists. Pass one or the other.");

        // Only the hosted-auth lane provisions; GitHub-App discovery has nothing to create with.
        if (args.Contains("--github"))
            return (null, "--org/--slug need Kurrent's hosted auth, which --github opts out of.");

        // Canonicalized AND validated here, so the slug this run reports, checks and compares against
        // the workspace it lands on is one value, and a malformed one is named as malformed wherever
        // it is caught rather than only on the path that reaches the provisioner.
        var canonical = SlugValidator.Canonicalize(slug!);
        var check     = SlugValidator.Validate(canonical);

        if (!check.Ok) return (null, SpectreTenantProvisioner.SlugRejection(canonical, check.Reason, "pass a different --slug"));

        return (new RequestedWorkspace(orgName!.Trim(), canonical), null);
    }

    internal async Task<(string ServerUrl, string Provider, bool LoginComplete)?> RunDiscoveryAsync(
            string[] args, bool forceDevice, RequestedWorkspace? requested = null) {
        var chosen   = OAuthLoginFlow.ChooseDiscoveryProvider(args);
        var headless = HeadlessEnvironment.IsHeadless();

        AnsiConsole.MarkupLine($"  Proxy: [dim]{Markup.Escape(AuthProxyEndpoint.Url)}[/]");

        // WorkOS no longer follows headlessness: its ladder opens the browser either way, and only an
        // explicit --device takes the device grant. GitHub's exchange URL is not known until the proxy
        // answers inside DiscoverAsync, so its label stays the environment guess it has always been.
        var signinMode = chosen == AuthProvider.WorkOS
            ? OAuthLoginFlow.ChooseWorkOSFlow(forceDevice) == WorkOSFlow.Device ? "device" : "browser"
            : forceDevice || headless ? "device" : "browser";
        SetupFunnel.SigninOpened(signinMode, chosen);

        // Armed for every WorkOS session, headless included: that path has a device grant now, so
        // a zero-workspace headless user now completes a sign-in and would otherwise hold a live
        // credential with nowhere to spend it. GitHub never provisions.
        // Resolved once and handed to both, rather than each defaulting its own seam off the same
        // ambient property: one question, one answer. A terminal is not enough — `--no-prompt` says
        // not to ask, and a workspace picker on a TTY would still stop an unattended run dead.
        var canPrompt = AnsiConsole.Profile.Capabilities.Interactive && !args.Contains("--no-prompt");

        var provisioner = chosen == AuthProvider.WorkOS
            ? new SpectreTenantProvisioner(
                new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url,
                isInteractive: () => canPrompt, requested: requested)
            : null;

        var result = await NewFacade(provisioner, DefaultPicker(browser, () => canPrompt), requested)
            .DiscoverAsync(chosen, forceDevice, CancellationToken.None);

        // WorkOS's own signin_completed/tenant_none fire from inside Core — only GitHub is derived here.
        if (chosen == AuthProvider.GitHubApp) {
            switch (result) {
                case AuthResult.Failed { Reason: AuthFailureReason.SigninDenied }:
                    SetupFunnel.SigninFailed("github_token_denied");

                    break;
                // Other and NoTenantsFound only occur once AcquireGitHubTokenAsync already succeeded.
                case AuthResult.Committed:
                case AuthResult.Failed { Reason: AuthFailureReason.Other }:
                    SetupFunnel.SigninCompleted(AuthProvider.GitHubApp);

                    break;
                case AuthResult.Failed { Reason: AuthFailureReason.NoTenantsFound }:
                    SetupFunnel.SigninCompleted(AuthProvider.GitHubApp);
                    SetupFunnel.TenantNone(AuthProvider.GitHubApp);

                    break;
            }
        }

        switch (result) {
            case AuthResult.Committed committed: {
                var cfg    = await AppConfig.LoadProfileConfig(config);
                var active = cfg.Profiles.GetValueOrDefault(cfg.ActiveProfile);

                if (active?.ServerUrl is null) {
                    AnsiConsole.MarkupLine("  [red]✗[/] Sign-in did not set an active profile.");

                    return null;
                }

                // WorkOS already reported "Logged in as … → …" via the façade; GitHub gets its own line.
                if (committed.Provider != AuthProvider.WorkOS) {
                    AnsiConsole.MarkupLine(
                        $"  [green]✓[/] Discovered {committed.Published.Count} tenant(s). Active: [cyan]{Markup.Escape(committed.ActiveProfile)}[/]");
                }

                return (active.ServerUrl, committed.Provider, true);
            }
            case AuthResult.Retarget retarget: {
                // Origin first, then slug expansion: a pasted "acme.kcap.ai/sessions" must lose its
                // path before ResolveTenantArg decides it already looks like a host.
                var retargeted = await ResolveServerAndProviderAsync(
                    ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(retarget.ServerInput)));

                return retargeted is null ? null : (retargeted.Value.ServerUrl, retargeted.Value.Provider, false);
            }
            default:
                // Failed/Cancelled — already rendered through the façade's progress sink, bar the tail setup owns.
                StepProgress.ReportFailure(result);

                return null;
        }
    }

    internal static string? ResolvePluginPath(string? overrideDir = null) {
        overrideDir ??= Environment.GetEnvironmentVariable("KCAP_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir)) {
            return overrideDir;
        }

        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kcap
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "kcap"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kcap/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap", "kcap"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "kcap"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap"));

        return Directory.Exists(repoPlugin) ? repoPlugin : null;
    }

    // best-effort signal to the server that this user has completed CLI setup.
    // Silently swallows network/auth/server errors: the welcome-modal nudge is a UX
    // affordance, not part of the contract of `kcap setup`.
    //
    // Two reliability rules (kcap-cli#113 review):
    //   • Don't use HttpClientExtensions.CreateAuthenticatedClientAsync — its
    //     TokenStore.GetValidTokensAsync refresh path makes HTTP calls that
    //     don't honor a CancellationToken and can block far longer than any
    //     CTS-based timeout. The user just logged in moments ago in this same
    //     command, so a non-expired token is the expected case; if it's
    //     missing or expired we silently skip rather than triggering a refresh.
    //   • Cap the operation with Task.WhenAny(ping, Task.Delay(5s)) so the
    //     wall-clock bound is enforced independently of what HttpClient does
    //     internally. If the delay wins, HttpClient disposal on method-exit
    //     cancels the in-flight POST.
    /// <summary>
    /// The cli-setup ping body, hand-built on purpose. A typed DTO here would inherit
    /// CapacitorJsonContext's global SnakeCaseLower policy and serialise <c>cli_version</c>,
    /// silently breaking an endpoint that works today — the mirror image of why
    /// <c>ProvisionRequest.JoinId</c> needs an explicit attribute. So both names stay literal
    /// camelCase, and <c>joinId</c> is omitted entirely rather than sent as null when telemetry is
    /// off. Both inputs are ours — an assembly version and 32 hex chars — so neither can carry a
    /// quote that would break the literal.
    /// </summary>
    internal static string CliSetupPingBody(string? version, string? joinId) {
        var versionJson = version is null ? "null" : $"\"{version}\"";
        var joinJson    = joinId is null ? "" : $",\"joinId\":\"{joinId}\"";

        return $$"""{"cliVersion":{{versionJson}}{{joinJson}}}""";
    }

    async Task PingCliSetupAsync(string serverUrl, string profile, string provider) {
        // The ping is intentionally silent (see method-doc), which also hides why the
        // dashboard welcome modal never flips when it fails — e.g. a token the server
        // rejects or maps to a different identity. Set KCAP_DEBUG to surface the
        // outcome on stderr without changing the best-effort, non-blocking contract.
        var  debug = Environment.GetEnvironmentVariable("KCAP_DEBUG") is { Length: > 0 };
        void Debug(string message) {
            if (debug) Console.Error.WriteLine($"[kcap] cli-setup ping: {message}");
        }

        // A None-provider server neither needs nor should receive a bearer. Setup performs no login
        // on that path, so any token still in the profile belongs to whatever server it pointed at
        // before — sending it here would disclose it to an unrelated host.
        if (provider == AuthProvider.None) {
            Debug("skipped — server requires no authentication");

            return;
        }

        try {
            var tokens = await new TokenStore(config).LoadAsync(profile);
            if (tokens is null || tokens.IsExpired) {
                Debug(tokens is null ? "skipped — no stored token" : "skipped — token expired");

                return;
            }

            // Re-running setup to point an authenticated profile at a different server would
            // otherwise send the previous server's bearer to the new one. Checked inline rather
            // than through the resolving accessor to keep this path refresh-free and time-bound.
            if (tokens.ServerUrl is not null && !ServerIdentity.SameServer(tokens.ServerUrl, serverUrl)) {
                Debug($"skipped — stored token belongs to {tokens.ServerUrl}");

                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

            var version = typeof(SetupCommand).Assembly.GetName().Version?.ToString();
            var payload = new StringContent(
                CliSetupPingBody(version, SetupJoin.Current),
                System.Text.Encoding.UTF8,
                "application/json");

            var pingTask = http.PostAsync($"{serverUrl.TrimEnd('/')}/api/users/me/cli-setup", payload);
            var winner   = await Task.WhenAny(pingTask, Task.Delay(TimeSpan.FromSeconds(5)));

            if (winner == pingTask) {
                // Observe the result so any exception is consumed by the outer
                // catch (instead of surfacing as UnobservedTaskException later).
                using var resp = await pingTask;
                Debug($"{(int)resp.StatusCode} {resp.StatusCode} (provider={tokens.Provider})");
            } else {
                // Wall-clock cap hit. HttpClient.Dispose() at method-exit
                // cancels the in-flight POST; observe the orphan so its
                // cancellation exception doesn't go unhandled.
                Debug("timed out after 5s");
                _ = pingTask.ContinueWith(
                    t => {
                        if (t.IsCompletedSuccessfully) t.Result.Dispose();
                        _ = t.Exception; // mark observed
                    },
                    TaskScheduler.Default);
            }
        } catch (Exception e) {
            // Swallow — see method-doc. KCAP_DEBUG surfaces the reason.
            Debug($"failed — {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// the end-of-setup reminder that live recording only starts on a
    /// <em>new</em> coding-agent session. Claude Code (and the other agents) load hooks
    /// at session start, so a session that was already running when setup installed the
    /// hooks keeps running without them and never streams live. Returns the Spectre-markup
    /// note when at least one agent's hooks were installed, or <c>null</c> when nothing was
    /// wired up (so we don't promise recording that can't happen).
    /// </summary>
    internal static string? LiveRecordingRestartTip(CodingAgentsStep.Result result) {
        if (!result.AnyHooksInstalled) return null;

        // The "how to restart" hint is agent-specific: Claude can resume with
        // --continue; Pi loads the kcap extension at process start; Codex/Cursor/
        // Copilot just need a fresh session. Build it from what was actually
        // installed instead of always naming Claude — the old text read as a
        // Claude instruction even on a Pi/Cursor/Copilot-only setup.
        var how = result.ClaudeInstalled
            ? "Restart your agent (or run [cyan]claude --continue[/])"
            : result.PiExtensionInstalled
                ? "Restart [cyan]pi[/] so the kcap extension loads"
                : "Restart your agent";

        return
            "[yellow]![/] Live recording begins on a [bold]new[/] coding-agent session — hooks only load at session start.\n"
          + $"  {how} to begin streaming; a session that was already\n"
          + "  running when you ran setup isn't being recorded yet.";
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Registers the kcap plugin in a Claude Code settings.json file by merging
    /// the marketplace source and enabling the plugin. Preserves all existing settings.
    /// </summary>
    internal static bool InstallPlugin(string settingsPath, string marketplacePath) {
        try {
            JsonObject root = [];

            if (File.Exists(settingsPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
                        root = obj;
                } catch {
                    // Malformed JSON — start fresh
                }
            }

            // Ensure extraKnownMarketplaces.kcap exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                   = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kcap"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Remove stale marketplace entries from earlier shapes
            marketplaces.Remove("kurrent");
            marketplaces.Remove("kapacitor");

            // Ensure enabledPlugins.kcap@kcap is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kcap@kcap"] = true;

            // Remove stale plugin entries from earlier shapes
            enabled.Remove("kcap@kurrent");
            enabled.Remove("kapacitor@kapacitor");
            enabled.Remove("kapacitor@kurrent");

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            ClaudePluginInstaller.WriteMarker(settingsPath);

            return true;
        } catch {
            return false;
        }
    }
}
