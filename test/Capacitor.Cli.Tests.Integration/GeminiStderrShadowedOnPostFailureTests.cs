using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The failed-POST case for the events that are not SessionStart. A rejected lifecycle POST makes
/// <c>AgentHookPoster</c> write a diagnostic to stderr, and Gemini reads
/// <c>stdout.trim() || stderr.trim()</c> — so with nothing on stdout that diagnostic BECOMES the hook
/// result and reaches the model as hook-sourced content.
///
/// <para>The stderr assertion here is a positive control, not decoration. Without it this test passes
/// vacuously the day the diagnostic moves or is silenced: "stdout wins" is only evidence of shadowing
/// when there is genuinely something to shadow.</para>
///
/// <para><see cref="GeminiSessionStartHandshakeOnPostFailureTests"/> is the SessionStart half, where the
/// same write also has to carry the memory envelope.</para>
/// </summary>
public class GeminiStderrShadowedOnPostFailureTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test, NotInParallel]
    public async Task A_rejected_session_end_post_shadows_its_own_stderr_diagnostic() {
        await UseServerProfileAsync();

        // A genuine non-2xx: the one outcome that logs to stderr rather than spooling silently.
        _server.Given(Request.Create().WithPath("/hooks/session-end/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        var (exit, stdout, stderr) = await RunAsync($$"""
            {"hook_event_name":"SessionEnd","session_id":"{{Guid.NewGuid()}}","cwd":"/tmp","reason":"exit"}
            """);

        // Positive control: the diagnostic this test exists to shadow really was written.
        await Assert.That(stderr).Contains("session-end/gemini");

        // And Gemini's own selection expression picks stdout over it.
        await Assert.That(SelectedByGemini(stdout, stderr)).IsEqualTo(stdout.Trim());
        await Assert.That(stdout.Trim()).IsEqualTo("""{"continue":true}""");

        // The rejection is still reported — shadowing is not pretending the POST worked. Staying below
        // 2 also keeps Gemini's plain-text fallback in its allow band if the payload ever fails to parse.
        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(exit).IsLessThan(2);

        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/gemini").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task A_rejected_notification_post_still_emits_a_hook_result() {
        await UseServerProfileAsync();

        _server.Given(Request.Create().WithPath("/hooks/notification").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var (exit, stdout, _) = await RunAsync($$"""
            {"hook_event_name":"Notification","session_id":"{{Guid.NewGuid()}}","cwd":"/tmp",
             "message":"waiting for input","notification_type":"idle"}
            """);

        // Notification forwarding is telemetry and swallows its own failures, so it contributes no
        // stderr of its own — but Program.cs's pre-dispatch spool drain and the auth layer do, on the
        // very invocations where a server is unhealthy. The emit is what makes that irrelevant.
        await Assert.That(stdout.Trim()).IsEqualTo("""{"continue":true}""");
        await Assert.That(exit).IsEqualTo(0);

        using var doc = JsonDocument.Parse(stdout.Trim());
        await Assert.That(doc.RootElement.Str("decision")).IsNull();
    }

    async Task UseServerProfileAsync() =>
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        });

    /// <summary>Gemini 0.53.0's <c>const textToParse = stdout.trim() || stderr.trim()</c>.</summary>
    static string SelectedByGemini(string stdout, string stderr) =>
        stdout.Trim() is { Length: > 0 } o ? o : stderr.Trim();

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string payload) {
        using var capture = ConsoleOutput.StartFullCapture();


        var exit = await new GeminiHookCommand(
                Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .Handle(new StringReader(payload));

        return (exit, capture.GetCapturedOutput(), capture.GetCapturedError());
    }
}
