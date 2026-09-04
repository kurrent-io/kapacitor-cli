using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// Discover + classify + import historical Cursor agent sessions from
/// <c>~/.cursor/projects/&lt;sanitized&gt;/agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl</c>.
/// Each JSONL file is one session in Anthropic content-block format — the same
/// shape the live hook path ships per line via
/// <see cref="CursorTranscriptBackfill"/>, so historical and live import
/// converge on a single canonical-event stream on the server.
///
/// <para>
/// The <c>&lt;sanitized&gt;</c> path segment is Cursor's encoding of the
/// workspace folder (leading slash stripped, remaining separators rewritten
/// as <c>-</c>). The encoding is lossy — folder names that contain <c>-</c>
/// produce ambiguous reversals — so we don't try to invert it. Instead we
/// derive a sanitized-name → real-folder lookup by walking
/// <c>workspaceStorage/&lt;hash&gt;/workspace.json</c> and applying the same
/// forward encoding. Sessions whose <c>&lt;sanitized&gt;</c> doesn't match any
/// known workspace are still imported, just without <c>cwd</c> / git owner+repo
/// — the orchestrator's repo / path exclusion machinery is the only feature
/// that loses fidelity there.
/// </para>
/// </summary>
internal sealed class CursorImportSource : IImportSource {
    readonly string                                      _projectsDir;
    readonly string                                      _workspaceStorageDir;
    readonly Lazy<IReadOnlyDictionary<string, string?>>  _sanitizedToFolder;
    readonly Func<string, Task<RepositoryPayload?>>      _repoDetector;

    // per-cwd cache of the detected repo payload, populated during
    // ImportSessionAsync. Historical import can walk many sessions that share one workspace
    // folder — without this, each session would re-run repo detection for the same cwd. Keyed
    // by the resolved workspace folder (Ordinal — the same string ImportSessionAsync already
    // compares against SourceMeta["WorkspaceFolder"], no case-folding needed for a cache key).
    // Caches the RepositoryPayload rather than the built JsonObject: a JsonNode can only ever
    // have one parent, so reusing the same JsonObject instance across two sessions' payloads
    // throws InvalidOperationException on the second attach — BuildRepositoryNode must be
    // called fresh per session from the cached payload.
    readonly Dictionary<string, RepositoryPayload?> _repoCache = new(StringComparer.Ordinal);
    readonly ConfigRoot                             _config;
    readonly CursorMarkers                          _markers;

