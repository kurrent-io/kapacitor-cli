using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap import --skip-title</c>: the opt-out `kcap watch` has always had for the same generator.
/// </summary>
/// <remarks>
/// The fake `claude` on PATH shadows a real, authenticated one — the positive control must prove a
/// title is posted, without that costing a live model call.
/// </remarks>
// Mutates PATH, and joins the vendor-override cohort because those suites resolve `kcap` and the
// vendor CLIs through PATH — a phantom `claude` is exactly what would mislead them.
[NotInParallel("VendorEnvOverrides")]
public class ImportSkipTitleTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    // The prompt reaches `claude` as one ~1.4KB argv element full of newlines. A .cmd shim gets it via
    // `cmd.exe /c`, whose parser does not survive embedded LF, so the fake cannot answer on Windows and
    // the control would fail for a reason that has nothing to do with the flag. The skip assertion
    // below is the load-bearing one and runs everywhere.
    [Test]
    public async Task import_posts_a_title_per_session_by_default() {
        Skip.When(OperatingSystem.IsWindows(), "a .cmd shim cannot receive the multi-line title prompt");

        using var fakeCli = new FakeClaudeOnPath();
        StubHooks();

        var exit = await RunImport("titles-on", skipTitle: false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(TitlePostCount())
                    .IsGreaterThan(0)
                    .Because("without the flag import titles each session — if this stops holding, the "
                           + "skip-title assertion below proves nothing");
    }

    [Test]
    public async Task skip_title_posts_no_title() {
        using var fakeCli = new FakeClaudeOnPath();
        StubHooks();

        var exit = await RunImport("titles-off", skipTitle: true);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(TitlePostCount())
                    .IsEqualTo(0)
                    .Because("--skip-title exists so a run spends nothing on the user's own agent quota");
    }

    Task<int> RunImport(string name, bool skipTitle) {
        var projectsDir = _tmp.CreateDir(name);

        // Real user text, so the generator gets past its own "nothing to title" skip.
        projectsDir.CreateDir("-tmp-skip-title-proj").CreateFile(
            $"{name}-session.jsonl",
            [.. Enumerable.Range(0, 20).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/skip-title-proj","message":{"content":"add a retry to the import loop {{{i}}}"}}""")]);

        return new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient())
            .HandleImport(
            filterCwd:        null,
            minLines:         1,
            sources:          [new ClaudeImportSource(Config.Root, projectsDir.Path)],
            scope:            new ImportScope.All(),
            skipConfirmation: true,
            skipTitle:        skipTitle);
    }

    int TitlePostCount() =>
        _server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/session-title");

    void StubHooks() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] {
                     "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start",
                     "/hooks/subagent-stop", "/hooks/session-title" })
            _server.Given(Request.Create().WithPath(path).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));

        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    /// <summary>
    /// Puts a `claude` on PATH that prints a title and exits 0. The runner falls back to treating
    /// non-JSON stdout as the result, so a one-line script satisfies it.
    /// </summary>
    sealed class FakeClaudeOnPath : IDisposable {
        readonly TempDir _bin;
        readonly string? _previousPath;

        public FakeClaudeOnPath() {
            _bin          = new TempDir();
            _previousPath = Environment.GetEnvironmentVariable("PATH");

            var script = _bin.CreateFile("claude", "#!/bin/sh\necho 'Retry the import loop'\n");

            // Only the Unix-only positive control executes this; the guard is for the compiler.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Environment.SetEnvironmentVariable("PATH", _bin.Path + Path.PathSeparator + _previousPath);
        }

        public void Dispose() {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
            _bin.Dispose();
        }
    }
}
