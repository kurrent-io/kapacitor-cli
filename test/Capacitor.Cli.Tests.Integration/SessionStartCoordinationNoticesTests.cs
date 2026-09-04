using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end coverage for the SessionStart coordination-notices lane, the client half of the
/// server's terminal-delivery lane (server ships first, capability-gated + inert until this):
/// <list type="bullet">
///   <item>the CLI advertises the <c>coordination_notices: "v1"</c> capability on the LIVE
///     Claude/generic session-start POST body;</item>
///   <item>when the server returns <c>coordination_notices: [{text}]</c>, the CLI injects them
///     into the SessionStart <c>additionalContext</c> envelope next to the team-memory index;</item>
///   <item>the <c>disable_coordination_notices</c> opt-out (mirroring <c>disable_memory_index</c>)
///     suppresses BOTH the capability and any render;</item>
///   <item>a malformed field never fails the hook (fail-open).</item>
/// </list>
///
/// Config is saved/restored per test (mirrors <see cref="SessionStartVisibilityTests"/>) so a
/// profile write can't leak across tests; payloads omit <c>transcript_path</c>/<c>session_id</c>
/// so no watcher spawns and no memory-index GET fires.
/// </summary>
public class SessionStartCoordinationNoticesTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server     = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static string Payload() =>
        // No transcript_path / session_id → WatcherManager spawn AND memory-index GET are skipped.
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

    async Task ConfigureProfileAsync(bool? disableCoordinationNotices) {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl                  = _server.Url,
                    DisableCoordinationNotices = disableCoordinationNotices
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);
    }

    void GivenServerReturns(string bodyJson) =>
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody(bodyJson));

    JsonNode PostedBody() {
        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        return JsonNode.Parse(requests.Single().RequestMessage.Body!)!;
    }

    [Test]
    public async Task Advertises_the_capability_and_renders_returned_notices() {
        await ConfigureProfileAsync(disableCoordinationNotices: null);
        GivenServerReturns(
            """
            {
              "coordination_notices": [
                { "text": "Priya is also working on the checkout refactor (SHOP-88)." },
                { "text": "+1 more in the notification centre" }
              ]
            }
            """);

        var stdout = new StringWriter();
        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(Payload()), stdout: stdout);

        // Capability advertised on the request.
        await Assert.That(PostedBody()["coordination_notices"]?.GetValue<string>()).IsEqualTo("v1");

        // Notices rendered into the SessionStart envelope.
        var json = JsonNode.Parse(stdout.ToString().Trim());
        var ctx  = json!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).Contains("## Coordination notices");
        await Assert.That(ctx).Contains("- Priya is also working on the checkout refactor (SHOP-88).");
        await Assert.That(ctx).Contains("- +1 more in the notification centre");
    }

    [Test]
    public async Task Opt_out_suppresses_capability_and_render() {
        await ConfigureProfileAsync(disableCoordinationNotices: true);
        GivenServerReturns("""{ "coordination_notices": [ { "text": "someone else is on this bug" } ] }""");

        var stdout = new StringWriter();
        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(Payload()), stdout: stdout);

        await Assert.That(PostedBody()["coordination_notices"]).IsNull();
        await Assert.That(stdout.ToString()).DoesNotContain("## Coordination notices");
        await Assert.That(stdout.ToString()).DoesNotContain("someone else is on this bug");
    }

    [Test]
    public async Task Malformed_notices_field_does_not_fail_the_hook() {
        await ConfigureProfileAsync(disableCoordinationNotices: null);
        // Server echoes the capability token back as a bare string instead of the {text}[] array.
        GivenServerReturns("""{ "coordination_notices": "v1" }""");

        var stdout = new StringWriter();
        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(Payload()), stdout: stdout);

        // Capability still advertised; render silently emits nothing (fail-open, no crash).
        await Assert.That(PostedBody()["coordination_notices"]?.GetValue<string>()).IsEqualTo("v1");
        await Assert.That(stdout.ToString()).DoesNotContain("## Coordination notices");
    }
}