    public CursorImportSource(
        ConfigRoot                               config,
        string                                   projectsDir,
        string                                   workspaceStorageDir,
        Func<string, Task<RepositoryPayload?>>?  repoDetector                = null
    ) {
        _config              = config;
        _markers             = new CursorMarkers(config);
        _projectsDir         = projectsDir;
        _workspaceStorageDir = workspaceStorageDir;
        _sanitizedToFolder   = new Lazy<IReadOnlyDictionary<string, string?>>(BuildSanitizedToFolderMap);
        // historical import must never attach a live PR to an old session —
        // stamping today's open PR onto a transcript from weeks ago is an anachronism, and it's
        // exactly the wasted `gh pr view` / `glab api` round-trip per cwd that already
        // skips for the other import sources. detectPullRequest:false still resolves
        // owner/repo/branch (BuildRepositoryNode's non-PR fields), so imported sessions keep
        // grouping under their repo — they just never carry pr_number/pr_title/pr_url/pr_head_ref.
        // The LIVE Cursor hook path (CursorHookCommand → EnrichWithRepositoryInfoFromCwd) is a
        // separate call site untouched by this default and keeps live PR detection.
        _repoDetector        = repoDetector ?? (cwd => RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false));
    }

    /// <summary>
    /// On Windows and macOS the filesystem is case-insensitive; on Linux it
    /// isn't. The workspace.json scan and Cursor's sanitized path segment both
    /// preserve case, but two equally-valid folder paths can differ only in
    /// case on the case-insensitive platforms, so we compare cwd accordingly.
    /// </summary>
    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    // Normalize separators to '/' and drop any trailing slash WITHOUT calling
    // Path.GetFullPath: on Windows GetFullPath rebases a driveless rooted path
    // (e.g. "/Users/me/dev/foo") onto the current drive ("C:\Users\me\dev\foo"),
    // which neither Cursor's sanitized project-dir naming nor a caller-supplied
    // cwd ever carries — breaking the sanitized-key match. Must stay
    // byte-identical to BuildSanitizedToFolderMap's normalization.
    static string NormalizeForComparison(string path) {
        var p = path.Replace('\\', '/').TrimEnd('/');

        return p.Length == 0 ? "/" : p;
    }

    public HarnessId Vendor => HarnessId.Cursor;

    public bool IsAvailable => Directory.Exists(_projectsDir);

    /// <summary>
    /// False — Cursor sessions ship a transcript-derived title via the live
    /// hook path. The historical importer feeds the same transcript route, so
    /// the server's title pipeline handles naming without help from the CLI.
    /// </summary>
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => true; // subagent lifecycle+content on replay

    /// <summary>
    /// Strip dashes from a Cursor session id. The server stores Cursor sessions
    /// under <c>AgentSession-{dashless}</c> streams, so the <c>--session</c>
    /// filter must compare dashless on both sides.
    /// </summary>
    public static string NormalizeCursorSessionId(string id) => id.Replace("-", "");

    /// <summary>
    /// Apply Cursor's workspace-path encoding: strip the leading separator and
    /// replace remaining ones with <c>-</c>. The reverse direction is ambiguous
    /// (folder names can contain <c>-</c>) so we go forward only.
    /// </summary>
    internal static string EncodeWorkspacePath(string folder) {
        var trimmed = folder.TrimStart('/', '\\');
        return trimmed.Replace('/', '-').Replace('\\', '-');
    }

    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        if (!Directory.Exists(_projectsDir))
            return Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);

        var sessionFilter = filters.FilterSession is { } sf ? NormalizeCursorSessionId(sf) : null;
        var normalizedCwd = filters.FilterCwd is { } cwd ? NormalizeForComparison(cwd) : null;
        var sinceUtc      = filters.Since is { } since
            ? new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var sanitizedMap = _sanitizedToFolder.Value;
        var result       = new List<DiscoveredSession>();

        foreach (var sanitizedDir in Directory.EnumerateDirectories(_projectsDir)) {
            try {
                var sanitized      = Path.GetFileName(sanitizedDir);
                var transcriptsDir = Path.Combine(sanitizedDir, "agent-transcripts");

                if (!Directory.Exists(transcriptsDir)) continue;

                // sanitizedMap value is null when the sanitized key collided with
                // multiple distinct workspace folders (lossy encoding) — treat as
                // unknown rather than misattributing to one of them.
                sanitizedMap.TryGetValue(sanitized, out var workspaceFolder);

                if (normalizedCwd is not null
                 && (workspaceFolder is null
                  || !workspaceFolder.Equals(normalizedCwd, PathComparison))) {
                    continue;
                }

                foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir)) {
                    try {
                        var sessionDirName = Path.GetFileName(sessionDir);
                        var jsonl          = Path.Combine(sessionDir, sessionDirName + ".jsonl");

                        if (!File.Exists(jsonl)) continue;

                        var dashless = NormalizeCursorSessionId(sessionDirName);

                        if (sessionFilter is not null && !string.Equals(dashless, sessionFilter, StringComparison.Ordinal))
                            continue;

                        // Use the JSONL file's creation time as the session-start
                        // proxy. Cursor's transcript JSONL lines carry no timestamp
                        // field (Anthropic content-block format without metadata),
                        // and the file is created when the session starts and
                        // appended throughout — so birth-time on supported
                        // filesystems (APFS, NTFS) is the closest proxy.
                        //
                        // `--since` MUST gate on session-start, not last-modified,
                        // or any old session appended to after the cutoff would be
                        // re-imported.
                        //
                        // Linux note: .NET's File.GetCreationTimeUtc on Linux ext4
                        // falls back to mtime when btime isn't queryable. On those
                        // hosts started_at == ended_at and `--since` effectively
                        // gates on last-write. Acceptable degradation — most Cursor
                        // users are on macOS/Windows and the production behavior is
                        // still strictly better than not having `--since` at all.
                        DateTimeOffset? firstTimestamp = null;
                        try {
                            firstTimestamp = File.GetCreationTimeUtc(jsonl);
                        } catch {
                            // Best effort.
                        }

                        if (sinceUtc is { } cutoff && firstTimestamp is { } ts && ts < cutoff) {
                            continue;
                        }

                        result.Add(new DiscoveredSession(
                            SessionId:      dashless,
                            Vendor:         Vendor,
                            Cwd:            workspaceFolder,
                            FirstTimestamp: firstTimestamp,
                            SourceMeta:     new Dictionary<string, object?> {
                                ["TranscriptPath"]  = jsonl,
                                ["WorkspaceFolder"] = workspaceFolder,
                                ["SanitizedDir"]    = sanitized,
                            }));
                    } catch {
                        // A hostile/inaccessible session subtree must not abort the whole scan.
                        continue;
                    }
                }
            } catch {
                // A hostile/inaccessible workspace subtree must not abort the whole scan.
                continue;
            }

            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult<IReadOnlyList<DiscoveredSession>>(result);
    }

    public async Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
            IReadOnlyList<DiscoveredSession> sessions,
            ClassifyContext                   ctx,
            CancellationToken                 ct
        ) {
        var results = new List<ImportCommand.SessionClassification>(sessions.Count);

        // Per-workspace repo cache so we only run RepositoryDetection once per
        // unique cwd in this Classify call — sessions cluster heavily inside
        // the same workspace folder.
        var repoCache    = new Dictionary<string, string?>(StringComparer.Ordinal); // cwd → "owner/repo" or null
        var hasExcludes  = ctx.ExcludedRepos is { Count: > 0 };

        // correlate subagent (child) sessions to their parent by prompt-hash across
        // all discovered transcripts. A child is ingested under the parent's AgentSubsession
        // stream *by the parent's own import* (subagent-start → transcript-with-agent_id →
        // subagent-stop, before the parent's session-end) rather than as a separate top-level
        // session. Doing it inside the parent import guarantees the parent's SessionEnded stays
        // the last event on its stream — a subagent-start posted after a parallel parent's
        // session-end would reactivate the ended parent (SubagentStarted is a reactivation event).
        //
        // the correlator's INPUT is widened to every session under the SAME
        // workspace's agent-transcripts/ dir — ignoring --session/--cwd/--since/scope for this
        // internal step (cheap local file reads only). Without this, a `--session <child>`
        // (or --cwd/--since-narrowed) import only ever sees the child in `sessions`, so it can
        // never discover the parent's Task prompt and the child would wrongly import top-level.
        // Repo detection below still runs ONLY on the filtered `sessions` slice — this widening
        // is purely a same-workspace file scan, no git/network work.
        // Qodo fix: same-workspace discovery below is only meant to ensure a filtered/scoped
        // import can still SEE a parent for visibility purposes — it must also CONSTRAIN
        // correlation. Correlating across the union of every workspace touched by this classify
        // call let a child in workspace A link to a parent in workspace B whenever their
        // canonical prompt hashes happened to match, corrupting nesting/attribution. Each
        // workspace gets its own path map and its own Correlate() call; the resulting links are
        // merged (session ids are unique across workspaces, so no collisions).
        var pathBySession      = new Dictionary<string, string>(StringComparer.Ordinal); // flat, for child-path lookups only
        // Keyed on the parts, not a joined string: (dir, null) for a real workspace and (null, id)
        // for a synthetic bucket cannot collide, whatever a directory happens to be called.
        var pathsByWorkspace   = new Dictionary<(string? SanitizedDir, string? SessionId), Dictionary<string, string>>();

        Dictionary<string, string> WorkspaceMap((string? SanitizedDir, string? SessionId) key) {
            if (!pathsByWorkspace.TryGetValue(key, out var map))
                pathsByWorkspace[key] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            return map;
        }

        var sanitizedDirs = sessions
            .Select(s => s.SourceMeta!.TryGetValue("SanitizedDir", out var sd) ? sd as string : null)
            .Where(sd => sd is not null)
            .Select(sd => sd!)
            .Distinct(StringComparer.Ordinal);

        foreach (var sanitizedDir in sanitizedDirs) {
            var workspaceMap = WorkspaceMap((sanitizedDir, null));
            foreach (var (sid, path) in DiscoverSameWorkspaceSessionPaths(sanitizedDir, ct)) {
                workspaceMap[sid] = path;
                pathBySession[sid] = path;
            }
        }

        // Ensure every session actually being classified in this run is present even if its own
        // workspace directory couldn't be (re-)walked above (e.g. injected SourceMeta without a
        // SanitizedDir in a test double) — this run's own slice must never be lost. Keyed by the
        // session's own SanitizedDir so correlation stays workspace-scoped; a session with no
        // SanitizedDir gets an isolated per-session bucket since its true peer set is unknown.
        foreach (var s in sessions) {
            if (!s.SourceMeta!.TryGetValue("TranscriptPath", out var tp) || tp is not string p || p.Length == 0)
                continue;

            (string? SanitizedDir, string? SessionId) workspaceKey =
                s.SourceMeta!.TryGetValue("SanitizedDir", out var sd) && sd is string sdStr
                    ? (sdStr, null)
                    : (null, s.SessionId);

            WorkspaceMap(workspaceKey)[s.SessionId] = p;
            pathBySession[s.SessionId] = p;
        }

        var subagentLinks = new Dictionary<string, CursorSubagentCorrelator.SubagentLink>(StringComparer.Ordinal);
        foreach (var workspaceMap in pathsByWorkspace.Values)
            foreach (var (childId, lnk) in CursorSubagentCorrelator.Correlate(workspaceMap.Select(kv => (kv.Key, kv.Value))))
                subagentLinks[childId] = lnk;
        var childrenByParent = new Dictionary<string, List<CursorSubagentChild>>(StringComparer.Ordinal);
        foreach (var (childId, lnk) in subagentLinks) {
            if (!childrenByParent.TryGetValue(lnk.ParentSessionId, out var kids)) {
                kids = [];
                childrenByParent[lnk.ParentSessionId] = kids;
            }
            kids.Add(new CursorSubagentChild(childId, pathBySession.GetValueOrDefault(childId, ""), lnk.SubagentType));
        }

        foreach (var s in sessions) {
            var transcriptPath = (string)s.SourceMeta!["TranscriptPath"]!;

            var meta = new SessionMetadata {
                SessionId      = s.SessionId,
                Cwd            = s.Cwd,
                FirstTimestamp = s.FirstTimestamp,
            };

            // A session already quarantined by the live watcher's runtime rewrite guard must never
            // be fed back through `kcap import` either: that's exactly the corrupted line-number
            // source D0's quarantine exists to shut off. Quarantine is always keyed on the FAMILY
            // identity — the top-level (parent) session id — since CursorRewriteGuard is
            // constructed from the watcher process's own `sessionId` argument, which for a
            // spawned CHILD watcher is the parent id (WatcherManager.BuildSpawnArgs:
            // sessionIdOverride ?? key). ResolveQuarantineIdentity resolves that mapping — see its
            // doc for round-2 review fix #7's fallback when `--session <child>` (or an
            // inaccessible/omitted parent transcript) filters the parent out of `subagentLinks`
            // entirely.
            var quarantineIdentity = ResolveQuarantineIdentity(_config, s.SessionId, subagentLinks);

            if (_markers.IsQuarantined(quarantineIdentity)) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "cursor session quarantined (transcript rewrite detected) — not imported"));
                continue;
            }

            int? lastNonBlankIndex;
            int  nonBlankCount;
            try {
                (lastNonBlankIndex, nonBlankCount) = await ReadTranscriptStatsAsync(transcriptPath, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "transcript read failed"));
                continue;
            }

            if (lastNonBlankIndex is null) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: 0,
                                               probeErrorReason: "empty transcript"));
                continue;
            }

            if (nonBlankCount < ctx.MinLines) {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.TooShort, totalLines: nonBlankCount));
                continue;
            }

            int? serverLastLine;
            try {
                serverLastLine = await FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, s.SessionId, ct);
            } catch {
                results.Add(MakeClassification(s, meta, ImportCommand.ClassificationStatus.ProbeError, totalLines: nonBlankCount,
                                               probeErrorReason: "watermark probe failed"));
                continue;
            }

            // Ended-at contract: unlike Copilot (session.shutdown record) or
            // Gemini/Pi (per-record "timestamp" field), Cursor's Anthropic content-block
            // transcript carries no authoritative session-end record to tail-scan. The
            // fallback is: the last parsed user-wrapper timestamp when the normalizer
            // surfaced one (live-hook path), else this file's last-write time. Do NOT
            // treat a tail-scan of this transcript as authoritative — there is no
            // per-line timestamp field here the way there is for Gemini/Pi.
            try {
                meta.LastTimestamp = File.GetLastWriteTimeUtc(transcriptPath);
            } catch {
                // Best effort.
            }

            string? repoKey = null;
            if (hasExcludes && s.Cwd is { } cwd) {
                if (!repoCache.TryGetValue(cwd, out repoKey)) {
                    try {
                        var repo = await _repoDetector(cwd);
                        repoKey = repo is { Owner: { } o, RepoName: { } n } ? $"{o}/{n}" : null;
                    } catch {
                        repoKey = null;
                    }
                    repoCache[cwd] = repoKey;
                }
            }

            var (excludedRepoKey, excludedPathKey) = ResolveExclusions(s.Cwd, repoKey, ctx);

            var status       = ImportCommand.ClassificationStatus.New;
            var resumeFromLn = 0;

            if (serverLastLine is { } srv) {
                if (srv >= lastNonBlankIndex.Value) {
                    status = ImportCommand.ClassificationStatus.AlreadyLoaded;
                } else {
                    status       = ImportCommand.ClassificationStatus.Partial;
                    resumeFromLn = srv + 1;
                }
            }

            results.Add(new ImportCommand.SessionClassification {
                SessionId       = s.SessionId,
                // FilePath stays empty: ImportCommand routes sessions with an
                // empty FilePath through the routed phase (ImportSessionAsync),
                // and sessions with a populated FilePath through the chain
                // worker — which assumes Claude/Codex-shaped lifecycle hooks
                // (/hooks/session-start without a vendor suffix, defaulting to
                // vendor=claude on the server). Cursor needs the routed phase
                // so the transcript posts via /hooks/transcript with
                // vendor:"cursor". The transcript path lives in SourceMeta.
                FilePath        = "",
                EncodedCwd      = "",
                Meta            = meta,
                Status          = status,
                Vendor          = Vendor,
                ResumeFromLine  = resumeFromLn,
                ExcludedRepoKey = excludedRepoKey,
                ExcludedPathKey = excludedPathKey,
                TotalLines      = nonBlankCount,
                SourceMeta      = StampSubagentMeta(s.SourceMeta!, s.SessionId, quarantineIdentity, subagentLinks, childrenByParent),
            });
        }

        return results;
    }

    public async Task<ImportSessionResult> ImportSessionAsync(
            ImportCommand.SessionClassification classification,
            ImportContext                       ctx,
            CancellationToken                   ct
        ) {
        // a correlated subagent child is imported by its parent (below), under the
        // parent's AgentSubsession stream — never as a standalone top-level session.
        //
        // this flag is no longer unconditional. ImportCommand reconciles `routed`
        // right after building it — an ORPHANED child (its correlated parent isn't itself part
        // of this run's plan) has IsSubagentChild/ParentSessionId cleared before reaching here,
        // so this check only still fires for a child whose parent IS about to run its own
        // ImportSessionAsync (and will import this child inline, below in that parent's call).
        // An orphan instead falls through to the ordinary standalone start→transcript→end path.
        if (classification.SourceMeta!.TryGetValue("IsSubagentChild", out var scObj) && scObj is true) {
            return ImportOutcome.Skipped;
        }

        // re-check quarantine FRESH, before ANY lifecycle/transcript
        // delivery. ClassifyAsync's own check (at classification time, above in this file) can be
        // stale by the time this runs: repo probing, an interactive confirmation prompt, or simply
        // queueing behind other sessions in the same import run all give the live watcher's
        // runtime rewrite guard time to trip and write the quarantine marker AFTER this session
        // was already classified clean. QuarantineIdentity (the family/parent id) was resolved
        // once at classify time via ResolveQuarantineIdentity and is stable for the run — only the
        // quarantine STATE needs a fresh disk read here.
        var quarantineIdentity = classification.SourceMeta!.TryGetValue("QuarantineIdentity", out var qiObj) && qiObj is string qi
            ? qi
            : classification.SessionId;

        if (_markers.IsQuarantined(quarantineIdentity)) {
            return ImportOutcome.Skipped;
        }

        var transcriptPath = (string)classification.SourceMeta!["TranscriptPath"]!;

        if (!File.Exists(transcriptPath)) return ImportOutcome.Failed;

        var workspaceFolder = classification.SourceMeta!.TryGetValue("WorkspaceFolder", out var wfObj)
            ? wfObj as string
            : null;

        var (createdUtc, modifiedUtc) = TryGetTranscriptTimes(transcriptPath);

        // detect the repo from the workspace folder so the synthetic
        // sessionStart carries a `repository` node → server emits RepositoryDetected
        // and the (historical/backfilled) session groups under its repo. Fail-open:
        // a non-git workspace or detection error leaves it null and unattributed.
        //
        // cached per cwd — a historical import walks every session under a
        // workspace, and many share one cwd, so this only runs detection once per distinct
        // folder for the whole import rather than once per session.
        JsonObject? repositoryNode = null;
        if (workspaceFolder is not null) {
            if (!_repoCache.TryGetValue(workspaceFolder, out var repo)) {
                try {
                    repo = await _repoDetector(workspaceFolder);
                } catch {
                    repo = null;
                }
                _repoCache[workspaceFolder] = repo;
            }
            if (repo is not null) repositoryNode = RepositoryDetection.BuildRepositoryNode(repo);
        }

        // sessionStart MUST succeed before transcript advances the server
        // watermark — otherwise a transient lifecycle failure plus a successful
        // transcript would leave the session permanently lifecycle-less
        // (next run sees AlreadyLoaded and never re-emits). Treat lifecycle
        // POST failure as a hard import failure; the orchestrator surfaces
        // Errored and the user re-runs, which is idempotent on the server
        // (canonical event ids are deterministic).
        var startPayload = BuildSessionStartPayload(classification.SessionId, workspaceFolder, transcriptPath, createdUtc, repositoryNode);
        // The only Cursor path that stamps one: its live hook sets no default_visibility at all.
        if (ctx.VisibilityStampFor(classification.Status) is { } visibility) {
            startPayload["default_visibility"] = visibility;
        }

        var startOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-start/cursor",
            startPayload,
            ct);
        if (!startOk) return ImportOutcome.Failed;

        // Partial → resume from server watermark + 1. AlreadyLoaded → nothing
        // past the watermark, so SendTranscriptBatches returns 0 immediately.
        // The routed-phase filter at ImportCommand.cs:749 now includes
        // AlreadyLoaded for Cursor so legacy lifecycle-less sessions get
        // re-synced; the transcript leg becomes a no-op in that case.
        var startLine = classification.Status switch {
            ImportCommand.ClassificationStatus.Partial       => classification.ResumeFromLine,
            ImportCommand.ClassificationStatus.AlreadyLoaded => classification.TotalLines,
            _                                                => 0,
        };

        // re-check again at the transcript boundary: the sessionStart POST
        // that just landed gave the runtime guard another window to trip. Unlike the pre-flight
        // check above (nothing posted yet, so Skipped there is exactly right), the session now
        // legitimately exists server-side — best-effort close it with session-end so it doesn't
        // hang open "active" forever, but send NO transcript content (skip the children too — the
        // same corrupted-source concern applies to them) and surface Failed so a re-run is
        // attempted, which will hit the pre-flight check above and cleanly Skip from then on.
        if (_markers.IsQuarantined(quarantineIdentity)) {
            var abortDurationMs = createdUtc is { } ac && modifiedUtc is { } am && am >= ac
                ? (long?)(am - ac).TotalMilliseconds
                : null;
            await PostSyntheticHookAsync(
                ctx.HttpClient, ctx.BaseUrl, "session-end/cursor",
                BuildSessionEndPayload(classification.SessionId, transcriptPath, abortDurationMs, modifiedUtc),
                ct);
            return ImportOutcome.Failed;
        }

        // the best-effort close-and-fail contract shared by
        // EVERY quarantine-abort seam below (the parent's own mid-transcript trip AND a child's):
        // best-effort session-end so the session doesn't hang open "active" forever (subagent-start
        // may already have posted for a child — this closes the parent/subsession the SAME way
        // regardless of which delivery aborted), and Failed so a re-run hits the pre-flight check
        // above and cleanly Skips from then on. Factored out so the child loop's catch (below) can
        // share it instead of duplicating the parent-batch catch's logic.
        async Task<ImportOutcome> CloseAndFailAsync() {
            var abortDurationMs = createdUtc is { } midAc && modifiedUtc is { } midAm && midAm >= midAc
                ? (long?)(midAm - midAc).TotalMilliseconds
                : null;
            await PostSyntheticHookAsync(
                ctx.HttpClient, ctx.BaseUrl, "session-end/cursor",
                BuildSessionEndPayload(classification.SessionId, transcriptPath, abortDurationMs, modifiedUtc),
                ct);
            return ImportOutcome.Failed;
        }

        int sent;
        try {
            // abortDelivery re-checks the ALREADY-resolved
            // quarantineIdentity (a cheap marker-file read, no correlator re-run) before every
            // 100-line batch. Without this, a quarantine written by the live watcher's runtime
            // rewrite guard between batch 1 and batch 2 (or later) still let every remaining batch
            // post — this closes that window by aborting delivery mid-flight.
            sent = await SessionImporter.SendTranscriptBatches(
                httpClient:    ctx.HttpClient,
                baseUrl:       ctx.BaseUrl,
                sessionId:     classification.SessionId,
                filePath:      transcriptPath,
                agentId:       null,
                startLine:     startLine,
                vendor:        Vendor,
                abortDelivery: () => _markers.IsQuarantined(quarantineIdentity));
        } catch (SessionImporter.TranscriptDeliveryAbortedException) {
            // Quarantine tripped mid-delivery — no children/remaining batches (we return before
            // reaching them).
            return await CloseAndFailAsync();
        } catch {
            return ImportOutcome.Failed;
        }

        // import this parent's subagent children BEFORE its session-end, so the
        // parent's SessionEnded remains the last event on its stream. A subagent-start
        // (reactivation event) appended after the parent's SessionEnded would flip the
        // ended parent back to Active. Hard-fail on child failure (same contract as the
        // parent lifecycle) so a re-run — idempotent via deterministic ids — repairs it.
        //
        // track whether any child ACTUALLY sent new transcript bytes,
        // independent of the parent's own `sent`/`startLine`. An AlreadyLoaded parent (nothing
        // past its own watermark) can still attach a previously-unloaded child here — that's
        // real new work, and ImportCommand's IsLifecycleOnlyRoutedReplay must not suppress it.
        //
        // a child-transcript quarantine trip is surfaced as
        // the SAME typed TranscriptDeliveryAbortedException the parent's own delivery throws (see
        // SendSubagentLifecycleAsync below); catching it here routes it through CloseAndFailAsync
        // instead of letting SendSubagentLifecycleAsync's old catch-all swallow it into a bare
        // `false` — which returned Failed WITHOUT ever posting the parent's best-effort session-end,
        // even though this child's subagent-start had already landed (leaving the parent/subsession
        // stuck Active forever, since the quarantine marker makes the NEXT run Skip at preflight
        // rather than repair it).
        var sentChildContent = false;
        if (classification.SourceMeta!.TryGetValue("SubagentChildren", out var kidsObj)
         && kidsObj is List<CursorSubagentChild> children) {
            try {
                foreach (var child in children) {
                    var (childOk, childSent) = await SendSubagentLifecycleAsync(classification.SessionId, child, ctx, quarantineIdentity, ct);
                    if (!childOk) return ImportOutcome.Failed;
                    sentChildContent |= childSent;
                }
            } catch (SessionImporter.TranscriptDeliveryAbortedException) {
                return await CloseAndFailAsync();
            }
        }

        // sessionEnd: same hard-fail contract — if it can't be appended, the
        // session would otherwise look perpetually "active" in the read-model.
        var durationMs = createdUtc is { } c && modifiedUtc is { } m && m >= c
            ? (long?)(m - c).TotalMilliseconds
            : null;
        var endOk = await PostSyntheticHookAsync(
            ctx.HttpClient, ctx.BaseUrl, "session-end/cursor",
            BuildSessionEndPayload(classification.SessionId, transcriptPath, durationMs, modifiedUtc),
            ct);
        if (!endOk) return ImportOutcome.Failed;

        if (sent == 0) {
            var noOwnContentOutcome = startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Skipped;
            return new ImportSessionResult(noOwnContentOutcome, SentChildContent: sentChildContent);
        }

        var outcome = startLine > 0 ? ImportOutcome.Resumed : ImportOutcome.Loaded;
        return new ImportSessionResult(outcome, SentChildContent: sentChildContent);
    }

    /// <summary>A Cursor subagent child correlated to a parent: its own session id (used as the
    /// AgentSubsession agent id), its transcript path, and the parent's declared subagent_type.</summary>
    internal sealed record CursorSubagentChild(string SessionId, string TranscriptPath, string? SubagentType);

    /// <summary>
    /// Stamps subagent correlation onto a session's SourceMeta (SourceMeta is read-only, so every
    /// session gets a fresh copy). Children carry <c>IsSubagentChild</c> + <c>ParentSessionId</c>
    /// (D5 — the parent id lets <see cref="ImportCommand"/> reconcile an orphaned child
    /// to standalone when the parent isn't itself part of this run's plan) so their own import
    /// no-ops (unless reconciled); parents carry <c>SubagentChildren</c> so they import them inline.
    /// Every classification also carries <c>QuarantineIdentity</c> — review fix #6 — so
    /// <see cref="ImportSessionAsync"/> can re-check <see cref="CursorMarkers.IsQuarantined"/>
    /// FRESH immediately before any lifecycle/transcript delivery (the family identity itself is
    /// stable for the run; only the quarantine STATE needs a live re-check, since the live
    /// watcher's runtime rewrite guard can trip at any moment between classification and import).
    /// </summary>
    static IReadOnlyDictionary<string, object?> StampSubagentMeta(
        IReadOnlyDictionary<string, object?>                       src,
        string                                                     sessionId,
        string                                                     quarantineIdentity,
        Dictionary<string, CursorSubagentCorrelator.SubagentLink>  links,
        Dictionary<string, List<CursorSubagentChild>>              childrenByParent
    ) {
        var d = new Dictionary<string, object?>(src) { ["QuarantineIdentity"] = quarantineIdentity };
        if (links.TryGetValue(sessionId, out var link)) {
            d["IsSubagentChild"] = true;
            d["ParentSessionId"] = link.ParentSessionId;
        }
        if (childrenByParent.TryGetValue(sessionId, out var kids)) d["SubagentChildren"] = kids;
        return d;
    }

    /// <summary>
    /// Every session id → transcript path found directly under ONE workspace's
    /// <c>agent-transcripts/</c> directory — no <c>--session</c>/<c>--cwd</c>/<c>--since</c>/scope
    /// filtering, cheap local file reads only. Widens the subagent correlator's input so a
    /// filtered/scoped import still sees a session's parent/siblings for correlation even when
    /// they fall outside this run's classify slice. Mirrors the per-workspace walk in
    /// <see cref="DiscoverAsync"/> minus the filters.
    /// </summary>
    IReadOnlyDictionary<string, string> DiscoverSameWorkspaceSessionPaths(string sanitizedDir, CancellationToken ct) {
        var map            = new Dictionary<string, string>(StringComparer.Ordinal);
        var transcriptsDir = Path.Combine(_projectsDir, sanitizedDir, "agent-transcripts");

        if (!Directory.Exists(transcriptsDir)) return map;

        try {
            foreach (var sessionDir in Directory.EnumerateDirectories(transcriptsDir)) {
                ct.ThrowIfCancellationRequested();
                try {
                    var sessionDirName = Path.GetFileName(sessionDir);
                    var jsonl          = Path.Combine(sessionDir, sessionDirName + ".jsonl");

                    if (!File.Exists(jsonl)) continue;

                    map[NormalizeCursorSessionId(sessionDirName)] = jsonl;
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    // A hostile/inaccessible session subtree must not abort the whole scan.
                    continue;
                }
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // A hostile/inaccessible workspace subtree must not abort the whole scan.
        }

        return map;
    }

    /// <summary>
    /// resolves the FAMILY (quarantine) identity for <paramref name="sessionId"/>:
    /// its correlated parent's id when <paramref name="subagentLinks"/> (computed from THIS batch's
    /// discovered sessions) has a link, falling back to the persisted
    /// <see cref="CursorLiveSubagentLinker"/> marker — written independently by the LIVE hook
    /// dispatcher at the child's own <c>sessionStart</c>, so it resolves the same parent even when
    /// a <c>--session &lt;child&gt;</c> filter (or an inaccessible/omitted parent transcript)
    /// excludes the parent from this batch entirely and the in-batch correlator has nothing to
    /// correlate against. Falls back to the session's own id when neither source has a link
    /// (a genuine top-level session, or one never seen live and whose parent transcript isn't in
    /// this batch either — an inherent limitation the marker fallback can't close).
    /// </summary>
    internal static string ResolveQuarantineIdentity(
        ConfigRoot                                                 config,
        string                                                     sessionId,
        IReadOnlyDictionary<string, CursorSubagentCorrelator.SubagentLink> subagentLinks
    ) {
        if (subagentLinks.TryGetValue(sessionId, out var ownLink)) return ownLink.ParentSessionId;

        return CursorLiveSubagentLinker.TryLoadLink(config, sessionId) is { } marker
            ? marker.ParentSessionId
            : sessionId;
    }

    /// <summary>
    /// ingest one Cursor subagent child under its parent's <c>AgentSubsession</c>
    /// stream. Mirrors <c>SessionImporter.SendAgentLifecycle</c>: <c>subagent-start</c>
    /// (session_id=parent, agent_id=child) → transcript batches routed with <c>agent_id</c>=child
    /// (resumed from the subsession watermark so re-imports don't repost the whole child) →
    /// <c>subagent-stop</c>. Called from the parent's <see cref="ImportSessionAsync"/> BEFORE the
    /// parent's session-end. Returns <c>(false, _)</c> on a hard failure (start/transcript/stop
    /// POST failed); a missing child transcript is skipped non-fatally (<c>(true, false)</c>). No
    /// standalone session lifecycle is emitted for the child, so it never becomes a top-level
    /// session card.
    ///
    /// <para>
    /// also reports whether this call actually POSTed new transcript
    /// bytes for the child (<c>SentContent</c>) — the caller needs that to know real new work
    /// happened even when the PARENT's own <c>sent</c> count is zero (e.g. an AlreadyLoaded
    /// parent attaching a previously-unloaded child).
    /// </para>
    ///
    /// <para>
    /// a fail-open resend — the child subsession watermark
    /// probe itself threw, so <c>startLine</c> resets to 0 and the whole child is reposted — is
    /// INDETERMINATE, not proof of "no new content". The round-3 fix treated a probe failure as
    /// "definitely a duplicate resend" and forced <c>SentContent = false</c>; but when the child
    /// is genuinely NEW and the watermark endpoint merely 500s transiently, that full resend
    /// really does POST new content — yet the caller was told nothing new happened. For an
    /// AlreadyLoaded parent, that meant a newly-attached child's content could stay on a public
    /// parent under <c>--private</c>: a privacy leak. So a probe failure must NOT be treated as
    /// "no new content"; it's indeterminate, and the safe default is to treat a posted resend as
    /// content that MAY be new (<c>SentContent = true</c>), same as the probe-succeeded path.
    /// This can cause a cosmetic double-count for an already-complete child that gets
    /// fail-open-resent (its AlreadyLoaded parent counted in both Loaded and AlreadyLoaded) —
    /// that's a known, separately-tracked follow-up (deferred P2); privacy correctness wins over
    /// count precision.
    /// </para>
    /// </summary>
    async Task<(bool Success, bool SentContent)> SendSubagentLifecycleAsync(
            string            parentSessionId,
            CursorSubagentChild child,
            ImportContext     ctx,
            string            quarantineIdentity,
            CancellationToken ct
        ) {
        if (string.IsNullOrEmpty(child.TranscriptPath) || !File.Exists(child.TranscriptPath))
            return (true, false); // missing child transcript — skip, non-fatal

        // never start a NEW child under a family that is
        // ALREADY quarantined by the time its turn comes up in the parent's loop. Without this, a
        // quarantine tripped by some earlier child's own delivery (or any other concurrent trip —
        // the live watcher runs alongside this import) still let every LATER child's subagent-start
        // post, since nothing here re-checked the marker before that first POST. Throwing the same
        // typed exception the transcript-delivery abort below throws lets the caller's loop (in
        // ImportSessionAsync) route this through the identical best-effort close-and-fail contract.
        if (_markers.IsQuarantined(quarantineIdentity)) {
            throw new SessionImporter.TranscriptDeliveryAbortedException();
        }

        var agentId      = child.SessionId; // the child session id doubles as the subagent id
        var subagentType = string.IsNullOrEmpty(child.SubagentType) ? "task" : child.SubagentType!;

        // subagent-start on the parent stream (creates AgentSubsession-{parent}-{child}).
        var startOk = await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-start", new JsonObject {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = subagentType,
            ["transcript_path"] = child.TranscriptPath, // required by HookBase
            ["cwd"]             = "",                    // required by HookBase
            ["strict"]          = true,                  // fail-closed: 500 if SubagentStarted isn't persisted
        }, ct);
        if (!startOk) return (false, false);

        // Resume from the subsession watermark (AgentSubsession-{parent}-{child}) so a re-import
        // doesn't repost the full child transcript every time. Fail-open to a full send.
        //
        // a fail-open resend (the probe itself threw — e.g. a
        // transient 5xx) resets startLine to 0 and reposts the WHOLE child. That resend is
        // INDETERMINATE — it MAY be a duplicate of an already-complete child, or it MAY be
        // genuinely new content that a transient probe failure prevented us from resuming
        // correctly. Treating probe failure as proof of "no new content" (the round-3 fix) is a
        // privacy regression: a real new child attached to an AlreadyLoaded parent would then be
        // reported as no content sent, the parent would be excluded from `--private`, and its
        // new child content would leak on a public session. So probe failure alone is no longer
        // tracked as a reason to suppress SentContent below — the safe default when content was
        // actually posted is to count it, whether or not the watermark was known.
        //
        // Before falling open, give a status-bearing 5xx/408 one extra local re-attempt (2
        // attempts total, fixed, no added backoff) via FetchServerLastLineWithCappedRetryAsync —
        // a single transient 500 no longer skips straight to fail-open on the first failure.
        // Statusless transport failures (already retried inside GetWithRetryAsync) and 429 (a
        // rate-limit signal) are excluded from this outer retry and still fail open on the
        // first attempt. Cancellation is never treated as a transient failure — it propagates
        // immediately.
        var startLine = 0;
        try {
            if (await FetchServerLastLineWithCappedRetryAsync(
                    probeCt => FetchServerLastLineAsync(ctx.HttpClient, ctx.BaseUrl, parentSessionId, probeCt, agentId), ct
                ) is { } last)
                startLine = last + 1;
        } catch (OperationCanceledException) {
            throw;
        } catch {
            startLine = 0;
        }

        int childSent;
        try {
            // failOnError: fail-closed like the parent lifecycle — a rejected/failed child
            // transcript POST must abort so the parent import fails and a re-run repairs it,
            // rather than leaving an empty completed subagent while reporting success.
            //
            // abortDelivery closes over the SAME
            // already-resolved quarantineIdentity as the parent's own send (no extra correlator
            // work), so a quarantine tripping mid-child-transcript also aborts the remaining
            // child batches, not just the parent's.
            childSent = await SessionImporter.SendTranscriptBatches(
                httpClient:    ctx.HttpClient,
                baseUrl:       ctx.BaseUrl,
                sessionId:     parentSessionId,
                filePath:      child.TranscriptPath,
                agentId:       agentId,
                startLine:     startLine,
                vendor:        Vendor,
                failOnError:   true,
                abortDelivery: () => _markers.IsQuarantined(quarantineIdentity));
        } catch (SessionImporter.TranscriptDeliveryAbortedException) {
            // a quarantine trip during THIS child's own
            // transcript delivery must propagate to the caller's close-and-fail path, not collapse
            // into the same bare `false` an ordinary POST failure returns below. A `false` here
            // returned Failed from ImportSessionAsync WITHOUT ever posting the parent's best-effort
            // session-end, even though this child's subagent-start had already landed — leaving the
            // parent/subsession stuck Active forever (the quarantine marker makes the next run Skip
            // at preflight instead of repairing it).
            throw;
        } catch {
            return (false, false);
        }

        // Full SubagentStopHook shape (mirrors the Gemini/OpenCode builders) — the server
        // binds all fields, so an incomplete body can be rejected before HandleSubagentStop.
        var stopOk = await PostSyntheticHookAsync(ctx.HttpClient, ctx.BaseUrl, "subagent-stop", new JsonObject {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = subagentType,
            ["transcript_path"]        = child.TranscriptPath, // required by HookBase
            ["cwd"]                    = "",                   // required by HookBase
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = child.TranscriptPath,
            ["last_assistant_message"] = "",
            ["strict"]                 = true,                 // fail-closed: 500 if SubagentCompleted isn't persisted
        }, ct);

        // Privacy-safe by construction: whether the probe succeeded (known watermark) or failed
        // (fail-open full resend), any lines actually POSTED count as SentContent. A probe
        // failure is indeterminate, never a "definitely no new content" verdict — see the
        // fail-open comment above. Known trade-off: an already-complete child that gets
        // fail-open-resent will also report SentContent=true, which can cosmetically double-count
        // its AlreadyLoaded parent (Loaded + AlreadyLoaded buckets) — deferred, separately
        // tracked follow-up; privacy correctness wins over count precision.
        return (stopOk, stopOk && childSent > 0);
    }

    internal static JsonObject BuildSessionStartPayload(
        string sessionId, string? workspaceFolder, string transcriptPath, DateTimeOffset? startedAt,
        JsonObject? repository = null
    ) {
        var payload = new JsonObject {
            ["hook_event_name"]     = "sessionStart",
            ["session_id"]          = sessionId,
            ["transcript_path"]     = transcriptPath,
            ["is_background_agent"] = false,
        };
        if (workspaceFolder is not null) {
            payload["workspace_roots"] = new JsonArray(workspaceFolder);
        }
        // git-detected repo (owner/repo/branch/...) so the server emits
        // RepositoryDetected for historical/backfilled Cursor sessions.
        if (repository is not null) {
            payload["repository"] = repository;
        }
        // server prefers started_at over UtcNow when present, so
        // historical sessions surface with their real start time. Use an
        // ISO-8601 round-trip ("O") string — DateTimeOffset? on the server
        // record deserialises that shape directly.
        if (startedAt is { } ts) {
            payload["started_at"] = ts.ToString("O");
        }
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static JsonObject BuildSessionEndPayload(
        string sessionId, string transcriptPath, long? durationMs, DateTimeOffset? endedAt
    ) {
        var payload = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = "historical-import",
            ["transcript_path"] = transcriptPath,
        };
        if (durationMs is { } d) {
            payload["duration_ms"] = d;
        }
        if (endedAt is { } ts) {
            payload["ended_at"] = ts.ToString("O");
        }
        payload["origin"] = ImportOrigins.Historical;
        return payload;
    }

    static (DateTimeOffset? Created, DateTimeOffset? Modified) TryGetTranscriptTimes(string transcriptPath) {
        try {
            return (File.GetCreationTimeUtc(transcriptPath), File.GetLastWriteTimeUtc(transcriptPath));
        } catch {
            return (null, null);
        }
    }

    static async Task<bool> PostSyntheticHookAsync(
        HttpClient client, string baseUrl, string routeSegment, JsonObject payload, CancellationToken ct
    ) {
        try {
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostWithRetryAsync($"{baseUrl}/hooks/{routeSegment}", content, ct: ct);
            return resp.IsSuccessStatusCode;
        } catch {
            return false;
        }
    }

    static ImportCommand.SessionClassification MakeClassification(
        DiscoveredSession                  s,
        SessionMetadata                    meta,
        ImportCommand.ClassificationStatus status,
        int                                totalLines,
        string?                            probeErrorReason = null
    ) => new() {
        SessionId        = s.SessionId,
        // See FilePath note in ClassifyAsync — empty keeps Cursor on the
        // routed phase.
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = meta,
        Status           = status,
        Vendor           = HarnessId.Cursor,
        ProbeErrorReason = probeErrorReason,
        TotalLines       = totalLines,
        SourceMeta       = s.SourceMeta,
    };

    /// <summary>
    /// Single-pass read returning the largest line index of a non-blank line
    /// (matches the server's <c>last_line_number</c> convention — only non-blank
    /// lines get accepted and counted) and the non-blank line count for
    /// <c>--min-lines</c> filtering.
    /// </summary>
    static async Task<(int? LastNonBlankIndex, int NonBlankCount)> ReadTranscriptStatsAsync(
        string transcriptPath, CancellationToken ct
    ) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx = null;
        var  count   = 0;
        var  lineIdx = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;
            }
            lineIdx++;
        }
        return (lastIdx, count);
    }

    static async Task<int?> FetchServerLastLineAsync(HttpClient http, string baseUrl, string sessionId, CancellationToken ct, string? agentId = null) {
        // agentId set → probe the AgentSubsession-{sessionId}-{agentId} watermark.
        var url = string.IsNullOrEmpty(agentId)
            ? $"{baseUrl}/api/sessions/{sessionId}/last-line"
            : $"{baseUrl}/api/sessions/{sessionId}/last-line?agentId={Uri.EscapeDataString(agentId)}";
        using var resp = await http.GetWithRetryAsync(url, ct: ct);

        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
        // Construct via the (string?, Exception?, HttpStatusCode?) overload so .StatusCode is
        // inspectable by FetchServerLastLineWithCappedRetryAsync's status check, rather than
        // message-string parsing.
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"watermark probe returned {(int)resp.StatusCode}", null, resp.StatusCode);

        var       body = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(body);

        return doc.RootElement.TryGetProperty("last_line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
            ? ln.GetInt32()
            : null;
    }

    /// <summary>
    /// A small, fixed 2-attempt cap around a watermark-probe call. Retries ONLY a
    /// status-bearing 5xx or 408 (Request Timeout) response — a single transient 500 no longer
    /// skips straight to the caller's fail-open path on the first failure. Two categories are
    /// explicitly excluded from this outer re-attempt: statusless transport failures (a
    /// network-level <see cref="HttpRequestException"/> with no <c>.StatusCode</c> — these
    /// already had their own exponential-backoff retry inside <c>GetWithRetryAsync</c>'s inner
    /// loop, so a second full retry cycle would double an already-exhausted budget for no
    /// reason to expect a different outcome), and 429 (Too Many Requests — a rate-limit signal,
    /// not a transient blip; immediately retrying it is the wrong response). No additional
    /// backoff is added beyond <c>GetWithRetryAsync</c>'s own per-attempt retry loop. Cancellation
    /// propagates immediately and is never treated as a transient failure to retry. After both
    /// attempts are exhausted (or on a non-retryable failure), the exception propagates to the
    /// caller's existing fail-open handling, unchanged by this cap.
    ///
    /// <paramref name="probe"/> is a delegate (rather than concrete HttpClient/url parameters) so
    /// this pure retry-decision shell is directly, deterministically unit-testable.
    /// </summary>
    internal static async Task<int?> FetchServerLastLineWithCappedRetryAsync(
            Func<CancellationToken, Task<int?>> probe, CancellationToken ct
        ) {
        try {
            return await probe(ct);
        } catch (OperationCanceledException) {
            throw;
        } catch (HttpRequestException ex) when (IsRetryableWatermarkProbeStatus(ex.StatusCode)) {
            return await probe(ct);
        }
    }

    /// <summary>
    /// True for a status-bearing 5xx or 408 — the only categories the 2-attempt cap retries.
    /// A null status (statusless transport failure) and 429 both return false.
    /// </summary>
    internal static bool IsRetryableWatermarkProbeStatus(HttpStatusCode? statusCode) =>
        statusCode is { } code && ((int)code >= 500 && (int)code <= 599 || code == HttpStatusCode.RequestTimeout);

    static (string? ExcludedRepoKey, string? ExcludedPathKey) ResolveExclusions(
        string? cwd, string? repoKey, ClassifyContext ctx
    ) {
        string? excludedRepoKey = null;
        if (repoKey is not null && ctx.ExcludedRepos is { Count: > 0 } repos
         && repos.Any(r => string.Equals(r, repoKey, StringComparison.OrdinalIgnoreCase))) {
            excludedRepoKey = repoKey;
        }

        string? excludedPathKey = null;
        if (cwd is not null && ctx.ExcludedPaths is { Count: > 0 } paths) {
            foreach (var entry in paths) {
                if (PathExclusion.IsExcluded(cwd, [entry], ctx.Home)) {
                    excludedPathKey = PathExclusion.Normalize(entry, ctx.Home);
                    break;
                }
            }
        }
        return (excludedRepoKey, excludedPathKey);
    }

    IReadOnlyDictionary<string, string?> BuildSanitizedToFolderMap() {
        // EncodeWorkspacePath is lossy — "/foo/bar" and "/foo-bar" both encode
        // to "foo-bar". When two distinct workspaces collide on the same
        // sanitized key we can't tell which one a given JSONL session belongs
        // to, so we mark the key ambiguous (null) and let discovery treat the
        // affected sessions as having no resolvable cwd. Picking one
        // arbitrarily would misattribute git owner+repo and risk applying the
        // wrong excluded-repo gating.
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (!Directory.Exists(_workspaceStorageDir)) return map;

        foreach (var subdir in Directory.EnumerateDirectories(_workspaceStorageDir)) {
            var wsJson = Path.Combine(subdir, "workspace.json");
            if (!File.Exists(wsJson)) continue;

            string? folder;
            try {
                using var doc = JsonDocument.Parse(File.ReadAllText(wsJson));
                folder = doc.RootElement.TryGetProperty("folder", out var f) ? f.GetString() : null;
            } catch {
                continue;
            }

            if (folder is null) continue;

            // Separator-normalize only (see NormalizeForComparison): NOT
            // Path.GetFullPath, which injects the current drive for driveless
            // rooted paths on Windows and breaks the sanitized-key match.
            var normalized = NormalizeForComparison(StripFileUri(folder));

            var sanitized = EncodeWorkspacePath(normalized);

            if (map.TryGetValue(sanitized, out var existing)) {
                // Already ambiguous (null) — leave it. Otherwise: if the new
                // folder matches the existing one (case-insensitive on
                // macOS/Windows), keep the existing entry; if it differs,
                // collapse to ambiguous.
                if (existing is not null && !existing.Equals(normalized, PathComparison)) {
                    map[sanitized] = null;
                }
            } else {
                map[sanitized] = normalized;
            }
        }

        return map;
    }

    static string StripFileUri(string uri) {
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return uri;

        var path = uri["file://".Length..];

        // Windows: file:///C:/foo → /C:/foo → C:/foo
        if (path.StartsWith('/') && path.Length > 2 && path[2] == ':') {
            path = path.TrimStart('/');
        }

        return Uri.UnescapeDataString(path);
    }
}
