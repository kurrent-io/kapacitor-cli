using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The dispositions each guard owes when the server URL cannot be used.
///
/// <para>Where a guard sits in front of an injectable seam, the assertion is that the guarded
/// operation was NEVER ENTERED — not that its effects are absent. Effects are reproducible by the
/// catch-all every one of these paths already has, so an effect-only assertion passes with the guard
/// deleted; six review rounds found exactly that, repeatedly.</para>
/// </summary>
// The two spawn guards below install WatcherManager.ProcessStarterForTesting and Dispose nulls it,
// both process-global: a concurrent peer's spawn lands in this class's counter, and its own override
// is cleared under it. Bare, as every other writer of that seam already carries.
[NotInParallel]
public class UnusableUrlGuardTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    // Deliberately the wrong-scheme class: an implementation validating only UriKind.Absolute
    // accepts this while still violating the invariant.
    const string BadUrl = "ftp://host";
    const string Sid    = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // The suppression gate reads its budget only for the repo probe, which no payload here reaches
    // (no profile, so no excluded repos). One clock per test so the command and its budget agree.
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    readonly HookClock _clock = new(TimeProvider.System);

    readonly TempDir _tmp = new();
    readonly string  _dir;
    readonly string  _tdir;

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Every guard below reads the URL off the resolution, so the unusable one lives there — a
    // parameter would let a caller point a guard at a URL the process never resolved.
    ProfileContext  Bad => field ??= Resolutions.At(BadUrl, Config.Root);

    AgentHookPoster  Poster => field ??= new(Config.Root, Bad, new FixedCapacitorHttpClient());

    WatcherManager  Watchers => field ??= new(Config.Root, Bad, new FixedCapacitorHttpClient());

    public UnusableUrlGuardTests() {
        _tdir = _tmp.PathTo("tdir");
        _dir  = _tmp.PathTo("dir");
    }

    public void Dispose() {
        WatcherManager.ProcessStarterForTesting = null;
        _tmp.Dispose();
    }

    [Test]
    public async Task PostOrSpool_spools_the_payload_and_reports_Spooled() {
        var spool   = new HookSpool(_dir);
        var outcome = await Poster.PostOrSpoolAsync(
            "session-start/codex", """{"session_id":"x"}""", "codex-hook", spool, Sid, "session-start/codex");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Spooled);
        await Assert.That(spool.HasBacklog(Sid)).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(_dir, $"{Sid}.jsonl"))).Contains("session-start/codex");
    }

    [Test]
    public async Task PostOrSpool_reports_Skipped_when_the_spool_write_itself_fails() {
        // An unwritable spool dir must not be reported as durably spooled — Skipped promises nothing.
        var unwritable = Path.Combine(_dir, "nope.txt");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(unwritable, "not a directory");

        var outcome = await Poster.PostOrSpoolAsync(
            "session-start/codex", "{}", "codex-hook", new HookSpool(unwritable), Sid, "session-start/codex");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Skipped);
    }

    [Test]
    public async Task PostAsync_reports_Skipped_never_Failed() {
        // Failed would make every caller exit non-zero — the hook must still exit 0.
        var outcome = await Poster.PostAsync("session-end/gemini", "{}", "gemini-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Skipped);
        await Assert.That(outcome).IsNotEqualTo(HookPostOutcome.Failed);
    }

    [Test]
    public async Task ShouldSpawn_refuses_for_an_unusable_url_whatever_the_outcome() {
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Posted,  BadUrl)).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Spooled, BadUrl)).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Skipped, BadUrl)).IsFalse();
    }

    [Test]
    public async Task ShouldSpawn_allows_Skipped_when_the_url_is_usable() {
        // The server supports a transcript arriving before its session-start, so suppressing capture
        // after a spool write failure would guarantee loss it is designed to recover.
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Skipped, "http://localhost:5108")).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Failed,  "http://localhost:5108")).IsFalse();
    }

    [Test]
    public async Task Drain_reaps_the_backlog_but_never_builds_a_client() {
        Directory.CreateDirectory(_dir);
        var stale = Path.Combine(_dir, $"{Sid}.jsonl");
        File.WriteAllText(stale, "{}\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-40));

        var entered = false;

        await Poster.DrainSpoolsCoreAsync(
            new HookSpool(_dir), new TranscriptSpool(_tdir), Sid,
            _ => {
                entered = true;
                throw new InvalidOperationException("the drain guard did not run");
            });

        // Non-entry is the proof: the drain's own catch would otherwise hide an exception here.
        await Assert.That(entered).IsFalse();
        // Retention still runs — Program.cs skips this call entirely while the URL is bad, so a
        // reap that lived only past the guard would never happen on a broken config.
        await Assert.That(File.Exists(stale)).IsFalse();
    }

    [Test]
    public async Task SpawnWatcher_never_starts_a_process() {
        var starts = 0;
        WatcherManager.ProcessStarterForTesting = _ => { starts++; return null; };

        await Watchers.SpawnWatcher(Sid, Path.Combine(_dir, "t.jsonl"), agentId: null);

        await Assert.That(starts).IsEqualTo(0);
    }

    [Test]
    public async Task SpawnCopilotFinalizeDrain_never_starts_a_process() {
        // This one writes no marker at all, so "no child left behind" is unfalsifiable — a deleted
        // guard merely lets Process.Start throw or return null, leaving every effect identical.
        var starts = 0;
        WatcherManager.ProcessStarterForTesting = _ => { starts++; return null; };

        Watchers.SpawnCopilotFinalizeDrain(Sid, Path.Combine(_dir, "t.jsonl"));

        await Assert.That(starts).IsEqualTo(0);
    }

    // Globally sequential: this test swaps the process-global Console.Error to capture the
    // diagnostic, so it must not overlap another Console-redirecting test — concurrent capturers
    // save each other's writers as their "original" and restore them mid-flight, sending this
    // assertion's output to the other test's buffer.
    [Test, NotInParallel]
    public async Task InlineDrain_emits_its_own_guard_diagnostic() {
        // A stopwatch assertion here was vacuous: the unguarded path can also return quickly. The
        // proof is the diagnostic, which only this guard emits — distinct from the POST guard's and
        // from the drain guard's, so it cannot be satisfied by a neighbouring path.
        using var capture = ConsoleOutput.StartErrorCapture();

        await Watchers.InlineDrainAsync(Sid, Path.Combine(_dir, "t.jsonl"), agentId: null);

        await Assert.That(capture.GetCapturedError()).Contains($"inline drain skipped for {Sid}");
    }

    // Console again, so globally sequential for the same reason.
    [Test, NotInParallel]
    public async Task InlineDrain_names_the_source_it_was_handed() {
        // The remediation has to follow the source: `kcap config set server_url` does not repair a
        // malformed KCAP_URL. A guard rendering a fixed source passes every other test here.
        using var capture = ConsoleOutput.StartErrorCapture();

        var watchers = new WatcherManager(Config.Root, Resolutions.At(BadUrl, Config.Root, UrlSource.Environment), new FixedCapacitorHttpClient());

        await watchers.InlineDrainAsync(Sid, Path.Combine(_dir, "t.jsonl"), agentId: null);

        await Assert.That(capture.GetCapturedError()).Contains("KCAP_URL");
        await Assert.That(capture.GetCapturedError()).DoesNotContain("kcap config set server_url");
    }

    [Test]
    [Arguments("ses_619a78374ffe7o0x1iTK74jFRg")]
    [Arguments("ses_ABCDEF")]
    [Arguments("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Mixed_case_vendor_ids_are_accepted(string sessionId) {
        // OpenCode ids are base62 and genuinely mixed case; rejecting them would lose the session
        // outright. Case safety on the filesystem is handled by escaping the filename, not by
        // narrowing what is admitted.
        await Assert.That(new HookSpool(_dir).Append(sessionId, "session-start/opencode", "{}")).IsTrue();
    }

    /// <summary>
    /// The gates live in HandleCore, which the degraded arm never reaches — without the extracted
    /// helper an unusable URL would capture a session the user explicitly disabled.
    /// </summary>
    [Test]
    public async Task Suppresses_a_disabled_session_given_a_dashed_payload_id() {
        var dashed   = Guid.NewGuid().ToString();
        var dashless = dashed.Replace("-", "");

        Config.CreateFile(Path.Combine("disabled", dashless));

        // Dashed id in the payload, dashless marker on disk. DisabledSessions does no
        // normalization, so passing the raw payload id straight through would miss it entirely.
        var body = $$"""{"session_id":"{{dashed}}","hook_event_name":"SessionStart"}""";

        await Assert.That(await new ClaudeHookCommand(Config.Root, Resolutions.None(Config.Root), _clock, Home, new FixedCapacitorHttpClient()).ShouldSuppressCaptureAsync(
            dashless, body, "session-start", activeProfile: null, _clock.Budget(Ceiling))).IsTrue();
    }

    [Test]
    public async Task Session_end_suppression_also_clears_the_marker() {
        var sid    = Guid.NewGuid().ToString("N");
        var marker = Config.CreateFile(Path.Combine("disabled", sid));

        var body = $$"""{"session_id":"{{sid}}","hook_event_name":"SessionEnd"}""";

        await Assert.That(await new ClaudeHookCommand(Config.Root, Resolutions.None(Config.Root), _clock, Home, new FixedCapacitorHttpClient()).ShouldSuppressCaptureAsync(
            sid, body, "session-end", activeProfile: null, _clock.Budget(Ceiling))).IsTrue();

        // Collapsing the gate into a plain boolean would have dropped this cleanup.
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task Does_not_suppress_an_ordinary_session() {
        // The negative control: without it, a helper that always returned true would pass above.
        var sid  = Guid.NewGuid().ToString("N");
        var body = $$"""{"session_id":"{{sid}}","hook_event_name":"SessionStart"}""";

        await Assert.That(await new ClaudeHookCommand(Config.Root, Resolutions.None(Config.Root), _clock, Home, new FixedCapacitorHttpClient()).ShouldSuppressCaptureAsync(
            sid, body, "session-start", activeProfile: null, _clock.Budget(Ceiling))).IsFalse();
    }

    /// <summary>Non-entry, not exit 0 — Cursor's outer catch-all also returns 0.</summary>
    [Test]
    public async Task Cursor_never_builds_a_client_for_an_unusable_url() {
        var entered = false;

        var exit = await new CursorHookCommand(Config.Root, Bad, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleWithDeps(
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""),
            _ => {
                entered = true;
                throw new InvalidOperationException("the cursor guard did not run");
            },
            () => new HookSpool(_dir));

        await Assert.That(entered).IsFalse();
        await Assert.That(exit).IsEqualTo(0);
    }

    /// <summary>
    /// Non-entry: deleting the guard lets <c>CreateClientWithinBudgetAsync</c> swallow the exception
    /// and take the same degraded branch, so every effect assertion still passes.
    /// </summary>
    [Test]
    public async Task Claude_never_builds_a_client_for_an_unusable_url() {
        var entered = false;

        var exit = await new ClaudeHookCommand(Config.Root, Bad, new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleWithDeps(
            new HookSpool(_dir),
            stdin: new StringReader($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}"}"""),
            clientFactory: () => {
                entered = true;
                throw new InvalidOperationException("the claude guard did not run");
            });

        await Assert.That(entered).IsFalse();
        await Assert.That(exit).IsEqualTo(0);
    }
}
