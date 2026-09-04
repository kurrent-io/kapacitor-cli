namespace Capacitor.Cli.Tests.Unit;

/// <summary>The two sources of the watcher directory, in precedence order. Bare
/// <c>[NotInParallel]</c> because <c>KCAP_WATCHER_DIR</c> is read by a production path helper and
/// inherited by every spawned watcher.</summary>
[NotInParallel]
public class WatcherDirTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherManager? _watchers;
    WatcherManager  Watchers => _watchers ??= new(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient());

    [Test]
    public async Task It_sits_under_the_root_it_was_handed() {
        using var _ = EnvScope.Exclusive("KCAP_WATCHER_DIR", null);

        await Assert.That(Watchers.GetWatcherDir()).IsEqualTo(Config.PathTo("watchers"));
    }

    [Test]
    public async Task KCAP_WATCHER_DIR_wins_over_the_root() {
        var elsewhere = Config.PathTo("elsewhere");
        using var _   = EnvScope.Exclusive("KCAP_WATCHER_DIR", elsewhere);

        await Assert.That(Watchers.GetWatcherDir()).IsEqualTo(elsewhere);
    }
}
