using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Curation;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class CurateCommand(ConfigRoot config, IRepositoriesApi repositories) {
    /// <summary>One page is all this command reads; hitting it exactly is what the warning below
    /// reports, so the request and the check must name the same number.</summary>
    const int PageLimit = 100;

    public async Task<int> HandleApply(bool dryRun, bool yes) {
        var cwd = Environment.CurrentDirectory;

        // 1. Authoritative repo-root gate (never AppConfig.RepoRoot).
        var repoRoot = GitRepository.FindRoot(cwd);
        if (repoRoot is null) {
            await Console.Error.WriteLineAsync("Not inside a git repository — run `kcap curate apply` from a repo.");
            return 1;
        }

        // 2. Identify the repo for the server key.
        var repo = await RepositoryDetection.DetectRepositoryAsync(config, cwd);
        if (repo?.Owner is null || repo.RepoName is null) {
            await Console.Error.WriteLineAsync("Could not determine the repo's owner/name from its git remote.");
            return 1;
        }
        var hash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);

        // 3. Fetch promoted curation decisions.
        CurationResult curation;
        try {
            curation = await repositories.GetPromotedCurationAsync(hash, PageLimit);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        if (curation is CurationResult.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return 1;
        }

        var items = ((CurationResult.Found)curation).Items;

        if (items.Count == PageLimit)
            await Console.Error.WriteLineAsync($"Warning: hit the {PageLimit}-item page limit; some guidelines may be omitted.");

        // 4. Keep claude_md targets, normalize text.
        var guidelines = new List<CuratedGuideline>();
        foreach (var it in items) {
            if (it.TargetKinds is null || !it.TargetKinds.Contains("claude_md")) continue;
            var text = CuratedTextNormalizer.Normalize(it.PromotedText);
            if (text is null) continue;
            guidelines.Add(new CuratedGuideline(it.Category ?? "", text));
        }

        // 5. Read candidate files and build the plan (fails closed on malformed markers).
        var claude = Path.Combine(repoRoot, CuratedTargets.ClaudeMdName);
        var agents = Path.Combine(repoRoot, CuratedTargets.AgentsMdName);
        var contentByPath = new Dictionary<string, string?> {
            [claude] = File.Exists(claude) ? await File.ReadAllTextAsync(claude) : null,
            [agents] = File.Exists(agents) ? await File.ReadAllTextAsync(agents) : null,
        };

        ApplyPlan plan;
        try {
            plan = CurationApplyPlanner.BuildPlan(repoRoot, contentByPath, guidelines);
        } catch (CuratedBlockException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        // De-dup symlinked CLAUDE.md/AGENTS.md (same real file → write once, to the real target).
        var actionable = ResolveAndDeduplicateTargets(plan.Files.Where(f => f.Action != CurateAction.NoOp).ToList());

        if (actionable.Count == 0) {
            await Console.Out.WriteLineAsync(guidelines.Count == 0
                ? "Nothing to apply (no promoted CLAUDE.md guidelines for this repo)."
                : "Up to date — instruction files already match.");
            return 0;
        }

        // 6. Preview.
        foreach (var f in actionable) {
            await Console.Out.WriteLineAsync($"{f.Action} {f.Path}");
            foreach (var a in f.Added)   await Console.Out.WriteLineAsync($"  + {a}");
            foreach (var r in f.Removed) await Console.Out.WriteLineAsync($"  - {r}");
        }

        if (dryRun) return 0;

        // 7. Confirm (daemon-stop pattern).
        if (!yes) {
            await Console.Out.WriteAsync("Apply? [y/N] ");
            var reply = await Console.In.ReadLineAsync();
            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");
                return 0;
            }
        }

        // 8. Write atomically.
        try {
            foreach (var f in actionable) {
                if (f.NewContent is null) continue;   // NoOp is already filtered out; defensive
                WriteFileAtomic(f.Path, f.NewContent);
            }
        } catch (IOException ex) {
            await Console.Error.WriteLineAsync($"Write failed: {ex.Message}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Applied to {actionable.Count} file(s).");
        return 0;
    }

    /// <summary>Temp-file + rename; preserves an existing file's Unix mode (default mode for new files).</summary>
    public static string WriteFileAtomic(string path, string content) {
        var tmp = path + ".tmp";
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(path)) mode = File.GetUnixFileMode(path);

        File.WriteAllText(tmp, content);
        if (mode is { } m && !OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, m);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Resolve each plan's path to its real target (following symlinks) and de-duplicate
    /// by that real path, so a symlinked CLAUDE.md/AGENTS.md pair is written once — to the
    /// original file — and the symlink itself is preserved (we never overwrite the link).
    /// </summary>
    public static List<FilePlan> ResolveAndDeduplicateTargets(List<FilePlan> files) {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FilePlan>();
        foreach (var f in files) {
            string real;
            try {
                real = File.Exists(f.Path)
                    ? Path.GetFullPath(new FileInfo(f.Path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? f.Path)
                    : f.Path;
            } catch {
                real = f.Path;
            }
            if (seen.Add(real)) result.Add(f with { Path = real });   // write to the resolved real path
        }
        return result;
    }
}
