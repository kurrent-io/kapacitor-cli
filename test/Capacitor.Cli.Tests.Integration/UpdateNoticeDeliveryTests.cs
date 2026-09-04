using System.Diagnostics;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end coverage of the deterministic exit-time update notice
/// (<c>Capacitor.Cli.UpdateNotice</c>): a human-facing invocation prints the hint on stderr
/// before the process exits, the suppressed population (hooks, <c>mcp</c>, <c>watch</c>,
/// <c>--no-update-check</c>, <c>update_check: false</c>) prints nothing, and the notice still
/// fires on the <c>--help</c> and no-server-configured early exits — both of which return before
/// the command dispatch <c>switch</c>, and must still fall through <c>Program.cs</c>'s outer
/// <c>finally</c>.
///
/// <para>Each case runs the real compiled binary in an isolated child process with its own
/// <c>KCAP_CONFIG_DIR</c> (mirroring <see cref="UnusableUrlHookMatrixTests"/>). A stub registry is
/// no use here: the child resolves its own registry client from its own container, so nothing the
/// parent registers reaches it. Instead, each
/// case pre-seeds the on-disk update-check cache file directly with a fresh, already-newer
/// record — the exact on-disk shape <c>UpdateCommand.UpdateCacheRecord</c> reads via its
/// <c>IsFresh</c> cache-hit path — so the child's check resolves from that local file without
/// ever touching the network, keeping these tests fast and deterministic while still exercising
/// the real process, the real cache-read path, and the real predicate wiring in
/// <c>Program.cs</c>.</para>
/// </summary>
public class UpdateNoticeDeliveryTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string NewerVersion = "999.0.0"; // deterministically newer than any real build's version.

    readonly List<(TempConfigRoot CfgDir, Process Process)> _spawned = [];

    public void Dispose() {
        foreach (var (cfgDir, p) in _spawned) {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
            cfgDir.Dispose();
        }
    }

    // --- Positive: a human-facing command prints the hint before exit ---

    [Test]
    public async Task HumanFacingCommand_PrintsTheTwoLineHint_WhenANewerVersionIsCached() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["config", "show"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stderr).Contains($"Update available:");
        await Assert.That(r.Stderr).Contains(NewerVersion);
        await Assert.That(r.Stderr).Contains("Run `kcap update` to update");
    }

    // --- Structural: --help and the no-server path fall through the same finally ---

    [Test]
    public async Task HelpCommand_StillFlushesTheNotice() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["--help"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).Contains("kcap");
        await Assert.That(r.Stderr).Contains("Update available:");
    }

    [Test]
    public async Task NoServerConfigured_StillFlushesTheNotice() {
        var cfgDir = SeedFreshNewerCache();

        // "whoami" needs a server URL and is not in Program.cs's offlineCommands list, so with
        // no KCAP_URL and no profile server_url it hits the early "No server configured" return
        // — well before the command switch — and must still flush from the outer finally.
        var r = await RunAsync(["whoami"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(1);
        await Assert.That(r.Stderr).Contains("No server configured");
        await Assert.That(r.Stderr).Contains("Update available:");
    }

    // --- Negative: the suppressed population prints nothing, even with the same newer-version cache ---

    [Test]
    public async Task Hook_PrintsNothing() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["hook"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(1);
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    [Test]
    public async Task Mcp_PrintsNothing() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["mcp"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(1);
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    [Test]
    public async Task Watch_PrintsNothing() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["watch"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(1);
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    [Test]
    public async Task NoUpdateCheckFlag_PrintsNothing() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["config", "show", "--no-update-check"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    [Test]
    public async Task ProfileUpdateCheckDisabled_PrintsNothing() {
        var cfgDir = SeedFreshNewerCache();
        cfgDir.CreateFile("config.json", """
            {"version":2,"active_profile":"default","profiles":{"default":{"update_check":false}},"profile_bindings":{},"cwd_remap":[]}
            """);

        var r = await RunAsync(["config", "show"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    // --- kcap status: the Version line reuses the same shared check and single-reports ---

    /// <summary>
    /// <c>kcap status</c> is itself human-facing (it is not in the suppressed population), so
    /// without the <see cref="Capacitor.Cli.UpdateNotice.MarkReported"/> wiring both the Version
    /// line's own inline annotation AND <c>Program.cs</c>'s outer-<c>finally</c> footer would
    /// print the "newer version available" information — once inline (stdout) and once as the
    /// two-line footer (stderr). Counting case-insensitive occurrences of "update available"
    /// across BOTH streams pins that it happens exactly once; checking only one stream would
    /// miss a regression that moved (rather than duplicated) the print.
    /// </summary>
    [Test]
    public async Task Status_PrintsInlineUpdateAnnotation_AndSuppressesTheExitFooter() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["status"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).Contains("kcap ");
        await Assert.That(r.Stdout).Contains($"(update available: {NewerVersion})");
        await Assert.That(r.Stderr).DoesNotContain("Update available:");

        var combined  = r.Stdout + r.Stderr;
        var occurrences = CountOccurrences(combined, "update available");
        await Assert.That(occurrences).IsEqualTo(1);
    }

    /// <summary>
    /// <c>--no-update-check</c> must skip the check entirely for the Version line too — not just
    /// suppress the print of an already-performed check — so the line prints the bare version
    /// with no annotation even though the seeded cache reports a newer version.
    /// </summary>
    [Test]
    public async Task Status_NoUpdateCheckFlag_PrintsBareVersion_NoAnnotation() {
        var cfgDir = SeedFreshNewerCache();

        var r = await RunAsync(["status", "--no-update-check"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).Contains("kcap ");
        await Assert.That(r.Stdout).DoesNotContain("update available");
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    /// <summary>Same opt-out, via the profile's persisted <c>update_check: false</c> instead of the flag.</summary>
    [Test]
    public async Task Status_ProfileUpdateCheckDisabled_PrintsBareVersion_NoAnnotation() {
        var cfgDir = SeedFreshNewerCache();
        cfgDir.CreateFile("config.json", """
            {"version":2,"active_profile":"default","profiles":{"default":{"update_check":false}},"profile_bindings":{},"cwd_remap":[]}
            """);

        var r = await RunAsync(["status"], cfgDir);

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).Contains("kcap ");
        await Assert.That(r.Stdout).DoesNotContain("update available");
        await Assert.That(r.Stderr).DoesNotContain("Update available:");
    }

    static int CountOccurrences(string haystack, string needle) {
        var count = 0;
        var idx   = 0;

        while ((idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0) {
            count++;
            idx += needle.Length;
        }

        return count;
    }

    // --- helpers ---

    /// <summary>
    /// Creates a fresh isolated config dir and pre-seeds it with an already-newer,
    /// just-checked cache record for the default ("latest") channel — the exact shape
    /// <c>UpdateCommand.UpdateCacheRecord.ToJson()</c> writes — so the child process's check
    /// resolves from the cache-fresh path (a local file read) without any network round-trip.
    /// </summary>
    static TempConfigRoot SeedFreshNewerCache() {
        var cfgDir = new TempConfigRoot();

        var checkedAt = DateTimeOffset.UtcNow.ToString("O");
        cfgDir.CreateFile("update-check-latest.json",
            $$"""{"latest_version":"{{NewerVersion}}","checked_at":"{{checkedAt}}","attempted_at":null,"failed":false}""");

        return cfgDir;
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] args, TempConfigRoot cfgDir) {
        var psi = KcapProcess.StartInfo(Daemons.Store, cfgDir.Root, args);
        psi.WorkingDirectory = cfgDir.Directory;
        psi.Environment["KCAP_URL"] = "";
        psi.Environment["KCAP_SESSION_ID"] = "";

        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add((cfgDir, process));

        try { process.StandardInput.Close(); } catch (IOException) { }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, stdout, stderr);
    }
}
