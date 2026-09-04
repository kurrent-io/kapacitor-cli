using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A rejected lifecycle POST must still deliver the memory index to Gemini — and must spend the lease
/// exactly once while doing so.
///
/// <para>Gemini's runner selects the text to parse as <c>stdout.trim() || stderr.trim()</c>, so zero
/// bytes on this path would hand it kcap's failed-POST stderr diagnostic as the hook result. That is the
/// hazard the Codex adapter actually shipped once (an early <c>return 1</c> before the handshake), and
/// the reason the write is ordered before every return.</para>
///
/// <para>This is the integration half of the design's non-zero-exit question. The live half — that a
/// real <c>gemini</c> turn CONSUMES context from a hook that exited non-zero — is
/// <c>GeminiMemoryIndexLiveCertTests</c>; only the two together justify "no commit gate", because this
/// test can prove the bytes were written but not that Gemini used them.</para>
/// </summary>
public class GeminiSessionStartHandshakeOnPostFailureTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test, NotInParallel]
    public async Task A_rejected_lifecycle_post_still_delivers_the_memory_index_and_spends_the_lease_once() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        // A permanent rejection: PostOrSpoolAsync returns Failed only for a genuine non-2xx (transport
        // and auth failures spool instead), which is the one outcome that keeps the non-zero exit.
        _server.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        // The index is served on the SAME base URL as the rejected POST — that combination is the whole
        // point. An index fetched successfully must not be discarded because the lifecycle POST failed.
        _server.Given(Request.Create().WithPath("/api/memories/index").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """
                [{"memory_id":"m1","slug":"cert-slug","audience":"user",
                  "description":"failed-post index delivery","kind":"reference"}]
                """));

        // A BARE GUID, deliberately: the dispatcher validates `session_id` with `Guid.TryParse`, and a
        // decorated id takes the same emit-and-return-0 path as a suppressed session — producing a
        // plausible-looking allow object with no POST attempted at all.
        var sessionId = Guid.NewGuid().ToString();

        var (startupExit, startupStdout) = await RunAsync(sessionId, source: "startup");

        // The rejection is still reported — this is not "pretend it worked".
        await Assert.That(startupExit).IsEqualTo(1);

        // ...but the index still reached Gemini, as a parseable decision payload.
        await Assert.That(AdditionalContextOf(startupStdout)).Contains("cert-slug");

        // And the POST really was attempted and really was rejected, so the assertions above describe
        // the Failed path rather than some earlier short-circuit.
        var posts = _server.FindLogEntries(
            Request.Create().WithPath("/hooks/session-start/gemini").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);

        // The design's branch-specific lease assertion. The chosen branch is "no commit gate": the
        // failed-POST invocation COMPLETES the lease because the injection was delivered. A `resume` on
        // the same session must therefore emit the allow object and no context — if this ever produced
        // the index a second time, the lease was released and the branch has silently flipped.
        var (resumeExit, resumeStdout) = await RunAsync(sessionId, source: "resume");

        await Assert.That(resumeExit).IsEqualTo(1);
        await Assert.That(resumeStdout).IsEqualTo("""{"continue":true}""");
    }

    async Task<(int ExitCode, string Stdout)> RunAsync(string sessionId, string source) {
        // No transcript_path → short-circuits before the watcher spawn.
        var payload =
            $$"""
              {
                "hook_event_name": "SessionStart",
                "session_id":      "{{sessionId}}",
                "cwd":             "/tmp",
                "source":          "{{source}}"
              }
              """;

        using var capture = ConsoleOutput.StartCapture();

        // The real memory factory, resolving against this test's own config root: discovery finds no
        // /auth/config, falls back to a token store that holds nothing, and hands back an
        // unauthenticated client — which is exactly what the stub wants, without a seam.
        var exit = await new GeminiHookCommand(
                Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home, new FixedCapacitorHttpClient())
            .Handle(new StringReader(payload));

        return (exit, capture.GetCapturedOutput());
    }

    static string AdditionalContextOf(string stdout) {
        using var doc = JsonDocument.Parse(stdout.Trim());

        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }
}
