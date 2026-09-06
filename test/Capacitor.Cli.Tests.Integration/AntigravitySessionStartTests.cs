using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// CLI→server integration for the Antigravity hook dispatcher: a PreInvocation drives a POST
/// to /hooks/session-start/antigravity carrying the enriched payload (session id, version,
/// profile default visibility), while an excluded-path PreInvocation is skipped entirely.
/// The watcher spawn that normally follows the POST is neutralized by pre-seeding a live
/// watcher pid file so <c>EnsureWatcherRunning</c> no-ops.
/// </summary>
public class AntigravitySessionStartTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server     = WireMockServer.Start();
    readonly List<string>   _pidFiles = [];

    public void Dispose() {
        _server.Stop();
        foreach (var p in _pidFiles) { try { File.Delete(p); } catch { /* ignore */ } }
    }

    // Pre-seed a live pid file so EnsureWatcherRunning sees a running watcher and skips
    // spawning `kcap watch` during the test.
    void NeutralizeWatcherSpawn(string conversationId) {
        var dir = Config.PathTo("watchers");
        Directory.CreateDirectory(dir);
        var pidFile = Path.Combine(dir, $"{conversationId}.pid");
        File.WriteAllText(pidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        _pidFiles.Add(pidFile);
    }

    [Test]
    public async Task PreInvocation_posts_session_start_with_profile_visibility() {
        // Antigravity conversation ids are dashed UUIDs; the CLI must canonicalize to the
        // dashless form for session_id + the watcher key + disable (matching `kcap watch`),
        // so everything resolves to ONE stream.
        const string convId  = "e80c33bf-c10f-4d2f-b626-b0043f488fc0";
        const string dashless = "e80c33bfc10f4d2fb626b0043f488fc0";
        NeutralizeWatcherSpawn(dashless);

        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile { ServerUrl = _server.Url, DefaultVisibility = "private" }
            }
        });

        _server.Given(Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var tmp = new TempDir();
        var transcript = tmp.PathTo($"{convId}.jsonl");
        var payload =
            $$"""
            {
              "conversationId":     "{{convId}}",
              "transcriptPath":     "{{transcript.Replace("\\", "\\\\")}}",
              "workspacePaths":     ["/tmp"],
              "antigravityVersion": "2.2.1"
            }
            """;

            var exit = await new AntigravityHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"], new StringReader(payload),
            new StringWriter());

        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        // session_id is the DASHLESS canonical form, not the raw dashed conversationId.
        await Assert.That(body["session_id"]?.GetValue<string>()).IsEqualTo(dashless);
        await Assert.That(body["hook_event_name"]?.GetValue<string>()).IsEqualTo("sessionStart");
        await Assert.That(body["antigravity_version"]?.GetValue<string>()).IsEqualTo("2.2.1");
        await Assert.That(body["default_visibility"]?.GetValue<string>()).IsEqualTo("private");
    }

    [Test]
    public async Task PreInvocation_for_excluded_path_is_skipped_without_posting() {
        const string convId = "agexcludedsess1";
        using var tmp = new TempDir();
        var excludedDir = tmp.PathTo("excluded");

        NeutralizeWatcherSpawn(convId);

        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"] = new Profile { ServerUrl = _server.Url, ExcludedPaths = [excludedDir] }
            }
        });

        _server.Given(Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        // Antigravity attributes the working dir from workspacePaths[0]; an excluded
        // path short-circuits before any POST, so the transcript is never opened.
        var transcript = tmp.PathTo($"{convId}.jsonl");
        var payload =
            $$"""
            {
              "conversationId": "{{convId}}",
              "transcriptPath": "{{transcript.Replace("\\", "\\\\")}}",
              "workspacePaths": ["{{excludedDir.Replace("\\", "\\\\")}}"]
            }
            """;

            var exit = await new AntigravityHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(["hook", "--antigravity", "PreInvocation"], new StringReader(payload),
            new StringWriter());
        await Assert.That(exit).IsEqualTo(0);

        var requests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(0);
    }
}
