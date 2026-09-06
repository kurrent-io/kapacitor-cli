using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Tests for TokenStore.RefreshWithCrossProcessLockAsync — the profile-scoped lock that both the
/// reactive (GetValidTokensAsync) and proactive (RefreshIfExpiringAsync) paths refresh through.
///
/// Covers two review findings on the proactive-refresh PR:
///   1. The refreshed token must be persisted under the SAME profile the lock was taken on, not
///      the (possibly since-switched) active profile — the refresh delegates no longer self-save.
///   2. A refresh that a peer already performed while we waited for the lock must NOT be repeated,
///      even if the freshly-rotated token is still inside the proactive window (short-lived token).
///
/// A fake refresh delegate is injected so no network or real provider endpoint is touched.
/// </summary>
public class CrossProcessRefreshTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    string TokensDir => Config.PathTo("tokens");

    static StoredTokens Token(string accessToken, DateTimeOffset expiresAt) => new() {
        AccessToken    = accessToken,
        RefreshToken   = "rt",
        ClientId       = "cid",
        ExpiresAt      = expiresAt,
        GitHubUsername = "alice",
        Provider       = AuthProvider.WorkOS
    };

    static bool WithinWindow(StoredTokens t) => DateTimeOffset.UtcNow >= t.ExpiresAt - Window;

    [Test]
    public async Task Persists_refreshed_token_under_the_locked_profile() {
        // Lock a NON-default profile ("alpha"); the active profile resolves to "default". The
        // refreshed token must land in alpha.json — a persist path that re-resolved the active
        // profile would write it to default.json and leave alpha.json stale.
        var current = Token("old", DateTimeOffset.UtcNow.AddMinutes(-10)); // expired → default predicate refreshes
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("alpha", current);

        var refreshed = Token("new-alpha", DateTimeOffset.UtcNow.AddHours(1));

        var result = await AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
            "alpha", current, _ => Task.FromResult<StoredTokens?>(refreshed));

        await Assert.That(result!.AccessToken).IsEqualTo("new-alpha");
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("alpha"))!.AccessToken).IsEqualTo("new-alpha");
        await Assert.That(File.Exists(Path.Combine(TokensDir, "default.json"))).IsFalse();
    }

    [Test]
    public async Task Refreshes_and_persists_when_no_peer_changed_the_token() {
        // On-disk token equals `current` (no peer refresh) and is inside the proactive window →
        // refresh, and the result is persisted under the lock.
        var current = Token("same", DateTimeOffset.UtcNow.AddMinutes(3));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("alpha", current);

        var refreshed     = Token("refreshed", DateTimeOffset.UtcNow.AddHours(1));
        var refreshCalled = false;

        var result = await AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
            "alpha", current,
            _ => { refreshCalled = true; return Task.FromResult<StoredTokens?>(refreshed); },
            WithinWindow);

        await Assert.That(refreshCalled).IsTrue();
        await Assert.That(result!.AccessToken).IsEqualTo("refreshed");
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("alpha"))!.AccessToken).IsEqualTo("refreshed");
    }

    [Test]
    public async Task Does_not_re_refresh_a_token_a_peer_already_refreshed() {
        // We read `current` before the lock; under the lock the on-disk token has CHANGED to a
        // peer-refreshed one that is still valid but ALSO still inside the window (short lifetime).
        // The within-window predicate alone would re-refresh it — the peer-refresh guard must not.
        var current       = Token("old",  DateTimeOffset.UtcNow.AddMinutes(3));
        var peerRefreshed = Token("peer", DateTimeOffset.UtcNow.AddMinutes(3)); // changed, valid, still within window
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("alpha", peerRefreshed);

        var refreshCalled = false;

        var result = await AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
            "alpha", current,
            _ => { refreshCalled = true; return Task.FromResult<StoredTokens?>(current); },
            WithinWindow);

        await Assert.That(refreshCalled).IsFalse();            // suppressed — peer already refreshed
        await Assert.That(result!.AccessToken).IsEqualTo("peer");
    }

    [Test]
    public async Task Reactive_default_predicate_still_refreshes_expired_token() {
        // Guard: the default (reactive) predicate is unchanged — an expired token still refreshes
        // and persists, even though the on-disk token changed (peer wrote an expired token).
        var current       = Token("old",     DateTimeOffset.UtcNow.AddMinutes(-10));
        var peerButExpired = Token("peer-x", DateTimeOffset.UtcNow.AddMinutes(-5)); // changed but still expired
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("alpha", peerButExpired);

        var refreshed     = Token("fresh", DateTimeOffset.UtcNow.AddHours(1));
        var refreshCalled = false;

        var result = await AuthFixtures.NewTokenStore(Config.Root).RefreshWithCrossProcessLockAsync(
            "alpha", current,
            _ => { refreshCalled = true; return Task.FromResult<StoredTokens?>(refreshed); });

        await Assert.That(refreshCalled).IsTrue();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("alpha"))!.AccessToken).IsEqualTo("fresh");
    }
}
