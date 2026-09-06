using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A live Cursor <c>sessionStart</c> hook stamps the active profile's <c>default_visibility</c>
/// onto the payload (mirrors <c>CodexSessionStartVisibilityTests</c>). <c>workspace_roots</c> is
/// set so the enrichment reserialization actually runs — the round-trip the stamp must survive.
/// See #579.
/// </summary>
public class CursorSessionStartVisibilityTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server     = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    async Task<JsonNode> RunSessionStartAndCaptureBodyAsync(string defaultVisibility) {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = defaultVisibility
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        // Best-effort side calls (memory index, auth) must never fail the test.
        _server.Given(Request.Create().WithPath("/api/*").UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        // workspace_roots present → the enrichment reparse runs (the round-trip the stamp must
        // survive); transcript_path omitted → no watcher spawn.
        using var tmp = new TempDir();
        var body =
            $$"""
            {
              "hook_event_name": "sessionStart",
              "session_id":      "cursorvistestsession",
              "model":           "claude-3.5-sonnet",
              "workspace_roots": ["{{tmp.Path.Replace("\\", "\\\\")}}"]
            }
            """;

        using var client = new HttpClient();
        var spool = new HookSpool(tmp.CreateDir("spool").Path);

        var exit = await new CursorHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).HandleCore(client, new StringReader(body), spool);
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/cursor").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        return JsonNode.Parse(requests[0].RequestMessage.Body!)!;
    }

    [Test]
    public async Task SessionStart_stamps_default_visibility_from_active_profile() {
        var body = await RunSessionStartAndCaptureBodyAsync("private");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task SessionStart_stamps_the_profiles_configured_visibility_value() {
        // Fidelity: the stamped value is the profile's configured visibility, not a hardcoded
        // constant — a different profile value must round-trip verbatim.
        var body = await RunSessionStartAndCaptureBodyAsync("public");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("public");
    }
}
