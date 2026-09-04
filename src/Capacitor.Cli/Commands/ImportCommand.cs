using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Cursor;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

class ImportCommand(
        ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http) {
    /// <summary>
    /// Maximum parallel worker count for the Importing phase. Both the
    /// channel-based dispatcher in ImportChainsAsync and the TTY slot-row
    /// renderer in HandleImport size themselves to this value, so they
    /// MUST stay in lockstep.
    /// </summary>
    const int ImportWorkerCount = 4;

    internal readonly struct ImportDisplay {
        public bool Tty { get; init; }

        /// <summary>
        /// Suppresses every progress line. Set for <c>--discover --json</c>, whose whole stdout has to
        /// parse — one "Found 24 sessions." ahead of the payload and it does not.
        /// </summary>
        public bool Quiet { get; init; }

        /// <summary>This run is a step inside setup, which has already drawn the rule its sections sit
        /// under — so each one is subordinate to that rule rather than a sibling of it. A full-width rule
        /// per section is right only where the run is itself the command.</summary>
        public bool Nested { get; init; }

        /// <summary>
        /// Where a line of this run's own output starts. <b>Column 0 belongs to headings</b>, and only a
        /// heading that is a rule sits there — everything a section contains is indented under it.
        ///
        /// <para>Renderables need it as a <see cref="Padder"/> rather than as text: a grid or a table
        /// ignores leading spaces and would otherwise draw against the margin while the prose beside it
        /// is indented.</para>
        /// </summary>
        int Detail => Nested ? 4 : 2;

        /// <summary>The same step as <see cref="Detail"/>, for the callers that prefix text rather than
        /// pad a renderable.</summary>
        public string Indented => new(' ', Detail);

        public void Line(string plain, string? markup = null) {
            if (Quiet) return;

            if (Tty) AnsiConsole.MarkupLine(Indented + (markup ?? Markup.Escape(plain)));
            else Console.WriteLine(Indented + plain);
        }

        /// <summary>
        /// Every section label, so a nested run cannot end up subordinating its phases and then closing
        /// with a full-width rule of its own.
        ///
        /// <para>Plain text keeps a blank line above the label. A <see cref="Rule"/> carries its own
        /// break and weight distinguishes a nested label, but neither is available with output
        /// redirected, where the blank line is the only thing separating a section from the last one's
        /// rows.</para>
        /// </summary>
        void Heading(string title, string colour) {
            if (Tty) {
                if (Nested) AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(title)}[/]");
                else AnsiConsole.Write(new Rule($"[{colour}]{Markup.Escape(title)}[/]").LeftJustified());
            } else {
                Console.WriteLine();
                Console.WriteLine(Nested ? $"  -- {title} --" : $"== {title} ==");
            }
        }

        public void BeginPhase(string title) {
            if (Quiet) return;

            Heading(title, "yellow");
        }

        public void WritePlanGrid(
                ClassificationCounts                               c,
                IReadOnlyDictionary<string, ClassificationCounts>? bySource = null
            ) {
            if (Tty) {
                if (bySource is { Count: > 1 }) {
                    Heading("By source", "yellow");

                    var sub = new Grid()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn();

                    sub.AddRow(
                        "[dim]Source[/]",
                        "[dim]New[/]",
                        "[dim]Resumable[/]",
                        "[dim]Already loaded[/]",
                        "[dim]Too short[/]",
                        "[dim]Excluded[/]",
                        "[dim]Probe errors[/]"
                    );

                    foreach (var kv in bySource.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                        var sc = kv.Value;

                        sub.AddRow(
                            $"[bold]{Markup.Escape(kv.Key)}[/]",
                            sc.New.ToString(),
                            sc.Partial.ToString(),
                            sc.AlreadyLoaded.ToString(),
                            sc.TooShort.ToString(),
                            sc.Excluded.ToString(),
                            sc.ProbeError > 0 ? $"[red]{sc.ProbeError}[/]" : "0"
                        );
                    }

                    AnsiConsole.Write(new Padder(sub).Padding(Detail, 0, 0, 0));
                }

                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]New[/]", c.New.ToString());
                grid.AddRow("[bold]Resumable[/]", c.Partial.ToString());
                grid.AddRow("[bold]Already loaded[/]", c.AlreadyLoaded.ToString());
                grid.AddRow("[bold]Too short[/]", c.TooShort.ToString());
                grid.AddRow("[bold]Excluded[/]", c.Excluded.ToString());
                if (c.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{c.ProbeError}[/]");
                AnsiConsole.Write(new Padder(grid).Padding(Detail, 0, 0, 0));
            } else {
                if (bySource is { Count: > 1 }) {
                    Heading("By source", "yellow");

                    foreach (var kv in bySource.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                        var sc = kv.Value;
                        Console.WriteLine($"{Indented}[{kv.Key}]");
                        Console.WriteLine($"{Indented}  New               {sc.New}");
                        Console.WriteLine($"{Indented}  Resumable         {sc.Partial}");
                        Console.WriteLine($"{Indented}  Already loaded    {sc.AlreadyLoaded}");
                        Console.WriteLine($"{Indented}  Too short         {sc.TooShort}");
                        Console.WriteLine($"{Indented}  Excluded          {sc.Excluded}");
                        if (sc.ProbeError > 0) Console.WriteLine($"{Indented}  Probe errors      {sc.ProbeError}");
                    }

                    Console.WriteLine();
                }

                Console.WriteLine($"{Indented}New               {c.New}");
                Console.WriteLine($"{Indented}Resumable         {c.Partial}");
                Console.WriteLine($"{Indented}Already loaded    {c.AlreadyLoaded}");
                Console.WriteLine($"{Indented}Too short         {c.TooShort}");
                Console.WriteLine($"{Indented}Excluded          {c.Excluded}");
                if (c.ProbeError > 0) Console.WriteLine($"{Indented}Probe errors      {c.ProbeError}");
            }
        }

        public void WriteDoneGrid(
                FinalCounts                               f,
                IReadOnlyDictionary<string, FinalCounts>? bySource = null
            ) {
            // Says nothing about the skipped: too-short and excluded were never sent, so "everything else
            // is in" would claim they landed. Re-running is safe — classification asks the server first.
            var failureNote = $"{f.Failed} didn't land. Re-run to retry them — anything already imported isn't sent again.";

            if (Tty) {
                Heading("Done", "green");

                // Three buckets, not one: import knows the difference and a single number would hide it.
                AnsiConsole.MarkupLine(
                    Indented
                  + $"[green]{f.Imported}[/] imported · {f.Skipped} skipped · "
                  + (f.Failed > 0 ? $"[red]{f.Failed}[/] failed" : "0 failed"));

                if (bySource is { Count: > 1 }) {
                    Heading("By source", "green");

                    var sub = new Grid()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn()
                        .AddColumn();

                    sub.AddRow(
                        "[dim]Source[/]",
                        "[dim]Loaded[/]",
                        "[dim]Resumed[/]",
                        "[dim]Already loaded[/]",
                        "[dim]Too short[/]",
                        "[dim]Excluded[/]",
                        "[dim]Probe errors[/]",
                        "[dim]Errored[/]"
                    );

                    foreach (var kv in bySource.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                        var sf = kv.Value;

                        sub.AddRow(
                            $"[bold]{Markup.Escape(kv.Key)}[/]",
                            sf.Loaded.ToString(),
                            sf.Resumed.ToString(),
                            sf.AlreadyLoaded.ToString(),
                            sf.TooShort.ToString(),
                            sf.Excluded.ToString(),
                            sf.ProbeError > 0 ? $"[red]{sf.ProbeError}[/]" : "0",
                            sf.Errored    > 0 ? $"[red]{sf.Errored}[/]" : "0"
                        );
                    }

                    AnsiConsole.Write(new Padder(sub).Padding(Detail, 0, 0, 0));
                }

                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("[bold]Loaded[/]", f.Loaded.ToString());
                grid.AddRow("[bold]Resumed[/]", f.Resumed.ToString());
                grid.AddRow("[bold]Already loaded[/]", f.AlreadyLoaded.ToString());
                if (f.TooShort   > 0) grid.AddRow("[bold]Too short[/]", f.TooShort.ToString());
                if (f.Excluded   > 0) grid.AddRow("[bold]Excluded[/]", f.Excluded.ToString());
                if (f.ProbeError > 0) grid.AddRow("[bold]Probe errors[/]", $"[red]{f.ProbeError}[/]");
                if (f.Errored    > 0) grid.AddRow("[bold]Errored[/]", $"[red]{f.Errored}[/]");

                if (f.RanBackground) {
                    // Gated on the count, not on RanBackground: with --skip-title --generate-summaries
                    // there is background work but no titling, and a row of zeroes reads as titling that
                    // found nothing rather than titling that never ran.
                    if (f.RequestedTitles)
                        grid.AddRow("[bold]Titles[/]", $"{f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        grid.AddRow("[bold]Summaries[/]", $"{f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }

                AnsiConsole.Write(new Padder(grid).Padding(Detail, 0, 0, 0));

                if (f.Failed > 0) AnsiConsole.MarkupLine($"{Indented}[dim]{failureNote}[/]");
            } else {
                Heading("Done", "green");
                Console.WriteLine($"{Indented}{f.Imported} imported · {f.Skipped} skipped · {f.Failed} failed");

                if (bySource is { Count: > 1 }) {
                    Heading("By source", "green");

                    foreach (var kv in bySource.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                        var sf = kv.Value;
                        Console.WriteLine($"{Indented}[{kv.Key}]");
                        Console.WriteLine($"{Indented}  Loaded              {sf.Loaded}");
                        Console.WriteLine($"{Indented}  Resumed             {sf.Resumed}");
                        Console.WriteLine($"{Indented}  Already loaded      {sf.AlreadyLoaded}");
                        if (sf.TooShort   > 0) Console.WriteLine($"{Indented}  Too short           {sf.TooShort}");
                        if (sf.Excluded   > 0) Console.WriteLine($"{Indented}  Excluded            {sf.Excluded}");
                        if (sf.ProbeError > 0) Console.WriteLine($"{Indented}  Probe errors        {sf.ProbeError}");
                        if (sf.Errored    > 0) Console.WriteLine($"{Indented}  Errored             {sf.Errored}");
                    }

                    Console.WriteLine();
                }

                Console.WriteLine($"{Indented}Loaded              {f.Loaded}");
                Console.WriteLine($"{Indented}Resumed             {f.Resumed}");
                Console.WriteLine($"{Indented}Already loaded      {f.AlreadyLoaded}");
                if (f.TooShort   > 0) Console.WriteLine($"{Indented}Too short           {f.TooShort}");
                if (f.Excluded   > 0) Console.WriteLine($"{Indented}Excluded            {f.Excluded}");
                if (f.ProbeError > 0) Console.WriteLine($"{Indented}Probe errors        {f.ProbeError}");
                if (f.Errored    > 0) Console.WriteLine($"{Indented}Errored             {f.Errored}");

                if (f.RanBackground) {
                    if (f.RequestedTitles)
                        Console.WriteLine($"{Indented}Titles              {f.TitlesGenerated} generated, {f.TitlesSkipped} skipped, {f.TitlesFailed} failed");

                    if (f.RequestedSummaries)
                        Console.WriteLine($"{Indented}Summaries           {f.SummariesGenerated} generated, {f.SummariesFailed} failed");
                }

                if (f.Failed > 0) Console.WriteLine($"{Indented}{failureNote}");
            }
        }

        public static ImportDisplay Create(bool quiet = false, bool nested = false) =>
            new() { Tty = !quiet && !Console.IsOutputRedirected, Quiet = quiet, Nested = nested };
    }

    internal enum ClassificationStatus {
        /// <summary>Session does not exist on the server — needs a full import.</summary>
        New,

        /// <summary>Session exists on the server with a partial line count — resume from ResumeFromLine.</summary>
        Partial,

        /// <summary>Session is fully loaded on the server — no work to do.</summary>
        AlreadyLoaded,

        /// <summary>Transcript line count is below the minLines threshold.</summary>
        TooShort,

        /// <summary>Session's repository is in the user's excluded list and the user declined to include it.</summary>
        Excluded,

        /// <summary>The last-line probe failed (HTTP error, network error).</summary>
        ProbeError,

        /// <summary>Capacitor-spawned sub-session (title generation, what's-done summary) — never imported.</summary>
        InternalSubSession,
    }

    internal sealed record SessionClassification {
        public required string               SessionId  { get; init; }
        public required string               FilePath   { get; init; }
        public required string               EncodedCwd { get; init; }
        public required SessionMetadata      Meta       { get; init; }
        public required ClassificationStatus Status     { get; init; }

        /// <summary>Picks the matching metadata extractor, title extractor, session-start hook
        /// shape, and TranscriptBatch.Vendor tag.</summary>
        public HarnessId Vendor { get; init; } = HarnessId.Claude;

        /// <summary>Only populated when Status == Partial.</summary>
        public int ResumeFromLine { get; init; }

        /// <summary>Only populated when Status == ProbeError. Short human-readable reason.</summary>
        public string? ProbeErrorReason { get; init; }

        /// <summary>Populated when the session is a continuation in the continuation map.</summary>
        public string? PreviousSessionId { get; init; }

        /// <summary>
        /// Populated when Status == Excluded OR when the session would otherwise be New/Partial
        /// but its cwd maps to an excluded repo and the user has not yet been consulted.
        /// Format: "{Owner}/{RepoName}".
        /// </summary>
        public string? ExcludedRepoKey { get; init; }

        /// <summary>
        /// Populated when the session's cwd lies within a path configured via
        /// <c>kcap ignore</c>. Holds the matching excluded-path entry
        /// (normalized) so prompts can group sessions by the entry that excluded
        /// them.
        /// </summary>
        public string? ExcludedPathKey { get; init; }

        /// <summary>Total transcript line count (cached so we don't re-read the file downstream).</summary>
        public int TotalLines { get; init; }

        /// <summary>
        /// Source-specific opaque metadata attached during DiscoverAsync.
        /// Claude/Codex sources don't need this (their fields live in FilePath/EncodedCwd).
        /// Cursor uses it to carry ComposerId, WorkspacePath, GlobalDbPath, CliOwner, CliRepo.
        /// The orchestrator does not inspect this dictionary; only the originating
        /// IImportSource reads it back in ImportSessionAsync.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? SourceMeta { get; init; }
    }

    /// <summary>
    /// Reconciles Cursor subagent correlation against this run's actual routed plan, which may be
    /// narrower than the same-workspace scan <see cref="CursorImportSource.ClassifyAsync"/> used
    /// to discover links. An orphaned child — correlated to a parent that isn't itself in
    /// <paramref name="routed"/> — has its child flags cleared so it imports standalone instead
    /// of being silently skipped by <see cref="CursorImportSource.ImportSessionAsync"/>'s
    /// nested-child skip (the server-side adoption sweep attaches it to its real parent later).
    /// Conversely, a routed parent's <c>SubagentChildren</c> is pruned to children that are
    /// actually in <paramref name="routed"/> — the wider discovery scan may only be used to
    /// DISCOVER links, never to WIDEN the import plan with a session the caller filtered out.
    /// Children whose parent IS in <paramref name="routed"/> are left untouched.
    /// </summary>
    internal static List<SessionClassification> ReconcileOrphanedCursorSubagentChildren(
        List<SessionClassification> routed
    ) {
        var routedIds = routed.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);

        return [
            .. routed.Select(c => {
                if (c.SourceMeta is not { } meta) return c;

                var reconciled = meta;
                var changed    = false;

                // Child side: an orphan whose parent isn't part of this run's routed plan must
                // import standalone rather than being silently skipped by ImportSessionAsync.
                if (meta.TryGetValue("IsSubagentChild", out var isChildObj) && isChildObj is true) {
                    var parentId = meta.TryGetValue("ParentSessionId", out var parentIdObj) ? parentIdObj as string : null;

                    // Parent is (or will be) imported in this same run — leave the nested-child
                    // flags in place so CursorImportSource keeps skipping this child's own routed
                    // import in favor of the parent importing it inline.
                    if (parentId is null || !routedIds.Contains(parentId)) {
                        var d = new Dictionary<string, object?>(reconciled);
                        d.Remove("IsSubagentChild");
                        d.Remove("ParentSessionId");
                        reconciled = d;
                        changed    = true;
                    }
                }

                // Parent side: prune SubagentChildren down to children that are actually part of
                // this run's routed plan. A child excluded from `routed` must never be attached
                // and imported here.
                if (reconciled.TryGetValue("SubagentChildren", out var kidsObj)
                 && kidsObj is List<CursorImportSource.CursorSubagentChild> kids) {
                    var keptKids = kids.Where(k => routedIds.Contains(k.SessionId)).ToList();

                    if (keptKids.Count != kids.Count) {
                        var d = new Dictionary<string, object?>(reconciled);
                        if (keptKids.Count > 0) d["SubagentChildren"] = keptKids;
                        else                     d.Remove("SubagentChildren");
                        reconciled = d;
                        changed    = true;
                    }
                }

                return changed ? c with { SourceMeta = reconciled } : c;
            })
        ];
    }

    /// <summary>
    /// A routed-phase
    /// <c>AlreadyLoaded</c> classification (Cursor) is included in `routed` solely so its
    /// <c>ImportSessionAsync</c> call can re-assert lifecycle hooks and backfill the
    /// <c>repository</c> node on an already-fully-ingested session (the C2 suppressed-repo-import
    /// contract), or — for a correlated nested child — because its parent needs it present in
    /// `routed` to import it inline; neither case is "new work" on its own. Because nothing past
    /// the parent's own watermark exists, its own call still returns <see cref="ImportOutcome.Loaded"/>
    /// or <see cref="ImportOutcome.Resumed"/> (no line 0 to distinguish "nothing sent" from "brand
    /// new"), and a nested child's own call always short-circuits to <see cref="ImportOutcome.Skipped"/>
    /// (its content is sent inline by the parent, not by its own call). Without this check either
    /// success would double-count on top of the classify-time <c>AlreadyLoaded</c> bucket the same
    /// session already contributes to (as both Loaded/Already-loaded, or as both Excluded/Already-
    /// loaded), and a parent replay would wrongly join <c>importedSessionIds</c> so a later
    /// <c>--private</c> pass re-privates a session this run never actually (re)imported.
    ///
    /// <para>
    /// Round-2 finding 1: an <c>AlreadyLoaded</c> parent can still ship brand-new content by
    /// attaching a previously-unloaded nested child — real work that must NOT be suppressed just
    /// because the parent's OWN outcome looks like a no-op replay. <c>sentChildContent</c> is the
    /// out-of-band signal (see <see cref="ImportSessionResult"/>) that lets this distinguish the
    /// two: only a replay that sent no child content either is truly lifecycle-only.
    /// </para>
    ///
    /// <para>
    /// This gate is vendor-NEUTRAL: <c>sentChildContent</c> carries a real signal for Cursor,
    /// Antigravity, and Gemini alike (Copilot/Kiro/Pi/OpenCode permanently default it
    /// <c>false</c>, which is already correct for their no-op replay shape).
    /// </para>
    ///
    /// <para>
    /// This gate alone only decides whether a replay is suppressed — it does NOT decide which
    /// bucket a non-suppressed call lands in for counting. See
    /// <see cref="IsSkippedChildContentOverride"/> and <see cref="ResolveRoutedOutcomeForCounting"/>
    /// for the central aggregation boundary that fixes the case this gate can't: a <c>Skipped</c>
    /// <c>AlreadyLoaded</c> call (e.g. Antigravity's) that attached genuinely-new nested-child
    /// content must count as Loaded, even though its own raw outcome is <c>Skipped</c>.
    /// </para>
    /// </summary>
    internal static bool IsLifecycleOnlyRoutedReplay(
            ClassificationStatus  status,
            ImportOutcome         outcome,
            bool                  sentChildContent
        ) =>
        status == ClassificationStatus.AlreadyLoaded
     && outcome is ImportOutcome.Loaded or ImportOutcome.Resumed or ImportOutcome.Skipped
     && !sentChildContent;

    /// <summary>
    /// True iff a <c>Skipped</c> <c>AlreadyLoaded</c> call attached genuinely-new nested-child
    /// content — the only raw outcome whose Done-grid bucket needs
    /// correcting. <c>Loaded</c>/<c>Resumed</c> already land correctly via the ordinary outcome
    /// path and are deliberately left untouched (PRESERVED, not reclassified) — this predicate
    /// does not engage for them. <c>Failed</c> is excluded by the same exact-match: a failed
    /// lifecycle POST must always surface as Errored, never get reclassified as Loaded just
    /// because content happened to post first.
    /// </summary>
    internal static bool IsSkippedChildContentOverride(
            ClassificationStatus  status,
            ImportOutcome         outcome,
            bool                  sentChildContent
        ) =>
        status == ClassificationStatus.AlreadyLoaded
     && outcome == ImportOutcome.Skipped
     && sentChildContent;

    /// <summary>
    /// The single aggregation boundary — what outcome should this routed call count as for
    /// the Done-grid's counters, per-vendor tracker, and printed line (
    /// <c>null</c> = suppressed, don't count at all). This is a COUNTING-ONLY boundary: it
    /// deliberately does NOT drive <c>importedSessionIds</c> membership, which stays governed by
    /// the pre-existing suppression check and outcome switch — see the call sites below for the
    /// exact membership condition. <c>--private</c> no longer rides on that membership alone
    /// either: sources that can attach child content on a replay are captured outcome-independently
    /// into <c>privateScopeSessionIds</c>, precisely because this resolver's inputs can't be
    /// trusted to reveal an attach (a hardcoded Skipped, or a Failed lifecycle POST after the
    /// content already persisted).
    /// <see cref="IsLifecycleOnlyRoutedReplay"/> requires <c>!sentChildContent</c> and
    /// <see cref="IsSkippedChildContentOverride"/> requires <c>sentChildContent</c>, so the two
    /// are mutually exclusive by construction — there's no ordering ambiguity between them.
    /// </summary>
    internal static ImportOutcome? ResolveRoutedOutcomeForCounting(
            ClassificationStatus  status,
            ImportOutcome         outcome,
            bool                  sentChildContent
        ) {
        if (IsLifecycleOnlyRoutedReplay(status, outcome, sentChildContent))   return null;
        if (IsSkippedChildContentOverride(status, outcome, sentChildContent)) return ImportOutcome.Loaded;
        return outcome;
    }

    internal sealed record ClassificationCounts(
            int New,
            int Partial,
            int AlreadyLoaded,
            int TooShort,
            int Excluded,
            int ProbeError
        );

    /// <summary>
    /// Aggregate a per-source slice of classifications into a Plan-grid row.
    /// </summary>
    static ClassificationCounts ComputeCounts(IReadOnlyList<SessionClassification> classifications) =>
        new(
            New: classifications.Count(c => c.Status           == ClassificationStatus.New),
            Partial: classifications.Count(c => c.Status       == ClassificationStatus.Partial),
            AlreadyLoaded: classifications.Count(c => c.Status == ClassificationStatus.AlreadyLoaded),
            TooShort: classifications.Count(c => c.Status      == ClassificationStatus.TooShort),
            Excluded: classifications.Count(c => c.Status      == ClassificationStatus.Excluded),
            ProbeError: classifications.Count(c => c.Status    == ClassificationStatus.ProbeError)
        );

    /// <summary>
    /// True when the import scheduled background title/summary work the Done phase must await
    /// (and whose results the Titles/Summaries rows report). A named seam so the await-guard
    /// decision is unit-testable and can't silently re-invert.
    /// </summary>
    internal static bool HadBackgroundWork(ConcurrentBag<Task> backgroundTasks) => !backgroundTasks.IsEmpty;

    /// <summary>
    /// Compute the per-source Done-grid row for a single vendor.
    /// </summary>
    /// <param name="classifications">All classifications for this vendor (already filtered).</param>
    /// <param name="imported">Count of sessions in <paramref name="classifications"/> whose SessionId appears in importedSessionIds (i.e. chain-phase imports).</param>
    /// <param name="routedOutcomes">Per-vendor routed-phase outcomes, or null if this vendor ran through the chain phase only.</param>
    /// <remarks>
    /// Routed-phase vendors (Cursor today) get exact attribution: <c>routedOutcomes.Skipped</c>
    /// folds into Excluded (server said "already current"), <c>routedOutcomes.Failed</c> into Errored.
    /// Chain-phase vendors (Claude/Codex) still use the best-effort approximation:
    /// imported sessions → Loaded; (New+Partial)−imported → Errored — because the chain worker
    /// doesn't record per-session outcome by vendor yet.
    /// </remarks>
    internal static FinalCounts ComputePerSourceFinalCounts(
            IReadOnlyList<SessionClassification>   classifications,
            int                                    imported,
            (int Loaded, int Skipped, int Failed)? routedOutcomes
        ) {
        var counts = ComputeCounts(classifications);

        if (routedOutcomes is { } r) {
            // Routed-phase vendor: take exact counts from the per-vendor tracker.
            return new FinalCounts(
                Loaded: r.Loaded,
                Resumed: 0,
                AlreadyLoaded: counts.AlreadyLoaded,
                TooShort: counts.TooShort,
                Excluded: counts.Excluded + r.Skipped,
                ProbeError: counts.ProbeError,
                Errored: r.Failed,
                TitlesGenerated: 0,
                TitlesSkipped: 0,
                TitlesFailed: 0,
                SummariesGenerated: 0,
                SummariesFailed: 0,
                RanBackground: false,
                RequestedSummaries: false
            );
        }

        // Chain-phase vendor: best-effort approximation because the chain worker
        // doesn't record per-session outcome by vendor yet.
        return new FinalCounts(
            Loaded: imported,
            Resumed: 0,
            AlreadyLoaded: counts.AlreadyLoaded,
            TooShort: counts.TooShort,
            Excluded: counts.Excluded,
            ProbeError: counts.ProbeError,
            Errored: classifications.Count(c => c.Status is ClassificationStatus.New or ClassificationStatus.Partial) - imported,
            TitlesGenerated: 0,
            TitlesSkipped: 0,
            TitlesFailed: 0,
            SummariesGenerated: 0,
            SummariesFailed: 0,
            RanBackground: false,
            RequestedSummaries: false
        );
    }

    internal sealed record FinalCounts(
            int  Loaded,
            int  Resumed,
            int  AlreadyLoaded,
            int  TooShort,
            int  Excluded,
            int  ProbeError,
            int  Errored,
            int  TitlesGenerated,
            int  TitlesSkipped,
            int  TitlesFailed,
            int  SummariesGenerated,
            int  SummariesFailed,
            bool RanBackground,
            bool RequestedSummaries,
            bool RequestedTitles = false
        ) {
        /// <summary>Reached the server this run. A resume is an import that finished, not a third thing.</summary>
        internal int Imported => Loaded + Resumed;

        /// <summary>Deliberately not sent — already there, too short, or an excluded repo. Not failures.</summary>
        internal int Skipped => AlreadyLoaded + TooShort + Excluded;

        /// <summary>Should have landed and did not. Re-running retries exactly these.</summary>
        internal int Failed => ProbeError + Errored;
    }

    /// <summary>
    /// How a run ended, for a caller that has to say so.
    /// </summary>
    /// <remarks>
    /// <b>The exit code cannot answer this.</b> <c>HandleImport</c> returns 0 for a run whose sessions
    /// failed — import is best-effort and the Done grid is where that is reported — so a caller
    /// deriving success from the exit code calls a partial or total failure a success.
    /// </remarks>
    /// <summary>Reports a run that deliberately moved nothing: it reached a decision, and the decision
    /// was that there was nothing to do.</summary>
    static void ReportNothing(Action<ImportRunOutcome>? onFinished) =>
        onFinished?.Invoke(new ImportRunOutcome(
            new FinalCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false,
                            RequestedSummaries: false),
            VisibilityFailures: 0));

    internal sealed record ImportRunOutcome(FinalCounts Counts, int VisibilityFailures) {
        /// <summary>Anything the user asked for that did not happen.</summary>
        internal bool AnythingFailed => Counts.Failed > 0 || VisibilityFailures > 0;
    }

    /// <summary>
    /// What a discovery run produced.
    /// </summary>
    /// <param name="ScannedVendors">The sources actually walked — what was asked for, intersected with
    /// what this machine has. A caller reporting figures upstream needs this rather than its own
    /// request, or it claims to have looked somewhere it did not.</param>
    internal sealed record ImportDiscoveryResult(
        ImportDiscoverySummary   Summary,
        IReadOnlyList<HarnessId> ScannedVendors);

    /// <summary>
    /// Every "found nothing" exit still reports. Zero sessions is an answer; no output at all is
    /// indistinguishable from the process having died.
    /// </summary>
    static int WriteEmptyDiscoveryReport(
            Action<ImportDiscoveryResult> sink, IReadOnlyList<HarnessId> scanned, DateTimeOffset? windowsAsOf) {
        sink(new ImportDiscoveryResult(
            ImportDiscoverySummary.Build(
                [], new Dictionary<string, (string, string)?>(), DiscoveryWindows(windowsAsOf)),
            scanned));

        return 0;
    }

    /// <summary>In registry order, so two runs of an unchanged machine report the same list.</summary>
    static IReadOnlyList<HarnessId> ScannedVendors(IReadOnlyList<IImportSource> sources) {
        var walked = sources.Select(s => s.Vendor).ToHashSet();

        return [.. HarnessRegistry.Identities.Select(h => h.Id).Where(walked.Contains)];
    }

    static void WriteDiscoveryReport(ImportDiscoverySummary summary, bool asJson) =>
        Console.WriteLine(asJson
            ? ImportDiscoveryRender.ToJson(summary)
            : ImportDiscoveryRender.ToText(summary));

    /// <summary>
    /// The <c>--since</c> windows discovery reports totals for, newest first, ending in "everything".
    /// The same keys the first-run flow reports under, so the picker there and this command's own
    /// output cannot offer different windows.
    /// </summary>
    internal static IReadOnlyList<ImportDiscoveryWindow> DiscoveryWindows(DateTimeOffset? now = null) {
        var today = DateOnly.FromDateTime((now ?? DateTimeOffset.UtcNow).UtcDateTime);

        return [.. FirstRunImportWindows.All.Select(
            key => new ImportDiscoveryWindow(key, FirstRunImportWindows.Since(key, today)))];
    }

    /// <summary>
    /// Builds the Spectre markup for the "✗ Skipping {sid} [reason]" line.
    /// The outer literal brackets around <paramref name="reason"/> MUST be
    /// doubled — Spectre treats a bare <c>[word ...]</c> as a markup tag and
    /// throws InvalidOperationException ("Could not find color or style ...")
    /// when the first word isn't a known color/style. <see cref="Markup.Escape"/>
    /// only escapes characters inside the substring, not the literal brackets
    /// we wrap around it.
    /// </summary>
    internal static string FormatSkippedReasonMarkup(string sessionId, string reason) =>
        $"[red]✗[/] Skipping [cyan]{Markup.Escape(sessionId)}[/] [[{Markup.Escape(reason)}]]";

    /// <summary>
    /// Builds the Spectre markup for the "✓ Loading {sid}... N lines [verb]"
    /// line. See <see cref="FormatSkippedReasonMarkup"/> for why the outer
    /// brackets around <paramref name="verb"/> are doubled.
    /// </summary>
    internal static string FormatLoadedSummaryMarkup(string sessionId, int lines, string verb) =>
        $"[green]✓[/] Loading [cyan]{Markup.Escape(sessionId)}[/]... {lines} lines [[{Markup.Escape(verb)}]]";

    public async Task<int> HandleImport(
            string?                       filterCwd,
            string?                       filterSession           = null,
            int                           minLines                = 15,
            bool                          generateSummaries       = false,
            IReadOnlyList<IImportSource>? sources                 = null,
            bool                          explicitVendorSelection = false,
            DateOnly?                     since                   = null,
            ImportScope?                  scope                   = null,
            bool                          skipConfirmation        = false,
            bool                          forcePrivate            = false,
            (string Owner, string Name)?  currentRepo             = null,
            bool                          needOrgPick             = false,
            string?                       storedOrg               = null,
            bool                          autoSkipExclusions      = false,
            string?                       defaultVisibility       = null,
            bool                          reimport                = false,
            bool                          skipTitle               = false,
            bool                          shareWithOrg            = false,
            bool                          discoverOnly            = false,
            bool                          discoverJson            = false,
            DateTimeOffset?               windowsAsOf             = null,
            Action<ImportRunOutcome>?     onFinished              = null,
            Action<ImportDiscoveryResult>? onDiscovered           = null,
            bool                          nested                  = false
        ) {
        // A caller that wants the figures rather than the rendering says so by handing one over; the
        // console is the default consumer, not the only one.
        var discoverySink = onDiscovered ?? (result => WriteDiscoveryReport(result.Summary, discoverJson));
        // Discovery never calls the server and is reached with none configured.
        var baseUrl = profiles.Resolution.ServerUrl ?? "";

        // Discovery is a local scan and never touches this client, so building the authenticated one
        // would probe the server and warn an unauthenticated user about a command that needs neither.
        using var httpClient = discoverOnly
            ? http.Anonymous()
            : await http.ForCommandAsync();
        var       display    = ImportDisplay.Create(quiet: discoverJson, nested: nested);

        // --- Sources ---
        // Back-compat: a null caller (legacy or test) means "Claude only". Once
        // Program.cs migrates in E3, every production caller passes sources
        // explicitly.
        sources ??= [new ClaudeImportSource(config, ClaudeHarness.FromEnvironment(home).Paths.Projects)];

        // --- No-source exit policy ---
        var available = sources.Where(s => s.IsAvailable).ToList();
        var missing   = sources.Where(s => !s.IsAvailable).Select(s => s.Vendor).ToList();

        if (available.Count == 0) {
            if (explicitVendorSelection) {
                var flagList = string.Join(", ", missing.Select(v => v.Flag));

                await Console.Error.WriteLineAsync(
                    $"{flagList} specified but no matching installation detected on this machine."
                );

                return 1;
            }

            if (discoverOnly) return WriteEmptyDiscoveryReport(discoverySink, [], windowsAsOf);

            display.Line("No coding-agent sessions found. Install Claude, Codex, or Cursor and try again.");

            // Zero is an answer, and a caller that hears nothing cannot tell it from a run that died
            // before it got here — the same rule the discovery report already follows.
            ReportNothing(onFinished);

            return 0;
        }

        if (explicitVendorSelection) {
            foreach (var v in missing) {
                await Console.Error.WriteLineAsync($"Skipping {v.VendorId} (not detected on this machine).");
            }
        }

        sources = available;

        // --- Discovery (parallel fan-out) ---
        display.BeginPhase("Discovering");

        var filters = new DiscoveryFilters(
            FilterCwd: filterCwd,
            FilterSession: filterSession,
            // A report of what each window WOULD select cannot start from a set one window already
            // pruned: 8 of the 9 sources filter on --since during discovery, which would collapse
            // every row to the same total and make the "everything" row untrue.
            Since: discoverOnly ? null : since,
            MinLines: minLines
        );

        var discoveriesPerSource = await Task.WhenAll(
            sources.Select(s => s.DiscoverAsync(filters, CancellationToken.None))
        );

        for (var i = 0; i < sources.Count; i++) {
            var count = discoveriesPerSource[i].Count;
            display.Line($"Found {count} {sources[i].Vendor.VendorId} session{(count == 1 ? "" : "s")}.");
        }

        var totalDiscovered = discoveriesPerSource.Sum(d => d.Count);

        if (totalDiscovered == 0) {
            // When --session was given but nothing matched, surface the legacy
            // not-found error instead of the friendly "No transcript files" exit.
            // Keep the message aligned with the dead branch lower in this method
            // (cleanup follow-up) so downstream tooling sees consistent output.
            if (filterSession is not null) {
                if (discoverOnly) return WriteEmptyDiscoveryReport(discoverySink, ScannedVendors(sources), windowsAsOf);

                await Console.Error.WriteLineAsync($"Session not found: {NormalizeGuid(filterSession)}");

                return 1;
            }

            if (discoverOnly) return WriteEmptyDiscoveryReport(discoverySink, ScannedVendors(sources), windowsAsOf);

            display.Line("No transcript files found.");

            // Zero is an answer, and a caller that hears nothing cannot tell it from a run that died
            // before it got here — the same rule the discovery report already follows.
            ReportNothing(onFinished);

            return 0;
        }

        // Build a vendor → source map for downstream lookups.
        var byVendor = sources.ToDictionary(s => s.Vendor);

        // --- Cwd resolution for scope filtering ---
        // For file-based sources (Claude/Codex) extract cwd from the transcript.
        // For Cursor the workspace folder is already populated in DiscoveredSession.Cwd.
        // ResolveTranscriptReposAsync still operates on file tuples so we adapt
        // per-source: file-based sources project their DiscoveredSessions into
        // (SessionId, FilePath, EncodedCwd) tuples; for Cursor we resolve cwd→repo
        // directly here.
        var allFileTuples = new List<(string SessionId, string FilePath, string EncodedCwd, HarnessId Vendor)>();
        var cursorCwds    = new Dictionary<string, string?>(StringComparer.Ordinal); // sessionId → workspace path

        for (var i = 0; i < sources.Count; i++) {
            var src = sources[i];

            foreach (var s in discoveriesPerSource[i]) {
                var fp = s.SourceMeta.TryGetValue("FilePath", out var f) ? f as string : null;
                var ec = s.SourceMeta.TryGetValue("EncodedCwd", out var e) ? e as string : null;

                if (fp is not null && ec is not null) {
                    allFileTuples.Add((s.SessionId, fp, ec, src.Vendor));
                } else {
                    // Cursor or other future non-file source.
                    cursorCwds[s.SessionId] = s.Cwd;
                }
            }
        }

        // Run file-based repo resolution. The helper takes a single codex bool;
        // since Claude and Codex differ only in cwd-extraction, run it per
        // vendor and merge. User-configured cwd remaps let historic transcripts
        // referencing since-renamed local repo paths still resolve to a real
        // git directory.
        var profileConfig      = profiles.Snapshot;
        var cwdRemap           = profileConfig.CwdRemap;
        var resolved           = new Dictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);
        var sessionCwds        = new Dictionary<string, string>(StringComparer.Ordinal);
        var worktreeAttributed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var vendor in new[] { HarnessId.Claude, HarnessId.Codex }) {
            var slice = allFileTuples
                .Where(t => t.Vendor == vendor)
                .Select(t => (t.SessionId, t.FilePath, t.EncodedCwd))
                .ToList();

            if (slice.Count == 0) continue;

            var partial                                  = await ResolveTranscriptReposAsync(slice, codex: vendor is HarnessId.Codex, display, cwdRemap, sessionCwds, worktreeAttributed);
            foreach (var kv in partial) resolved[kv.Key] = kv.Value;
        }

        // Resolve Cursor sessions in parallel by workspace path (dedup like
        // ResolveTranscriptReposAsync).
        if (cursorCwds.Count > 0) {
            // Resolve once per session so repo detection AND the missing-cwd
            // report below see the same path (user remap + worktree-strip
            // fallback for ephemeral worktree paths).
            var cursorRemapped = cursorCwds.ToDictionary(
                kv => kv.Key,
                kv => kv.Value is null ? null : ResolveCwd(kv.Value, cwdRemap, worktreeAttributed, kv.Key),
                StringComparer.Ordinal
            );

            foreach (var (sid, cwd) in cursorRemapped) {
                if (cwd is not null) sessionCwds[sid] = cwd;
            }

            var uniqueWorkspaces = cursorRemapped.Values
                .Where(c => c is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var repoByCwd = new ConcurrentDictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);

            if (uniqueWorkspaces.Count > 0) {
                var opts = new ParallelOptions { MaxDegreeOfParallelism = 8 };

                await Parallel.ForEachAsync(
                    uniqueWorkspaces,
                    opts,
                    async (cwd, _) => {
                        try {
                            // Import only needs owner/repo here — skip the PR/MR provider round-trip.
                            var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false);
                            repoByCwd[cwd] = repo is { Owner: { } o, RepoName: { } n } ? (o, n) : null;
                        } catch {
                            repoByCwd[cwd] = null;
                        }
                    }
                );
            }

            foreach (var (sid, cwd) in cursorRemapped) {
                resolved[sid] = cwd is not null && repoByCwd.TryGetValue(cwd, out var r) ? r : null;
            }
        }

        // --- Missing cwd report ---
        // Surface transcripts whose (possibly remapped) cwd no longer exists on
        // disk so the user understands why some sessions won't match an org or
        // repo scope, and can fix it by adding cwd_remap entries.
        ReportWorktreeAttributions(worktreeAttributed.Count, display);
        ReportMissingCwds(sessionCwds, cwdRemap, display, home);

        if (discoverOnly) {
            discoverySink(new ImportDiscoveryResult(
                ImportDiscoverySummary.Build(
                    // Each source dates its own sessions: the rule differs per vendor and only the
                    // source knows which one its --since applies.
                    sources.SelectMany((src, i) =>
                        discoveriesPerSource[i].Select(d => (d.SessionId, src.DiscoveryAge(d)))),
                    resolved,
                    DiscoveryWindows(windowsAsOf)),
                ScannedVendors(sources)));

            return 0;
        }

        // --- Scope picker ---
        var profile = profiles.Effective;

        if (scope is null) {
            var distinct = resolved.Values
                .Where(v => v is not null)
                .Select(v => v!.Value)
                .ToList();

            if (needOrgPick) {
                // Bare `--org` with no value/remembered org: pick the GitHub org to
                // scope on from the owners seen across discovered sessions.
                var owner = ImportScopePrompt.RunOrgPicker(distinct);
                scope = owner is null ? null : new ImportScope.Org(owner);
            } else {
                scope = ImportScopePrompt.RunPicker(currentRepo, distinct);
            }

            if (scope is null) {
                // The picker has already printed the specific reason (e.g. "No
                // git-remote owners detected" or "No repositories detected in
                // discovered sessions") via AnsiConsole. Don't tack on a
                // misleading "cancelled" message.
                return 1;
            }
        }

        // Remember an org chosen interactively or passed via `--org <owner>` so a
        // later bare `kcap import --org` reuses it without re-prompting.
        if (scope is ImportScope.Org chosenOrg &&
            !string.Equals(chosenOrg.OrgLogin, storedOrg, StringComparison.OrdinalIgnoreCase)) {
            await PersistImportOrgAsync(profiles.Name, chosenOrg.OrgLogin);
        }

        // --- Per-source scope filtering ---
        // ImportScopeFilter.Apply needs (SessionId, FilePath, EncodedCwd) tuples.
        // For file-based sources we already have those; for Cursor we project a
        // tuple with empty FilePath/EncodedCwd (the filter only consults the
        // injected resolver, which reads from `resolved`).
        var filteredPerSource = new List<DiscoveredSession>[sources.Count];

        for (var i = 0; i < sources.Count; i++) {
            var disc = discoveriesPerSource[i];

            var tuples = disc.Select(s => (s.SessionId, FilePath: "", EncodedCwd: "")).ToList();

            var keptIds = (await ImportScopeFilter.Apply(
                    tuples,
                    scope,
                    (t, _) => new ValueTask<(string?, string?)>(
                        resolved.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null)
                    )
                ))
                .Select(t => t.SessionId)
                .ToHashSet(StringComparer.Ordinal);

            filteredPerSource[i] = [.. disc.Where(s => keptIds.Contains(s.SessionId))];
        }

        var totalAfterScope = filteredPerSource.Sum(d => d.Count);

        // --- Confirmation ---
        var sampleRepos = new List<string>();

        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var disc in filteredPerSource) {
                foreach (var s in disc) {
                    if (!resolved.TryGetValue(s.SessionId, out var v) || v is not { } x) continue;

                    if (seen.Add($"{x.Owner}/{x.Name}")) sampleRepos.Add($"{x.Owner}/{x.Name}");
                }
            }
        }

        // A repo that is spelled plausibly but wrongly resolves fine and simply matches nothing, so the
        // run imports the others and says nothing. Naming the misses is the only way that reads as a
        // mistake rather than as a smaller history than expected.
        if (scope is ImportScope.Repo requested) {
            var matched = new HashSet<(string Owner, string Name)>(ImportScope.RepoComparer);

            foreach (var repo in sampleRepos) {
                var parts = repo.Split('/');
                if (parts.Length == 2) matched.Add((parts[0], parts[1]));
            }

            var missed = requested.Repos.Where(r => !matched.Contains(r)).ToList();

            if (missed.Count > 0) {
                display.Line(
                    $"No sessions found for {string.Join(", ", missed.Select(m => $"{m.Owner}/{m.Name}"))} — check the spelling, or `kcap remap` if the directory was renamed.");
            }
        }

        if (totalAfterScope == 0) {
            display.Line("No sessions match the selected scope.");

            // Zero is an answer, and a caller that hears nothing cannot tell it from a run that died
            // before it got here — the same rule the discovery report already follows.
            ReportNothing(onFinished);

            return 0;
        }

        var visibilityDesc = forcePrivate
            ? "private (--private)"
            : $"{profile?.DefaultVisibility ?? "org_public"} (from profile)";

        if (!ImportScopePrompt.PromptConfirm(
                scope,
                totalAfterScope,
                sampleRepos,
                visibilityDesc,
                skipConfirmation,
                display.Indented
            )) {
            await Console.Error.WriteLineAsync("Import cancelled.");

            return 0;
        }

        // --- Classification (parallel fan-out per source) ---
        var excludedRepos = profile?.ExcludedRepos;
        var excludedPaths = profile?.ExcludedPaths;

        var classifyCtx = new ClassifyContext(
            HttpClient: httpClient,
            BaseUrl: baseUrl,
            MinLines: minLines,
            ExcludedRepos: excludedRepos,
            ExcludedPaths: excludedPaths,
            Home: home,
            Reimport: reimport
        );

        IReadOnlyList<SessionClassification>[] classificationsPerSource;

        if (display.Tty) {
            classificationsPerSource = new IReadOnlyList<SessionClassification>[sources.Count];

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx => {
                        var bar = ctx.AddTask($"{display.Indented}[yellow]Probing[/]", maxValue: totalAfterScope);

                        var probeTasks = sources.Select(async (s, idx) => {
                                    var slice = filteredPerSource[idx];
                                    var res   = await s.ClassifyAsync(slice, classifyCtx, CancellationToken.None);
                                    // Per-source progress accounting: each ClassifyAsync produces
                                    // one classification per discovered session, so we tick the
                                    // bar by the slice size when the task completes. Per-probe
                                    // ticks would require threading a callback through the
                                    // IImportSource interface — we punt that to a follow-up.
                                    bar.Increment(slice.Count);

                                    return (idx, res);
                                }
                            )
                            .ToArray();

                        var all = await Task.WhenAll(probeTasks);

                        foreach (var (idx, res) in all) classificationsPerSource[idx] = res;
                    }
                );
        } else {
            display.Line($"Probing {totalAfterScope} sessions...");

            classificationsPerSource = await Task.WhenAll(
                sources.Select(async (s, idx) => await s.ClassifyAsync(filteredPerSource[idx], classifyCtx, CancellationToken.None))
            );
        }

        // --- --since filter (file-based sources only — Codex pruned at discovery, Cursor n/a) ---
        if (since is { } sinceCutoff) {
            var cutoff = sinceCutoff.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            for (var i = 0; i < sources.Count; i++) {
                // Skip Codex (already pruned in CodexPaths.Discover) and Cursor
                // (no FilePath to stat). Only Claude needs the post-classify mtime
                // fallback. Asked by identity, so the orchestrator stays agnostic of
                // source-implementation details.
                if (sources[i].Vendor is not HarnessId.Claude) continue;

                classificationsPerSource[i] = [
                    .. classificationsPerSource[i]
                        .Where(c => {
                                var ts = c.Meta.FirstTimestamp?.UtcDateTime;

                                if (ts is null) {
                                    try { ts = File.GetLastWriteTimeUtc(c.FilePath); } catch { return true; }
                                }

                                return ts >= cutoff;
                            }
                        )
                ];
            }
        }

        // Flatten classifications.
        var classifications = classificationsPerSource.SelectMany(c => c).ToList();

        // --- Resolve excluded-repo / excluded-path prompts (TTY only; non-TTY auto-skips) ---
        // Repo and path exclusions are independent gates: a session is included only when
        // EVERY applicable exclusion key has been opted-in. Without that, opting into a
        // repo would silently bypass a path the user had explicitly ignored.
        var excludedByRepo = classifications
            .Where(c => c.ExcludedRepoKey is not null)
            .GroupBy(c => c.ExcludedRepoKey!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var excludedByPath = classifications
            .Where(c => c.ExcludedPathKey is not null)
            .GroupBy(c => c.ExcludedPathKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (excludedByRepo.Count > 0 || excludedByPath.Count > 0) {
            var includedRepoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var includedPathKeys = new HashSet<string>(StringComparer.Ordinal);
            // Only prompt when both stdin and stdout are interactive. Writing prompts to stderr
            // keeps them visible even when stdout is redirected, but we still can't ReadLine
            // meaningfully without a TTY on stdin. autoSkipExclusions forces the non-interactive
            // path regardless of TTY state (e.g. the embedded `kcap setup` import call, which must
            // never block on stdin).
            var canPrompt = display.Tty && !Console.IsInputRedirected && !autoSkipExclusions;

            if (canPrompt) {
                foreach (var (key, sessions) in excludedByRepo) {
                    await Console.Error.WriteAsync($"Repository {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} from it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedRepoKeys.Add(key);
                }

                foreach (var (key, sessions) in excludedByPath) {
                    await Console.Error.WriteAsync($"Path {key} is excluded. Include {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} under it? (y/N) ");
                    var answer = Console.ReadLine()?.Trim();
                    if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)) includedPathKeys.Add(key);
                }
            } else {
                var distinctSessions = excludedByRepo.Values.SelectMany(v => v)
                    .Concat(excludedByPath.Values.SelectMany(v => v))
                    .Select(c => c.SessionId)
                    .Distinct()
                    .Count();
                var totalGroups = excludedByRepo.Count + excludedByPath.Count;
                await Console.Error.WriteLineAsync(
                    $"{display.Indented}Auto-skipping {distinctSessions} session(s) from {totalGroups} excluded source(s) (non-interactive).");
            }

            for (var i = 0; i < classifications.Count; i++) {
                var c = classifications[i];

                if (ShouldExclude(c, includedRepoKeys, includedPathKeys)) {
                    classifications[i] = c with { Status = ClassificationStatus.Excluded };
                }
            }
        }

        // --- Plan grid ---
        // Re-slice classifications by vendor for the sub-grid AFTER any
        // post-classification mutations (--since prune, excluded-repo flips).
        // The classifications list reflects every per-source-array entry by
        // reference identity, but the per-source arrays were captured before
        // the excluded-repo prompt — so rebuild from the flat list.
        var planCounts = ComputeCounts(classifications);

        Dictionary<string, ClassificationCounts>? planBySource = null;

        if (sources.Count > 1) {
            planBySource = classifications
                .GroupBy(c => c.Vendor)
                .ToDictionary(g => g.Key.VendorId, g => ComputeCounts(g.ToList()), StringComparer.Ordinal);
        }

        display.BeginPhase("Plan");
        display.WritePlanGrid(planCounts, planBySource);

        // --- Build chains + set continuation predecessors ---
        // Chain workers operate on file-based classifications (Claude/Codex);
        // they read FilePath to send transcript batches. Source-routed
        // classifications (Cursor — empty FilePath) go through a separate
        // parallel import phase that calls source.ImportSessionAsync per
        // session.
        var continuationMap = BuildContinuationMapFromClassifications(classifications);

        classifications = [
            .. classifications.Select(c =>
                continuationMap.TryGetValue(c.SessionId, out var prev) ? c with { PreviousSessionId = prev } : c
            )
        ];

        var fileBased = classifications.Where(c => !string.IsNullOrEmpty(c.FilePath)).ToList();

        // AlreadyLoaded gets routed too so vendor-specific sources (Cursor) can
        // re-assert lifecycle hooks on already-ingested sessions. The legacy
        // contract was New/Partial only — but if a previous import advanced
        // the transcript watermark while a lifecycle POST failed, the session
        // is forever lifecycle-less under the old filter. Server-side
        // idempotency (canonical event ids) makes re-emission safe;
        // CursorImportSource.ImportSessionAsync short-circuits the transcript
        // batch when there's nothing past the watermark and just fires the
        // lifecycle hooks.
        var routed = classifications.Where(c => string.IsNullOrEmpty(c.FilePath)
             && c.Status is ClassificationStatus.New
                    or ClassificationStatus.Partial
                    or ClassificationStatus.AlreadyLoaded
            )
            .ToList();

        // a Cursor subagent child whose correlated parent isn't itself among
        // `routed` (e.g. `--session <child>` excluded the parent from this run's classify slice,
        // or the parent was classified but excluded from import for any other reason) must import
        // standalone rather than being silently dropped by CursorImportSource.ImportSessionAsync's
        // nested-child skip.
        routed = ReconcileOrphanedCursorSubagentChildren(routed);

        var chains = BuildImportChains(fileBased);

        // --- Import ---
        // ConcurrentBag rather than List: OnTitleTaskReady / OnBackgroundWorkReady
        // callbacks fire from parallel chain-worker threads, so a plain List's
        // Add would race. No ordering guarantees needed — we only enumerate at
        // the end via Task.WhenAll.
        var       backgroundTasks    = new ConcurrentBag<Task>();
        var       titleTaskCount     = 0;
        var       summaryTaskCount   = 0;
        using var concurrencyLimit   = new SemaphoreSlim(3);
        var       titlesGenerated    = 0;
        var       titlesSkipped      = 0;
        var       titlesFailed       = 0;
        var       summariesGenerated = 0;
        var       summariesFailed    = 0;
        var       titleFailures      = new ConcurrentBag<(string SessionId, string Reason)>();
        var       summaryFailures    = new ConcurrentBag<(string SessionId, string Reason)>();
        var       importedSessionIds = new ConcurrentBag<string>();

        var events = new ChainWorkerEvents {
            OnSessionStarted  = (_, _) => { },    // non-TTY: session start is silent; TTY overrides below
            OnSubagentStarted = (_, _, _) => { }, // non-TTY: subagent start is silent; TTY overrides below
            OnSessionProgress = (_, _, _) => { }, // non-TTY: no live per-session bar; TTY overrides below
            OnSubagentFinished = (_, _, aid, lines) => display.Line(
                $"  ↳ imported subagent {aid} ({lines} lines)",
                $"  [dim]↳[/] imported subagent [cyan]{Markup.Escape(aid)}[/] ({lines} lines)"
            ),
            OnSessionErrored = (_, sid, reason) => display.Line(
                $"Skipping {sid} [{reason}]",
                FormatSkippedReasonMarkup(sid, reason)
            ),
            OnSessionEnded = (_, c, outcome, lines) => {
                importedSessionIds.Add(c.SessionId);

                var verb = outcome == SessionImportOutcome.Resumed
                    ? $"resuming from line {c.ResumeFromLine}"
                    : "new";

                display.Line(
                    $"Loading {c.SessionId}... {lines} lines [{verb}]",
                    FormatLoadedSummaryMarkup(c.SessionId, lines, verb)
                );
            },
            OnTitleTaskReady = t => {
                var (sid, fp, _, vnd) = t;

                // Titling shells out to the user's own `claude` / `codex`, on their subscription.
                if (skipTitle) return;

                // Don't schedule a title task for a source that doesn't support it.
                // Cursor sets SupportsTitleGeneration=false because the composer
                // header carries a name that the server maps to a
                // SessionTitleCreatedEvent at ingest time.
                if (byVendor.TryGetValue(vnd, out var src) && !src.SupportsTitleGeneration) return;

                Interlocked.Increment(ref titleTaskCount);

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var result = await GenerateTitleForImportAsync(httpClient, baseUrl, sid, fp, vnd);

                                switch (result) {
                                    case TitleResult.Generated: Interlocked.Increment(ref titlesGenerated); break;
                                    case TitleResult.Skipped:   Interlocked.Increment(ref titlesSkipped); break;
                                    case TitleResult.Failed:
                                        Interlocked.Increment(ref titlesFailed);
                                        titleFailures.Add((sid, "generation error"));

                                        break;
                                }
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
            OnBackgroundWorkReady = t => {
                if (!t.GenerateWhatsDone || !generateSummaries) return;

                Interlocked.Increment(ref summaryTaskCount);
                var sid = t.SessionId;
                var vnd = t.Vendor;

                backgroundTasks.Add(
                    Task.Run(async () => {
                            await concurrencyLimit.WaitAsync();

                            try {
                                var rc = await new WhatsDoneCommand(config, profiles, home, http)
                                    .GenerateForSessionAsync(baseUrl, sid, _ => { }, vnd.VendorId);

                                if (rc == 0) Interlocked.Increment(ref summariesGenerated);
                                else {
                                    Interlocked.Increment(ref summariesFailed);
                                    summaryFailures.Add((sid, $"exit {rc}"));
                                }
                            } catch (Exception ex) {
                                Interlocked.Increment(ref summariesFailed);
                                summaryFailures.Add((sid, ex.Message));
                            } finally { concurrencyLimit.Release(); }
                        }
                    )
                );
            },
        };

        ImportChainsResult importResult;
        // Counts for routed-source imports. These add on top of the
        // chain-worker counts when both phases run.
        var routedLoaded   = 0;
        var routedErrored  = 0;
        var routedExcluded = 0;
        // Per-vendor routed outcomes for the Done sub-grid. Aggregate
        // routedLoaded/routedExcluded/routedErrored above remain authoritative
        // for the totals row; this tracker is what feeds doneBySource so the
        // sub-grid attributes Skipped-at-import to Excluded (not Errored).
        var routedOutcomesByVendor = new ConcurrentDictionary<HarnessId, (int Loaded, int Skipped, int Failed)>();

        // A SEPARATE tracker from `importedSessionIds`, feeding ONLY the --private pass and never
        // the Done-grid counting. Membership in `importedSessionIds` keys off the raw
        // ImportOutcome, but privacy must not: a source can report a hardcoded Skipped for a repair
        // that DID attach a child, a lifecycle POST can fail AFTER that child's content persisted,
        // and a later retry can read the child as already-complete even though a PRIOR run
        // attached content that was never privatized. Each case would exclude the session and
        // leave a public session public, so every routed classification touched here is captured
        // regardless of outcome.
        //
        // Scoped by AttachesChildContentOnReplay, not a vendor name: the source owning the
        // child-import pass is what knows. Excluding a source claims only that its AlreadyLoaded
        // call posts no transcript CONTENT — not that it posts nothing: Copilot/Kiro/Pi still
        // replay session-start/session-end lifecycle there and simply have no child import, while
        // OpenCode alone returns before any POST. Nor does it claim the source can never add
        // content — all of them post on New and Partial, and a lifecycle POST failing after that
        // content persisted leaves the same kind of gap for every routed vendor. That residual is
        // deliberately a separate issue, not something this gate covers.
        var privateScopeSessionIds = new ConcurrentBag<string>();

        // Every in-scope session the server already has, captured before the import rather than from
        // its outcome. A stamp cannot narrow a session that already exists, and importedSessionIds
        // gains one only where this run did new work — so for anything this run merely revisits, the
        // explicit write is the only mechanism there is, and an outcome must not decide whether the
        // visibility the user chose is applied.
        var scopedSessionIds = new ConcurrentBag<string>();

        // Sessions whose explicit visibility write was lost — part of the run's outcome, because one
        // the user chose a visibility for that still carries the old one is a failure of the thing
        // they asked for, whatever the transcript did.
        var visibilityFailures = 0;
        // Read-only inside the parallel loops below; resolved from the sources actually in play.
        var replayChildContentVendors = byVendor.Values
            .Where(s => s.AttachesChildContentOnReplay)
            .Select(s => s.Vendor)
            .ToHashSet();

        static (int Loaded, int Skipped, int Failed) AddRoutedOutcome(
                (int Loaded, int Skipped, int Failed) prev,
                ImportOutcome                         outcome
            ) => outcome switch {
            ImportOutcome.Loaded or ImportOutcome.Resumed => (prev.Loaded                            + 1, prev.Skipped, prev.Failed),
            ImportOutcome.Skipped                         => (prev.Loaded, prev.Skipped              + 1, prev.Failed),
            _                                             => (prev.Loaded, prev.Skipped, prev.Failed + 1),
        };

        // ImportContext.VisibilityStampFor's rule, for the one path that has no ImportContext. A
        // stamp and not an omission: an absent default_visibility coalesces to org_public.
        //
        // It only decides a session's visibility AT CREATION. The read model's import-overlap branch
        // omits default_visibility from its update, so a stamp on a session that already exists is
        // discarded — which is why scopedSessionIds above, and not this, is what privacy rests on
        // for anything this run only revisits.
        var chainDefaultVisibility = forcePrivate ? "private" : defaultVisibility;

        // Close the window before anything is uploaded into it.
        //
        // A stamp cannot narrow a session that already exists, so for one this run revisits the only
        // way to avoid publishing new content into a session the user just asked to be private is to
        // make it private FIRST. New sessions are absent on purpose: they do not exist yet, so there
        // is nothing to narrow and their creation stamp is the mechanism that works.
        //
        // The closing pass stays, as recovery for a session created during the run.
        //
        // <b>Fail-closed per session.</b> The write is best-effort — it logs and swallows — so awaiting
        // it does not establish that anything is private. A session whose write was lost is dropped
        // from this run instead: uploading into it would publish new content to exactly the audience
        // the user just excluded, which is worse than not importing it at all.
        if (forcePrivate) {
            var existing = classifications
                .Where(c => c.Status is ClassificationStatus.Partial
                                     or ClassificationStatus.AlreadyLoaded)
                .Select(c => c.SessionId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (existing.Count > 0) {
                display.BeginPhase("Making existing sessions private");

                var unprivatized = await SetVisibilityNoneForAll(httpClient, baseUrl, existing, display.Indented);

                if (unprivatized.Count > 0) {
                    var blocked = unprivatized.ToHashSet(StringComparer.Ordinal);

                    visibilityFailures += blocked.Count;

                    chains = [
                        .. chains
                            .Select(chain => chain.Where(c => !blocked.Contains(c.SessionId)).ToList())
                            .Where(chain => chain.Count > 0)
                    ];

                    routed = [.. routed.Where(c => !blocked.Contains(c.SessionId))];

                    foreach (var sessionId in blocked) {
                        await Console.Error.WriteLineAsync(
                            $"{display.Indented}! skipping {sessionId}: could not make it private first, and importing "
                          + "into it would publish new content to the audience it already has");
                    }
                }
            }
        }

        // The shared stop only, because it is the only one with nothing else: there is no pass above it
        // (sharing widens, so it opens no window to close) and no stamp it can use, since org_public as
        // a default lands in `default:org` rather than the class the predicate admits unconditionally.
        // Under --private this would add writes and no protection — New is private from creation and
        // everything else was narrowed above.
        //
        // Status carries both filters by this point: the scope filter ran before classification, and an
        // excluded source had its status flipped to Excluded — so these three statuses are exactly the
        // sessions the user selected AND the server already has. AlreadyLoaded is the one that matters:
        // it reaches neither chains nor routed for a file-based source, so nothing else would see it.
        if (shareWithOrg) {
            foreach (var c in classifications) {
                if (c.Status is ClassificationStatus.New
                             or ClassificationStatus.Partial
                             or ClassificationStatus.AlreadyLoaded) {
                    scopedSessionIds.Add(c.SessionId);
                }
            }
        }

        if (chains.Count > 0) {
            display.BeginPhase($"Importing {chains.Sum(c => c.Count)} sessions");

            if (display.Tty) {
                var r = default(ImportChainsResult);

                await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var bar = ctx.AddTask($"{display.Indented}[green]Importing[/]", maxValue: chains.Sum(c => c.Count));

                            // Four progress tasks rendered as live "slot rows" beneath the
                            // main bar — one per worker. Each fills as its session's transcript
                            // lines are posted: MaxValue is the session's sendable-line count and
                            // Value advances per flushed batch via OnSessionProgress. A slot that
                            // has no batch yet (or whose total couldn't be counted) draws an
                            // indeterminate stripe rather than a stuck 0%.
                            var slots = new ProgressTask[ImportWorkerCount];

                            for (var i = 0; i < ImportWorkerCount; i++) {
                                slots[i]                 = ctx.AddTask($"{display.Indented}  Slot {i + 1} — idle", maxValue: 1);
                                slots[i].IsIndeterminate = false;
                            }

                            // Per-slot render state. currentSid/currentVerb let us revert the
                            // description after a subagent finishes (from "↳ subagent X" back to
                            // "Loading <parent>"); totalKnown tracks whether OnSessionProgress has
                            // supplied a real denominator yet, so subagent⇄parent transitions
                            // restore a determinate bar only when one exists.
                            var currentVerb = new string[ImportWorkerCount];
                            var currentSid  = new string[ImportWorkerCount];
                            var totalKnown  = new bool[ImportWorkerCount];

                            var wrappedEvents = events with {
                                // TTY renders live per-session bars, so opt into the
                                // sendable-line pre-count that sets their denominator.
                                TrackPerSessionProgress = true,
                                OnSessionStarted = (slot, c) => {
                                    var verb = c.Status == ClassificationStatus.Partial
                                        ? $"resuming from line {c.ResumeFromLine}"
                                        : "new";
                                    currentSid[slot]  = c.SessionId;
                                    currentVerb[slot] = verb;
                                    // Fresh bar; indeterminate stripe until the first batch reports
                                    // the line total.
                                    totalKnown[slot]            = false;
                                    slots[slot].Value           = 0;
                                    slots[slot].MaxValue        = 1;
                                    slots[slot].IsIndeterminate = true;
                                    slots[slot].Description     = LoadingDesc(slot, c.SessionId, verb);
                                },
                                OnSessionProgress = (slot, linesAdded, total) => {
                                    if (total > 0) {
                                        slots[slot].MaxValue        = total;
                                        slots[slot].IsIndeterminate = false;
                                        totalKnown[slot]            = true;
                                    }

                                    slots[slot].Increment(linesAdded);
                                },
                                OnSubagentStarted = (slot, sid, aid) => {
                                    // Subagent lines don't advance the parent bar; show a stripe
                                    // and swap the description while the subagent streams.
                                    slots[slot].IsIndeterminate = true;
                                    slots[slot].Description     = $"{display.Indented}  [bold]Slot {slot + 1}[/] — [dim]↳[/] subagent [cyan]{Markup.Escape(aid)}[/] (parent {Markup.Escape(sid)})";
                                },
                                OnSubagentFinished = (slot, _, _, _) => {
                                    // Revert to the parent's "Loading" description and resume the
                                    // determinate bar (only if a real total was reported; otherwise
                                    // keep the stripe). No scrollback line in TTY mode — subagent
                                    // activity was already visible on the slot row while it ran.
                                    if (!string.IsNullOrEmpty(currentSid[slot])) {
                                        slots[slot].IsIndeterminate = !totalKnown[slot];
                                        slots[slot].Description     = LoadingDesc(slot, currentSid[slot], currentVerb[slot]);
                                    }
                                },
                                OnSessionEnded = (slot, c, _, _) => {
                                    importedSessionIds.Add(c.SessionId);
                                    // Snap the slot bar to 100% and park the stripe; the description
                                    // stays on the just-finished session until the next
                                    // OnSessionStarted swaps it.
                                    bar.Increment(1);
                                    slots[slot].IsIndeterminate = false;
                                    slots[slot].Value           = slots[slot].MaxValue;
                                    // Suppress the legacy per-session log line in TTY mode
                                    // by NOT calling the base handler. Slot rows showed the
                                    // session while it ran; errors render via scrollback below.
                                },
                                OnSessionErrored = (slot, sid, reason) => {
                                    bar.Increment(1);
                                    IdleSlot(slot);
                                    // Errors print to scrollback above the live region —
                                    // Spectre.Console.Progress flushes prior writes.
                                    AnsiConsole.MarkupLine(display.Indented + FormatSkippedReasonMarkup(sid, reason));
                                },
                            };

                            r = await ImportChainsAsync(httpClient, baseUrl, chains, wrappedEvents, CancellationToken.None, sessionCwds, chainDefaultVisibility);

                            // Insurance, not presentation: the frame is erased on the way out, and an
                            // erase that ever fails should strand idle rows rather than a session name.
                            for (var i = 0; i < ImportWorkerCount; i++) IdleSlot(i);

                            return;

                            void IdleSlot(int slot) {
                                slots[slot].Description     = $"{display.Indented}  Slot {slot + 1} — idle";
                                slots[slot].IsIndeterminate = false;
                                slots[slot].Value           = 0;
                                slots[slot].MaxValue        = 1;
                                totalKnown[slot]            = false;
                                currentSid[slot]            = "";
                                currentVerb[slot]           = "";
                            }

                            string LoadingDesc(int slot, string sid, string verb) =>
                                $"{display.Indented}  [bold]Slot {slot + 1}[/] — Loading [cyan]{Markup.Escape(sid)}[/] ({verb})";
                        }
                    );
                importResult = r!;
            } else {
                importResult = await ImportChainsAsync(httpClient, baseUrl, chains, events, CancellationToken.None, sessionCwds, chainDefaultVisibility);
            }
        } else {
            importResult = new(0, 0, 0);
        }

        // --- Routed-source import phase (every non-chain source) ---
        // Sessions without a FilePath are imported directly via the source's
        // ImportSessionAsync. They share the 4-worker concurrency budget with
        // the chain phase but run sequentially after it; the TTY renderer is
        // hard-sized to 4 slots and concurrent chain-and-routed pools would
        // collide visually.
        if (routed.Count > 0) {
            display.BeginPhase($"Importing {routed.Count} routed session{(routed.Count == 1 ? "" : "s")}");

            var importCtx = new ImportContext(
                HttpClient: httpClient,
                BaseUrl: baseUrl,
                ForcePrivate: forcePrivate,
                DefaultVisibility: defaultVisibility
            );

            async Task<ImportSessionResult> ImportOne(SessionClassification c) {
                if (!byVendor.TryGetValue(c.Vendor, out var src)) {
                    return ImportOutcome.Failed;
                }

                try {
                    return await src.ImportSessionAsync(c, importCtx, CancellationToken.None);
                } catch {
                    return ImportOutcome.Failed;
                }
            }

            // Every non-rendering effect of a routed call lives here — the privatize capture, the
            // counting resolution, the per-vendor tracker, importedSessionIds membership and the
            // aggregate totals — so the TTY and non-TTY branches below differ ONLY in how they
            // draw. Duplicating any of it across both branches let them drift independently, and a
            // regression in just one was invisible to tests that exercise a single display mode.
            // Returns the resolved outcome for the caller's switch, plus the raw one (its
            // `null when outcome is Skipped` arm needs it).
            async Task<(ImportOutcome? Resolved, ImportOutcome Raw)> RecordRoutedResultAsync(SessionClassification c) {
                var result  = await ImportOne(c);
                var outcome = result.Outcome;

                // Capture for privatization BEFORE any Loaded/Failed/AlreadyLoaded classification
                // — see the declaration comment on privateScopeSessionIds. Deliberately
                // unconditional: even a Failed outcome (lifecycle POST failed after content
                // already persisted) must still get privatized.
                if (forcePrivate && replayChildContentVendors.Contains(c.Vendor)) {
                    privateScopeSessionIds.Add(c.SessionId);
                }

                // An AlreadyLoaded session's routed call is a lifecycle/repo-backfill replay (or,
                // for a nested child, an inline-handled no-op), not a new import — it must not
                // double-count on top of the classify-time AlreadyLoaded bucket. sentChildContent
                // overrides this for an AlreadyLoaded parent that attached a brand-new nested
                // child — that IS real new work.
                //
                // `resolved` — not the raw `outcome` — is the single aggregation boundary driving
                // every counting/display consumer. This is what fixes a Skipped AlreadyLoaded call
                // (e.g. Antigravity's) that attached genuinely-new child content: it resolves to
                // Loaded for counting even though its own raw outcome stays Skipped.
                //
                // importedSessionIds membership is explicitly EXCLUDED from that — it keeps
                // switching on the raw `outcome`, gated by the same suppression. Pinned by
                // RoutedPrivatizeMembershipTests. See ResolveRoutedOutcomeForCounting's doc.
                var resolved = ResolveRoutedOutcomeForCounting(c.Status, outcome, result.SentChildContent);

                if (resolved is { } resolvedForVendor) {
                    routedOutcomesByVendor.AddOrUpdate(
                        c.Vendor,
                        addValueFactory: _ => AddRoutedOutcome((0, 0, 0), resolvedForVendor),
                        updateValueFactory: (_, prev) => AddRoutedOutcome(prev, resolvedForVendor)
                    );
                }

                if (resolved is not null && outcome is ImportOutcome.Loaded or ImportOutcome.Resumed) {
                    importedSessionIds.Add(c.SessionId);
                }

                // Aggregate Done-grid totals. Also a non-rendering effect, so it belongs here
                // rather than once per renderer — the suppressed (`null`) cases contribute to no
                // bucket, which is what keeps a replay off the totals.
                switch (resolved) {
                    case ImportOutcome.Loaded or ImportOutcome.Resumed: Interlocked.Increment(ref routedLoaded);   break;
                    case ImportOutcome.Skipped:                         Interlocked.Increment(ref routedExcluded); break;
                    case ImportOutcome.Failed:                          Interlocked.Increment(ref routedErrored);  break;
                }

                return (resolved, outcome);
            }

            if (display.Tty) {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var bar = ctx.AddTask($"{display.Indented}[green]Importing[/]", maxValue: routed.Count);

                            await Parallel.ForEachAsync(
                                routed,
                                new ParallelOptions { MaxDegreeOfParallelism = ImportWorkerCount },
                                async (c, _) => {
                                    var (resolved, outcome) = await RecordRoutedResultAsync(c);

                                    switch (resolved) {
                                        case ImportOutcome.Loaded:
                                        case ImportOutcome.Resumed:
                                            AnsiConsole.MarkupLine(
                                                $"{display.Indented}[green]✓[/] Loading [cyan]{Markup.Escape(c.SessionId)}[/] ({c.Vendor.VendorId})"
                                            );

                                            break;
                                        case ImportOutcome.Skipped:
                                            AnsiConsole.MarkupLine(
                                                $"{display.Indented}[yellow]~[/] Skipping [cyan]{Markup.Escape(c.SessionId)}[/] (already current)"
                                            );

                                            break;
                                        case ImportOutcome.Failed:
                                            AnsiConsole.MarkupLine(
                                                $"{display.Indented}[red]✗[/] Failed [cyan]{Markup.Escape(c.SessionId)}[/]"
                                            );

                                            break;
                                        case null when outcome is ImportOutcome.Skipped:
                                            // A suppressed Skipped replay is a plain no-op (could
                                            // be a Cursor nested child, an Antigravity/OpenCode
                                            // lifecycle-only repair, etc.), not always "nested
                                            // under parent".
                                            AnsiConsole.MarkupLine(
                                                $"{display.Indented}[grey]·[/] Already loaded [cyan]{Markup.Escape(c.SessionId)}[/] (no new content)"
                                            );

                                            break;
                                        case null:
                                            AnsiConsole.MarkupLine(
                                                $"{display.Indented}[grey]·[/] Refreshed [cyan]{Markup.Escape(c.SessionId)}[/] (repository backfill, already loaded)"
                                            );

                                            break;
                                    }

                                    bar.Increment(1);
                                }
                            );
                        }
                    );
            } else {
                await Parallel.ForEachAsync(
                    routed,
                    new ParallelOptions { MaxDegreeOfParallelism = ImportWorkerCount },
                    async (c, _) => {
                        var (resolved, outcome) = await RecordRoutedResultAsync(c);

                        switch (resolved) {
                            case ImportOutcome.Loaded:
                            case ImportOutcome.Resumed:
                                display.Line($"Loading {c.SessionId} ({c.Vendor.VendorId})");

                                break;
                            case ImportOutcome.Skipped:
                                display.Line($"Skipping {c.SessionId} (already current)");

                                break;
                            case ImportOutcome.Failed:
                                display.Line($"Failed {c.SessionId}");

                                break;
                            case null when outcome is ImportOutcome.Skipped:
                                // See the Tty branch above.
                                display.Line($"Already loaded {c.SessionId} (no new content)");

                                break;
                            case null:
                                display.Line($"Refreshed {c.SessionId} (repository backfill, already loaded)");

                                break;
                        }
                    }
                );
            }
        }

        // --- An explicit visibility for everything this run touched ---
        //
        // Both stops need one, and for the same reason: the profile default cannot express either.
        // `none` because an omitted default_visibility coalesces to org-public; `org` because the
        // default's own `default:org` class is admitted only where the repository's owner matches the
        // tenant's configured org, which is empty on every provider but GitHubApp — so leaning on it
        // promises a workspace can read this and delivers owner-only nearly everywhere.
        //
        // The set unions three: importedSessionIds (what did new work), privateScopeSessionIds
        // (routed classifications under --private whose source can attach child content on a replay),
        // and scopedSessionIds (every in-scope session the server has). Widening, never replacing —
        // an outcome must not decide whether the visibility the user chose is applied.
        //
        // For the private stop this is recovery only: New sessions are private from creation, existing
        // ones were narrowed before any content moved, and what is left here is a retry for a write
        // that pass lost. scopedSessionIds is therefore empty under --private, by design.
        if (ExplicitVisibility(forcePrivate, shareWithOrg) is { } explicitVisibility) {
            var touched = new HashSet<string>(importedSessionIds, StringComparer.Ordinal);
            touched.UnionWith(privateScopeSessionIds);
            touched.UnionWith(scopedSessionIds);

            if (touched.Count > 0) {
                display.BeginPhase(forcePrivate
                    ? "Marking imported sessions private"
                    : "Sharing imported sessions with your workspace");

                var lost = await SetVisibilityForAll(
                    httpClient, baseUrl, [.. touched], explicitVisibility, display.Indented);

                visibilityFailures += lost.Count;

                // The per-session writes only speak up for a failure, so without this the heading
                // above renders as a section with nothing in it.
                display.Line(lost.Count == 0
                    ? $"{touched.Count} session{(touched.Count == 1 ? "" : "s")} updated"
                    : $"{touched.Count - lost.Count} of {touched.Count} updated, {lost.Count} failed");
            }
        }

        // --- Background phase (titles / summaries) ---
        var hasBackgroundWork = HadBackgroundWork(backgroundTasks);

        if (hasBackgroundWork) {
            display.BeginPhase("Titles & summaries");

            if (display.Tty) {
                await AnsiConsole.Progress()
                    .AutoClear(false)
                    .HideCompleted(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                    .StartAsync(async ctx => {
                            var titleTask   = titleTaskCount   > 0 ? ctx.AddTask($"{display.Indented}[cyan]Titles[/]", maxValue: titleTaskCount) : null;
                            var summaryTask = summaryTaskCount > 0 ? ctx.AddTask($"{display.Indented}[cyan]Summaries[/]", maxValue: summaryTaskCount) : null;
                            var seenT       = 0;
                            var seenS       = 0;

                            while (backgroundTasks.Any(t => !t.IsCompleted)) {
                                titleTask?.Value   = titlesGenerated    + titlesFailed + titlesSkipped;
                                summaryTask?.Value = summariesGenerated + summariesFailed;
                                var tList = titleFailures.ToList();
                                var sList = summaryFailures.ToList();

                                for (var i = seenT; i < tList.Count; i++) {
                                    var (sid, reason) = tList[i];
                                    AnsiConsole.MarkupLine($"{display.Indented}[red]✗[/] title failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenT = tList.Count;

                                for (var i = seenS; i < sList.Count; i++) {
                                    var (sid, reason) = sList[i];
                                    AnsiConsole.MarkupLine($"{display.Indented}[red]✗[/] summary failed for [cyan]{Markup.Escape(sid)}[/]: {Markup.Escape(reason)}");
                                }

                                seenS = sList.Count;
                                await Task.Delay(250);
                            }

                            try { await Task.WhenAll(backgroundTasks); } catch {
                                /* per-task try/catch */
                            }

                            titleTask?.Value   = titlesGenerated    + titlesFailed + titlesSkipped;
                            summaryTask?.Value = summariesGenerated + summariesFailed;
                        }
                    );
            } else {
                display.Line($"Waiting for {backgroundTasks.Count} background task(s) (titles/summaries)...");

                try { await Task.WhenAll(backgroundTasks); } catch {
                    /* per-task */
                }

                foreach (var (sid, reason) in titleFailures)
                    display.Line($"  ✗ title failed for {sid}: {reason}");

                foreach (var (sid, reason) in summaryFailures)
                    display.Line($"  ✗ summary failed for {sid}: {reason}");
            }
        }

        // --- Done ---
        // Aggregate chain + routed outcomes. Routed-source successes are folded
        // into Loaded (Cursor maps Loaded/Resumed to the same observable
        // outcome); routed Skipped is folded into Excluded (the watermark
        // already covered them).
        var final = new FinalCounts(
            Loaded: importResult.Loaded + routedLoaded,
            Resumed: importResult.Resumed,
            AlreadyLoaded: planCounts.AlreadyLoaded,
            TooShort: planCounts.TooShort,
            Excluded: planCounts.Excluded + routedExcluded,
            ProbeError: planCounts.ProbeError,
            Errored: importResult.Errored + routedErrored,
            TitlesGenerated: titlesGenerated,
            TitlesSkipped: titlesSkipped,
            TitlesFailed: titlesFailed,
            SummariesGenerated: summariesGenerated,
            SummariesFailed: summariesFailed,
            RanBackground: hasBackgroundWork,
            RequestedTitles: titleTaskCount > 0,
            RequestedSummaries: summaryTaskCount > 0
        );

        // Per-source FinalCounts. With a single source there's no sub-grid;
        // with N sources we attribute Loaded/Resumed/Errored from the imported
        // SessionIds back to their vendor.
        Dictionary<string, FinalCounts>? doneBySource = null;

        if (sources.Count > 1) {
            var importedSet = importedSessionIds.ToHashSet(StringComparer.Ordinal);

            doneBySource = classifications
                .GroupBy(c => c.Vendor)
                .ToDictionary(
                    g => g.Key.VendorId,
                    g => {
                        var slice    = g.ToList();
                        var imported = slice.Count(c => importedSet.Contains(c.SessionId));
                        routedOutcomesByVendor.TryGetValue(g.Key, out var routed);
                        var hasRouted = routedOutcomesByVendor.ContainsKey(g.Key);

                        return ComputePerSourceFinalCounts(
                            slice,
                            imported,
                            hasRouted ? routed : null
                        );
                    },
                    StringComparer.Ordinal
                );
        }

        display.WriteDoneGrid(final, doneBySource);

        onFinished?.Invoke(new ImportRunOutcome(final, visibilityFailures));

        return 0;
    }

    /// <summary>
    /// Timestamp used to order a continuation chain. Prefers the transcript's first
    /// observed message timestamp; falls back to the file's last-write mtime when
    /// the transcript has no parseable timestamp in its first 50 lines (best-effort
    /// `ExtractSessionMetadata` leaves `FirstTimestamp` null in that case).
    /// <br/>
    /// Without this fallback, timestamp-less transcripts would all sort at
    /// `DateTimeOffset.MinValue` and could displace real predecessors — corrupting
    /// `previous_session_id` links in the session-start hook. The mtime lookup is
    /// wrapped so a file deleted between discovery and chain building can't crash
    /// the whole import run; ordering is best-effort.
    /// </summary>
    static DateTimeOffset ChainTimestamp(SessionClassification c) {
        if (c.Meta.FirstTimestamp is { } ts) return ts;

        try {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(c.FilePath), TimeSpan.Zero);
        } catch {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Build a sessionId → previousSessionId map from classifications, grouping by slug.
    /// Replaces BuildContinuationMap which read transcripts again.
    /// </summary>
    static Dictionary<string, string> BuildContinuationMapFromClassifications(List<SessionClassification> classifications) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var bySlug = classifications
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal);

        foreach (var group in bySlug) {
            var chain = group
                .OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList();

            for (var i = 1; i < chain.Count; i++)
                map[chain[i].SessionId] = chain[i - 1].SessionId;
        }

        return map;
    }

    internal static SessionMetadata ExtractSessionMetadata(string filePath) {
        var meta = new SessionMetadata();

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 50) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc  = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Skip file-history-snapshot entries
                    if (root.Str("type") == "file-history-snapshot") {
                        continue;
                    }

                    // Extract cwd from metadata
                    meta.Cwd ??= root.Str("cwd");

                    // Extract model from assistant message
                    meta.Model ??= root.Obj("message")?.Str("model");

                    // Extract slug from metadata
                    meta.Slug ??= root.Str("slug");

                    // Extract sessionId
                    meta.SessionId ??= root.Str("sessionId");

                    // Extract first timestamp for continuation ordering
                    if (meta.FirstTimestamp is null
                     && root.Str("timestamp") is { } tsStr
                     && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        meta.FirstTimestamp = ts;
                    }

                    // Stop early once we have all metadata
                    if (meta.Cwd is not null && meta.Model is not null && meta.Slug is not null) {
                        break;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort metadata extraction
        }

        return meta;
    }

    internal static DateTimeOffset? ExtractLastTimestamp(string filePath) {
        try {
            // Read backward from end of file to find the last timestamp without loading everything into memory.
            // Strategy: read the last ~64KB chunk which covers well over 50 JSONL lines.
            using var fs        = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int chunkSize = 64 * 1024;
            var       offset    = Math.Max(0, fs.Length - chunkSize);
            fs.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);

            // If we seeked mid-file, skip the first partial line
            if (offset > 0) reader.ReadLine();

            // Collect the last 50 non-empty lines
            var tail = new List<string>(50);

            while (reader.ReadLine() is { } line) {
                if (!string.IsNullOrWhiteSpace(line)) {
                    tail.Add(line);

                    if (tail.Count > 50) tail.RemoveAt(0);
                }
            }

            // Scan from the end. Confirmed that Codex rollout
            // records also key their per-record timestamp under root-level
            // "timestamp" (same envelope shape session_meta/response_item/
            // event_msg all share) — verified against
            // test/data/codex/sessions/2026/05/07/rollout-...019e0228-....jsonl
            // and .../2026/07/11/rollout-2026-07-11-update-plan-fixture.jsonl
            // in the kcap-server repo, and against CodexImportSource /
            // ImportCommand.ExtractCwdFromTranscript(codex: true) which reads
            // the sibling "payload" envelope at the same root level. No second
            // probe is needed.
            for (var i = tail.Count - 1; i >= 0; i--) {
                try {
                    using var doc  = JsonDocument.Parse(tail[i]);
                    var       root = doc.RootElement;

                    if (root.Str("timestamp") is { } tsStr && DateTimeOffset.TryParse(tsStr, out var ts)) {
                        return ts;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    internal static string? ExtractCwdFromTranscript(string filePath, bool codex = false) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 20) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    var doc = JsonDocument.Parse(line);

                    // Codex stores cwd inside a session_meta envelope; Claude stores it
                    // at the JSONL root.
                    var cwd = codex
                        ? doc.RootElement.Obj("payload")?.Str("cwd")
                        : doc.RootElement.Str("cwd");

                    if (cwd is { }) {
                        return cwd;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Single ≤20-line scan that both extracts a transcript's cwd and detects
    /// whether it is one of kcap's own headless sub-session runs (title /
    /// what's-done generation). Folding the two probes into one file read keeps
    /// import discovery to a single scan per transcript instead of opening the
    /// file once for <see cref="TitleGenerator.IsCapacitorSubSession"/> and
    /// again for <see cref="ExtractCwdFromTranscript"/>.
    ///
    /// <para>
    /// The sub-session marker is the opening <c>queue-operation</c>/<c>enqueue</c>
    /// entry carrying a known helper prompt — the same signal (and first-5-line
    /// window) <see cref="TitleGenerator.IsCapacitorSubSession"/> uses, so the
    /// two stay in agreement. The marker wins over any cwd seen in that window,
    /// matching the original two-pass behaviour. Detection is Claude-only;
    /// Codex rollouts have no analogous transcript, so <paramref name="codex"/>
    /// short-circuits the marker check.
    /// </para>
    /// </summary>
    internal static (bool IsSubSession, string? Cwd) ExtractCwdAndDetectSubSession(string filePath, bool codex) {
        string? cwd = null;

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 20) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) {
                    continue;
                }

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    // Marker lives in the opening queue-operation entry; mirror
                    // IsCapacitorSubSession's first-5-line window so the two
                    // never disagree. A detected helper run wins over any cwd.
                    if (!codex
                     && linesChecked <= 5
                     && root.Str("type")      == "queue-operation"
                     && root.Str("operation") == "enqueue"
                     && root.Str("content") is { } content
                     && TitleGenerator.IsKnownCapacitorPrompt(content)) {
                        return (true, null);
                    }

                    // Codex stores cwd inside a session_meta envelope; Claude
                    // stores it at the JSONL root.
                    cwd ??= codex ? root.Obj("payload")?.Str("cwd") : root.Str("cwd");

                    // Once the marker window is past and a cwd is in hand there
                    // is nothing left to find — stop early.
                    if (cwd is not null && linesChecked >= 5) {
                        break;
                    }
                } catch (JsonException) { }
            }
        } catch {
            // Best effort — same posture as ExtractCwdFromTranscript.
        }

        return (false, cwd);
    }

    /// <summary>
    /// Codex-shape variant of <see cref="ExtractSessionMetadata"/>. Reads the first
    /// <c>session_meta</c> line and pulls cwd, the inner timestamp (when codex
    /// started, not when the envelope was written), and the model. Codex rollouts
    /// have no slug, so <see cref="SessionMetadata.Slug"/> stays null.
    ///
    /// Model resolution: <c>turn_context.payload.model</c> (the real model name,
    /// e.g. <c>gpt-5.5</c>) is preferred when present; falls back to
    /// <c>session_meta.payload.model_provider</c> (e.g. <c>openai</c>) when the
    /// rollout never reaches its first turn. The first turn_context typically
    /// arrives within the first few lines, but we scan generously in case
    /// future Codex versions interleave more preludes.
    /// </summary>
    internal static SessionMetadata ExtractCodexSessionMetadata(string filePath) {
        const int maxLines = 50;

        var meta             = new SessionMetadata();
        var sessionMetaFound = false;
        var turnModelFound   = false;

        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < maxLines) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    var type = root.Str("type");

                    switch (sessionMetaFound) {
                        case false when type == "session_meta": {
                            if (root.Obj("payload") is not { } payload) continue;

                            meta.Cwd       = payload.Str("cwd");
                            meta.Model     = payload.Str("model_provider");
                            meta.SessionId = payload.Str("id");

                            if (payload.Str("timestamp") is { } tsStr
                             && DateTimeOffset.TryParse(tsStr, out var ts)) {
                                meta.FirstTimestamp = ts;
                            }

                            sessionMetaFound = true;

                            break;
                        }
                        case true when !turnModelFound && type == "turn_context": {
                            // Only honor turn_context AFTER session_meta — a turn_context
                            // that appears before the header (truncated/corrupt rollout) is
                            // unreliable and would otherwise stamp a model name onto an
                            // otherwise-empty meta.
                            if (root.Obj("payload")?.Str("model") is { Length: > 0 } turnModel) {
                                meta.Model     = turnModel;
                                turnModelFound = true;
                            }

                            break;
                        }
                    }

                    if (sessionMetaFound && turnModelFound) break;
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return meta;
    }

    internal sealed record CodexGitInfo(string? RemoteUrl, string? Branch, string? CommitHash);

    /// <summary>
    /// Extract the optional <c>git</c> block from the first <c>session_meta</c> line
    /// of a Codex rollout (commit_hash, branch, repository_url). Returns null when
    /// there's no git block — codex omits it for sessions started outside a repo.
    /// </summary>
    internal static CodexGitInfo? ExtractCodexGitInfo(string filePath) {
        try {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var linesChecked = 0;

            while (reader.ReadLine() is { } line && linesChecked < 5) {
                linesChecked++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    if (root.Str("type") != "session_meta") continue;

                    var git = root.Obj("payload")?.Obj("git");

                    if (git is null) return null;

                    return new CodexGitInfo(
                        RemoteUrl: git.Value.Str("repository_url"),
                        Branch: git.Value.Str("branch"),
                        CommitHash: git.Value.Str("commit_hash")
                    );
                } catch (JsonException) { }
            }
        } catch {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Whole-transcript version of the live watcher's evidence scan (see
    /// <c>RepoEvidenceScanner</c>): feeds every line through the two-slot mutation/read rule,
    /// then promotes the read fallback if no mutation ever attributed. Import has no live tail
    /// to keep scanning, so — unlike the watcher's one-shot prefix scan — this always runs to
    /// end of file. <paramref name="findRoot"/>/<paramref name="detect"/> are injected so the
    /// D1 gating in <see cref="ImportSingleSessionAsync"/> is unit-testable without disk.
    /// </summary>
    internal static async Task<JsonObject?> TryBuildEvidenceRepositoryNodeAsync(
            string                                 vendor,
            IEnumerable<string>                    transcriptLines,
            Func<string, string?>                  findRoot,
            Func<string, Task<RepositoryPayload?>> detect
        ) {
        try {
            var scanner = new RepoEvidenceScanner<RepositoryPayload>(
                findRoot, detect, p => p.Owner is not null && p.RepoName is not null);

            foreach (var line in transcriptLines) {
                if (await scanner.OnLineAsync(vendor, line) is { } attributed) {
                    return RepositoryDetection.BuildRepositoryNode(attributed);
                }
            }

            if (await scanner.PromoteReadFallbackAsync() is { } fallback) {
                return RepositoryDetection.BuildRepositoryNode(fallback);
            }

            return null;
        } catch {
            return null; // fail-open: a bad transcript must never break import
        }
    }

    /// <summary>
    /// Path-based overload: reads the transcript INSIDE this method's own try, so an
    /// invalid/empty path degrades to "no evidence" here too, instead of throwing as a bare call
    /// argument at the caller, outside any try/catch. Uses <c>File.ReadLinesShared</c>
    /// (see <see cref="SharedFileText"/>), not <c>File.ReadLines</c>, whose FileShare.Read is
    /// Windows-mandatory and denies the write handle a still-flushing agent owns on its transcript.
    /// </summary>
    internal static async Task<JsonObject?> TryBuildEvidenceRepositoryNodeAsync(
            string                                 vendor,
            string                                 transcriptPath,
            Func<string, string?>                  findRoot,
            Func<string, Task<RepositoryPayload?>> detect
        ) {
        try {
            return await TryBuildEvidenceRepositoryNodeAsync(vendor, File.ReadLinesShared(transcriptPath), findRoot, detect);
        } catch {
            return null; // fail-open: an unreadable/invalid path must never break import
        }
    }

    /// <summary>
    /// Build a SessionId → repo lookup for every discovered transcript. Repo
    /// detection (git + `gh pr view`) is the slow part of the discovery phase
    /// and previously ran sequentially per-session, making `kcap import`
    /// look frozen on large histories. This version deduplicates by cwd
    /// (transcripts in the same project share a repo) and runs detection in
    /// parallel, surfacing progress via a Spectre status spinner on a TTY and
    /// a plain status line otherwise.
    /// </summary>
    /// <summary>
    /// Print a one-shot summary of transcript cwds that don't exist on disk.
    /// Most users with a long history accumulate references to deleted
    /// worktrees and renamed repo directories, and those sessions silently
    /// fail to match an --org/--repo scope. This output lets them spot the
    /// gap before the import proceeds.
    /// </summary>
    /// <summary>
    /// Surface how many sessions were transparently attributed to their parent
    /// project via the worktree-path fallback (cwd lived under
    /// <c>&lt;dir&gt;/.&lt;X&gt;/worktrees/&lt;slug&gt;</c> but the worktree
    /// itself no longer exists on disk). Stays silent when zero.
    /// </summary>
    internal static void ReportWorktreeAttributions(int count, ImportDisplay display) {
        if (count <= 0) return;

        var sessionWord = count == 1 ? "session" : "sessions";
        display.Line($"Attributed {count} {sessionWord} to a parent project via worktree path.");
    }

    /// <summary>
    /// Persist the org chosen for <c>kcap import --org</c> onto the active profile so a
    /// later bare <c>--org</c> reuses it. Best-effort: failing to remember the choice must
    /// not abort the import. Creates the profile entry if it doesn't exist yet (e.g. the
    /// <c>default</c> profile), so the org is remembered even without a tenant-bound profile.
    /// </summary>
    async Task PersistImportOrgAsync(string profileName, string org) {
        if (string.IsNullOrWhiteSpace(org)) return;
        org = org.Trim();

        try {
            await ConfigMutator.MutateAsync(config, c => {
                var profile = c.Profiles.GetValueOrDefault(profileName) ?? new Core.Config.Profile();

                return c with {
                    Profiles = new Dictionary<string, Core.Config.Profile>(c.Profiles) {
                        [profileName] = profile with { ImportOrg = org }
                    }
                };
            });
        } catch {
            // Remembering the org is a convenience, not part of the import contract.
        }
    }

    internal static void ReportMissingCwds(
            IReadOnlyDictionary<string, string> sessionCwds,
            IReadOnlyList<CwdRemap>?            cwdRemap,
            ImportDisplay                       display,
            UserHome                            home
        ) {
        if (sessionCwds.Count == 0) return;

        var uniqueCwds = sessionCwds.Values.Distinct(StringComparer.Ordinal).ToList();
        var missing    = uniqueCwds.Where(c => !Directory.Exists(c)).ToHashSet(StringComparer.Ordinal);

        if (missing.Count == 0) return;

        // Collapse descendants under missing ancestors — if /repo is missing,
        // there's no value in also listing /repo/.claude/worktrees/agent-X.
        var roots = CollapseDescendants(missing);

        var sessionsAffected = sessionCwds.Values.Count(missing.Contains);
        var sortedRoots      = roots.OrderBy(c => c, StringComparer.Ordinal).ToList();

        var sessionWord = sessionsAffected == 1 ? "session references" : "sessions reference";
        var pathWord    = sortedRoots.Count == 1 ? "path that no longer exists" : "distinct paths that no longer exist";
        display.Line($"{sessionsAffected} {sessionWord} {sortedRoots.Count} {pathWord} on disk:");

        const int sampleSize = 5;
        foreach (var cwd in sortedRoots.Take(sampleSize)) display.Line($"  {ShortenHome(cwd, home.Path)}");

        if (sortedRoots.Count > sampleSize) {
            display.Line($"  ... and {sortedRoots.Count - sampleSize} more");
        }

        var hint = cwdRemap is { Count: > 0 }
            ? "Run `kcap remap <from> <to>` to update or add mappings to their new on-disk paths."
            : "Run `kcap remap <from> <to>` to map these to their new on-disk paths.";

        display.Line(hint);
    }

    /// <summary>
    /// Drop any path from <paramref name="paths"/> whose parent (at any depth)
    /// is also in the set. Used to collapse worktree-style descendants like
    /// <c>/repo/.claude/worktrees/agent-X</c> under their already-missing
    /// parent <c>/repo</c> for a less noisy report.
    /// </summary>
    internal static HashSet<string> CollapseDescendants(IReadOnlySet<string> paths) {
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in paths) {
            if (!HasAncestorIn(p, paths)) roots.Add(p);
        }

        return roots;

        static bool HasAncestorIn(string path, IReadOnlySet<string> set) {
            // Walk parent directories: /a/b/c → /a/b → /a (stop at the first
            // segment). Trim the last separator-delimited segment ourselves
            // instead of Path.GetDirectoryName, which on Windows normalizes
            // '/' → '\' and would break the Ordinal set comparison for
            // forward-slash transcript cwds. Both '/' and '\' are
            // honored so mixed-style paths collapse consistently on every OS.
            var parent = TrimLastSegment(path);

            while (parent is not null) {
                if (set.Contains(parent)) return true;
                parent = TrimLastSegment(parent);
            }

            return false;

            static string? TrimLastSegment(string p) {
                var i = p.Length - 1;

                // Skip trailing separators (e.g. a stray "/a/b/").
                while (i >= 0 && CwdRemapper.IsSeparator(p[i])) i--;

                // Find the separator that ends the parent segment.
                while (i >= 0 && !CwdRemapper.IsSeparator(p[i])) i--;

                // No separator left, or only a leading-root separator remains
                // ("/foo" → root) → no further ancestor to test.
                if (i <= 0) return null;

                // Collapse any run of separators so "/a//b" trims cleanly to "/a".
                var end = i;
                while (end > 0 && CwdRemapper.IsSeparator(p[end - 1])) end--;

                return end <= 0 ? null : p[..end];
            }
        }
    }

    /// <summary>
    /// Replace the user's home directory prefix with <c>~</c> for display only.
    /// Uses path-boundary matching (either <c>/</c> or <c>\</c>) so siblings
    /// like <c>/Users/alexeyfoo</c> aren't accidentally shortened, and follows
    /// the host filesystem's case-sensitivity policy.
    /// </summary>
    internal static string ShortenHome(string path, string home) {
        var comparison = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.IsNullOrEmpty(home) || !path.StartsWith(home, comparison)) return path;
        if (path.Length == home.Length) return "~";
        return CwdRemapper.IsSeparator(path[home.Length]) ? "~" + path[home.Length..] : path;
    }

    /// <summary>
    /// Two-step cwd resolution shared between Claude/Codex transcripts and
    /// Cursor workspaces: apply user-configured prefix remaps first, then
    /// fall back to attributing ephemeral worktree paths (e.g.
    /// <c>.../&lt;project&gt;/.claude/worktrees/&lt;slug&gt;</c>) to
    /// <c>&lt;project&gt;</c> when the worktree itself no longer exists.
    /// </summary>
    string ResolveCwd(
            string                   raw,
            IReadOnlyList<CwdRemap>? cwdRemap,
            ISet<string>?            worktreeAttributed,
            string                   sessionId
        ) {
        var remapped              = CwdRemapper.Apply(raw, cwdRemap, home);
        var (final, wasStripped) = WorktreePathResolver.Resolve(remapped);

        if (wasStripped) worktreeAttributed?.Add(sessionId);

        return final;
    }

    internal async Task<Dictionary<string, (string Owner, string Name)?>> ResolveTranscriptReposAsync(
            IReadOnlyList<(string SessionId, string FilePath, string EncodedCwd)> transcripts,
            bool                                                                  codex,
            ImportDisplay                                                         display,
            IReadOnlyList<CwdRemap>?                                              cwdRemap            = null,
            IDictionary<string, string>?                                          sessionCwds        = null,
            ISet<string>?                                                         worktreeAttributed = null
        ) {
        // Extract cwd per transcript first (cheap: ≤20-line file read).
        // Apply user-configured prefix remaps so historic transcripts pointing
        // at since-renamed local directories still resolve, then transparently
        // attribute ephemeral worktree cwds back to their parent project when
        // the worktree itself no longer exists on disk. The per-session
        // (resolved) cwd is fed back to the caller via sessionCwds so the
        // import flow can report which paths are still missing.
        var perTranscript = new (string SessionId, string? Cwd)[transcripts.Count];

        for (var i = 0; i < transcripts.Count; i++) {
            // One scan recovers the cwd and flags kcap's own headless
            // sub-sessions (title / what's-done generation). Those helper runs
            // execute in an ephemeral temp working dir that is deleted the
            // instant the run ends, so their recorded cwd never exists on disk.
            // Classification already drops them from the import as internal
            // sub-sessions, but leaving them in here floods the missing-cwd
            // report with dozens of dead temp paths the user can neither remap
            // nor act on. Excluding them keeps that report scoped to genuine
            // user sessions whose repo really did move.
            var (isSubSession, raw) = ExtractCwdAndDetectSubSession(transcripts[i].FilePath, codex);

            if (isSubSession) {
                perTranscript[i] = (transcripts[i].SessionId, null);

                continue;
            }

            var effective = raw is null ? null : ResolveCwd(raw, cwdRemap, worktreeAttributed, transcripts[i].SessionId);

            perTranscript[i] = (transcripts[i].SessionId, effective);

            // Indexer assignment (not Add) so duplicate SessionIds across
            // project dirs / backups can't abort the import. Last-write-wins
            // is fine for the missing-cwd report — the cwd is functionally
            // the same path anyway.
            if (effective is not null && sessionCwds is not null) {
                sessionCwds[transcripts[i].SessionId] = effective;
            }
        }

        var uniqueCwds = perTranscript
            .Select(p => p.Cwd)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();

        var repoByCwd = new ConcurrentDictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);

        if (uniqueCwds.Count > 0) {
            var options = new ParallelOptions { MaxDegreeOfParallelism = 8 };
            var done    = 0;
            var total   = uniqueCwds.Count;

            async ValueTask DetectOne(string cwd) {
                // Import only needs owner/repo here — skip the PR/MR provider round-trip.
                var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false);
                repoByCwd[cwd] = repo is { Owner: { } o, RepoName: { } n } ? (o, n) : null;
            }

            if (display.Tty) {
                var statusLock = new Lock();

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(
                        $"Scanning repositories (0/{total})…",
                        async ctx => {
                            await Parallel.ForEachAsync(
                                uniqueCwds,
                                options,
                                async (cwd, _) => {
                                    await DetectOne(cwd);
                                    var d = Interlocked.Increment(ref done);

                                    // Spectre's StatusContext isn't documented as thread-safe; serialize
                                    // the per-completion update so parallel workers can't race on the
                                    // status renderer.
                                    lock (statusLock) {
                                        ctx.Status($"Scanning repositories ({d}/{total})…");
                                    }
                                }
                            );
                        }
                    );
            } else {
                display.Line($"Scanning {total} repositor{(total == 1 ? "y" : "ies")}...");
                await Parallel.ForEachAsync(uniqueCwds, options, async (cwd, _) => await DetectOne(cwd));
            }
        }

        var resolved = new Dictionary<string, (string Owner, string Name)?>(StringComparer.Ordinal);

        foreach (var (sid, cwd) in perTranscript) {
            resolved[sid] = cwd is not null && repoByCwd.TryGetValue(cwd, out var r) ? r : null;
        }

        return resolved;
    }

    /// <summary>
    /// Enumerate ~/.claude/projects/*/*.jsonl transcripts, deduplicating directories
    /// by their resolved path (so symlinked project dirs don't scan the same files
    /// twice). Returns one entry per transcript with the normalized session id.
    /// </summary>
    internal static List<(string SessionId, string FilePath, string EncodedCwd)> DiscoverTranscripts(string projectsDir) {
        var results = new List<(string, string, string)>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(projectsDir)) return results;

        foreach (var cwdDir in Directory.GetDirectories(projectsDir)) {
            var realPath = new DirectoryInfo(cwdDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
             ?? Path.GetFullPath(cwdDir);

            if (!seen.Add(realPath)) continue;

            var encodedCwd = Path.GetFileName(cwdDir);

            try {
                results.AddRange(
                    from jsonlFile in Directory.GetFiles(cwdDir, "*.jsonl")
                    let sessionId = NormalizeGuid(Path.GetFileNameWithoutExtension(jsonlFile))
                    select (sessionId, jsonlFile, encodedCwd)
                );
            } catch {
                // A hostile/inaccessible project dir must not abort the whole scan.
            }
        }

        return results;
    }

    /// <summary>
    /// Group the import-bound subset (New + Partial) into ordered chains by slug.
    /// A chain is a list of classifications sharing the same slug, ordered by
    /// FirstTimestamp ascending. Sessions without a slug (or with a unique slug)
    /// become chains of length 1. Chain order (across chains) is stable by slug
    /// string so re-runs import in the same order.
    /// </summary>
    internal static List<List<SessionClassification>> BuildImportChains(List<SessionClassification> classifications) {
        var importable = classifications
            .Where(c => c.Status is ClassificationStatus.New or ClassificationStatus.Partial)
            .ToList();

        var withSlug = importable
            .Where(c => c.Meta.Slug is not null)
            .GroupBy(c => c.Meta.Slug!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var chains = withSlug.Select(group => group.OrderBy(ChainTimestamp)
                .ThenBy(c => c.SessionId, StringComparer.Ordinal)
                .ToList()
            )
            .ToList();
        chains.AddRange(importable.Where(c => c.Meta.Slug is null).OrderBy(c => c.SessionId, StringComparer.Ordinal).Select(solo => (List<SessionClassification>)[solo]));

        return chains;
    }

    /// <summary>
    /// Normalize a GUID string to dashless format (matching the live CLI's NormalizeGuidField).
    /// Non-GUID strings are returned as-is.
    /// </summary>
    internal static string NormalizeGuid(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("N") : value;

    async Task<TitleResult> GenerateTitleForImportAsync(HttpClient httpClient, string baseUrl, string sessionId, string filePath, HarnessId vendor) {
        try {
            var (userText, assistantText) = vendor is HarnessId.Codex
                ? TitleGenerator.ExtractCodexTitleContext(filePath)
                : TitleGenerator.ExtractTitleContext(filePath);

            if (userText is null) {
                return TitleResult.Skipped;
            }

            var result = await TitleGeneration.GenerateAsync(
                userText, assistantText, _ => { }, profiles.Resolution.Profile, home, vendor.VendorId);

            if (result is null) {
                return TitleResult.Skipped;
            }

            var payload = new SessionTitlePayload {
                SessionId        = sessionId,
                Title            = result.Result,
                Model            = result.Model,
                InputTokens      = result.InputTokens,
                OutputTokens     = result.OutputTokens,
                CacheReadTokens  = result.CacheReadTokens,
                CacheWriteTokens = result.CacheWriteTokens
            };

            var       payloadJson = JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.SessionTitlePayload);
            using var content     = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var titleResp   = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-title", content);

            return TitleResult.Generated;
        } catch {
            return TitleResult.Failed;
        }
    }

    enum TitleResult { Generated, Skipped, Failed }

    internal sealed record ImportChainsResult(int Loaded, int Resumed, int Errored);

    internal sealed record ChainWorkerEvents {
        /// <summary>Fired before a worker begins importing a session on its slot.</summary>
        public required Action<int, SessionClassification> OnSessionStarted { get; init; }

        /// <summary>Fired when the worker begins streaming a subagent's transcript inline.</summary>
        public required Action<int, string, string> OnSubagentStarted { get; init; } // slot, sessionId, agentId

        /// <summary>Fired after a subagent's transcript has been fully streamed.</summary>
        public required Action<int, string, string, int> OnSubagentFinished { get; init; } // slot, sessionId, agentId, lines

        /// <summary>Fired when a session import fails on a worker slot.</summary>
        public required Action<int, string, string> OnSessionErrored { get; init; } // slot, sessionId, reason

        /// <summary>
        /// Fired after a session import completes (loaded or resumed). The slot is
        /// available for the next session as soon as this returns.
        /// </summary>
        public required Action<int, SessionClassification, SessionImportOutcome, int> OnSessionEnded { get; init; }
        // slot, classification, outcome (Loaded|Resumed), linesSent

        /// <summary>
        /// Fired after each parent-transcript batch is flushed, to advance the
        /// per-slot progress bar. <c>linesAdded</c> is the size of the batch just
        /// posted; <c>total</c> is the session's full sendable-line count (the
        /// bar's denominator, or 0 when unknown). Subagent batches are excluded —
        /// they're surfaced via <see cref="OnSubagentStarted"/> / <see cref="OnSubagentFinished"/>
        /// and don't advance the parent bar.
        /// </summary>
        public required Action<int, int, int> OnSessionProgress { get; init; } // slot, linesAdded, total

        /// <summary>
        /// Whether a live per-session bar consumes <see cref="OnSessionProgress"/>.
        /// When false (non-TTY / redirected output), <see cref="ImportSingleSessionAsync"/>
        /// skips the per-session line pre-count — a full transcript read whose
        /// denominator nothing would render. Defaults to false; the TTY slot
        /// renderer sets it true.
        /// </summary>
        public bool TrackPerSessionProgress { get; init; }

        /// <summary>Fired when a successfully-imported session is ready for title generation.</summary>
        public required Action<(string SessionId, string FilePath, string? PreviousSessionId, HarnessId Vendor)> OnTitleTaskReady { get; init; }

        /// <summary>
        /// Fired when a session's session-end hook returned, signalling that the
        /// background phase may enqueue title / what's-done work for it.
        /// Renamed from the previous `OnSessionEnded` to disambiguate from the
        /// slot-aware lifecycle event above.
        /// </summary>
        public required Action<(string SessionId, bool GenerateWhatsDone, HarnessId Vendor)> OnBackgroundWorkReady { get; init; }
    }

    /// <summary>
    /// Dispatch chains across 4 parallel workers; sessions within a chain run
    /// serially. Thread-safe: counters use Interlocked, callbacks must be
    /// thread-safe (production wiring uses AnsiConsole + ConcurrentBag).
    /// </summary>
    internal async Task<ImportChainsResult> ImportChainsAsync(
            HttpClient                        httpClient,
            string                            baseUrl,
            List<List<SessionClassification>> chains,
            ChainWorkerEvents                 events,
            CancellationToken                 ct,
            IReadOnlyDictionary<string, string>? sessionCwds       = null,
            string?                              defaultVisibility = null
        ) {
        var loaded  = 0;
        var resumed = 0;
        var errored = 0;

        var queue = Channel.CreateUnbounded<List<SessionClassification>>(
            new() {
                SingleReader = false,
                SingleWriter = true,
            }
        );

        foreach (var chain in chains) await queue.Writer.WriteAsync(chain, ct);
        queue.Writer.Complete();

        var workers = new Task[ImportWorkerCount];

        for (var i = 0; i < ImportWorkerCount; i++) {
            var slot = i; // capture

            workers[i] = Task.Run(
                async () => {
                    while (await queue.Reader.WaitToReadAsync(ct)) {
                        while (queue.Reader.TryRead(out var chain)) {
                            foreach (var session in chain) {
                                ct.ThrowIfCancellationRequested();
                                events.OnSessionStarted(slot, session);

                                SessionImportOutcome r;

                                var linesSent = 0;

                                try {
                                    (r, linesSent) = await ImportSingleSessionAsync(httpClient, baseUrl, session, slot, events, ct, sessionCwds, defaultVisibility);
                                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                                    throw;
                                } catch (Exception ex) {
                                    events.OnSessionErrored(slot, session.SessionId, ex.Message);
                                    r = SessionImportOutcome.Errored;
                                }

                                switch (r) {
                                    case SessionImportOutcome.Loaded:  Interlocked.Increment(ref loaded); break;
                                    case SessionImportOutcome.Resumed: Interlocked.Increment(ref resumed); break;
                                    case SessionImportOutcome.Errored: Interlocked.Increment(ref errored); break;
                                }

                                if (r != SessionImportOutcome.Errored) {
                                    events.OnSessionEnded(slot, session, r, linesSent);
                                }
                            }
                        }
                    }
                },
                ct
            );
        }

        await Task.WhenAll(workers);

        return new(loaded, resumed, errored);
    }

    internal enum SessionImportOutcome { Loaded, Resumed, Errored }

    /// <summary>
    /// Prefer the already remap-/worktree-resolved cwd computed up front into
    /// <paramref name="sessionCwds"/> (see <see cref="ResolveTranscriptReposAsync"/>) so
    /// workspace_root discovery and repo detection agree with what the missing-cwd report
    /// showed the user — falling back to the raw transcript cwd when this session has no
    /// entry (e.g. a non-file source, or resolution failed to produce one). Shared by both
    /// the New and Partial branches of <see cref="ImportSingleSessionAsync"/>.
    /// </summary>
    static string? ResolveCwd(SessionClassification session, IReadOnlyDictionary<string, string>? sessionCwds) =>
        sessionCwds is not null && sessionCwds.TryGetValue(session.SessionId, out var resolvedCwd)
            ? resolvedCwd
            : session.Meta.Cwd ?? SessionImporter.DecodeCwdFromDirName(session.EncodedCwd);

    async Task<(SessionImportOutcome Outcome, int LinesSent)> ImportSingleSessionAsync(
            HttpClient            httpClient,
            string                baseUrl,
            SessionClassification session,
            int                   slot,
            ChainWorkerEvents     events,
            CancellationToken     ct,
            IReadOnlyDictionary<string, string>? sessionCwds       = null,
            string?                              defaultVisibility = null
        ) {
        // Denominator for the per-slot progress bar: the number of parent-transcript
        // lines this import will POST. For a resume, only the lines past the server's
        // watermark are sent. 0 when the file is missing/unreadable — the slot then
        // stays indeterminate instead of stuck at 0%. Skipped entirely when
        // no live bar consumes it (non-TTY) so we don't pre-scan every transcript.
        var sendableTotal = events.TrackPerSessionProgress
            ? SessionImporter.CountSendableLines(
                session.FilePath,
                session.Status == ClassificationStatus.Partial ? session.ResumeFromLine : 0
            )
            : 0;

        IProgress<ImportProgress> perSessionProgress = new CallbackProgress(ev => {
                switch (ev) {
                    // Only parent-transcript batches (AgentId == null) advance the
                    // slot bar; subagent batches are surfaced via the subagent events.
                    case BatchFlushed { AgentId: null } bf: events.OnSessionProgress(slot, bf.LinesAdded, sendableTotal); break;
                    case SubagentStarted ss:               events.OnSubagentStarted(slot, session.SessionId, ss.AgentId); break;
                    case SubagentFinished sf:              events.OnSubagentFinished(slot, session.SessionId, sf.AgentId, sf.LinesSent); break;
                }
            }
        );

        if (session.Status == ClassificationStatus.Partial) {
            try {
                // Fail-closed tail: a rejected/half-applied resume must NOT be finalized
                // (posting session-end after a gap would mark the session ended with a hole).
                var linesSent = await SessionImporter.SendTranscriptBatches(
                    httpClient,
                    baseUrl,
                    session.SessionId,
                    session.FilePath,
                    agentId: null,
                    startLine: session.ResumeFromLine,
                    progress: perSessionProgress,
                    vendor: session.Vendor,
                    failOnError: true
                );

                // End-only reassertion: session-end has server-side idempotency guards,
                // whereas the generic SessionStarted uses random ids — re-asserting start
                // would duplicate it. So finalize a resumed session with end ONLY.
                var resumeLastTs  = ExtractLastTimestamp(session.FilePath);
                var resumeEndHook = new JsonObject {
                    ["session_id"]      = session.SessionId,
                    ["transcript_path"] = session.FilePath,
                    ["cwd"]             = ResolveCwd(session, sessionCwds) ?? "",
                    ["reason"]          = "Other",
                    ["hook_event_name"] = "session_end",
                    ["origin"]          = ImportOrigins.Historical,
                };
                if (resumeLastTs is not null) resumeEndHook["ended_at"] = resumeLastTs.Value.ToString("O");

                using var endContent = new StringContent(resumeEndHook.ToJsonString(), Encoding.UTF8, "application/json");
                using var endResp    = await httpClient.PostWithRetryAsync(
                    $"{baseUrl}/hooks/session-end/{session.Vendor.VendorId}", endContent, ct: ct, retryStatuses: true);

                if (!endResp.IsSuccessStatusCode) {
                    events.OnSessionErrored(slot, session.SessionId, $"resume session-end failed: HTTP {(int)endResp.StatusCode}");

                    return (SessionImportOutcome.Errored, linesSent);
                }

                return (SessionImportOutcome.Resumed, linesSent);
            } catch (HttpRequestException ex) {
                // Includes the fail-closed tail's thrown rejection — do NOT finalize.
                events.OnSessionErrored(slot, session.SessionId, $"resume tail failed: {ex.Message}");

                return (SessionImportOutcome.Errored, 0);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                events.OnSessionErrored(slot, session.SessionId, ex.Message);

                return (SessionImportOutcome.Errored, 0);
            }
        }

        // status == New: session-start → import → session-end → enqueue background tasks
        var meta = session.Meta;
        var cwd  = ResolveCwd(session, sessionCwds);

        var startHook = new JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["source"]          = "Startup",
            ["hook_event_name"] = "session_start",
            ["model"]           = meta.Model,
            ["origin"]          = ImportOrigins.Historical,
        };
        if (meta.FirstTimestamp is not null) startHook["started_at"]                = meta.FirstTimestamp.Value.ToString("O");
        if (session.PreviousSessionId is not null) startHook["previous_session_id"] = session.PreviousSessionId;
        if (meta.Slug is not null) startHook["slug"]                                = meta.Slug;
        // New-only: this branch runs only for ClassificationStatus.New, Partial having returned above.
        if (defaultVisibility is not null) startHook["default_visibility"]          = defaultVisibility;

        // best-effort git-root discovery from the (already remap-resolved) cwd, so
        // historical imports carry the same workspace_root the live hooks do. Fail-open:
        // GitRepository.FindRoot swallows I/O errors and returns null when no repo is found
        // on this machine (e.g. the recorded path no longer exists), in which case the field
        // is simply omitted.
        if (!string.IsNullOrEmpty(cwd) && GitRepository.FindRoot(cwd) is { } workspaceRoot) {
            startHook["workspace_root"] = workspaceRoot;
        }

        // Codex sessions carry a `git` block on session_meta — prefer it over a fresh
        // RepositoryDetection probe (which reads the live git config and might disagree
        // with what was true when the rollout was recorded). Detection still runs as a
        // fallback for fields the rollout omits (user_name / user_email).
        var codexRepo = session.Vendor is HarnessId.Codex ? ExtractCodexGitInfo(session.FilePath) : null;

        if (cwd is not null) {
            // The imported session-start payload carries no PR fields (only owner/repo/branch/user),
            // so skip the PR/MR provider round-trip.
            var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false);

            if (repo is not null || codexRepo is not null) {
                var repoNode = new JsonObject();

                if (repo?.UserName is not null) repoNode["user_name"]   = repo.UserName;
                if (repo?.UserEmail is not null) repoNode["user_email"] = repo.UserEmail;

                var remoteUrl = codexRepo?.RemoteUrl ?? repo?.RemoteUrl;

                if (remoteUrl is not null)
                    repoNode["remote_url"] = remoteUrl;

                var (codexOwner, codexRepoName) = GitUrlParser.ParseRemoteUrl(codexRepo?.RemoteUrl);
                var owner    = codexOwner        ?? repo?.Owner;
                var repoName = codexRepoName     ?? repo?.RepoName;
                var branch   = codexRepo?.Branch ?? repo?.Branch;

                if (owner is not null) repoNode["owner"]        = owner;
                if (repoName is not null) repoNode["repo_name"] = repoName;
                if (branch is not null) repoNode["branch"]      = branch;

                if (repoNode.Count > 0) startHook["repository"] = repoNode;
            }
        }

        // D1: the cwd itself isn't inside any git repo (or is missing entirely), so neither
        // workspace_root above nor the cwd-based repository detection could find anything —
        // fall back to scanning the transcript's own tool-use paths for a resolvable git root,
        // same evidence rule the live watcher applies for sessions launched outside a repo.
        if (session.Vendor is HarnessId.Claude
            && !startHook.ContainsKey("repository")
            && (cwd is null || GitRepository.FindRoot(cwd) is null)) {
            var evidenceNode = await TryBuildEvidenceRepositoryNodeAsync(
                session.Vendor.VendorId,
                session.FilePath,
                GitRepository.FindRoot,
                root => RepositoryDetection.DetectRepositoryAsync(config, root, detectPullRequest: false));

            if (evidenceNode is not null) startHook["repository"] = evidenceNode;
        }

        // The /hooks/session-start route binds vendor from the URL path
        // (/session-start/{vendor=claude}); a body-level `vendor` field is
        // ignored. Codex history imports that posted to /hooks/session-start
        // (no path vendor) silently landed as claude-tagged SessionStarted
        // events on KurrentDB, and the projector then wrote sessions.vendor =
        // claude even though the transcript was a codex rollout.
        try {
            using var startContent = new StringContent(startHook.ToJsonString(), Encoding.UTF8, "application/json");
            using var startResp    = await httpClient.PostWithRetryAsync(
                $"{baseUrl}/hooks/session-start/{session.Vendor.VendorId}", startContent, ct: ct, retryStatuses: true);

            if (!startResp.IsSuccessStatusCode) {
                events.OnSessionErrored(slot, session.SessionId, $"session-start failed: HTTP {(int)startResp.StatusCode}");

                return (SessionImportOutcome.Errored, 0);
            }
        } catch (HttpRequestException ex) {
            events.OnSessionErrored(slot, session.SessionId, $"server unreachable: {ex.Message}");

            return (SessionImportOutcome.Errored, 0);
        }

        ImportResult importResult;

        try {
            importResult = await SessionImporter.ImportSessionAsync(
                httpClient,
                baseUrl,
                session.FilePath,
                session.SessionId,
                meta,
                session.EncodedCwd,
                perSessionProgress,
                session.Vendor
            );
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            events.OnSessionErrored(slot, session.SessionId, ex.Message);

            return (SessionImportOutcome.Errored, 0);
        }

        var lastTs = ExtractLastTimestamp(session.FilePath);

        var endHook = new JsonObject {
            ["session_id"]      = session.SessionId,
            ["transcript_path"] = session.FilePath,
            ["cwd"]             = cwd ?? "",
            ["reason"]          = "Other",
            ["hook_event_name"] = "session_end",
            ["origin"]          = ImportOrigins.Historical,
        };
        if (lastTs is not null) endHook["ended_at"] = lastTs.Value.ToString("O");

        var generateWhatsDone = false;

        // Same vendor-in-URL contract applies to /hooks/session-end (see the
        // session-start comment above) — symmetric route, same default of claude.
        try {
            using var endContent = new StringContent(endHook.ToJsonString(), Encoding.UTF8, "application/json");
            using var endResp    = await httpClient.PostWithRetryAsync(
                    $"{baseUrl}/hooks/session-end/{session.Vendor.VendorId}", endContent, ct: ct, retryStatuses: true);

            if (endResp.IsSuccessStatusCode) {
                try {
                    var body = await endResp.Content.ReadAsStringAsync(ct);
                    var node = JsonNode.Parse(body);
                    generateWhatsDone = node?["generate_whats_done"]?.GetValue<bool>() == true;
                } catch {
                    /* best effort */
                }
            }
        } catch {
            /* best effort */
        }

        events.OnTitleTaskReady((session.SessionId, session.FilePath, session.PreviousSessionId, session.Vendor));
        events.OnBackgroundWorkReady((session.SessionId, generateWhatsDone, session.Vendor));

        return (SessionImportOutcome.Loaded, importResult.LinesSent);
    }

    sealed class CallbackProgress(Action<ImportProgress> onReport) : IProgress<ImportProgress> {
        public void Report(ImportProgress value) => onReport(value);
    }

    /// <summary>
    /// Probe each transcript against the server's last-line API and classify
    /// what the import phase should do with it. Probes run concurrently via
    /// SemaphoreSlim(8) — idempotent GETs, safe to parallelize.
    /// </summary>
    /// <summary>
    /// Decides whether a classified session should be force-flipped to Excluded
    /// after the prompt loop. Repo and path exclusions are independent gates:
    /// the session is excluded if ANY applicable exclusion key was not opted-in.
    /// Exposed for testing.
    /// </summary>
    internal static bool ShouldExclude(
            SessionClassification c,
            HashSet<string>       includedRepoKeys,
            HashSet<string>       includedPathKeys
        ) {
        if (c.ExcludedRepoKey is { } repoKey && !includedRepoKeys.Contains(repoKey)) return true;
        if (c.ExcludedPathKey is { } pathKey && !includedPathKeys.Contains(pathKey)) return true;

        return false;
    }

    /// <summary>
    /// The visibility to write explicitly over what this run imported, or null to leave each session
    /// on whatever its <c>default_visibility</c> resolved to.
    /// </summary>
    /// <remarks>
    /// Refuses both at once rather than picking: they are opposite promises, and a caller asking for
    /// both has a bug that silently choosing one would hide.
    ///
    /// <para><c>org</c> and not the profile default, because leaning on <c>org_public</c> produces the
    /// <c>default:org</c> class, which the visibility predicate admits only where the repository's
    /// owner matches the tenant's configured org — empty on every provider but <c>GitHubApp</c>. So the
    /// default route promises a team can read this and delivers owner-only on most tenants;
    /// <c>explicit:org</c> is admitted unconditionally.</para>
    /// </remarks>
    internal static string? ExplicitVisibility(bool forcePrivate, bool shareWithOrg) {
        if (forcePrivate && shareWithOrg)
            throw new ArgumentException("An import cannot be both private and shared with the org.", nameof(shareWithOrg));

        return forcePrivate ? "none" : shareWithOrg ? "org" : null;
    }

    /// <summary>PUT visibility=none for every imported session id.</summary>
    internal static Task<IReadOnlyList<string>> SetVisibilityNoneForAll(
            HttpClient            httpClient,
            string                baseUrl,
            IReadOnlyList<string> sessionIds,
            string                indent = ""
        ) => SetVisibilityForAll(httpClient, baseUrl, sessionIds, "none", indent);

    /// <summary>
    /// Failures are logged inline (one line per session) but never throw — the import already
    /// succeeded; users can re-run `kcap hide` or `kcap share` for any that failed. <b>Returns the ids
    /// it could not write</b>, because a caller cannot report the run a success while a session the
    /// user chose a visibility for still carries the old one — and a caller writing BEFORE the content
    /// has to know which sessions it must now leave alone.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> SetVisibilityForAll(
            HttpClient            httpClient,
            string                baseUrl,
            IReadOnlyList<string> sessionIds,
            string                visibility,
            string                indent = ""
        ) {
        var lost = new List<string>();

        foreach (var sessionId in sessionIds) {
            var       payload = new JsonObject { ["visibility"] = visibility };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            try {
                using var resp = await httpClient.PutWithRetryAsync(
                    $"{baseUrl}/api/sessions/{sessionId}/visibility",
                    content,
                    retryStatuses: true
                );

                if (!resp.IsSuccessStatusCode) {
                    lost.Add(sessionId);

                    await Console.Error.WriteLineAsync(
                        $"{indent}! visibility={visibility} failed for {sessionId}: HTTP {(int)resp.StatusCode}"
                    );
                }
            } catch (Exception ex) {
                lost.Add(sessionId);

                await Console.Error.WriteLineAsync(
                    $"{indent}! visibility={visibility} failed for {sessionId}: {ex.Message}"
                );
            }
        }

        return lost;
    }
}
