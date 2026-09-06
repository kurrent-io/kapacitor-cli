using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupCommandTests {
    // Never reached: these tests drive the import and discovery steps, which do not provision.
    static readonly TenantProvisioningClient Provisioning = new(new HttpClient());
    static readonly WorkOSClient Workos = new(new PlainHttpClientFactory());
    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());
    static readonly IHttpClientFactory HttpFactory = new PlainHttpClientFactory();
    static readonly IAuthProxyClient Proxy = new AuthProxyClient(new HttpClient());

    // Memoized per baseUrl, and every WireMock server here gets its own ephemeral port, so sharing
    // one instance across tests carries no cross-test state.
    static readonly AuthProviderDiscovery Discovery = new(HttpFactory);

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // --- The browser leg's one outcome line ---

    [Test]
    public async Task BrowserFlowOutcome_marks_only_a_finished_flow_as_a_success() {
        await Assert.That(SetupCommand.BrowserFlowOutcome(new FirstRunFlowResult.Finished(new() { FlowId = "x" })))
                    .Contains("[green]");
    }

    [Test]
    [Arguments(typeof(FirstRunFlowResult.Expired))]
    [Arguments(typeof(FirstRunFlowResult.Abandoned))]
    public async Task BrowserFlowOutcome_warns_about_a_flow_that_did_not_finish(Type kind) {
        var result = kind == typeof(FirstRunFlowResult.Expired)
            ? new FirstRunFlowResult.Expired()
            : (FirstRunFlowResult)new FirstRunFlowResult.Abandoned(null);

        await Assert.That(SetupCommand.BrowserFlowOutcome(result)).Contains("[yellow]");
    }

    // A keypress is a choice, and dressing a chosen thing up as something gone wrong is how a CLI
    // teaches people to read past its warnings.
    [Test]
    public async Task BrowserFlowOutcome_does_not_warn_about_a_wait_the_user_ended() {
        var line = SetupCommand.BrowserFlowOutcome(new FirstRunFlowResult.Dismissed(null));

        await Assert.That(line).DoesNotContain("[yellow]");
        await Assert.That(line).DoesNotContain("[green]");
    }

    [Test]
    public async Task BrowserFlowOutcome_reports_the_rate_limit_in_whole_minutes() {
        // Rounded UP and floored at one: "available again in 0 min" reads as "try now", which is the
        // one thing the server has just refused.
        await Assert.That(SetupCommand.BrowserFlowOutcome(new FirstRunFlowResult.RateLimited(TimeSpan.FromMinutes(10))))
                    .Contains("10 min");
        await Assert.That(SetupCommand.BrowserFlowOutcome(new FirstRunFlowResult.RateLimited(TimeSpan.FromSeconds(30))))
                    .Contains("1 min");
    }

    [Test]
    public async Task BrowserFlowOutcome_escapes_a_failure_message__which_reaches_Spectre_markup() {
        var line = SetupCommand.BrowserFlowOutcome(new FirstRunFlowResult.Failed("bad [thing] here"));

        await Assert.That(line).Contains("[[thing]]");
    }

    // --- What Step 4 says when the browser already answered it ---

    static FirstRunAgentsAnswer Answer(int unrecognised, params HarnessId[] harnesses) =>
        new([.. harnesses.Select(h => new FirstRunAgentsChoice(h, true, true))],
            new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero),
            unrecognised);

    // Step 4 must omit a choice the live progress line already named, or the same fact lands twice.
    [Test]
    public async Task BrowserAgentsSummary_omits_a_choice_the_live_line_already_named() {
        var lines = SetupCommand.BrowserAgentsSummary(Answer(0, HarnessId.Cursor, HarnessId.Claude));

        await Assert.That(string.Join("\n", lines)).DoesNotContain("Claude Code");
    }

    [Test]
    public async Task The_live_step_line_names_the_harnesses_that_were_chosen() {
        var line = SpectreFirstRunFlowProgress.StepLine(
            FirstRunFlowStep.Agents, FirstRunStepOutcome.Completed, "Claude Code, Cursor");

        await Assert.That(line).Contains("Claude Code, Cursor");
        await Assert.That(line).Contains("[green]");
    }

    // No detail reaches this line for two different answers — a real decline, and one naming only
    // vendors this build cannot map — and the renderer cannot tell them apart. So it must not claim
    // either: "chose not to" is false of the second, which asked for agents and got none.
    [Test]
    public async Task The_live_step_line_does_not_claim_a_decline_it_cannot_tell_apart() {
        var line = SpectreFirstRunFlowProgress.StepLine(
            FirstRunFlowStep.Agents, FirstRunStepOutcome.Completed, null);

        await Assert.That(line).Contains("No agents to set up");
        await Assert.That(line).DoesNotContain("Chose");
    }

    // Done needs none: the leg's own outcome line lands a moment later and says the same thing.
    [Test]
    public async Task The_live_step_line_leaves_the_last_step_to_the_legs_outcome_line() {
        await Assert.That(SpectreFirstRunFlowProgress.StepLine(
            FirstRunFlowStep.Done, FirstRunStepOutcome.Completed, null)).IsNull();
    }

    [Test]
    public async Task The_live_step_line_marks_a_failed_step_as_a_warning_not_a_tick() {
        var line = SpectreFirstRunFlowProgress.StepLine(
            FirstRunFlowStep.Import, FirstRunStepOutcome.Failed, null);

        await Assert.That(line).Contains("[yellow]");
        await Assert.That(line).DoesNotContain("[green]");
    }

    // A spinner naming the screen the user is supposedly looking at, while the server has answered
    // nothing for minutes, states a fact nothing in the CLI has.
    [Test]
    public async Task The_spinner_says_the_server_is_unreachable_rather_than_naming_a_screen() {
        await Assert.That(SpectreFirstRunFlowProgress.WaitText(FirstRunFlowStep.Import, healthy: true))
                    .Contains("Choose what to import");
        await Assert.That(SpectreFirstRunFlowProgress.WaitText(FirstRunFlowStep.Import, healthy: false))
                    .IsEqualTo(SpectreFirstRunFlowProgress.Unreachable);
    }

    // A step a newer server invented reaches the renderer as null, and the wait still has to read as
    // a wait rather than throwing or going blank.
    [Test]
    public async Task The_spinner_has_wording_for_a_step_this_build_does_not_know() {
        await Assert.That(SpectreFirstRunFlowProgress.WaitText(null, healthy: true))
                    .IsEqualTo("Waiting on the browser");
    }

    // With no pinned line to carry it the offer has to be said outright, or a terminal too narrow to
    // host a block gets neither the spinner nor the offer.
    [Test]
    public async Task The_offer_is_said_outright_when_no_pinned_line_carries_it() {
        await Assert.That(SpectreFirstRunFlowProgress.SaysOfferOutright(
            pinned: false, canWatch: true, pickedUp: false, alreadySaid: false)).IsTrue();
    }

    [Test]
    [Arguments(true, true, false, false)]    // a pinned line is already carrying it
    [Arguments(false, false, false, false)]  // no keyboard, so nothing to offer
    [Arguments(false, true, true, false)]    // withdrawn: a decision has been made in the browser
    [Arguments(false, true, false, true)]    // said once already, and a line cannot be taken back
    public async Task The_offer_is_not_said_outright_otherwise(
            bool pinned, bool canWatch, bool pickedUp, bool alreadySaid) {
        await Assert.That(SpectreFirstRunFlowProgress.SaysOfferOutright(
            pinned, canWatch, pickedUp, alreadySaid)).IsFalse();
    }

    // Naming the key in one place is what stops the offer and the loop disagreeing about it.
    [Test]
    public async Task The_offer_names_the_key_the_loop_actually_acts_on() {
        await Assert.That(SpectreFirstRunFlowProgress.Offer)
                    .StartsWith(BrowserFirstRunFlow.HandoverKey.ToString());
    }

    // Without this the user turns a harness on, nothing happens, and there is no reason anywhere for
    // why — the vendor was simply dropped as unreadable by a CLI older than the server.
    [Test]
    public async Task BrowserAgentsSummary_says_when_this_build_could_not_read_part_of_the_answer() {
        var lines = SetupCommand.BrowserAgentsSummary(Answer(2, HarnessId.Claude));

        await Assert.That(string.Join("\n", lines)).Contains("kcap update");
    }

    [Test]
    public async Task BrowserAgentsSummary_says_nothing_at_all_when_it_read_the_whole_answer() {
        var lines = SetupCommand.BrowserAgentsSummary(Answer(0, HarnessId.Claude));

        await Assert.That(lines.Count).IsEqualTo(0);
    }

    static FirstRunImportAnswer ImportAnswer(
            int unreadable = 0, string window = FirstRunImportWindows.Last90, params string[] repos) =>
        new([.. repos.Select(r => new FirstRunImportChoice("kurrent-io", r, FirstRunImportLevel.Shared))],
            window,
            FirstRunImportTitles.Server,
            null,
            new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero),
            unreadable);

    [Test]
    public async Task BrowserImportSummary_reports_what_ran_rather_than_offering_to_run_it_again() {
        // Step 6 can only offer the current repository, and the screen just chose several — so
        // re-prompting would offer to redo a subset of what already happened.
        var lines = SetupCommand.BrowserImportSummary(ImportAnswer(repos: ["kcap-server", "kcap-cli"]));

        await Assert.That(string.Join("\n", lines)).Contains("2 repositories");
    }

    [Test]
    public async Task BrowserImportSummary_names_the_window_so_the_figure_can_be_reconciled() {
        var lines = SetupCommand.BrowserImportSummary(ImportAnswer(window: FirstRunImportWindows.Last30, repos: "kcap"));

        await Assert.That(string.Join("\n", lines)).Contains("last 30 days");
    }

    [Test]
    public async Task BrowserImportSummary_says_a_decline_imported_nothing() {
        var lines = SetupCommand.BrowserImportSummary(ImportAnswer());

        await Assert.That(string.Join("\n", lines)).Contains("chose not to import");
    }

    // Without this a repository the user selected simply never arrives, with no reason anywhere.
    [Test]
    public async Task BrowserImportSummary_says_when_this_build_could_not_read_part_of_the_answer() {
        var lines = SetupCommand.BrowserImportSummary(ImportAnswer(unreadable: 1, repos: "kcap"));

        await Assert.That(string.Join("\n", lines)).Contains("kcap update");
    }

    // The failure that would otherwise be silent: repositories chosen, nothing scanned, and a line
    // saying it imported them.
    [Test]
    public async Task BrowserImportSummary_says_nothing_was_imported_when_it_knew_none_of_the_agents() {
        var answer = ImportAnswer(repos: "kcap") with { Vendors = [] };

        var lines = SetupCommand.BrowserImportSummary(answer);

        await Assert.That(string.Join("\n", lines)).Contains("Nothing was imported");
        await Assert.That(string.Join("\n", lines)).Contains("kcap update");
    }

    // The closing summary must not contradict the warning the import itself printed while running.
    [Test]
    public async Task BrowserImportSummary_says_partly_imported_when_a_pass_returned_non_zero() {
        var lines = SetupCommand.BrowserImportSummary(ImportAnswer(repos: "kcap"), failed: true);

        await Assert.That(string.Join("\n", lines)).Contains("Partly imported");
        await Assert.That(string.Join("\n", lines)).DoesNotContain("[green]");
    }

    [Test]
    public async Task BrowserImportSummary_an_answer_it_read_whole_gets_one_line() {
        await Assert.That(SetupCommand.BrowserImportSummary(ImportAnswer(repos: "kcap")).Count).IsEqualTo(1);
    }

    static FirstRunAgentsAnswer VisibilityAnswer(string? visibility) =>
        new([new FirstRunAgentsChoice(HarnessId.Claude, true, true)],
            new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero),
            0,
            visibility);

    [Test]
    public async Task DecideVisibility_applies_what_the_browser_chose() {
        var decided = SetupCommand.DecideVisibility(VisibilityAnswer("public"), current: "private");

        await Assert.That(decided.Apply).IsEqualTo("public");
        await Assert.That(decided.Kept).IsFalse();
    }

    // Falling through to the prompt would not leave the profile alone: its cursor starts on org_public,
    // so one Return widens an existing private on a screen the user already answered.
    [Test]
    public async Task DecideVisibility_keeps_the_profile_when_the_screen_was_answered_and_left_unset() {
        var decided = SetupCommand.DecideVisibility(VisibilityAnswer(null), current: "private");

        await Assert.That(decided.Apply).IsEqualTo("private");
        await Assert.That(decided.Kept).IsTrue();
    }

    [Test]
    public async Task DecideVisibility_prompts_when_that_screen_never_settled() {
        // Never asked is not the same as asked and declined: the terminal still has to put the
        // question, which is what a null Apply means.
        var decided = SetupCommand.DecideVisibility(null, current: "private");

        await Assert.That(decided.Apply).IsNull();
        await Assert.That(decided.Kept).IsFalse();
    }

    [Test]
    public async Task DecideVisibility_never_narrows_or_widens_a_kept_profile() {
        // Whatever the profile holds is what comes back, for every stop - the branch must not have a
        // fallback of its own.
        foreach (var stop in AppConfig.ValidVisibilities) {
            await Assert.That(SetupCommand.DecideVisibility(VisibilityAnswer(null), stop).Apply)
                        .IsEqualTo(stop);
        }
    }

    [Test]
    public async Task VisibilityLabel_names_every_stop_the_wire_can_carry() {
        // One list behind the prompt and behind the browser-answer line, so the two cannot describe the
        // same stop differently.
        foreach (var stop in AppConfig.ValidVisibilities) {
            await Assert.That(SetupCommand.VisibilityLabel(stop))
                        .IsNotEqualTo(stop)
                        .Because($"'{stop}' has no human label");
        }
    }

    [Test]
    public async Task VisibilityLabel_falls_back_to_the_value_for_a_stop_it_does_not_know() {
        // Reachable only if the closed set grows without this switch; showing the raw value beats
        // showing nothing.
        await Assert.That(SetupCommand.VisibilityLabel("telepathy")).IsEqualTo("telepathy");
    }

    // --- Step 6 import auth-eligibility probe (IsAuthSatisfiedAsync) ---

    [Test]
    public async Task IsAuthSatisfied_ProviderNone_TrueAndNeverProbesToken() {
        var probed = false;

        var ok = await SetupCommand.IsAuthSatisfiedAsync(AuthProvider.None, () => {
            probed = true;

            return Task.FromResult(false);
        });

        await Assert.That(ok).IsTrue();
        await Assert.That(probed).IsFalse(); // provider None short-circuits — no token probe
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_UsableToken_True() {
        // Models an expired-but-refreshable (or valid) token: the probe resolves to true.
        var ok = await SetupCommand.IsAuthSatisfiedAsync("github", () => Task.FromResult(true));

        await Assert.That(ok).IsTrue();
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_NoUsableToken_False() {
        var ok = await SetupCommand.IsAuthSatisfiedAsync("github", () => Task.FromResult(false));

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task IsAuthSatisfied_AuthedProvider_ProbeThrows_FalseNotThrow() {
        // A token I/O / refresh failure (e.g. non-writable token dir) must degrade to an
        // ineligible skip, NOT propagate out of setup.
        var ok = await SetupCommand.IsAuthSatisfiedAsync(
            "github", () => throw new UnauthorizedAccessException("token dir not writable"));

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task InstallPlugin_CreatesNewSettingsFile() {
        using var tmp          = new TempDir();
        var       settingsPath = tmp.PathTo("settings.json");
        var       marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    public async Task InstallPlugin_PreservesExistingSettings() {
        using var    tmp          = new TempDir();
        var          settingsPath = tmp.PathTo("settings.json");
        const string marketplace  = "/opt/kcap";

        // Pre-populate with existing settings
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "permissions": { "allow": ["Bash"] },
              "enabledPlugins": { "other-plugin@foo": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        // Original settings preserved
        await Assert.That(root["permissions"]?["allow"]?[0]?.GetValue<string>())
            .IsEqualTo("Bash");

        await Assert.That(root["enabledPlugins"]?["other-plugin@foo"]?.GetValue<bool>() ?? false)
            .IsTrue();

        // Plugin added
        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);
    }

    [Test]
    public async Task InstallPlugin_UpdatesExistingMarketplacePath() {
        using var    tmp          = new TempDir();
        var          settingsPath = tmp.PathTo("settings.json");
        const string newPath      = "/new/path";

        // Pre-populate with old marketplace path
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "extraKnownMarketplaces": {
                "kurrent": { "source": { "source": "directory", "path": "/old/path" } }
              },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, newPath);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(newPath);
    }

    [Test]
    public async Task InstallPlugin_CreatesIntermediateDirectories() {
        using var    tmp          = new TempDir();
        var          settingsPath = tmp.PathTo(".claude", "nested", "settings.json");
        const string marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();
        await Assert.That(File.Exists(settingsPath)).IsTrue();
    }

    [Test]
    public async Task InstallPlugin_MalformedJson_StartsFromScratch() {
        using var    tmp          = new TempDir();
        var          settingsPath = tmp.PathTo("settings.json");
        const string marketplace  = "/opt/kcap";

        await File.WriteAllTextAsync(settingsPath, "not json {{{");

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    public async Task Setup_save_profile_config_round_trips_active_profile() {
        // Smoke-check that the discovery-path SetupCommand can save and reload the active
        // profile after MergeProfiles has set it to a non-"default" name. The full discovery
        // flow is end-to-end-tested by the integration suite.
        var cfg = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new() { ServerUrl = "https://a.example", DefaultVisibility = "org_public" }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => cfg);

        var reloaded = await AppConfig.LoadProfileConfig(Config.Root);
        await Assert.That(reloaded.ActiveProfile).IsEqualTo("acme");
        await Assert.That(reloaded.Profiles["acme"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task LiveRecordingRestartTip_returns_note_when_any_agent_installed() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       true,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("new");
        await Assert.That(tip!).Contains("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_note_fires_for_non_claude_agents_too() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: true);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNotNull();
    }

    [Test]
    public async Task LiveRecordingRestartTip_pi_only_tells_user_to_restart_pi() {
        // A Pi-only install must not print a Claude-specific hint — it should
        // tell the user to restart pi so the kcap extension loads.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false,
            PiExtensionInstalled:  true);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("pi");
        await Assert.That(tip!).DoesNotContain("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_is_null_when_no_hooks_installed() {
        // No hooks wired up (e.g. every agent declined or none detected) — don't
        // promise live recording that won't happen.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNull();
    }

    [Test]
    public async Task GuidedTourCallToAction_is_the_exact_agreed_wording() {
        // Pinned wording — this is the only discovery path for the guided-tour skill, and the
        // prompt it names must appear verbatim in that skill's description (asserted below)
        // or the phrase we tell users to type won't reliably fire it.
        await Assert.That(SetupCommand.GuidedTourCallToAction).IsEqualTo(
            "New to Capacitor? Prompt \"Start kcap guided tour\" in your coding agent to see what "
          + "Capacitor can do for you");
    }

    [Test]
    public async Task ServerSetupCallToAction_is_the_exact_agreed_wording() {
        // Pinned wording: a question the reader self-selects on, then the instruction itself.
        await Assert.That(SetupCommand.ServerSetupQuestion).IsEqualTo(
            "Did you create this Capacitor server?");

        await Assert.That(SetupCommand.ServerSetupAction).IsEqualTo(
            "Complete server setup with instructions here:");

        await Assert.That(SetupCommand.ServerSetupDocsUrl).IsEqualTo(
            "https://capacitor.kurrent.io/docs/getting-started/setup-server/");
    }

    [Test]
    public async Task NextSteps_always_lists_server_setup_and_only_adds_the_tour_when_offered() {
        // Server setup is ungated, so it must survive a run where no agent carried the tour skill.
        var withoutTour = SetupCommand.NextStepItems(offerGuidedTour: false);

        await Assert.That(withoutTour.Count).IsEqualTo(1);
        await Assert.That(withoutTour[0].Question).IsEqualTo(SetupCommand.ServerSetupQuestion);
        await Assert.That(withoutTour[0].Answer)
                    .Contains($"[cyan]{SetupCommand.ServerSetupDocsUrl}[/]");

        var withTour = SetupCommand.NextStepItems(offerGuidedTour: true);

        await Assert.That(withTour.Count).IsEqualTo(2);
        await Assert.That(withTour[0].Question).IsEqualTo(SetupCommand.ServerSetupQuestion);
        await Assert.That(withTour[1].Question).IsEqualTo(SetupCommand.GuidedTourQuestion);
        await Assert.That(withTour[1].Answer)
                    .Contains($"[cyan]{SetupCommand.GuidedTourPromptQuoted}[/]");
    }

    [Test]
    public async Task GuidedTourCallToAction_prompt_is_a_trigger_in_the_skill_description() {
        // Vendors match a user's message against the frontmatter `description:` only — the body
        // is read after the skill fires. So asserting the phrase is somewhere in the file would
        // pass even if it moved below the fences, where it can no longer trigger anything.
        var skill = Path.Combine(RepoTree.SkillsSource(), SetupCommand.GuidedTourSkillName, "SKILL.md");

        await Assert.That(File.Exists(skill)).IsTrue();

        var description = FrontmatterDescription(await File.ReadAllTextAsync(skill));

        await Assert.That(description).Contains(SetupCommand.GuidedTourPrompt);
    }

    /// <summary>
    /// The value of the YAML frontmatter's <c>description:</c> field, flattened to one line.
    /// Throws when there is no frontmatter — an unparseable SKILL.md must fail the test, not
    /// silently return an empty string that a Contains assertion would report as a mismatch.
    /// </summary>
    static string FrontmatterDescription(string content) {
        var lines = content.Replace("\r\n", "\n").Split('\n');

        if (lines.Length < 2 || lines[0].Trim() != "---")
            throw new InvalidOperationException("SKILL.md has no YAML frontmatter block.");

        var value    = new List<string>();
        var inValue  = false;

        for (var i = 1; i < lines.Length; i++) {
            var line = lines[i];

            if (line.Trim() == "---") break;

            if (line.StartsWith("description:", StringComparison.Ordinal)) {
                inValue = true;
                // Covers `description: text` as well as the block form `description: >-`.
                value.Add(line["description:".Length..].Trim().TrimEnd('>', '|', '-').Trim());

                continue;
            }

            if (!inValue) continue;

            // An unindented line is the next top-level key, which ends this value.
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;

            value.Add(line.Trim());
        }

        if (!inValue) throw new InvalidOperationException("SKILL.md frontmatter has no description field.");

        return string.Join(' ', value).Trim();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_nothing_carries_the_skill() {
        using var tmp = new TempDir();

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, tmp.PathTo(".claude", "settings.json"), GuidedTourPaths(tmp.Path))).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_no_agent_is_detected_even_if_skills_exist() {
        // A machine carrying the skill but running no agent CLI has nothing to type the prompt
        // into. Skill-presence alone is not enough — both halves are required.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            false, tmp.PathTo(".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_when_the_registered_plugin_ships_the_skill() {
        using var tmp          = new TempDir();
        var       settingsPath = tmp.PathTo(".claude", "settings.json");
        var       marketplace  = tmp.PathTo("plugin");

        SetupCommand.InstallPlugin(settingsPath, marketplace);
        Directory.CreateDirectory(Path.Combine(marketplace, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(marketplace, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, GuidedTourPaths(tmp.Path))).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_uses_a_legacy_registered_marketplace_path() {
        // IsInstalled recognises pre-rename keys (kurrent/kapacitor); the path reader must use
        // the same key set, or the gate falls back to THIS build's plugin dir — which ships the
        // skill — while Claude actually loads the legacy dir, which does not.
        using var tmp          = new TempDir();
        var       settingsPath = tmp.PathTo(".claude", "settings.json");
        var       legacyDir    = tmp.PathTo("legacy-plugin");

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "extraKnownMarketplaces": {
                "kapacitor": { "source": { "source": "directory", "path": {{System.Text.Json.JsonSerializer.Serialize(legacyDir)}} } }
              },
              "enabledPlugins": { "kapacitor@kapacitor": true }
            }
            """);
        Directory.CreateDirectory(Path.Combine(legacyDir, "skills", "recap"));

        // Current build's plugin dir DOES ship the skill — the wrong fallback target.
        var modernPlugin = tmp.PathTo("modern-plugin");
        Directory.CreateDirectory(Path.Combine(modernPlugin, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(modernPlugin, "skills", "guided-tour", "SKILL.md"), "skill");

        var paths = GuidedTourPaths(tmp.Path) with { PluginDir = modernPlugin };

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_an_old_plugin_is_registered_and_pluginDir_is_null() {
        // Codex round 4: an older kcap plugin registered in settings, run from a layout where
        // ResolvePluginPath finds nothing. Registration alone must not advertise a skill the
        // registered directory does not ship.
        using var tmp          = new TempDir();
        var       settingsPath = tmp.PathTo(".claude", "settings.json");
        var       oldPlugin    = tmp.PathTo("old-plugin");

        SetupCommand.InstallPlugin(settingsPath, oldPlugin);
        Directory.CreateDirectory(Path.Combine(oldPlugin, "skills", "recap")); // pre-guided-tour layout

        var paths = GuidedTourPaths(tmp.Path); // PluginDir stays null

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        // The registered dir gaining the skill flips it — the gate tracks what Claude loads.
        Directory.CreateDirectory(Path.Combine(oldPlugin, "skills", "guided-tour"));
        await File.WriteAllTextAsync(Path.Combine(oldPlugin, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_when_shared_agent_skills_are_present() {
        // The case a "was it installed THIS run?" gate gets wrong: skills already on disk mean a
        // wired-up machine, but every installer reports false because there was no work to do.
        using var tmp    = new TempDir();
        var       paths  = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));
        await File.WriteAllTextAsync(
            Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, tmp.PathTo(".claude", "settings.json"), paths)).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_the_skill_folder_is_empty() {
        // A failed copy creates the folder before writing SKILL.md, so an empty folder means a
        // broken install, not a usable skill.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-guided-tour"));

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, tmp.PathTo(".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task HasSkill_empty_targetDir_is_false_not_a_cwd_probe() {
        // Paths defaults some skill dirs to "" — Path.Combine("", ...) would otherwise probe a
        // relative kcap-guided-tour against whatever directory setup was run from.
        await Assert.That(AgentsSkillsInstaller.HasSkill("", "guided-tour")).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_true_from_the_kiro_and_antigravity_skill_dirs() {
        using var kiro = new TempDir();
        using var anti = new TempDir();

        kiro.CreateDir("kiro-skills", "kcap-guided-tour");
        kiro.CreateFile(["kiro-skills", "kcap-guided-tour", "SKILL.md"], "skill");
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, kiro.PathTo("none.json"), GuidedTourPaths(kiro.Path))).IsTrue();

        anti.CreateDir("antigravity-skills", "kcap-guided-tour");
        anti.CreateFile(["antigravity-skills", "kcap-guided-tour", "SKILL.md"], "skill");
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, anti.PathTo("none.json"), GuidedTourPaths(anti.Path))).IsTrue();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_only_older_skills_are_installed() {
        // An upgrade from a kcap that predates guided-tour leaves kcap-recap and friends on disk.
        // "Has this installer ever run here" is true there; "can the user run the tour" is not.
        using var tmp   = new TempDir();
        var       paths = GuidedTourPaths(tmp.Path);

        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-recap"));
        Directory.CreateDirectory(Path.Combine(paths.AgentsSkillsDir, "kcap-errors"));

        await Assert.That(AgentsSkillsInstaller.IsInstalled(paths.AgentsSkillsDir)).IsTrue();
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(
            true, tmp.PathTo(".claude", "settings.json"), paths)).IsFalse();
    }

    [Test]
    public async Task ShouldOfferGuidedTour_false_when_the_claude_plugin_dir_lacks_the_skill() {
        // Registration alone isn't enough when the resolved plugin dir is a stale install.
        using var tmp          = new TempDir();
        var       settingsPath = tmp.PathTo(".claude", "settings.json");
        var       pluginDir    = tmp.PathTo("stale-plugin");

        SetupCommand.InstallPlugin(settingsPath, pluginDir);
        Directory.CreateDirectory(Path.Combine(pluginDir, "skills", "recap"));

        var paths = GuidedTourPaths(tmp.Path) with { PluginDir = pluginDir };

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        Directory.CreateDirectory(Path.Combine(pluginDir, "skills", "guided-tour"));

        // Folder alone is still not enough — the file is the skill.
        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsFalse();

        await File.WriteAllTextAsync(
            Path.Combine(pluginDir, "skills", "guided-tour", "SKILL.md"), "skill");

        await Assert.That(SetupCommand.ShouldOfferGuidedTour(true, settingsPath, paths)).IsTrue();
    }

    /// <summary>Paths record carrying only the four fields GuidedTourReachable reads.</summary>
    static CodingAgentsStep.Paths GuidedTourPaths(string root) =>
        new(ClaudeSettingsPath:   Path.Combine(root, ".claude", "settings.json"),
            ClaudeScopeLabel:     "user",
            PluginDir:            null,
            CodexHooksPath:       Path.Combine(root, "codex-hooks.json"),
            CursorHooksPath:      Path.Combine(root, "cursor-hooks.json"),
            CopilotHooksPath:     Path.Combine(root, "copilot-hooks.json"),
            GeminiSettingsPath:   Path.Combine(root, "gemini-settings.json"),
            AgentsSkillsDir:      Path.Combine(root, "agents-skills"),
            LegacyCodexSkillsDir: Path.Combine(root, "legacy-codex-skills"),
            KiroHooksPath:        Path.Combine(root, "kiro-hooks.json"),
            PiExtensionPath:      Path.Combine(root, "pi-ext.ts"),
            OpenCodeExtensionPath: Path.Combine(root, "oc-ext.ts"),
            AntigravityHooksPath: Path.Combine(root, "antigravity-hooks.json"),
            CodexConfigTomlPath:  Path.Combine(root, "config.toml"),
            CursorMcpPath:        Path.Combine(root, "cursor-mcp.json"),
            CopilotMcpPath:       Path.Combine(root, "copilot-mcp.json"),
            CopilotInstructionsPath: Path.Combine(root, "copilot-instructions.md"),
            GeminiInstructionsPath: Path.Combine(root, "GEMINI.md"),
            AntigravityMcpPath:   Path.Combine(root, "antigravity-mcp.json"),
            AntigravityInstructionsPath: Path.Combine(root, "antigravity-instructions.md"),
            AntigravitySkillsDir: Path.Combine(root, "antigravity-skills"),
            OpenCodeMcpPath:      Path.Combine(root, "oc-mcp.json"),
            OpenCodeInstructionsPath: Path.Combine(root, "AGENTS.md"),
            KiroMcpPath:          Path.Combine(root, "kiro-mcp.json"),
            KiroSkillsDir:        Path.Combine(root, "kiro-skills"),
            PiMcpExtensionPath:   Path.Combine(root, "pi-mcp.ts"),
            PiAgentsMdPath:       Path.Combine(root, "pi-AGENTS.md"));

    // --- Step 6 (RunImportStepAsync) wiring ---
    //
    // SetupCommand.ImportRunnerOverride is process-global static state (mutated by
    // RunImportStepAsync's caller — HandleAsync — only via this seam), so every test that sets it
    // must run serialized against the others and reset it to null in a finally block.
    const string ImportRunnerOverrideMutation = nameof(ImportRunnerOverrideMutation);

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunDecision_InvokesRunnerWithPinnedArgs() {
        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };
        var passed = Resolutions.At("https://example.test", Config.Root);

        try {
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt under --no-prompt"),
                profiles:          passed,
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Profiles.Resolution.ServerUrl).IsEqualTo("https://example.test");
        await Assert.That(captured.Repo).IsEqualTo(("acme", "widgets"));
        await Assert.That(captured.DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(captured.AutoSkipExclusions).IsTrue();
        await Assert.That(captured.ForcePrivate).IsFalse();
        await Assert.That(captured.Profiles).IsSameReferenceAs(passed);
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_InteractiveAccept_InvokesRunner() {
        var invoked = false;
        SetupCommand.ImportRunnerOverride = _ => {
            invoked = true;
            return Task.FromResult(0);
        };

        try {
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => true,
                profiles:          Resolutions.At("https://example.test", Config.Root),
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerReturnsNonZero_DoesNotThrowAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => Task.FromResult(1);

        try {
            // Completing without an unhandled exception is the assertion: a non-zero exit
            // code must be swallowed (warned about, not propagated) so setup still finishes.
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                profiles:          Resolutions.At("https://example.test", Config.Root),
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerThrows_DoesNotPropagateAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("boom");

        try {
            // Completing without the InvalidOperationException escaping is the assertion —
            // import is best-effort and must never fail setup.
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                profiles:          Resolutions.At("https://example.test", Config.Root),
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_NoCurrentRepo_SkipsWithoutInvokingRunnerOrPrompting() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       null,
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                profiles:          Resolutions.At("https://example.test", Config.Root),
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_SkipImportFlag_SkipsWithoutInvokingRunner() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        true,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                profiles:          Resolutions.At("https://example.test", Config.Root),
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    // HandleAsync-level acceptance coverage for the import wiring: the whole wizard — flag parsing,
    // server normalization and probe, auth discovery, profile save, import — against a real WireMock
    // server, with only the final import call intercepted through ImportRunnerOverride.
    //
    // Every test here:
    //   • runs from a throwaway git repo (real `git init` + `remote add origin`) so repository
    //     detection resolves an owner/repo — HandleAsync reads Environment.CurrentDirectory itself,
    //     so the process cwd has to move.
    //   • passes every --skip-*-hooks/-mcp/-instructions/-skills flag, so no coding-agent install
    //     runs against the injected home.
    //   • uses auth provider "None" (a WireMock /auth/config stub): with any other provider the
    //     --server-url path has no way to no-prompt past the login.
    //
    // They move the working directory, read every vendor override variable through SetupCommand's
    // own HarnessPaths, and stub /auth/config — so they join all three cohorts below.
    const string HandleAsyncNotInParallelGroups_VendorEnvOverrides = "VendorEnvOverrides"; // shared w/ UninstallCommandTests
    const string HandleAsyncNotInParallelGroups_CwdMutation        = "CwdMutation";        // shared w/ UninstallCommandTests
    const string HandleAsyncNotInParallelGroups_ProviderCache      = "AuthProviderDiscoveryCache"; // shared w/ every /auth/config stubber

    static string[] SkipAllAgentInstallFlags => [
        "--skip-claude-hooks", "--skip-codex-hooks", "--skip-codex-network-access",
        "--skip-cursor-hooks", "--skip-cursor-mcp",
        "--skip-copilot-hooks", "--skip-copilot-mcp", "--skip-copilot-instructions",
        "--skip-gemini-hooks", "--skip-gemini-mcp", "--skip-gemini-instructions",
        "--skip-kiro-hooks", "--skip-kiro-mcp", "--skip-kiro-skills",
        "--skip-pi-hooks", "--skip-pi-mcp", "--skip-pi-instructions",
        "--skip-opencode-hooks", "--skip-opencode-mcp", "--skip-opencode-instructions",
        "--skip-antigravity-hooks", "--skip-antigravity-mcp", "--skip-antigravity-instructions", "--skip-antigravity-skills",
    ];

    static string[] BuildArgs(params string[] extra) => ["setup", .. extra, .. SkipAllAgentInstallFlags];

    static void StubAuthProviderNone(WireMockServer server) =>
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_VendorEnvOverrides, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ProviderCache,
        ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_NoPromptWithServerUrl_AutoImportsWithPinnedInvocation_UnderAuthProviderNoneAndNoToken() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-auto-import", "widgets", Config.Root);

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt", "--default-visibility", "org_public");

            var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Repo).IsEqualTo(("acme-auto-import", "widgets"));
            await Assert.That(captured.AutoSkipExclusions).IsTrue();
            await Assert.That(captured.ForcePrivate).IsFalse();
            await Assert.That(captured.DefaultVisibility).IsEqualTo("org_public");
            await Assert.That(captured.Profiles.Resolution.ServerUrl).IsEqualTo(server.Url!.TrimEnd('/'));

            // Auth provider None makes Step 6 eligible WITHOUT any token: Step 2 short-circuits
            // to "no login required" (no OAuth flow ran), so nothing was ever stored — yet
            // import still ran (asserted above). Confirm no token exists for the profile the
            // import actually saw.
            await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync(
                captured.Profiles.Name)).IsNull();
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_VendorEnvOverrides, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ProviderCache,
        ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_SkipImportFlag_SuppressesAutoImport() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-skip-import", "widgets", Config.Root);

        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt", "--skip-import");

            // Completing with exit 0 without the override's exception escaping is the
            // assertion — --skip-import must suppress the Step 6 call entirely.
            var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel([
        HandleAsyncNotInParallelGroups_VendorEnvOverrides, HandleAsyncNotInParallelGroups_CwdMutation,
        HandleAsyncNotInParallelGroups_ProviderCache,
        ImportRunnerOverrideMutation
    ])]
    public async Task HandleAsync_SchemeLessServerUrl_ReachesImportRunnerNormalizedWithHttpScheme() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        var port                = new Uri(server.Url!).Port;
        var schemeLessServerUrl = $"localhost:{port}";

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-schemeless", "widgets", Config.Root);

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", schemeLessServerUrl, "--no-prompt");

            var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            // Step-1 normalization: the scheme-less --server-url must reach the import runner
            // already normalized (http:// for a loopback host), not the raw scheme-less string.
            await Assert.That(captured!.Profiles.Resolution.ServerUrl).IsEqualTo($"http://localhost:{port}");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    // Bare, not the shared keys: both vars are read by every profile resolution in the assembly
    // and inherited by every spawned child, so no cohort of key-holders can exclude their observers.
    [NotInParallel]
    public async Task HandleAsync_ConflictingKcapUrlAndProfileEnvVars_DoesNotHijackSavedServerOrProfile() {
        using var server = WireMockServer.Start();
        StubAuthProviderNone(server);

        await using var fixture = await HandleAsyncE2EFixture.CreateAsync("acme-envconflict", "widgets", Config.Root);

        // Deliberately conflicting: neither matches the --server-url this run actually saves.
        using var kcapUrl     = EnvScope.Exclusive("KCAP_URL", "http://conflicting-env.invalid");
        using var kcapProfile = EnvScope.Exclusive("KCAP_PROFILE", "conflicting-profile");

        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            var args = BuildArgs("--server-url", server.Url!, "--no-prompt");

            var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(args);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Profiles.Resolution.ServerUrl).IsEqualTo(server.Url!.TrimEnd('/'));
            // Setup hands the import the resolution it just persisted, not a re-resolution — so a
            // conflicting KCAP_PROFILE in the environment cannot redirect it.
            await Assert.That(captured.Profiles.Resolution.ProfileName).IsEqualTo("default");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    /// <summary>
    /// Isolation fixture for the HandleAsync acceptance tests above. See the comment block
    /// preceding them for what each piece of isolation guards against.
    /// </summary>
    sealed class HandleAsyncE2EFixture : IAsyncDisposable {
        readonly GitRepo _repo;
        readonly string  _originalCwd;

        public string RepoDir => _repo.Path;

        HandleAsyncE2EFixture(GitRepo repo, string originalCwd) {
            _repo        = repo;
            _originalCwd = originalCwd;
        }

        public static async Task<HandleAsyncE2EFixture> CreateAsync(string owner, string repo, ConfigRoot configRoot) {
            var repoDir = GitRepo.Create();
            repoDir.AddRemote($"https://github.com/{owner}/{repo}.git");

            var originalCwd = Environment.CurrentDirectory;

            var configPath = AppConfig.GetConfigPath(configRoot);
            if (File.Exists(configPath)) File.Delete(configPath);

            var tokensDir = configRoot.Path("tokens");
            if (Directory.Exists(tokensDir)) Directory.Delete(tokensDir, recursive: true);

            var legacyTokens = configRoot.Path("tokens.json");
            if (File.Exists(legacyTokens)) File.Delete(legacyTokens);

            Environment.CurrentDirectory = repoDir.Path;

            return new HandleAsyncE2EFixture(repoDir, originalCwd);
        }

        public ValueTask DisposeAsync() {
            Environment.CurrentDirectory = _originalCwd;

            _repo.Dispose();

            return ValueTask.CompletedTask;
        }

    }

    // --- --org / --slug, the create-a-workspace prompts answered up front ---

    static (RequestedWorkspace? Workspace, string? Error) Parse(params string[] args) =>
        SetupCommand.ParseRequestedWorkspace(args, haveServerUrl: false);

    [Test]
    public async Task ParseRequestedWorkspace_asks_for_nothing_when_neither_flag_is_passed() {
        var (workspace, error) = Parse("setup", "--no-prompt");

        await Assert.That(workspace).IsNull();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task ParseRequestedWorkspace_carries_both_answers_through() {
        var (workspace, error) = Parse("setup", "--org", "  Acme  ", "--slug", "  ACME  ");

        await Assert.That(error).IsNull();
        await Assert.That(workspace!.OrgName).IsEqualTo("Acme");
        await Assert.That(workspace.Slug).IsEqualTo("acme");
        await Assert.That(workspace.Origin).IsEqualTo("https://acme.kcap.ai");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_half_a_pair() {
        await Assert.That(Parse("setup", "--org", "Acme").Error).Contains("--slug");
        await Assert.That(Parse("setup", "--slug", "acme").Error).Contains("--org");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_flag_where_a_value_should_be() {
        var (workspace, error) = Parse("setup", "--org", "--slug", "acme");

        await Assert.That(workspace).IsNull();
        await Assert.That(error!).Contains("--org needs a value");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_flag_with_no_value_at_all() {
        await Assert.That(Parse("setup", "--slug", "acme", "--org").Error!).Contains("--org needs a value");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_blank_value() {
        await Assert.That(Parse("setup", "--org", "   ", "--slug", "acme").Error!).Contains("--org needs a value");
        await Assert.That(Parse("setup", "--org", "Acme", "--slug", "   ").Error!).Contains("--slug needs a value");
    }

    // Not this CLI's spelling, so an exact-token search cannot see it - and the flags would be
    // dropped in silence, which is the one outcome this parse refuses.
    [Test]
    public async Task ParseRequestedWorkspace_refuses_the_equals_spelling() {
        await Assert.That(Parse("setup", "--org=Acme", "--slug=acme").Error!).Contains("--org needs a value");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_an_empty_value() {
        await Assert.That(Parse("setup", "--org", "", "--slug", "acme").Error!).Contains("--org needs a value");
    }

    // Caught here rather than only on the path that reaches the provisioner, so one message describes
    // a malformed slug wherever the run happens to notice it.
    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_slug_that_could_never_be_a_hostname() {
        await Assert.That(Parse("setup", "--org", "Acme", "--slug", "Acme Corp").Error!).Contains("not a valid slug");
        await Assert.That(Parse("setup", "--org", "Acme", "--slug", "api").Error!).Contains("reserved");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_to_create_and_point_at_a_positional_tenant_at_once() {
        var (workspace, error) = SetupCommand.ParseRequestedWorkspace(
            ["setup", "acme", "--org", "Acme", "--slug", "acme"], haveServerUrl: true);

        await Assert.That(workspace).IsNull();
        await Assert.That(error!).Contains("kcap setup <tenant>");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_to_create_and_point_at_a_server_at_once() {
        var (workspace, error) = SetupCommand.ParseRequestedWorkspace(
            ["setup", "--org", "Acme", "--slug", "acme"], haveServerUrl: true);

        await Assert.That(workspace).IsNull();
        await Assert.That(error!).Contains("--server-url");
    }

    static Task Guard(RequestedWorkspace requested, params string[] profiles) =>
        SetupCommand.WorkspaceGuard(requested)!(
            [.. profiles.Select(p => new AuthIdentity(p, $"https://{p}.kcap.ai"))], CancellationToken.None);

    // Refusing on the commit boundary's last cancellable step is what keeps the stop free of a
    // published profile, stamp or token.
    [Test]
    public async Task WorkspaceGuard_refuses_a_commit_that_would_publish_another_workspace() {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Guard(new RequestedWorkspace("Acme", "acme"), "globex"));

        await Assert.That(thrown!.Message).Contains("acme");
        await Assert.That(thrown.Message).Contains("globex");
    }

    // A re-run once the workspace exists lands on it, which is the asked-for outcome.
    [Test]
    public async Task WorkspaceGuard_lets_the_workspace_that_was_asked_for_through() {
        await Guard(new RequestedWorkspace("Acme", "acme"), "acme");
    }

    // The profile name is the comparison, not the URL: the server names the workspace it creates, so
    // a url in any other shape must not read as landing somewhere else.
    [Test]
    public async Task WorkspaceGuard_judges_by_slug_rather_than_by_the_url_the_server_returned() {
        await Guard(new RequestedWorkspace("Acme", "acme"), "acme");

        await SetupCommand.WorkspaceGuard(new RequestedWorkspace("Acme", "acme"))!(
            [new AuthIdentity("acme", "https://acme.eu.kcap.ai")], CancellationToken.None);
    }

    [Test]
    public async Task WorkspaceGuard_is_absent_when_no_workspace_was_asked_for() {
        await Assert.That(SetupCommand.WorkspaceGuard(null)).IsNull();
    }

    // These drive argv. The three rejections return before any config read, network call or console
    // rule, so they need none of the E2E fixture below.
    [Test]
    [NotInParallel]
    public async Task HandleAsync_rejects_half_a_pair_before_doing_anything() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(["setup", "--org", "Acme"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("--slug");
    }

    [Test]
    [NotInParallel]
    public async Task HandleAsync_rejects_creating_and_pointing_at_a_server_at_once() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(
            ["setup", "--org", "Acme", "--slug", "acme", "--server-url", "https://other.kcap.ai"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("--server-url");
    }

    [Test]
    [NotInParallel]
    public async Task HandleAsync_rejects_a_provider_that_cannot_create() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(["setup", "--org", "Acme", "--slug", "acme", "--github"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("--github");
    }

    [Test]
    [NotInParallel]
    public async Task HandleAsync_still_requires_a_server_url_with_no_prompt_and_no_answers() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await new SetupCommand(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), HttpFactory, Proxy, Workos, Github, new RecordingBrowser(), Home, new FixedCapacitorHttpClient(), Provisioning, Discovery).HandleAsync(["setup", "--no-prompt"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError()).Contains("--server-url is required");
        await Assert.That(capture.GetCapturedError()).Contains("--org");
    }

    // Presence, not value: a valueless --server-url parses as absent, and taking that as "no conflict"
    // turns a run meant to point at a workspace into one that creates a different one.
    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_server_flag_that_carries_no_value() {
        var (workspace, error) = SetupCommand.ParseRequestedWorkspace(
            ["setup", "--org", "Acme", "--slug", "acme", "--server-url"], haveServerUrl: true);

        await Assert.That(workspace).IsNull();
        await Assert.That(error!).Contains("--server-url");
    }

    [Test]
    public async Task ParseRequestedWorkspace_refuses_a_provider_that_cannot_create() {
        var (workspace, error) = Parse("setup", "--org", "Acme", "--slug", "acme", "--github");

        await Assert.That(workspace).IsNull();
        await Assert.That(error!).Contains("--github");
    }
}
