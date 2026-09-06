using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// What <c>kcap import --&lt;vendor&gt;</c> says about a harness it was asked for and could not find. The
/// names it prints are the ids a user types, not the enum the code carries them as — a script greps
/// these lines, and a user retypes what they read into the next flag.
/// </summary>
// Console is process-global.
[NotInParallel]
public class ImportVendorSelectionOutputTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    /// <summary>Discovers nothing, so the run reaches the selection messages and stops.</summary>
    sealed record Source(HarnessId Vendor, bool IsAvailable) : IImportSource {
        public bool SupportsTitleGeneration      => false;
        public bool AttachesChildContentOnReplay => false;

        public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredSession>>([]);

        public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
                IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportCommand.SessionClassification>>([]);

        public Task<ImportSessionResult> ImportSessionAsync(
                ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) =>
            Task.FromResult(new ImportSessionResult(ImportOutcome.Skipped));
    }

    Task<int> Run(params IImportSource[] sources) =>
        new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient())
            .HandleImport(
            filterCwd:               null,
            sources:                 sources,
            explicitVendorSelection: true,
            skipConfirmation:        true);

    [Test]
    public async Task a_named_harness_this_machine_lacks_is_reported_by_its_id() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Run(new Source(HarnessId.Claude, IsAvailable: true),
                             new Source(HarnessId.OpenCode, IsAvailable: false));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capture.GetCapturedError())
                    .Contains("Skipping opencode (not detected on this machine).");
    }

    [Test]
    public async Task nothing_named_being_present_lists_what_was_asked_for_as_flags() {
        using var capture = ConsoleOutput.StartErrorCapture();

        var exit = await Run(new Source(HarnessId.Cursor, IsAvailable: false),
                             new Source(HarnessId.Antigravity, IsAvailable: false));

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(capture.GetCapturedError())
                    .Contains("--cursor, --antigravity specified but no matching installation detected on this machine.");
    }
}
