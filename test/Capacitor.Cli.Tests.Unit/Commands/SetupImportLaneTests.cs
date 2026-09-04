using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Turning what discovery found into what the Import screen is told, and choosing which sources it
/// scanned in the first place. Both are places a figure can quietly stop matching the disk.
/// </summary>
public class SetupImportLaneTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ImportCommand.ImportDiscoveryResult Found(
            IEnumerable<ImportDiscoverySummary.RepoTotals> repos,
            IReadOnlyDictionary<string, int>?              unmatched = null,
            IReadOnlyList<HarnessId>?                     scanned   = null) =>
        new(new ImportDiscoverySummary(
                [.. repos],
                unmatched?.Values.Sum() ?? 0,
                [],
                unmatched ?? new Dictionary<string, int>()),
            scanned ?? []);

    static ImportDiscoverySummary.RepoTotals Repo(string owner, string name, int sessions = 1) =>
        new(owner, name, sessions, null,
            new Dictionary<string, int> { [FirstRunImportWindows.Everything] = sessions });

    [Test]
    public async Task Carries_each_repositorys_counts_keyed_by_window() {
        var report = SetupImportLane.Report(Found([Repo("kurrent-io", "kcap-server", 12)], scanned: [HarnessId.Claude]));

        var repo = report.Repos.Single();

        await Assert.That(repo.Owner).IsEqualTo("kurrent-io");
        await Assert.That(repo.Sessions[FirstRunImportWindows.Everything]).IsEqualTo(12);
        await Assert.That(report.Vendors).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task Reports_the_total_before_the_cap_so_what_it_hid_is_disclosable() {
        // A cap with no companion figure is data loss wearing a bound.
        var many = Enumerable.Range(0, ReportFirstRunImportRequest.MaxRepos + 5)
                             .Select(i => Repo("owner", $"repo-{i}"));

        var report = SetupImportLane.Report(Found(many));

        await Assert.That(report.Repos.Count).IsEqualTo(ReportFirstRunImportRequest.MaxRepos);
        await Assert.That(report.RepoTotal).IsEqualTo(ReportFirstRunImportRequest.MaxRepos + 5);
    }

    [Test]
    public async Task Keeps_the_order_discovery_produced_so_the_cap_keeps_the_newest() {
        var report = SetupImportLane.Report(Found([
            Repo("owner", "first"), Repo("owner", "second"), Repo("owner", "third")
        ]));

        await Assert.That(report.Repos.Select(r => r.Name)).IsEquivalentTo(["first", "second", "third"]);
    }

    [Test]
    public async Task An_over_long_identity_is_dropped_and_still_counted_in_the_total() {
        // Dropped, never truncated: owner and name are what resolve back to `--repo owner/name`, so a
        // shortened one names a repository that does not exist. It still counts, because it IS a
        // repository — one we cannot name — and the screen should say it is hiding it.
        var report = SetupImportLane.Report(Found([
            Repo("owner", "fine"),
            Repo(new string('o', ReportFirstRunImportRequest.MaxOwnerLength + 1), "nope"),
            Repo("owner", new string('n', ReportFirstRunImportRequest.MaxNameLength + 1))
        ]));

        await Assert.That(report.Repos.Single().Name).IsEqualTo("fine");
        await Assert.That(report.RepoTotal).IsEqualTo(3);
    }

    [Test]
    public async Task Unmatched_sessions_travel_per_window() {
        var report = SetupImportLane.Report(Found(
            [], new Dictionary<string, int> { [FirstRunImportWindows.Last30] = 8 }));

        await Assert.That(report.Unmatched[FirstRunImportWindows.Last30]).IsEqualTo(8);
    }

    [Test]
    public async Task An_empty_scan_is_a_report_rather_than_nothing_to_say() {
        // The one user with no history has to be told so, not left watching a spinner.
        var report = SetupImportLane.Report(Found([]));

        await Assert.That(report.Repos).IsEmpty();
        await Assert.That(report.RepoTotal).IsEqualTo(0);
    }

    static FirstRunImportAnswer Answer(
            IReadOnlyList<HarnessId>? vendors = null, params (string Name, FirstRunImportLevel Level)[] repos) =>
        new([.. repos.Select(r => new FirstRunImportChoice("kurrent-io", r.Name, r.Level))],
            FirstRunImportWindows.Last30,
            FirstRunImportTitles.Server,
            vendors,
            DateTimeOffset.UnixEpoch,
            0);

    SetupImportLane Lane(Func<SetupImportLane.Pass, Task<ImportCommand.ImportRunOutcome?>> runner) =>
        new(Config.Root, Resolutions.None(Config.Root), Home, new FixedCapacitorHttpClient(),
            HarnessPaths.FromEnvironment(Home), runner);

    /// <summary>A run that reported its Done grid with nothing failed.</summary>
    static Task<ImportCommand.ImportRunOutcome?> Clean() =>
        Task.FromResult<ImportCommand.ImportRunOutcome?>(new(Counts(), 0));

    static ImportCommand.FinalCounts Counts(int errored = 0, int probeError = 0) =>
        new(Loaded: 1, Resumed: 0, AlreadyLoaded: 0, TooShort: 0, Excluded: 0,
            ProbeError: probeError, Errored: errored,
            TitlesGenerated: 0, TitlesSkipped: 0, TitlesFailed: 0,
            SummariesGenerated: 0, SummariesFailed: 0, RanBackground: false, RequestedSummaries: false);

    [Test]
    public async Task One_pass_per_level_because_private_is_per_invocation() {
        var passes = new List<SetupImportLane.Pass>();

        await Lane(p => { passes.Add(p); return Clean(); }).ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(passes.Select(p => p.Level))
                    .IsEquivalentTo([FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared]);
        await Assert.That(passes[0].Repos.Single().Name).IsEqualTo("mine");
        await Assert.That(passes[1].Repos.Single().Name).IsEqualTo("ours");
    }

    [Test]
    public async Task Both_passes_take_the_same_since_boundary() {
        // Resolved once for the decision: two passes either side of UTC midnight would otherwise
        // import against different windows.
        var passes = new List<SetupImportLane.Pass>();

        await Lane(p => { passes.Add(p); return Clean(); }).ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(passes.Select(p => p.Since).Distinct().Count()).IsEqualTo(1);
        await Assert.That(passes[0].Since).IsEqualTo(new DateOnly(2026, 5, 16));
    }

    [Test]
    public async Task A_run_whose_sessions_failed_is_recorded_as_a_failure() {
        // The case an exit code cannot express: import is best-effort and returns 0 having printed a
        // Done grid with failures in it, so reading the code would call this a success.
        var lane = Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(new(Counts(errored: 2), 0)));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_lost_visibility_write_is_recorded_as_a_failure() {
        // The user chose who may read this history. A session still carrying the old visibility is a
        // failure of that, whatever the transcript did.
        var lane = Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(new(Counts(), 1)));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_run_that_reported_nothing_is_recorded_as_a_failure() {
        // No Done grid means it never got there — an early return, not a success.
        var lane = Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(null));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_clean_run_is_not_recorded_as_a_failure() {
        var lane = Lane(_ => Clean());

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsFalse();
    }

    [Test]
    public async Task A_pass_that_throws_is_recorded_as_a_failure_too() {
        // The closing summary reads Failed, so a swallowed throw would draw a tick over history that
        // never moved.
        var lane = Lane(_ => throw new HttpRequestException("server went away"));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_throwing_private_pass_does_not_cancel_the_shared_one() {
        // They are separate promises about separate repositories; one failing is not a reason to
        // silently drop the other.
        var seen = new List<FirstRunImportLevel>();

        var lane = Lane(p => {
            seen.Add(p.Level);

            return p.Level is FirstRunImportLevel.OnlyMe
                ? throw new HttpRequestException("private pass died")
                : Clean();
        });

        await lane.ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo([FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared]);
        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_cancel_propagates_rather_than_being_recorded_and_swallowed() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var lane = Lane(_ => throw new OperationCanceledException());

        await Assert.That(async () => await lane.ImportAsync(
                        Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), cts.Token))
                    .Throws<OperationCanceledException>();
    }

    // The window counts are only comparable with the import that follows them if both resolve from
    // the same instant. This pins the half that carries it into discovery; the flow supplies it.
    [Test]
    public async Task Discovery_resolves_its_windows_against_the_instant_it_is_given() {
        var projects = Config.PathTo("claude-projects");
        var cwdDir   = Path.Combine(projects, "-tmp-asof-proj");
        Directory.CreateDirectory(cwdDir);
        File.WriteAllLines(Path.Combine(cwdDir, "asof-session.jsonl"),
            Enumerable.Range(0, 20).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/asof-proj","message":{"content":"l{{{i}}}"}}"""));

        async Task<int?> ThirtyDayCountAsOf(DateTimeOffset asOf) {
            ImportCommand.ImportDiscoveryResult? found = null;

            await new ImportCommand(Config.Root, Resolutions.None(Config.Root), Home,
                new FixedCapacitorHttpClient()).HandleImport(
                filterCwd:    null,
                minLines:     1,
                sources:      [new ClaudeImportSource(Config.Root, projects)],
                discoverOnly: true,
                discoverJson: true,
                windowsAsOf:  asOf,
                onDiscovered: r => found = r);

            return found?.Summary.ByWindow
                        .Single(w => w.Key == FirstRunImportWindows.Last30)
                        .SessionCount;
        }

        // The session is 29 days old against the first instant and 31 against the second, so the
        // 30-day window has to answer differently for each.
        await Assert.That(await ThirtyDayCountAsOf(new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero)))
                    .IsEqualTo(1);
        await Assert.That(await ThirtyDayCountAsOf(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)))
                    .IsEqualTo(0);
    }

    [Test]
    public async Task Every_harness_has_a_source_when_nothing_filters_them() {
        var built = SetupCommand.BuildImportSources(Config.Root, HarnessPaths.FromEnvironment(Home));

        await Assert.That(built.Select(b => b.Vendor))
                    .IsEquivalentTo(HarnessRegistry.Identities.Select(h => h.Id));
    }

    [Test]
    public async Task Only_the_named_vendors_sources_are_built() {
        // The filter is applied to what gets scanned, which is what makes a reported figure already
        // scoped rather than needing subtraction afterwards.
        var built = SetupCommand.BuildImportSources(Config.Root, HarnessPaths.FromEnvironment(Home), [HarnessId.Claude, HarnessId.Codex]);

        await Assert.That(built.Select(s => s.Vendor)).IsEquivalentTo([HarnessId.Claude, HarnessId.Codex]);
    }

    [Test]
    public async Task An_empty_vendor_list_builds_nothing_rather_than_everything() {
        // "Scan nothing" is a real answer — every agent on the machine was left unrecorded — and
        // collapsing it to "no filter" would import exactly what the user declined.
        await Assert.That(SetupCommand.BuildImportSources(Config.Root, HarnessPaths.FromEnvironment(Home), [])).IsEmpty();
    }

    // ---- What the run reports back to the flow.

    static ImportCommand.FinalCounts Moved(
            int loaded = 0, int resumed = 0, int alreadyLoaded = 0, int tooShort = 0, int excluded = 0,
            int probeError = 0, int errored = 0) =>
        new(loaded, resumed, alreadyLoaded, tooShort, excluded, probeError, errored,
            TitlesGenerated: 0, TitlesSkipped: 0, TitlesFailed: 0,
            SummariesGenerated: 0, SummariesFailed: 0, RanBackground: false, RequestedSummaries: false);

    [Test]
    public async Task Sums_the_totals_across_both_passes() {
        // One report goes to the flow but --private forces two invocations, so a lane returning either
        // pass alone would halve the figures the screen shows.
        var queue = new Queue<ImportCommand.ImportRunOutcome?>([
            new(Moved(loaded: 3, alreadyLoaded: 1), 0),
            new(Moved(loaded: 4, resumed: 2, tooShort: 5, errored: 1), 0)
        ]);

        var totals = await Lane(_ => Task.FromResult(queue.Dequeue())).ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(totals).IsNotNull();
        await Assert.That((totals!.Value.Imported, totals.Value.Skipped, totals.Value.Failed))
                    .IsEqualTo((9, 6, 1));
    }

    [Test]
    public async Task A_resume_counts_as_imported_and_not_as_a_third_thing() {
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(
                new(Moved(resumed: 4), 0))).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That(totals!.Value.Imported).IsEqualTo(4);
    }

    [Test]
    public async Task A_probe_error_is_reported_as_failed_and_not_as_skipped() {
        // Re-running retries it, which is what failed means here — unlike too-short or already-loaded.
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(
                new(Moved(probeError: 2), 0))).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That((totals!.Value.Skipped, totals.Value.Failed)).IsEqualTo((0, 2));
    }

    [Test]
    public async Task A_session_held_back_by_a_lost_visibility_write_is_reported_as_failed() {
        // The preflight drops it before the upload, so it is in none of the run's own three counts —
        // and re-running retries exactly it. Left out, the total would silently understate.
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(
                new(Moved(loaded: 2), 3))).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That((totals!.Value.Imported, totals.Value.Failed)).IsEqualTo((2, 3));
    }

    [Test]
    public async Task A_pass_that_threw_reports_nothing_at_all() {
        // Its sessions are unaccounted, and the surviving pass's figures alone would state a clean
        // import over a run that lost one.
        var first = true;

        Task<ImportCommand.ImportRunOutcome?> Run(SetupImportLane.Pass pass) {
            if (first) {
                first = false;

                throw new InvalidOperationException("disk went away");
            }

            return Task.FromResult<ImportCommand.ImportRunOutcome?>(new(Moved(loaded: 5), 0));
        }

        var lane   = Lane(Run);
        var totals = await lane.ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(totals).IsNull();
        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_pass_that_reported_no_grid_reports_nothing_at_all() {
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(null)).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That(totals).IsNull();
    }

    [Test]
    public async Task A_run_that_measured_zero_reports_zero_rather_than_nothing() {
        // A pass that reached its grid and found nothing in scope is the "(0,0,0), no reason" row, not a
        // lost pass. Collapsing it into null would leave the screen unable to say the import finished.
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(
                new(Moved(), 0))).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That(totals).IsNotNull();
        await Assert.That((totals!.Value.Imported, totals.Value.Skipped, totals.Value.Failed))
                    .IsEqualTo((0, 0, 0));
    }

    [Test]
    public async Task A_clean_run_reports_totals_rather_than_null() {
        // The null case has to stay narrow: it is how the caller decides to send nothing, so a clean
        // run collapsing into it would leave the screen unable to say the import finished.
        var totals = await Lane(_ => Task.FromResult<ImportCommand.ImportRunOutcome?>(
                new(Moved(loaded: 1), 0))).ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15),
            CancellationToken.None);

        await Assert.That(totals).IsNotNull();
    }

    /// <summary>
    /// The lane imports through <see cref="ImportCommand"/>, which takes its base URL from the context
    /// it is handed — so the one this run resolved has to reach it. The leg's own context is resolved at
    /// process start, before setup has asked for a server, and on a first run it names none: `setup` is
    /// exempt from the entry gate that refuses an unconfigured command, so nothing downstream catches it.
    /// </summary>
    [Test]
    public async Task The_import_context_names_the_server_this_run_resolved() {
        var beforeSetup = new ProfileContext(new(null, "default", null, null), new ProfileConfig());

        var forImport = SetupCommand.ImportContext(beforeSetup, "https://tenant.kcap.ai");

        await Assert.That(forImport.Resolution.ServerUrl).IsEqualTo("https://tenant.kcap.ai");
    }

    /// <summary>Only the server moves: the profile the run is configuring still decides everything the
    /// import reads off it, visibility included.</summary>
    [Test]
    public async Task The_import_context_keeps_the_rest_of_the_resolution() {
        var profile     = new Profile { ServerUrl = "https://stale.kcap.ai", DefaultVisibility = "org_public" };
        var beforeSetup = new ProfileContext(new("https://stale.kcap.ai", "work", profile, null), new ProfileConfig());

        var forImport = SetupCommand.ImportContext(beforeSetup, "https://tenant.kcap.ai");

        await Assert.That(forImport.Resolution.ProfileName).IsEqualTo("work");
        await Assert.That(forImport.Resolution.Profile).IsSameReferenceAs(profile);
        await Assert.That(forImport.Snapshot).IsSameReferenceAs(beforeSetup.Snapshot);
    }
}
