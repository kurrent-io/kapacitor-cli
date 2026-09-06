using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Codex BLOCKS on its SessionStart hook's stdout and its parser rejects empty output. So a
/// permanent lifecycle-POST rejection must still produce the handshake — the recording outcome cannot
/// be allowed to decide whether the host can proceed.
///
/// <para>It used to. An early <c>return 1</c> on <see cref="HookPostOutcome.Failed"/> sat BEFORE the
/// handshake, so a server 4xx left Codex with zero bytes: no <c>continue</c>, no context, just a wait
/// for its hook timeout. Verified by hand at the time — a rejected payload produced literally no
/// stdout. The non-zero exit is still reported (the session genuinely was not recorded), just after
/// the handshake rather than instead of it.</para>
///
/// <para>Same family as the other known hazard where a hook exits before satisfying a stdout-blocking
/// host — the unguarded auth discovery in the lifecycle poster, tracked separately.</para>
/// </summary>
public class CodexSessionStartHandshakeOnPostFailureTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server         = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test, NotInParallel]
    public async Task A_rejected_lifecycle_post_still_satisfies_the_blocking_stdout_handshake() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        // A permanent rejection: PostOrSpoolAsync returns Failed for a genuine non-2xx (transport and
        // auth failures spool instead), which is exactly the case that used to skip the handshake.
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        // No transcript_path → short-circuits before the watcher spawn.
        const string payload =
            """
            {
              "hook_event_name": "SessionStart",
              "session_id":      "handshake-on-failure",
              "cwd":             "/tmp",
              "model":           "gpt-5"
            }
            """;

        using var capture = ConsoleOutput.StartCapture();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient()).Handle(new StringReader(payload));

        // The rejection is still reported — this is not "pretend it worked".
        await Assert.That(exit).IsEqualTo(1);

        // ...but Codex still got a parseable handshake rather than nothing at all.
        var stdout = capture.GetCapturedOutput();
        await Assert.That(stdout).IsNotEmpty();

        var doc = System.Text.Json.JsonDocument.Parse(stdout);
        await Assert.That(doc.RootElement.GetProperty("continue").GetBoolean()).IsTrue();

        // And the POST really was attempted and really was rejected, so the assertions above are
        // about the Failed path and not some earlier short-circuit.
        var requests = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);
    }
}
