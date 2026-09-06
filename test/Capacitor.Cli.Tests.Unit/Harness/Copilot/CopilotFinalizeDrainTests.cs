using System.Text.Json;
using Capacitor.Cli.Commands.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Pure detection logic for the Copilot finalize drain: the drainer
/// treats a terminal <c>session.shutdown</c> line as "the tail is complete".
/// </summary>
public class CopilotFinalizeDrainLastLineTests {
    const string ShutdownLine =
        """{"type":"session.shutdown","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":52320,"cacheReadTokens":34736,"cacheWriteTokens":17579}}}},"id":"evt-shutdown","timestamp":1234567890}""";

    const string AssistantLine =
        """{"type":"assistant.message","data":{"content":"all done"},"id":"evt-asst","timestamp":1234567000}""";

    static string WriteTemp(TempDir tmp, string contents) =>
        tmp.CreateFile("events.jsonl", contents);

    [Test]
    public async Task True_WhenTerminalLineIsShutdown() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + ShutdownLine + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsTrue();
    }

    [Test]
    public async Task True_WithTrailingBlankLines() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + ShutdownLine + "\n\n   \n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsTrue();
    }

    [Test]
    public async Task False_WhenTerminalLineIsOtherType() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, ShutdownLine + "\n" + AssistantLine + "\n");

        // Resume-safety: a prior run's shutdown sits mid-file with later events
        // after it; only the genuine terminal shutdown should trip detection.
        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }

    [Test]
    public async Task False_ForMissingFile() {
        using var pathDir = TempDir.WithPathTo("kcap_missing.jsonl", out var path);

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }

    [Test]
    public async Task False_ForMalformedTerminalLine() {
        using var tmp = new TempDir();
        var path = WriteTemp(tmp, AssistantLine + "\n" + "{not json" + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.LastLineIsShutdown(path)).IsFalse();
    }
}

/// <summary>
/// End-to-end timing behaviour of the Copilot finalize drain, driven
/// through the testable <see cref="CopilotFinalizeDrainCommand.RunAsync"/> seam
/// against a mock server. Mirrors the existing InlineDrain WireMock harness:
/// auth discovery degrades to "None" (no live server at the resolved default),
/// so requests flow without a token.
/// </summary>
public class CopilotFinalizeDrainRunTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() {
        _server.Stop();
    }

    const string ShutdownLine =
        """{"type":"session.shutdown","data":{"modelMetrics":{"gpt-5":{"usage":{"inputTokens":52320,"cacheReadTokens":34736,"cacheWriteTokens":17579}}}},"id":"evt-shutdown","timestamp":1234567890}""";

    const string AssistantLine =
        """{"type":"assistant.message","data":{"content":"final turn"},"id":"evt-asst","timestamp":1234567000}""";

    void StubServer(string sessionId) {
        // last_line_number: -1 → drain resumes from line 0 (send everything).
        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    static string NewTranscript(TempDir tmp, string contents) =>
        tmp.CreateFile("events.jsonl", contents);

    bool TranscriptPostContainsType(string type) {
        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        foreach (var req in requests) {
            var root = JsonDocument.Parse(req.RequestMessage.Body!).RootElement;

            foreach (var line in root.GetProperty("lines").EnumerateArray()) {
                if (line.GetString() is not { } s) continue;

                try {
                    using var doc = JsonDocument.Parse(s);

                    if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == type) {
                        return true;
                    }
                } catch {
                    // skip non-JSON lines
                }
            }
        }

        return false;
    }

    int TranscriptPostCount() =>
        _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost()).Count;

    [Test]
    public async Task DeliversShutdown_WhenAlreadyPresent() {
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-present";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n" + ShutdownLine + "\n");

        await new CopilotFinalizeDrainCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()).RunAsync(
            sessionId, path,
            pollBudget: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100)
        );

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsTrue();
    }

    [Test]
    public async Task DeliversShutdown_WhenWrittenAfterDelay() {
        // Models the real timing: the finalizer starts polling while the hook
        // tail is still being flushed, and session.shutdown only lands later.
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-delayed";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n");

        var runTask = new CopilotFinalizeDrainCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()).RunAsync(
            sessionId, path,
            pollBudget: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(100)
        );

        await Task.Delay(600);
        await File.AppendAllTextAsync(path, ShutdownLine + "\n");

        await runTask;

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsTrue();
    }

    [Test]
    public async Task TimeoutFallback_DrainsExistingTail_WhenShutdownNeverArrives() {
        // Copilot crash / shutdown never written: the budget elapses but the
        // finalizer must still deliver the final assistant turn it can see
        // (the secondary risk — a dropped final turn).
        using var tmp = new TempDir();
        const string sessionId = "test-finalize-timeout";
        StubServer(sessionId);

        var path = NewTranscript(tmp, AssistantLine + "\n");

        await new CopilotFinalizeDrainCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient()).RunAsync(
            sessionId, path,
            pollBudget: TimeSpan.FromMilliseconds(700),
            pollInterval: TimeSpan.FromMilliseconds(200)
        );

        await Assert.That(TranscriptPostCount()).IsEqualTo(1);
        await Assert.That(TranscriptPostContainsType("assistant.message")).IsTrue();
        await Assert.That(TranscriptPostContainsType("session.shutdown")).IsFalse();
    }
}
