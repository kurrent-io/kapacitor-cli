using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Cursor;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Outcome of an agent-hook recording POST (<see cref="AgentHookPoster.PostAsync(string,string,string)"/>).
/// </summary>
internal enum HookPostOutcome {
    /// <summary>Auth was usable and the POST completed successfully.</summary>
    Posted,

    /// <summary>
    /// Auth has lapsed (expired refresh credential, or never logged in) — the POST was SKIPPED
    /// because the server would 401. No request was sent and nothing was written to stderr. The
    /// caller should skip follow-on work that also needs auth (e.g. spawning the transcript
    /// watcher) and exit cleanly, so a lapsed session produces no per-turn error banner.
    /// </summary>
    AuthLapsed,

    /// <summary>
    /// Auth was usable but the POST failed (non-success status or the server was unreachable).
    /// An error was already written to stderr; the caller keeps its existing failure handling.
    /// </summary>
    Failed,

    /// <summary>The POST could not be delivered now (auth lapsed, a server 401, or a transient/unreachable
    /// failure) so the payload was durably spooled for a later drain pass. NOT delivered — but the caller should still
    /// proceed to spawn the watcher (spawn-before-post): capture must not depend on lifecycle delivery.</summary>
    Spooled,

    /// <summary>
    /// Nothing was delivered AND nothing was persisted — the server URL is unusable, or the spool
    /// append itself failed. Distinct from <see cref="Failed"/>, which every caller maps to a non-zero
    /// exit; a hook must still exit 0 here. Distinct from <see cref="Spooled"/>, which promises a later
    /// replay this outcome cannot.
    /// </summary>
    Skipped
}

/// <summary>
/// Shared recording-hook POST for the non-Claude agent hooks (Codex, Gemini, Copilot, Pi, Kiro,
/// OpenCode). A lapsed credential makes a POST a guaranteed 401 and its stderr line a misleading
/// per-turn <c>HTTP 401</c>, so rather than send one this helper reports
/// <see cref="HookPostOutcome.AuthLapsed"/> so the caller can skip the doomed work and exit
/// cleanly — carrying the Claude hook's #183 behaviour to the other hooks.
///
/// These agents have no user-facing stdout notice channel (stdout is either unused or a JSON
/// decision/context channel). A pre-flight lapse (<see cref="IsAuthLapsed"/> — the token store
/// already knows the credential is dead) stays silent here too, same as before: the doomed POST is
/// skipped and the expired state is visible via <c>kcap status</c> and the interactive CLI. A
/// server-returned 401 is different — the store thought the credential was fine, so nothing warned
/// the user before the request — and names the fix on stderr, the only channel these vendors have.
/// A no-op for the <c>None</c> provider (posts normally, unauthenticated) and unchanged when authenticated.
/// </summary>
internal sealed class AgentHookPoster(ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http) {
    readonly WatcherManager _watchers = new(config, profiles, http);

    // The one URL this process resolved. A hook posting to one server while its watcher streams to
    // another is not a configuration this can represent.
    string? Url => profiles.Resolution.ServerUrl;

    /// <summary>Auth has genuinely lapsed → any POST would 401. <c>Ok</c> and <c>NoAuthRequired</c> are usable.</summary>
    // Anything that isn't a usable client is a lapse. WrongServer especially: the client carries no
    // bearer, so posting anyway would only earn a 401 for a fixable profile mismatch.
    public static bool IsAuthLapsed(AuthStatus status) =>
        status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer or AuthStatus.UnusableServerUrl;

    /// <summary>
    /// Builds an auth-aware client for this process's resolved server and POSTs
    /// <paramref name="body"/> to <c>/hooks/{endpoint}</c>, skipping the POST when auth has lapsed.
    /// <paramref name="agentTag"/> is the stderr prefix on a real failure, e.g. <c>"codex-hook"</c>.
    /// </summary>
    public Task<HookPostOutcome> PostAsync(string endpoint, string body, string agentTag) {
        // Guard BEFORE the factory closure is constructed. There is no spool on this path, so the
        // payload is dropped — Skipped, never Failed, which every caller maps to a non-zero exit.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"{endpoint} dropped, not sent"));
            return Task.FromResult(HookPostOutcome.Skipped);
        }

