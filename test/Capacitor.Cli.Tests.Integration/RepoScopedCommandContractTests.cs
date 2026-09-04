using System.Diagnostics;
using Capacitor.Cli.Core;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins the server-facing contract of the commands keyed on a repository — <c>kcap curate apply</c>
/// and <c>kcap skills sync</c> — from outside the process. Both address the server by a hash of
/// <c>owner/name</c> read from the git remote, so the fixture is a real repository with a remote
/// and the hash in the asserted path is computed the same way the command computes it.
/// </summary>
public class RepoScopedCommandContractTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir("home")] public required TempDir         Home    { get; init; }

    const string BearerToken = "seed-token";
    const string Owner       = "acme";
    const string Name        = "widget";

    static readonly string RepoHash = RepoHashHelper.ComputeRepoHash(Owner, Name);

    readonly WireMockServer _server   = WireMockServer.Start();
    readonly List<Process>  _children = [];
    readonly GitRepo        _repo     = GitRepo.CreateWithCommit();

    public void Dispose() {
        foreach (var child in _children) {
            try {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            } catch {
                // best-effort cleanup
            }

            child.Dispose();
        }

        _repo.Dispose();
        _server.Stop();
    }

    /// <summary>Runs before every test — TUnit injects the temp directories before its hooks.</summary>
    [Before(Test)]
    public void SeedRepoProfileAndToken() {
        _repo.AddRemote($"https://github.com/{Owner}/{Name}.git");
        Config.CreateFile("config.json", ProfileJson);
        Config.CreateDir("tokens").CreateFile("default.json", TokenJson);

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Json(200, """{"provider":"GitHubApp"}"""));
    }

    // --- kcap curate apply ---

    /// <summary>The promoted set is one GET whose filter lives entirely in the query string, and a
    /// guideline reaches the preview as an addition to the repo's instruction file.</summary>
    [Test]
    public async Task Curate_reads_the_promoted_set_and_previews_an_addition() {
        const string body = """
            {"repo_hash":"h","items":[
              {"category":"testing","cluster_id":"c1","promoted_text":"Run the suite before pushing.",
               "target_kinds":["claude_md"],"status":"promoted"}]}
            """;

        _server.Given(Request.Create().WithPath($"/api/repositories/{RepoHash}/curation").UsingGet())
            .RespondWith(Json(200, body));

        var run = await RunAsync("curate", "apply", "--dry-run");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("Run the suite before pushing.");

        var gets = _server.FindLogEntries(
            Request.Create().WithPath($"/api/repositories/{RepoHash}/curation").UsingGet());
        await Assert.That(gets.Count).IsEqualTo(1);
        await Assert.That(gets[0].RequestMessage.RawQuery)
            .IsEqualTo("?status=promoted&minWeight=1&limit=100");
        await Assert.That(Header(gets[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
    }

    /// <summary>A target kind other than <c>claude_md</c> is dropped, and a repo with no promoted
    /// guidelines is a success that writes nothing.</summary>
    [Test]
    public async Task Curate_ignores_a_guideline_for_another_target() {
        const string body = """
            {"repo_hash":"h","items":[
              {"category":"testing","cluster_id":"c1","promoted_text":"Not for here.",
               "target_kinds":["cursor_rules"],"status":"promoted"}]}
            """;

        _server.Given(Request.Create().WithPath($"/api/repositories/{RepoHash}/curation").UsingGet())
            .RespondWith(Json(200, body));

        var run = await RunAsync("curate", "apply", "--dry-run");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("Nothing to apply");
    }

    /// <summary>A 404 is a visibility answer, not a missing route, so it names the profile to check.</summary>
    [Test]
    public async Task Curate_reports_a_repo_it_cannot_see() {
        _server.Given(Request.Create().WithPath($"/api/repositories/{RepoHash}/curation").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var run = await RunAsync("curate", "apply", "--dry-run");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Repo not found or not visible for this profile.");
    }

    /// <summary>A body that is not a curation payload fails rather than applying a partial read.</summary>
    [Test]
    public async Task Curate_refuses_a_malformed_payload() {
        _server.Given(Request.Create().WithPath($"/api/repositories/{RepoHash}/curation").UsingGet())
            .RespondWith(Json(200, "{\"items\": \"not-a-list\"}"));

        var run = await RunAsync("curate", "apply", "--dry-run");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Malformed response from server");
    }

    // --- kcap skills sync ---

    /// <summary>A 404 names the profile, the same answer curate gives for the same reason.</summary>
    [Test]
    public async Task Skills_sync_reports_a_repo_it_cannot_see() {
        SeedOwnedManifest("claude", etag: null);

        _server.Given(Request.Create().WithPath($"/api/repositories/{RepoHash}/skills").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var run = await RunAsync("skills", "sync", "--dry-run");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Repo not found or not visible for this profile.");
    }

    /// <summary>Only a target kcap already owns is reconciled, and ownership is the manifest's
    /// existence — so writing one is what puts a target in the sync set.</summary>
    void SeedOwnedManifest(string target, string? etag) =>
        Config.CreateDir("skills", RepoHash, target).CreateFile(
            "manifest.json",
            etag is null ? """{"skills":[]}""" : $$"""{"etag":"{{etag}}","skills":[]}""");

    // --- fixtures ---

    /// <summary><c>update_check: false</c> keeps the exit-time update notice — and its network
    /// probe — out of the child's stderr.</summary>
    const string ProfileJson =
        """{"version":2,"active_profile":"default","profiles":{"default":{"update_check":false}},"profile_bindings":{},"cwd_remap":[]}""";

    static string TokenJson =>
        $$"""
        {"access_token":"{{BearerToken}}","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}",
         "github_username":"seed-user","provider":"GitHubApp"}
        """;

    static IResponseBuilder Json(int status, string body) =>
        Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);

    static string? Header(ILogEntry entry, string name) {
        if (entry.RequestMessage.Headers is not { } headers) return null;

        foreach (var header in headers) {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) {
                return string.Join(',', header.Value);
            }
        }

        return null;
    }

    /// <summary>Runs from inside the repository: both commands read the repo from the working
    /// directory, not from configuration. The home is a throwaway one so nothing the child reads
    /// comes from the developer's.</summary>
    async Task<CliRun> RunAsync(params string[] args) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, args);
        psi.WorkingDirectory               = _repo.Path;
        psi.Environment["HOME"]            = Home.Path;
        psi.Environment["KCAP_URL"]        = _server.Url!;
        psi.Environment["KCAP_SESSION_ID"] = "";
        psi.Environment["CODEX_THREAD_ID"] = "";
        psi.Environment["DO_NOT_TRACK"]    = "1";

        var child = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _children.Add(child);
        child.StandardInput.Close();

        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await child.WaitForExitAsync(cts.Token);

        return new(child.ExitCode, await stdout, await stderr);
    }

    readonly record struct CliRun(int ExitCode, string Stdout, string Stderr);
}
