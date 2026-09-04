using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>
/// Covers the repo/path exclusion gate (<see cref="ClaudeHookCommand.IsSessionExcludedAsync"/>)
/// that guards the permission-request watcher self-heal — so a permission prompt in an
/// excluded project does not start a transcript-uploading watcher that session-start
/// intentionally skipped.
/// </summary>
public class ClaudeHookExclusionGateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly HookClock _clock = new(TimeProvider.System);

    // Instance, not static: the hook writes under the config dir (the repo-detection cache,
    // the lease store), so it must be handed this test's own root — which a static helper
    // cannot see, TUnit injecting it after construction.
    ClaudeHookCommand Hook() => new(Config.Root, Resolutions.None(Config.Root), _clock, Home, new FixedCapacitorHttpClient());

    // The gate reads the budget only for the repo probe, which these path-exclusion payloads never
    // reach; what they vary is the profile, not the clock. Any live ceiling will do.
    HookBudget Budget() => _clock.Budget(TimeSpan.FromSeconds(5));

    static string Body(string cwd) => new JsonObject { ["cwd"] = cwd }.ToJsonString();

    [Test]
    public async Task ExcludedPath_ReturnsTrue() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(excludedDir.PathTo("project"));

        var excluded = await Hook().IsSessionExcludedAsync(profile, body, Budget());

        await Assert.That(excluded).IsTrue();
    }

    [Test]
    public async Task NonExcludedPath_ReturnsFalse() {
        using var tmp = new TempDir();
        var excludedDir = tmp.CreateDir("excl");
        var otherDir    = tmp.CreateDir("other");

        var profile  = new Profile { ExcludedPaths = [excludedDir] };
        var body     = Body(otherDir.PathTo("project"));

        var excluded = await Hook().IsSessionExcludedAsync(profile, body, Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task NullProfile_ReturnsFalse() {
        var excluded = await Hook().IsSessionExcludedAsync(profile: null, Body("/tmp/anything"), Budget());

        await Assert.That(excluded).IsFalse();
    }

    [Test]
    public async Task ProfileWithoutExclusions_ReturnsFalse() {
        var excluded = await Hook().IsSessionExcludedAsync(new Profile(), Body("/tmp/anything"), Budget());

        await Assert.That(excluded).IsFalse();
    }
}
