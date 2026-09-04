using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap skills sync</c> — materializes the server's versioned skill-doc snapshot for this repo
/// into every present harness's skills tree. Server-canonical and centrally revocable: files land
/// under a kcap namespace, a per-(repo, target) manifest in the kcap config root records every
/// path kcap owns, and pruning walks the manifest — never a skills root — so user-authored skills
/// are untouchable. Nothing is ever written into a repo.
/// </summary>
class SkillsCommand(ConfigRoot config, UserHome home, IRepositoriesApi repositories) {
    // The background refresh keys off each manifest's synced_at, so a burst of session starts
    // costs one network round-trip per interval per target, not one per session.
    static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(6);

    /// <summary>The harness trees skills materialize into — the same set the packaged skills
    /// installer serves. A null vendor is a SHARED tree (several harnesses read it): its snapshot
    /// is fetched vendor-less, so unknown-excludes keeps vendor-restricted docs out of it — those
    /// reach their harness through a vendored tree instead.</summary>
    internal static IReadOnlyList<SkillsTarget> Targets(HarnessPaths paths) => [
        new("claude", paths.Claude.UserSkillsDir, "claude"),
        new("agents", paths.Agents.UserSkillsDir, null),
        new("kiro",   paths.Kiro.SkillsDir, "kiro"),
        new("gemini", paths.Antigravity.SkillsDir, null),   // shared: Gemini CLI + Antigravity
    ];