        return PostAsync(() => http.ForHookAsync(), endpoint, body, agentTag);
    }

    /// <summary>
    /// Core with an injectable <paramref name="clientFactory"/> (test seam — lets tests control the
    /// auth outcome without a token store or /auth/config discovery).
    /// </summary>
    internal async Task<HookPostOutcome> PostAsync(
            Func<Task<AuthAttempt>> clientFactory,
            string                                             endpoint,
            string                                             body,
            string                                             agentTag
        ) {
        var (client, status) = await clientFactory();

        using (client) {
            // Auth lapsed: the POST would 401. Skip it and report so the caller exits cleanly
            // (no per-turn stderr line / error banner); kcap status reports the expired state.
            if (IsAuthLapsed(status)) {
                return HookPostOutcome.AuthLapsed;
            }

            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            try {
                using var resp = await client.PostWithRetryAsync($"{Url}/hooks/{endpoint}", content);

                if (!resp.IsSuccessStatusCode) {
                    var code = (int)resp.StatusCode;
                    // These vendors have no systemMessage channel, so the stderr line is the only
                    // place a rejected credential can name its own fix.
                    Console.Error.WriteLine(AuthRejectionNotice.VendorStderrLine(agentTag, endpoint, code));
                    return HookPostOutcome.Failed;
                }

                return HookPostOutcome.Posted;
            } catch (HttpRequestException ex) {
                HttpClientExtensions.WriteUnreachableError(Url!, ex);
                return HookPostOutcome.Failed;
            }
        }
    }

    /// <summary>
    /// spawn-before-post variant. Like <see cref="PostAsync(string,string,string)"/>,
    /// but on a lapsed-auth or transient (5xx/408/429/unreachable) failure it durably spools the
    /// lifecycle payload to <paramref name="spool"/> and returns <see cref="HookPostOutcome.Spooled"/>
    /// (a global drain pass replays it after recovery). Callers treat <c>Posted</c> OR <c>Spooled</c>
    /// as "proceed to spawn the watcher"; never <c>Spooled</c> as delivered.
    /// </summary>
    public Task<HookPostOutcome> PostOrSpoolAsync(
            string endpoint, string body, string agentTag, HookSpool spool, string sessionId, string route) {
        // Guard BEFORE the factory closure is constructed, so the injectable core stays untouched and
        // its existing tests keep exercising the real post logic. An unusable URL is routed into the
        // same "cannot post now" arm an auth lapse already uses: persist and let the caller continue.
        if (!HookHttp.IsPostable(Url)) {
            var spooled     = spool.Append(sessionId, route, body);
            var disposition = spooled ? $"{endpoint} spooled, not sent" : $"{endpoint} dropped (spool write failed)";
            Console.Error.WriteLine(UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, disposition));

            return Task.FromResult(spooled ? HookPostOutcome.Spooled : HookPostOutcome.Skipped);
        }

        return PostOrSpoolAsync(() => http.ForHookAsync(),
                                endpoint, body, agentTag, spool, sessionId, route);
    }

    /// <summary>
    /// spawn-before-post decision. Capture must start regardless of whether the lifecycle
    /// POST was actually <em>delivered</em> — both <c>Posted</c> (delivered) and <c>Spooled</c>
    /// (durably persisted for a later drain) proceed to <c>WatcherManager.EnsureWatcherRunning</c>,
    /// because a spooled <c>SessionStarted</c> will still reach the server on the next drain pass.
    /// <c>AuthLapsed</c> does NOT spawn: the legacy <see cref="PostAsync(string,string,string)"/>
    /// path spools NOTHING on a lapse, so tailing a session whose <c>SessionStarted</c> was
    /// permanently dropped would produce an orphaned transcript. <c>Failed</c> (a real non-2xx) also
    /// skips the watcher. Vendors on <see cref="PostOrSpoolAsync(string,string,string,HookSpool,string,string)"/>
    /// get <c>Spooled</c> (never <c>AuthLapsed</c>) on a lapse — so capture-on-lapse is preserved
    /// for them via the spool, not via this predicate.
    /// </summary>
    /// <para><c>Skipped</c> DOES spawn when the URL is usable: the server supports a transcript
    /// arriving before its session-start (<c>ActiveSessionRegistry.EnsureEntryExistsAsync</c> creates
    /// the entry, owner-only until a later start reconciles it), so suppressing capture after a spool
    /// write failure would guarantee loss the server is designed to recover.</para>
    ///
    /// <para>An unusable <paramref name="baseUrl"/> never spawns, whatever the outcome. A watcher
    /// streams to SignalR and only spools an undelivered tail at shutdown, so one that can never
    /// connect captures nothing — spawning it would write a PID file asserting capture that is not
    /// happening. The URL is a parameter rather than a second conjunct at each call site so a caller
    /// cannot forget it.</para>
    public static bool ShouldSpawnAfter(HookPostOutcome outcome, string? baseUrl) =>
        outcome is HookPostOutcome.Posted or HookPostOutcome.Spooled or HookPostOutcome.Skipped
     && HookHttp.IsPostable(baseUrl);


    /// <summary>
    /// Maps an append attempt onto an honest outcome. <c>Spooled</c> promises a later replay, so it
    /// may only be reported when something was actually written; a rejected key or a disk fault is
    /// <c>Skipped</c>, with a line saying the payload was lost.
    /// </summary>
    static HookPostOutcome SpoolOrSkip(HookSpool spool, string sessionId, string route, string body, string agentTag) {
        if (spool.Append(sessionId, route, body)) return HookPostOutcome.Spooled;

        Console.Error.WriteLine($"[kcap] {agentTag} {route}: dropped — the spool write failed");

        return HookPostOutcome.Skipped;
    }

    /// <summary>Minimum wall-clock gap between drain attempts (see <see cref="DrainSpoolsAsync"/>).</summary>
    static readonly TimeSpan DrainThrottle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Task 4 (Task 12: centralized): bounded, best-effort drain of the cross-vendor
    /// lifecycle + transcript spools. Introduced in Task 4 as a per-vendor call from each
    /// JSON-payload dispatcher's <c>Handle</c>; Task 12 centralized the call site into
    /// <c>Program.cs</c>'s <c>case "hook":</c> (before dispatch, non-Codex) so it runs exactly once
    /// per invocation and additionally covers Claude/Cursor, which never called it directly. Codex
    /// still calls this itself, but from the BACKGROUND after its stdout contract. Also reused by
    /// the daemon's own periodic sweep (<c>SpoolDrainLoop</c>) for the equivalent Core primitives —
    /// the daemon can't reference this CLI-project method, so it composes
    /// <see cref="LifecycleSpoolDrain.RunAsync(CursorMarkers,HttpClient,string,HookSpool,TranscriptSpool,string?,TimeSpan,CancellationToken,Action{string,string}?)"/>
    /// directly instead.
    ///
    /// <para><b>Throttled.</b> Several vendors fire their lifecycle hook on
    /// EVERY prompt (Kiro's <c>agentSpawn</c>, OpenCode's <c>session.idle</c>-driven re-fire), so an
    /// un-throttled drain would attempt a ~1.5s network round-trip per prompt during a server outage.
    /// A cross-vendor on-disk stamp (<c>{spoolDir}/.last-drain</c>) caps attempts to one per
    /// <see cref="DrainThrottle"/>. An in-memory guard can't help — every hook is a fresh AOT process
    /// — so the stamp must be on disk. The drain is global (one pass replays ALL sessions' backlog),
    /// so a single shared stamp is the correct granularity; it also throttles the reap that piggybacks
    /// on the same gate. This is the Kiro-side analogue of the event-type gate applied to Copilot,
    /// whose per-turn <c>agentStop</c>/<c>notification</c> events skip this call entirely.</para>
    ///
    /// <para><b>Skips on auth lapse.</b> Every POST would 401 and stop the pass at its first entry,
    /// so the budget is better left to the vendor's own hook; the backlog waits for the login.</para>
    ///
    /// <para><b>Fresh client (review #3 — documented deviation).</b> The brief's Step 3
    /// suggested reusing the vendor's authenticated client, but the drain runs at the top of the
    /// dispatcher BEFORE any lifecycle POST, and the vendors never hold a reusable client — each
    /// <see cref="PostOrSpoolAsync(string,string,string,HookSpool,string,string)"/> builds and
    /// disposes its own internally. Threading a client through purely for reuse would leak an
    /// <see cref="HttpClient"/> into every code path (including those that never drain). A fresh,
    /// budget-scoped client built and disposed here is the cleaner seam.</para>
    ///
    /// Never throws — a spool-drain hiccup must not disrupt the vendor's own hook.
    /// </summary>
    public Task DrainSpoolsAsync(HookSpool lifecycle, TranscriptSpool transcript, string? sessionId)
        => DrainSpoolsCoreAsync(lifecycle, transcript, sessionId,
            http.ForHookAsync);

    /// <summary>
    /// The drain itself, with the client factory handed in. Separate from
    /// <see cref="DrainSpoolsAsync"/> so the unusable-URL guard can be proven to run BEFORE a client
    /// is ever built: the retention reap runs either way and the outer catch swallows everything, so
    /// a factory that throws is the only way to show non-entry.
    /// </summary>
    internal async Task DrainSpoolsCoreAsync(
            HookSpool lifecycle, TranscriptSpool transcript, string? sessionId,
            Func<CancellationToken, Task<AuthAttempt>> clientFactory) {
        if (!TryClaimDrainAttempt(lifecycle.Dir)) return; // throttled — a recent attempt already ran

        // Retention runs even when delivery cannot: the reap lives here, and Program.cs skips this
        // whole call while the URL is unacceptable, so an indefinitely broken config would otherwise
        // never reap anything.
        lifecycle.ReapOlderThan(TimeSpan.FromDays(30));
        transcript.ReapOlderThan(TimeSpan.FromDays(30));

        // Distinct from the POST guards' diagnostic: a reader must be able to tell which guard fired,
        // and a test must be able to prove THIS one did.
        if (!HookHttp.IsPostable(Url)) {
            Console.Error.WriteLine(UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, "spool drain skipped (backlog retained)"));
            return;
        }

        var budget = TimeSpan.FromSeconds(1.5);

        try {
            using var cts = new CancellationTokenSource(budget);
            var (client, status) = await clientFactory(cts.Token);

            using (client) {
                if (IsAuthLapsed(status)) return;

                // Task 12 / BLOCKER-2: a generically-drained session-end (any vendor — the
                // server's generate_whats_done signal is vendor-agnostic, see WatchCommand's
                // parent-exit path) must still trigger the what's-done generator, mirroring
                // ClaudeHookCommand.ClaudePoster's own session-end replay side effect.
                await LifecycleSpoolDrain.RunAsync(new CursorMarkers(config), client, Url!, lifecycle, transcript, sessionId, budget, cts.Token,
                    onWhatsDoneRequested: (sid, vendor) => _watchers.SpawnWhatsDoneGenerator(sid, vendor));
            }
        } catch {
            // Best-effort — a drain hiccup must never disrupt the vendor's own hook.
        }
    }

    /// <summary>
    /// Cross-process drain throttle: returns <c>true</c> (and stamps the attempt) only when the last
    /// recorded attempt is older than <see cref="DrainThrottle"/>. The stamp file name starts with a
    /// dot so <see cref="HookSpool"/>'s / <see cref="TranscriptSpool"/>'s session-id-keyed enumerations
    /// ignore it. Fail-open: a stamp-file hiccup must never suppress a drain, so any I/O error returns
    /// <c>true</c>.
    /// </summary>
    static bool TryClaimDrainAttempt(string spoolDir) {
        try {
            var stamp = Path.Combine(spoolDir, ".last-drain");

            if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < DrainThrottle) {
                return false;
            }

            Directory.CreateDirectory(spoolDir);
            File.WriteAllText(stamp, ""); // touch — mtime is the throttle clock
            return true;
        } catch {
            return true; // never let a throttle-file error swallow a legitimate drain
        }
    }

    /// <summary>Core with an injectable client factory (test seam).</summary>
    internal async Task<HookPostOutcome> PostOrSpoolAsync(
            Func<Task<AuthAttempt>> clientFactory,
            string endpoint, string body, string agentTag,
            HookSpool spool, string sessionId, string route) {
        var (client, status) = await clientFactory();

        using (client) {
            // Auth lapsed → the POST would 401. Spool for replay after `kcap login`; caller still spawns.
            if (IsAuthLapsed(status)) {
                return SpoolOrSkip(spool, sessionId, route, body, agentTag);
            }

            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            try {
                using var resp = await client.PostWithRetryAsync($"{Url}/hooks/{endpoint}", content);

                if (resp.IsSuccessStatusCode) {
                    return HookPostOutcome.Posted;
                }

                var code = (int)resp.StatusCode;

                // Before the spool decision, so a 401 still names its fix: an outage spools silently,
                // but a rejected credential needs the user, and stderr is the only channel here.
                if (code == 401 || !HookSpool.IsRetryable(code)) {
                    Console.Error.WriteLine(AuthRejectionNotice.VendorStderrLine(agentTag, endpoint, code));
                }

                return HookSpool.IsRetryable(code)
                    ? SpoolOrSkip(spool, sessionId, route, body, agentTag)
                    : HookPostOutcome.Failed;
            } catch (HttpRequestException) {
                // Unreachable after retries → transient; spool for a later drain rather than lose it.
                return SpoolOrSkip(spool, sessionId, route, body, agentTag);
            }
        }
    }
}
