using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// No-op behaviour of <see cref="TokenStore.RefreshIfExpiringAsync"/> — the paths that
/// must NOT touch the network. These are the daemon acceptance criteria: proactive refresh
/// is a no-op when no tokens are stored, for the None provider, and while the token is still
/// comfortably valid (refresh only inside the expiry window). The provider paths that DO hit
/// the WorkOS / server refresh endpoints are exercised by DecideProactiveRefresh's unit tests
/// and integration coverage, never here, so this suite stays offline and fast.
/// </summary>
public class RefreshIfExpiringTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Test]
    public async Task NotDue_when_no_tokens_stored() {
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }

    [Test]
    public async Task NotDue_and_leaves_token_untouched_when_comfortably_valid() {
        var original = new StoredTokens {
            AccessToken    = "at",
            RefreshToken   = "rt",
            ClientId       = "cid",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1), // well outside the 5-minute window
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS
        };
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", original);

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);

        // Untouched — no refresh attempted, so the persisted access token is unchanged.
        var after = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("default");
        await Assert.That(after!.AccessToken).IsEqualTo("at");
    }

    [Test]
    public async Task NotDue_for_none_provider_even_inside_window() {
        // A None-auth server stores no tokens; a stray Provider=None file must still be a no-op.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", new StoredTokens {
            AccessToken    = "at",
            ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(1), // inside the window
            GitHubUsername = "alice",
            Provider       = AuthProvider.None
        });

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RefreshIfExpiringAsync(ProfileConfig.DefaultName, Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }
}
