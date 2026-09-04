using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Tests.Unit.Policy;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Which permission requests <see cref="PermissionRequestCommand.Handle(string?, bool, TextWriter)"/>
/// hands to the approval policy, and what a seam-answered prompt costs the paths behind it: an
/// answered prompt is never also recorded, an excluded session is never evaluated, and a rendered
/// session's prompt still goes to the bridge that owns it.
/// </summary>
/// <remarks>Bare <c>[NotInParallel]</c>: <c>KCAP_RENDERED_AGENT</c> and <c>KCAP_DAEMON_URL</c> steer
/// the branch under test and are read outside any enumerable cohort.</remarks>
[NotInParallel]
public class PermissionRequestPolicySeamTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    const string Sid = "3f1c9a2b4d5e4f6a8b7c0d1e2f3a4b5c";

    public void Dispose() => _server.Stop();

    PermissionRequestCommand Command() =>
        new(Config.Root, Resolutions.At(_server.Urls[0], Config.Root), new RecordingCapacitorHttpClient());

    // No transcript_path: the watcher self-heal is a no-op, so selfHealWatcher carries only the
    // governance meaning this class is about.
    string Body(string command) => new JsonObject {
        ["hook_event_name"] = "PermissionRequest", ["session_id"] = Sid, ["tool_name"] = "Bash",
        ["tool_input"] = new JsonObject { ["command"] = command }, ["cwd"] = Tmp.PathTo("repo"),
    }.ToJsonString();

    void WriteDenyPolicy() => File.WriteAllText(Config.Root.Path("approvals.yaml"),
        "version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");

    void StubNoAuthDiscovery() =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("""{"provider":"None"}"""));

    void StubRecord() =>
        _server.Given(Request.Create().WithPath("/hooks/permission-record").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

    List<string> RecordedPaths(string path) =>
        [.. _server.LogEntries.Where(e => e.RequestMessage.Path == path).Select(e => e.RequestMessage.Path)];

    [Test]
    public async Task Deny_rule_answers_the_prompt_and_the_answered_prompt_is_not_also_recorded() {
        using var _ = EnvScope.Exclusive("KCAP_RENDERED_AGENT", null);
        WriteDenyPolicy();
        StubNoAuthDiscovery();
        StubRecord();

        var stdout = new StringWriter();
        var exit = await Command().Handle(Body("git push --force"), selfHealWatcher: true, stdout);

        await Assert.That(exit).IsEqualTo(0);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["hookEventName"]!.GetValue<string>()).IsEqualTo("PermissionRequest");
        await Assert.That(hso["decision"]!["behavior"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(RecordedPaths("/hooks/permission-record")).IsEmpty();
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, Sid).Count).IsEqualTo(1);
    }

    /// <summary>The control for the test above — the same payload the seam answered reaches
    /// record-only when the policy does not answer it, so "no record post" means the seam returned
    /// early rather than the post being unreachable.</summary>
    [Test]
    public async Task Excluded_session_is_ungoverned_and_falls_through_to_record_only() {
        using var _ = EnvScope.Exclusive("KCAP_RENDERED_AGENT", null);
        WriteDenyPolicy();
        StubNoAuthDiscovery();
        StubRecord();

        var stdout = new StringWriter();
        var exit = await Command().Handle(Body("git push --force"), selfHealWatcher: false, stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(RecordedPaths("/hooks/permission-record").Count).IsEqualTo(1);
        // An excluded session's decisions cannot be recorded, so none may be made.
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, Sid)).IsEmpty();
    }

    [Test]
    public async Task Rendered_session_skips_the_seam_and_forwards_the_prompt() {
        using var rendered = EnvScope.Exclusive("KCAP_RENDERED_AGENT", "1");
        using var daemon = EnvScope.Exclusive("KCAP_DAEMON_URL", null);
        WriteDenyPolicy();
        StubNoAuthDiscovery();
        _server.Given(Request.Create().WithPath("/hooks/permission-request").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("""{"behavior":"allow"}"""));

        var stdout = new StringWriter();
        var exit = await Command().Handle(Body("git push --force"), selfHealWatcher: true, stdout);

        await Assert.That(exit).IsEqualTo(0);
        // The forwarded answer, not the deny the same policy would have produced here.
        await Assert.That(stdout.ToString()).IsEqualTo("""{"behavior":"allow"}""");
        await Assert.That(RecordedPaths("/hooks/permission-request").Count).IsEqualTo(1);
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, Sid)).IsEmpty();
    }
}
