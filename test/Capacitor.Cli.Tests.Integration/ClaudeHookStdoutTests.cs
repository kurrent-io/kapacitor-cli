using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Setup;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Exercises <see cref="ClaudeHookCommand.Handle"/> end-to-end against a
/// WireMock server and validates the SessionStart <c>hookSpecificOutput</c>
/// envelope shape — including the single-envelope invariant when both
/// <c>top_clusters</c> and <c>version</c> are present.
///
/// Stdout is captured via an explicit <see cref="TextWriter"/> passed to
/// <c>Handle</c> (its <c>stdout</c> parameter), not by redirecting the global
/// <c>Console.Out</c> — so another concurrently-running test's <c>Console.Out</c>
/// write can't leak into the capture. The tests stay <c>[NotInParallel]</c>
/// because they still touch process-global config/auth-cache and real hook HTTP
/// timing; the injected writer is the durable fix for the contamination class.
///
/// Test payloads deliberately OMIT <c>transcript_path</c> so the session-start
/// path short-circuits before <c>WatcherManager.EnsureWatcherRunning</c>;
/// spawning the watcher's child process would still corrupt capture.
///
/// The per-test config root is load-bearing, not hygiene: a developer-side
/// <c>excluded_paths</c> entry covering the test <c>cwd</c> would silently
/// short-circuit <c>ClaudeHookCommand</c> and make these tests pass for the
/// wrong reason.
/// </summary>
public class ClaudeHookStdoutTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    // The harness nudge fires unless an on-disk stamp throttles it, and a private root starts with
    // none — these tests assert on the envelope alone, so they claim the window themselves.
    [Before(Test)]
    public void ThrottleHarnessNudge() =>
        new HarnessOfferStore(Config.Root).TryClaimCheck(HarnessNudgeEmitter.CheckThrottle);

    static string SessionStartPayloadWithoutTranscriptPath() =>
        // No transcript_path, no session_id → WatcherManager spawn is skipped.
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

    // Capture hook stdout via the injected writer — no Console.SetOut, so nothing another
    // concurrently-running test writes to Console can contaminate it.
    async Task<string> RunSessionStartAsync() {
        var stdout = new StringWriter();
        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(SessionStartPayloadWithoutTranscriptPath()), stdout: stdout);
        return stdout.ToString();
    }

    [Test, NotInParallel]
    public async Task Emits_nudge_envelope_when_server_returns_newer_version_only() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{ "version": "999.0.0" }""")
            );

        var stdout = await RunSessionStartAsync();

        var trimmed = stdout.Trim();
        await Assert.That(trimmed).IsNotEmpty();
        var json = JsonNode.Parse(trimmed);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");

        var ctx = json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("kcap update");
        await Assert.That(ctx).DoesNotContain("## Known patterns");
        await Assert.That(ctx).DoesNotContain("## Guidance from past sessions");
    }

    [Test, NotInParallel]
    public async Task Emits_combined_envelope_when_server_returns_top_clusters_and_newer_version() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(
                        """
                        {
                          "version": "999.0.0",
                          "top_clusters": [
                            { "category": "safety", "text": "always close the writer" }
                          ]
                        }
                        """
                    )
            );

        var stdout = await RunSessionStartAsync();

        var trimmed = stdout.Trim();

        // Single-envelope invariant.
        await Assert.That(trimmed).IsNotEmpty();
        var firstClose = trimmed.LastIndexOf('}');
        var afterClose = trimmed[(firstClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");

        var json = JsonNode.Parse(trimmed);
        var ctx  = json!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        // "safety" is not agent_guidance, so the cluster lands in the patterns block.
        await Assert.That(ctx).Contains("## Known patterns");
        await Assert.That(ctx).Contains("- always close the writer");
        await Assert.That(ctx).Contains("999.0.0");
        await Assert.That(ctx).Contains("kcap update");
    }

    [Test, NotInParallel]
    public async Task Emits_nothing_when_server_returns_empty_object() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var stdout = await RunSessionStartAsync();

        await Assert.That(stdout.Trim()).IsEqualTo("");
    }
}
