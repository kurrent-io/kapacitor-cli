using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Verifies that <see cref="ClaudeHookCommand"/> stamps the
/// <c>default_visibility</c> from the active V2 profile onto the
/// session-start payload — not from the legacy top-level config
/// shape, which would silently fall back to <c>org_public</c> on
/// every current (v2) config and ignore per-profile <c>private</c>
/// settings.
/// </summary>
public class SessionStartVisibilityTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static string SessionStartPayloadWithoutTranscriptPath() =>
        """
        {
          "cwd":             "/tmp/test",
          "model":           "claude-sonnet-4-6",
          "source":          "startup",
          "hook_event_name": "session-start"
        }
        """;

    [Test]
    public async Task Stamps_private_visibility_from_active_profile_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "private"
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task Lowercases_mixedcase_visibility_from_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "Private"
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task Falls_back_to_org_public_when_v2_config_visibility_is_invalid() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl         = _server.Url,
                    DefaultVisibility = "totally-bogus"
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("org_public");
    }

    // Surface 3: the session-start body carries this machine's harness inventory (machine id + all
    // nine vendors) — the hook-ingest carrier the server reads when no daemon runs. Same fragment
    // shape the daemon sends on its status report.
    [Test]
    public async Task Stamps_harness_inventory_onto_session_start_body() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(SessionStartPayloadWithoutTranscriptPath()));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        var inv  = body["harness_inventory"];
        await Assert.That(inv).IsNotNull();
        await Assert.That(string.IsNullOrEmpty(inv!["machine_id"]?.GetValue<string>())).IsFalse();
        await Assert.That(inv["vendors"]!.AsObject().Count).IsEqualTo(9);
        await Assert.That(inv["vendors"]!["claude"]!["wired"]).IsNotNull(); // per-vendor {detected,wired} shape
    }

    [Test]
    public async Task Skips_session_start_when_repo_is_excluded_by_active_profile_v2_config() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile {
                    ServerUrl     = _server.Url,
                    ExcludedRepos = ["acme/secret-repo"]
                }
            }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        const string payload =
            """
            {
              "cwd":             "/tmp/test",
              "model":           "claude-sonnet-4-6",
              "source":          "startup",
              "hook_event_name": "session-start",
              "repository":      { "owner": "acme", "repo_name": "secret-repo" }
            }
            """;

        await new ClaudeHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(0);
    }
}
