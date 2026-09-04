using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The refreshes run on a client the store is handed, never one it builds. A store that makes its
/// own reaches the real WorkOS host from a unit test, and the only way to tell the two apart is to
/// hand it a client and see whether the request arrives.
/// </summary>
public class TokenStoreRefreshLaneTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static StoredTokens Expired => new() {
        AccessToken    = "stale",
        RefreshToken   = "rt",
        ClientId       = "cid",
        ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(-1),
        GitHubUsername = "alice",
        Provider       = AuthProvider.WorkOS
    };

    [Test]
    public async Task The_workos_refresh_reaches_the_client_it_was_given() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        var refreshed = await store.GetValidTokensForProfileAsync("default");

        await Assert.That(script.Seen)
            .Contains("POST https://api.workos.com/user_management/authenticate");
        await Assert.That(refreshed!.AccessToken).IsEqualTo("fresh");
    }

    /// The rotated refresh token is persisted, not just returned: WorkOS spends the old one on use,
    /// so a store that returned the new pair without writing it would re-send a consumed token.
    [Test]
    public async Task The_rotated_refresh_token_is_written_back() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        await store.GetValidTokensForProfileAsync("default");

        var persisted = await store.LoadAsync("default");
        await Assert.That(persisted!.RefreshToken).IsEqualTo("rotated");
    }
}
