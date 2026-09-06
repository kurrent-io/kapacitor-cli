using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins that <c>--private</c> membership does not depend on what an import made of a session.
///
/// <para><b>Why it cannot depend on the outcome.</b> A <c>default_visibility</c> stamp decides a
/// session's visibility at creation only — the read model's import-overlap branch omits that column
/// from its update — so for a session this run merely revisits, the closing <c>visibility=none</c>
/// pass is the only mechanism there is. Anything that lets an outcome decide whether that pass
/// reaches a session leaves history org-visible for good.
/// </para>
///
/// <para><b>The probe is the hardest case for that.</b> It is deliberately contradictory —
/// <see cref="IImportSource.AttachesChildContentOnReplay"/> is <c>false</c> while the call reports
/// <c>SentChildContent: true</c> — a combination no shipped source has. That keeps it out of
/// <c>privateScopeSessionIds</c>, and its <c>Skipped</c> raw outcome keeps it out of
/// <c>importedSessionIds</c>; only the in-scope capture reaches it. The Loaded-count assertion is
/// kept alongside so the counting override is visibly firing, which is what stops the PUT assertion
/// passing for some unrelated reason.
/// </para>
///
/// <para>
/// This exercises the non-TTY renderer only, which is sound because the capture and the membership
/// decision live in <c>HandleImport</c> for both renderers, which differ only in how they draw. If
/// that bookkeeping is ever inlined back into the two <c>Parallel.ForEachAsync</c> bodies, this test
/// stops covering the TTY branch and a TTY variant becomes necessary.
/// </para>
/// </summary>
public class RoutedPrivatizeMembershipTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const HarnessId ProbeVendor = HarnessId.Kiro;
    const string ProbeSessionId = "9f9f0000000040008000000000000f0f";

    /// <summary>
    /// An <c>AlreadyLoaded</c> replay whose own outcome is <c>Skipped</c> but which attached
    /// brand-new nested-child content, while declaring it cannot — so neither the raw-outcome set
    /// nor the child-content scope holds it, and the in-scope capture is the only thing left.
    /// </summary>
    sealed class ChildContentOutsidePrivateScopeSource : IImportSource {
        public HarnessId Vendor                   => ProbeVendor;
        public bool   IsAvailable                 => true;
        public bool   SupportsTitleGeneration     => false;
        public bool   AttachesChildContentOnReplay => false;

        public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DiscoveredSession>>([
                new DiscoveredSession(ProbeSessionId, Vendor, Cwd: null, FirstTimestamp: null,
                    SourceMeta: new Dictionary<string, object?>())
            ]);

        public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
                IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ImportCommand.SessionClassification>>([
                new ImportCommand.SessionClassification {
                    SessionId  = ProbeSessionId,
                    FilePath   = "", // empty FilePath => routed phase, not the chain phase
                    EncodedCwd = "",
                    Meta       = new SessionMetadata(),
                    Status     = ImportCommand.ClassificationStatus.AlreadyLoaded,
                    TotalLines = 2,
                    SourceMeta = new Dictionary<string, object?>(),
                    Vendor     = Vendor,
                }
            ]);

        public Task<ImportSessionResult> ImportSessionAsync(
                ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) =>
            Task.FromResult(new ImportSessionResult(ImportOutcome.Skipped, SentChildContent: true));
    }

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        using var capture = ConsoleOutput.StartCapture();
        await action();
        return capture.GetCapturedOutput();
    }

    static bool LineMatches(string text, string label, int value) =>
        Regex.IsMatch(text, $@"(?m)^\s*{Regex.Escape(label)}\s+{value}\s*$");

    [Test, NotInParallel]
    public async Task a_routed_replay_is_not_imported_when_it_could_not_be_made_private_first() {
        // The routed half of the fail-closed preflight. The probe is an existing session, so it is
        // narrowed before any content moves; when that write is lost it must be dropped from the run
        // rather than replayed into while still carrying its old audience.
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        var stdout = await CaptureStdoutAsync(async () => {
            await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
                filterCwd: null,
                minLines: 0,
                sources: [new ChildContentOutsidePrivateScopeSource()],
                scope: new ImportScope.All(),
                skipConfirmation: true,
                forcePrivate: true
            );
        });

        await Assert.That(stdout).DoesNotContain($"Loading {ProbeSessionId}")
                    .Because("its import must not run at all once the floor could not be established");
    }

    [Test, NotInParallel]
    public async Task a_skipped_replay_outside_the_child_content_scope_is_still_privatized() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        var exitCode = 0;
        var stdout = await CaptureStdoutAsync(async () => {
            exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, new FixedCapacitorHttpClient()).HandleImport(
                filterCwd: null,
                minLines: 0,
                sources: [new ChildContentOutsidePrivateScopeSource()],
                scope: new ImportScope.All(),
                skipConfirmation: true,
                forcePrivate: true
            );
        });

        await Assert.That(exitCode).IsEqualTo(0);

        // The override DID fire: the raw outcome is Skipped, yet the run counts it as Loaded and
        // prints the "Loading" line. Without this the zero-PUT assertion below would pass
        // vacuously — a plain suppressed no-op replay also emits no PUT.
        await Assert.That(stdout).Contains($"Loading {ProbeSessionId} ({ProbeVendor.VendorId})");
        var doneIdx = stdout.IndexOf("== Done ==", StringComparison.Ordinal);
        await Assert.That(doneIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(LineMatches(stdout[doneIdx..], "Loaded", 1)).IsTrue();

        // ...and the session was privatized anyway, by the in-scope capture rather than by anything
        // the import concluded. Nothing else could have: the raw outcome is Skipped and the source
        // declares no child content on replay.
        // Which sessions, not how many writes: an existing session is narrowed before the content and
        // again by the closing pass, so the count is a property of that two-phase design.
        var privatized = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .Select(e => e.RequestMessage.Path)
            .Distinct()
            .ToList();

        await Assert.That(privatized).IsEquivalentTo([$"/api/sessions/{ProbeSessionId}/visibility"]);
    }
}
