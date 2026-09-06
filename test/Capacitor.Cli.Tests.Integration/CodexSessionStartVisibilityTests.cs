using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Codex sessions must honor the active V2 profile's
/// <c>default_visibility</c> and <c>excluded_repos</c> settings, the
/// same way the Claude hook does. Without this, Codex sessions
/// silently default to org-visible (the server treats null as the
/// org-visibility fallback) and ignore per-profile exclusions.
/// </summary>
public class CodexSessionStartVisibilityTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server         = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SessionStart_stamps_default_visibility_from_active_profile() {
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

        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        // No transcript_path → HandleSessionStart short-circuits before the
        // watcher spawn (mirrors ClaudeHookStdoutTests' approach).
        const string payload =
            """
            {
              "hook_event_name": "SessionStart",
              "session_id":      "abc",
              "cwd":             "/tmp",
              "model":           "gpt-5"
            }
            """;

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    // Globally sequential ([NotInParallel] with no group key): this test
    // captures Console.Out. Group keys don't prevent cross-group Console
    // interleaving — see the comment block at the top of
    // test/Capacitor.Cli.Tests.Unit/Codex/CodexHookCommandTests.cs.
    [Test, NotInParallel]
    public async Task SessionStart_for_excluded_repo_is_skipped_and_marks_session_disabled() {
        const string excludedSessionId = "codexexclusiontestsess";

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

        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var payload =
            $$"""
              {
                "hook_event_name": "SessionStart",
                "session_id":      "{{excludedSessionId}}",
                "cwd":             "/tmp",
                "repository":      { "owner": "acme", "repo_name": "secret-repo" }
              }
              """;

        using var capture = ConsoleOutput.StartCapture();

        try {
            var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));
            await Assert.That(exit).IsEqualTo(0);

            // No /hooks/session-start/codex POST.
            var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
            await Assert.That(requests.Count).IsEqualTo(0);

            // Codex's SessionStart parser rejects empty stdout.
            var doc = System.Text.Json.JsonDocument.Parse(capture.GetCapturedOutput());
            await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

            // Subsequent Stop on the same session must take the disabled-session
            // fast path (no /hooks call, no watcher refresh) — that's how
            // per-turn Stop hooks stay cheap for excluded sessions.
            await Assert.That(DisabledSessions.IsDisabled(excludedSessionId, Config.Root)).IsTrue();
        } finally {
            DisabledSessions.RemoveMarker(excludedSessionId, Config.Root);
        }
    }
}
