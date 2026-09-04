using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class ValidatePlanCommand(ISessionsApi sessionsApi) {
    public Task<int> Handle(string sessionId) => HandleCore(sessionsApi, sessionId);

    /// <summary>
    /// Test-friendly core: caller owns the <see cref="ISessionsApi"/>. Two-call flow:
    /// the discovered plan artifact set, then the existing chain-widened recap for
    /// current-session work rows and AI-generated "what's done" summaries. A 404 on the
    /// artifacts route (old server without the route, or a non-visible session) falls back
    /// to <see cref="RenderLegacyAsync"/> — the original recap-only behavior, unchanged.
    /// Exit codes: 0 for a normal render or "no plan found" (absence is a valid answer); 2
    /// when the PRIMARY artifact's content is unavailable (validation genuinely isn't
    /// possible); 1 for a refused or unreachable server.
    /// </summary>
    internal static async Task<int> HandleCore(ISessionsApi sessionsApi, string sessionId) {
        PlanArtifactsResult artifactsResult;

        try {
            artifactsResult = await sessionsApi.GetPlanArtifactsAsync(sessionId);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (artifactsResult is PlanArtifactsResult.NotFound) {
            // Older server without the route, or the session/candidate isn't visible —
            // preserve the original recap-only behavior byte-for-byte.
            return await RenderLegacyAsync(sessionsApi, sessionId);
        }

        var response  = ((PlanArtifactsResult.Found)artifactsResult).Response;
        var primary   = response?.Primary;
        var artifacts = response?.Artifacts ?? [];

        if (primary is null && artifacts.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Work done + AI summaries still come from the existing recap endpoint — the
        // plan-artifacts route only carries the discovered plan/spec/design/checklist set.
        RecapResult recapResult;

        try {
            recapResult = await sessionsApi.GetRecapAsync(sessionId, chain: true);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (recapResult is RecapResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var entries = ((RecapResult.Found)recapResult).Entries;

        // Work done: only from the current session being validated (matches the legacy filter).
        var work      = entries.Where(e => e.Type is "write" or "edit" && e.SessionId == sessionId).ToList();
        var summaries = entries.Where(e => e.Type == "whats_done").ToList();

        var primaryUnavailable = await RenderPlanArtifacts(primary, artifacts);
        await RenderWhatsDoneAndInstructions(summaries, work);

        // review finding 3: an unavailable PRIMARY means validation genuinely couldn't
        // happen — distinct from both success (0, including the "no plan found" case above,
        // where absence of a plan is itself a valid answer) and a generic error (1).
        return primaryUnavailable ? 2 : 0;
    }

    /// <summary>
    /// Bracketed marker for a degraded artifact (<c>IsComplete == false</c>) — a newer revision
    /// exists but hasn't resolved yet, so this is the last known complete text. Single-sourced
    /// here (rather than referenced from the server) because the CLI has no dependency on
    /// <c>Capacitor.Server</c>; text and spacing must stay byte-for-byte identical to the
    /// server's <c>PlanRowRendering.DegradedText</c>.
    /// </summary>
    const string DegradedMarker = "[plan state: unresolved newer revision — last known complete text]";

    /// <summary>
    /// Renders the "## Plan" section from the discovery response: the primary artifact
    /// first (the server's designated best candidate for validation — see
    /// <c>PlanArtifactComposer</c>), followed by any other discovered artifacts in the
    /// order returned (newest-first). A degraded artifact (<c>is_complete == false</c>) is
    /// prefixed with <see cref="DegradedMarker"/>; a truncated one additionally gets a
    /// byte-count marker (degraded composes WITH truncated: degraded line first, then the
    /// truncation line, mirroring the server's <c>PlanRowRendering</c> ordering); an
    /// unavailable one renders a placeholder — and, when the PRIMARY itself is unavailable,
    /// an explicit note that full validation isn't possible without its content.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the PRIMARY artifact's content could not be retrieved
    /// (<c>content_state == "unavailable"</c>) — the caller uses this to exit 2 instead of 0
    /// distinguishable from success (0) and from a generic error (1).
    /// </returns>
    static async Task<bool> RenderPlanArtifacts(PlanArtifactDto? primary, IReadOnlyList<PlanArtifactDto> artifacts) {
        var ordered = primary is null
            ? artifacts
            : new List<PlanArtifactDto> { primary }
                .Concat(artifacts.Where(a => a.ArtifactId != primary.ArtifactId))
                .ToList();

        var primaryUnavailable = false;

        await Console.Out.WriteLineAsync("## Plan");
        await Console.Out.WriteLineAsync();

        foreach (var artifact in ordered) {
            var isPrimary = primary is not null && artifact.ArtifactId == primary.ArtifactId;

            if (!artifact.IsComplete) {
                await Console.Out.WriteLineAsync(DegradedMarker);
            }

            // "truncated" with null Content is treated like "unavailable" (review
            // finding 2): the server contract pairs content_state=="truncated" with non-null
            // Content, but a malformed/edge response with Content == null must not render the
            // nonsensical "first 0 of ... bytes" — fall through to the unavailable placeholder.
            var effectiveState = artifact.ContentState == "truncated" && artifact.Content is null
                ? "unavailable"
                : artifact.ContentState;

            switch (effectiveState) {
                case "truncated": {
                    var n = Encoding.UTF8.GetByteCount(artifact.Content!);
                    // OriginalBytes is nullable — a well-formed truncated artifact always
                    // carries it, but fall back to "?" rather than emit "of  bytes" if it's
                    // ever missing.
                    var total = artifact.OriginalBytes?.ToString() ?? "?";
                    await Console.Out.WriteLineAsync($"[plan truncated: first {n} of {total} bytes]");
                    await Console.Out.WriteLineAsync(artifact.Content!);

                    break;
                }
                case "unavailable": {
                    await Console.Out.WriteLineAsync("[plan content unavailable due to size bounds]");

                    if (isPrimary) {
                        primaryUnavailable = true;
                        await Console.Out.WriteLineAsync(
                            "Validation is not possible: the plan content could not be retrieved (exceeds size bounds).");
                    }

                    break;
                }
                default: {
                    if (artifact.Content is not null) {
                        await Console.Out.WriteLineAsync(artifact.Content);
                    }

                    break;
                }
            }
        }

        await Console.Out.WriteLineAsync();

        return primaryUnavailable;
    }

    /// <summary>Shared "## What's Done" + "## Instructions" rendering, used by both the
    /// plan-artifacts path and the legacy recap-only path so the two stay in sync.</summary>
    static async Task RenderWhatsDoneAndInstructions(List<RecapEntry> summaries, List<RecapEntry> work) {
        await Console.Out.WriteLineAsync("## What's Done");
        await Console.Out.WriteLineAsync();

        if (summaries.Count > 0) {
            await Console.Out.WriteLineAsync("### Summary");
            await Console.Out.WriteLineAsync();

            foreach (var summary in summaries) {
                await Console.Out.WriteLineAsync(summary.Content);
            }

            await Console.Out.WriteLineAsync();
        }

        await Console.Out.WriteLineAsync("### Details");
        await Console.Out.WriteLineAsync();

        if (work.Count == 0) {
            await Console.Out.WriteLineAsync("No file writes or edits recorded.");
        } else {
            foreach (var entry in work) {
                var label = entry.Type == "write" ? "Write" : "Edit";
                var path  = entry.FilePath ?? "unknown";
                await Console.Out.WriteLineAsync($"- {label}: {path}");
            }
        }

        await Console.Out.WriteLineAsync();

        await Console.Out.WriteLineAsync("## Instructions");
        await Console.Out.WriteLineAsync();

        await Console.Out.WriteLineAsync(
            "Compare the plan above against the summary and file list under \"What's Done\". Identify any planned items that were NOT completed. If everything is done, confirm that. If there are gaps, list them and complete the remaining work now."
        );
    }

    /// <summary>
    /// Original recap-only behavior, preserved byte-for-byte for old servers that
    /// don't yet expose <c>GET /api/sessions/{id}/plan-artifacts</c> (or a session/candidate the
    /// route can't resolve). Plans come from <c>recap</c> entries of type "plan" across the
    /// session chain; work/summaries are filtered exactly as before.
    /// </summary>
    static async Task<int> RenderLegacyAsync(ISessionsApi sessionsApi, string sessionId) {
        RecapResult result;

        try {
            result = await sessionsApi.GetRecapAsync(sessionId, chain: true);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is RecapResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var entries = ((RecapResult.Found)result).Entries;

        if (entries.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Plans can come from any session in the chain (continuation planContent or ExitPlanMode write)
        var plans = entries.Where(e => e.Type == "plan").ToList();
        // Work done: only from the current session being validated
        var work = entries.Where(e => e.Type is "write" or "edit" && e.SessionId == sessionId).ToList();
        // AI-generated summaries
        var summaries = entries.Where(e => e.Type == "whats_done").ToList();

        if (plans.Count == 0) {
            await Console.Out.WriteLineAsync("No plan found for this session.");

            return 0;
        }

        // Output plan(s)
        await Console.Out.WriteLineAsync("## Plan");
        await Console.Out.WriteLineAsync();

        foreach (var plan in plans) {
            await Console.Out.WriteLineAsync(plan.Content);
        }

        await Console.Out.WriteLineAsync();

        await RenderWhatsDoneAndInstructions(summaries, work);

        return 0;
    }
}