    public async Task<int> HandleSync(bool dryRun, bool auto = false) {
        var cwd = Environment.CurrentDirectory;

        if (GitRepository.FindRoot(cwd) is null) {
            await Console.Error.WriteLineAsync("Not inside a git repository — run `kcap skills sync` from a repo.");
            return 1;
        }
        var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd);
        if (repo?.Owner is null || repo.RepoName is null) {
            await Console.Error.WriteLineAsync("Could not determine the repo's owner/name from its git remote.");
            return 1;
        }
        var hash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);

        // Real harness detection, not destination-parent existence: ~/.agents is created by
        // kcap's own installer, so a fresh machine with (say) Codex installed would otherwise
        // never adopt the shared tree.
        var paths     = HarnessPaths.FromEnvironment(home);
        var harnesses = HarnessRegistry.FromEnvironment(home);
        var exitCode = 0;
        foreach (var target in Targets(paths)) {
            var manifestName = Path.Combine("skills", hash, target.Key, "manifest.json");
            // Adopt a target only while a consuming harness is present; a target we already own
            // keeps reconciling (revocation must reach it) even after the harness is removed.
            if (!File.Exists(config.Path(manifestName)) && !ConsumerPresent(harnesses, target.Key))
                continue;
            exitCode = Math.Max(exitCode,
                await SyncTargetAsync(hash, target, manifestName, dryRun, auto));
        }
        return exitCode;
    }

    async Task<int> SyncTargetAsync(
            string hash, SkillsTarget target, string manifestName, bool dryRun, bool auto) {
        void Info(string line) { if (!auto) Console.WriteLine(line); }
        var manifestPath = config.Path(manifestName);

        // One sync per (repo, target) at a time, machine-wide: a burst of session starts must
        // collapse to ONE refresh — the throttle alone cannot do that, since every child of the
        // burst reads the same stale synced_at before the winner stamps it. The manifest is
        // re-read UNDER the lock, so waiters see the winner's stamp. In auto mode contention IS
        // the answer (someone else is refreshing); a manual sync reports it instead.
        IDisposable syncLock;
        try {
            syncLock = config.AcquireLock(manifestName, auto ? TimeSpan.FromMilliseconds(1) : null);
        } catch (TimeoutException) {
            if (auto) return 0;
            await Console.Error.WriteLineAsync($"Another kcap skills sync is already running for this repo ({target.Key}).");
            return 1;
        }
        using var heldSyncLock = syncLock;

        if (!TryLoadManifest(manifestPath, out var manifest)) return 1;
        if (auto && AutoThrottled(manifest, DateTimeOffset.UtcNow)) return 0;

        // Metadata alone cannot prove a skill is served: a deleted or hand-edited SKILL.md must be
        // re-materialized, so local drift forfeits the conditional request — a 304 would otherwise
        // report "up to date" over a missing file forever.
        var drifted = (manifest?.Skills ?? []).Where(SkillsMaterializer.HasDrifted)
            .Select(e => e.DocId).ToHashSet();

        SkillsSnapshotResult fetched;
        try {
            fetched = await repositories.GetSkillsSnapshotAsync(
                hash, target.Vendor, drifted.Count == 0 ? manifest?.Etag : null);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        if (fetched is SkillsSnapshotResult.NotModified) {
            if (!dryRun) SaveManifest(manifestPath, manifest! with { SyncedAt = DateTimeOffset.UtcNow });
            Info($"[{target.Key}] skills up to date ({manifest?.Skills?.Length ?? 0} materialized).");
            return 0;
        }
        if (fetched is SkillsSnapshotResult.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return 1;
        }

        var dto      = ((SkillsSnapshotResult.Found)fetched).Snapshot;
        var snapshot = dto.Skills ?? [];
        // Whole-snapshot validation BEFORE any filesystem mutation: acting on a partially-valid
        // snapshot and recording its etag would prune real skills, write no replacements, and
        // 304 forever after — refusing outright leaves everything intact and retried in full.
        var unsafeSlugs = snapshot.Where(s => !SkillsSyncPlanner.IsSafeSlug(s.Slug)).ToList();
        if (unsafeSlugs.Count > 0) {
            foreach (var u in unsafeSlugs)
                await Console.Error.WriteLineAsync($"Refusing snapshot: unsafe slug '{u.Slug}'.");
            return 1;
        }
        var plan   = SkillsSyncPlanner.Plan(manifest, snapshot);
        var root   = target.Root;
        var writes = plan.Writes.Concat(plan.Unchanged.Where(u => drifted.Contains(u.DocId))).ToList();

        if (writes.Count == 0 && plan.Prunes.Count == 0) {
            if (!dryRun) SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
            Info($"[{target.Key}] skills up to date ({snapshot.Length} materialized).");
            return 0;
        }

        foreach (var w in writes)
            Info($"{(dryRun ? "would write" : "write"),-12} {SkillsMaterializer.SkillDirFor(root, w.Slug)} (v{w.Version})");
        foreach (var p in plan.Prunes)
            Info($"{(dryRun ? "would prune" : "prune"),-12} {p.Path}");
        if (dryRun) return 0;

        foreach (var w in writes) SkillsMaterializer.Write(root, w);
        foreach (var p in plan.Prunes) SkillsMaterializer.Prune(root, p);
        SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, root));
        Info($"[{target.Key}] synced {writes.Count} skill(s), pruned {plan.Prunes.Count}; {snapshot.Length} materialized.");
        return 0;
    }

    /// <summary>Whether any harness that reads this target's tree is installed. The shared trees
    /// list their consumers: ~/.agents is read by the codex-family harnesses, the gemini tree by
    /// Gemini CLI and Antigravity; Claude and Kiro read only their own.</summary>
    internal static bool ConsumerPresent(HarnessRegistry harnesses, string targetKey) {
        bool Any(params HarnessId[] ids) => ids.Any(harnesses.Detected);

        return targetKey switch {
            "claude" => Any(HarnessId.Claude),
            "agents" => Any(HarnessId.Codex, HarnessId.Cursor, HarnessId.Copilot,
                            HarnessId.Pi, HarnessId.OpenCode),
            "kiro"   => Any(HarnessId.Kiro),
            "gemini" => Any(HarnessId.Gemini, HarnessId.Antigravity),
            _        => false,
        };
    }

    internal static bool AutoThrottled(SkillsManifest? manifest, DateTimeOffset now) =>
        // A future stamp (clock correction, tampered file) must read as stale, not as an
        // unbounded suppression of every revocation refresh until that future arrives.
        manifest?.SyncedAt is { } syncedAt && now - syncedAt is { } age
            && age >= TimeSpan.Zero && age < AutoSyncInterval;

    static SkillsManifest BuildManifest(string? etag, SkillSnapshotItem[] snapshot, string root) => new() {
        Etag = etag, SyncedAt = DateTimeOffset.UtcNow,
        Skills = [.. snapshot.Select(s => new SkillsManifestEntry {
            DocId = s.DocId, Slug = s.Slug, Version = s.Version, ContentHash = s.ContentHash,
            Path = SkillsMaterializer.SkillDirFor(root, s.Slug),
            FileHash = SkillsMaterializer.FileHash(SkillsSyncPlanner.RenderSkillFile(s)),
        })],
    };

    // The manifest is the ownership ledger, and the two failure classes diverge: an unreadable
    // file may be transient (sharing violation), so the sync ABORTS rather than reconciling
    // ledger-less over a still-valid file; a corrupt file proceeds from scratch only once the
    // evidence is genuinely preserved aside — a failed preserve also aborts.
    static bool TryLoadManifest(string path, out SkillsManifest? manifest) {
        manifest = null;
        if (!File.Exists(path)) return true;
        string text;
        try {
            text = File.ReadAllText(path);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Cannot read skills manifest ({ex.Message}); aborting sync.");
            return false;
        }
        try {
            manifest = JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.SkillsManifest);
        } catch (JsonException) {
            manifest = null;
        }
        // A parseable `null` or a missing skills collection is no ledger either — under a stored
        // etag it would 304 forever with zero owned paths, stranding every prior directory. Same
        // recovery route as unparseable content.
        if (manifest?.Skills is not null) return true;
        manifest = null;
        try {
            File.Move(path, path + ".corrupt", overwrite: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Corrupt skills manifest could not be preserved ({ex.Message}); aborting sync.");
            return false;
        }
        Console.Error.WriteLine($"Warning: corrupt skills manifest moved aside ({path}.corrupt); re-syncing from scratch.");
        return true;
    }

    static void SaveManifest(string path, SkillsManifest manifest) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic replace: a crash mid-write must never truncate the ownership ledger in place.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
        File.Move(tmp, path, overwrite: true);
    }
}
