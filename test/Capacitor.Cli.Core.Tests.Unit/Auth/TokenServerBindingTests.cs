using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The rules that keep a token from reaching a server that never issued it, and that keep token
/// lookup pointed at the profile the rest of the process resolved.
/// </summary>
public class TokenServerBindingTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Server = "https://kcap.example.com";

    // Deliberately unroutable (discard port): any refresh these tests did NOT intend gets refused
    // immediately, so a regression that reintroduces a rotation fails in milliseconds instead of
    // hanging on DNS or a retry budget.
    const string OtherServer = "http://127.0.0.1:9";

    string LegacyPath => Config.PathTo("tokens.json");

    // ── Binding ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Token_bound_to_the_target_server_is_handed_out() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: Server));

        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.Ok);
        await Assert.That(resolution.Tokens).IsNotNull();
    }

    [Test]
    public async Task Token_bound_to_another_server_is_withheld_with_a_diagnosable_status() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: OtherServer));

        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.WrongServer);
        // The whole point: the token must not be reachable by the caller...
        await Assert.That(resolution.Tokens).IsNull();
        // ...but the caller still needs to be able to say WHICH server it belongs to.
        await Assert.That(resolution.IssuedServerUrl).IsEqualTo(OtherServer);
    }

    [Test]
    public async Task Pre_upgrade_token_without_a_binding_is_not_enforced() {
        // Files written before server_url existed must keep working; they get stamped later.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: null));

        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.Ok);
        await Assert.That(resolution.Tokens).IsNotNull();
    }

    [Test]
    public async Task Binding_is_checked_before_any_refresh_is_attempted() {
        // An EXPIRED token bound elsewhere must not trigger a refresh: we already know we won't
        // use it, and refreshing would spend a rotating credential (and a round trip) for nothing.
        // The refresh endpoint here is unroutable, so a refresh attempt would stall or throw
        // rather than returning promptly with WrongServer.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: OtherServer, expiresIn: TimeSpan.FromMinutes(-5)));

        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.WrongServer);
        await Assert.That(resolution.Tokens).IsNull();
    }

    [Test]
    public async Task No_token_at_all_reports_not_authenticated() {
        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.NotAuthenticated);
        await Assert.That(resolution.Tokens).IsNull();
    }

    // ── Resolved-profile lookup ──────────────────────────────────────────────

    [Test]
    public async Task Lookup_follows_the_named_profile_not_the_active_one() {
        await WriteConfigAsync(active: "acme", extraProfile: "widgets");
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme",    Tokens(serverUrl: Server, username: "acme-user"));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("widgets", Tokens(serverUrl: Server, username: "widgets-user"));

        // The store reads exactly the profile it is handed. Which profile that IS — resolved name
        // else on-disk active — is ProfileContext.Name's answer, asserted there.
        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync("widgets", Server);

        await Assert.That(resolution.ProfileName).IsEqualTo("widgets");
        await Assert.That(resolution.Tokens!.GitHubUsername).IsEqualTo("widgets-user");
    }

    // ── Legacy tokens.json ownership ─────────────────────────────────────────

    [Test]
    public async Task Legacy_credential_is_available_to_its_migration_owner() {
        await WriteConfigAsync(active: "acme");
        await WriteLegacyAsync(Tokens(serverUrl: null, username: "legacy-user"));

        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("acme");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GitHubUsername).IsEqualTo("legacy-user");
    }

    [Test]
    public async Task Legacy_credential_is_withheld_from_a_different_resolved_profile() {
        // Otherwise a repo bound to a token-less profile silently borrows the active profile's
        // pre-upgrade credential — and it carries no binding, so nothing downstream can catch it.
        await WriteConfigAsync(active: "acme", extraProfile: "widgets");
        await WriteLegacyAsync(Tokens(serverUrl: null, username: "legacy-user"));

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync("widgets")).IsNull();
    }

    [Test]
    public async Task Legacy_credential_is_withheld_from_default_when_another_profile_is_active() {
        // "default" gets no special exemption: with acme active, the legacy credential is acme's.
        await WriteConfigAsync(active: "acme", extraProfile: "default");
        await WriteLegacyAsync(Tokens(serverUrl: null, username: "legacy-user"));

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync(ProfileConfig.DefaultName)).IsNull();
    }

    [Test]
    public async Task Legacy_credential_is_available_to_default_when_no_active_profile_is_set() {
        // An absent/empty active profile normalizes to "default", which then owns the credential.
        await WriteLegacyAsync(Tokens(serverUrl: null, username: "legacy-user"));

        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync(ProfileConfig.DefaultName);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GitHubUsername).IsEqualTo("legacy-user");
    }

    // ── Force-refresh dedup ──────────────────────────────────────────────────

    [Test]
    public async Task Force_refresh_adopts_a_peers_token_instead_of_rotating_again() {
        // The stored token is NOT the one that was rejected — a peer already refreshed. Rotating
        // again would spend a fresh credential that nothing rejected (and for WorkOS, whose
        // refresh token is single-use, that can invalidate the peer's session).
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer, username: "peer") with { AccessToken = "peer-token" });

        var result = await AuthFixtures.NewTokenStore(Config.Root).ForceRefreshAsync(ProfileConfig.DefaultName, "stale-rejected-token");

        // Returned without contacting any refresh endpoint — there is none reachable in this test,
        // so an attempted rotation would have yielded null instead.
        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AccessToken).IsEqualTo("peer-token");
    }

    [Test]
    public async Task Force_refresh_attempts_a_rotation_when_the_stored_token_is_the_rejected_one() {
        // Nobody else refreshed, so this really is the rejected credential: a rotation must be
        // attempted. It fails here (no reachable refresh endpoint), which is how we can tell the
        // attempt was made at all.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer) with { AccessToken = "rejected-token" });

        var result = await AuthFixtures.NewTokenStore(Config.Root).ForceRefreshAsync(ProfileConfig.DefaultName, "rejected-token");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Force_refresh_with_no_stored_token_is_a_no_op() {
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).ForceRefreshAsync(ProfileConfig.DefaultName, "anything")).IsNull();
    }

    [Test]
    public async Task Force_refresh_refuses_to_adopt_a_peer_token_for_a_different_server() {
        // The peer that replaced our rejected token may have logged into a DIFFERENT server. The
        // dedup rule alone would hand that token back, and the caller would send it to the server
        // that just rejected us.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer, username: "peer") with { AccessToken = "peer-token" });

        var adopted = await AuthFixtures.NewTokenStore(Config.Root).ForceRefreshAsync(ProfileConfig.DefaultName, "stale-rejected-token", Server);

        await Assert.That(adopted).IsNull();
    }

    [Test]
    public async Task Force_refresh_adopts_a_peer_token_bound_to_the_same_server() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: Server, username: "peer") with { AccessToken = "peer-token" });

        var adopted = await AuthFixtures.NewTokenStore(Config.Root).ForceRefreshAsync(ProfileConfig.DefaultName, "stale-rejected-token", Server);

        await Assert.That(adopted?.AccessToken).IsEqualTo("peer-token");
    }

    [Test]
    public async Task Accessor_rejects_a_token_swapped_to_another_server_after_the_first_read() {
        // Models a concurrent login/repoint landing between the accessor's snapshot read and its
        // load-and-refresh read: checking only the first snapshot would let the replacement through.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: null, username: "unbound"));

        var resolution = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);
        await Assert.That(resolution.Status).IsEqualTo(AuthStatus.Ok);

        // Now the stored token is bound elsewhere — the next resolution must refuse it.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default", Tokens(serverUrl: OtherServer, username: "swapped"));

        var after = await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForServerAsync(ProfileConfig.DefaultName, Server);

        await Assert.That(after.Status).IsEqualTo(AuthStatus.WrongServer);
        await Assert.That(after.Tokens).IsNull();
    }

    // ── Recovery after a rejection ───────────────────────────────────────────

    [Test]
    public async Task Recovery_adopts_a_differing_stored_token_without_a_second_rotation() {
        // A peer refresh (or a fresh login) landed while we were being rejected. The rotation
        // attempt finds the stored token no longer matches the rejected one and returns it as-is.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: Server, username: "peer") with { AccessToken = "peer-token" });

        var recovered = await AuthFixtures.NewTokenStore(Config.Root).RecoverForServerAsync(ProfileConfig.DefaultName, Server, "rejected-token");

        await Assert.That(recovered?.AccessToken).IsEqualTo("peer-token");
    }

    [Test]
    public async Task Recovery_falls_back_to_the_stored_token_without_a_second_rotation() {
        // Rotation was attempted and failed (unroutable refresh endpoint). The fallback is a RAW
        // read: the same token comes back for one more attempt, and — the point of the finding —
        // no second provider refresh is issued, so a single-use refresh token isn't re-spent.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer) with { AccessToken = "rejected-token" });

        var recovered = await AuthFixtures.NewTokenStore(Config.Root).RecoverForServerAsync(ProfileConfig.DefaultName, OtherServer, "rejected-token");

        await Assert.That(recovered?.AccessToken).IsEqualTo("rejected-token");
    }

    [Test]
    public async Task Recovery_of_an_expired_token_does_not_refresh_twice() {
        // The failure mode behind the finding: falling back to the refresh-aware accessor would
        // see this expired token and rotate a SECOND time. A raw fallback returns it untouched.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer, expiresIn: TimeSpan.FromMinutes(-5)) with { AccessToken = "rejected-token" });

        var recovered = await AuthFixtures.NewTokenStore(Config.Root).RecoverForServerAsync(ProfileConfig.DefaultName, OtherServer, "rejected-token");

        await Assert.That(recovered?.AccessToken).IsEqualTo("rejected-token");
    }

    [Test]
    public async Task Recovery_refuses_a_stored_token_bound_to_another_server() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("default",
            Tokens(serverUrl: OtherServer) with { AccessToken = "elsewhere-token" });

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).RecoverForServerAsync(ProfileConfig.DefaultName, Server, "rejected-token")).IsNull();
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Test]
    public async Task Saved_binding_survives_a_round_trip_and_is_absent_when_not_set() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("bound",   Tokens(serverUrl: Server));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("unbound", Tokens(serverUrl: null));

        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("bound"))!.ServerUrl).IsEqualTo(Server);
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("unbound"))!.ServerUrl).IsNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static StoredTokens Tokens(string? serverUrl, string username = "alice", TimeSpan? expiresIn = null) => new() {
        AccessToken    = "access-token",
        ExpiresAt      = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)),
        GitHubUsername = username,
        Provider       = AuthProvider.GitHubApp,
        ServerUrl      = serverUrl
    };

    async Task WriteConfigAsync(string active, string? extraProfile = null) {
        var profiles = new Dictionary<string, Profile> { [active] = new() { ServerUrl = Server } };
        if (extraProfile is not null) profiles[extraProfile] = new() { ServerUrl = Server };

        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig { ActiveProfile = active, Profiles = profiles });
    }

    async Task WriteLegacyAsync(StoredTokens tokens) {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens));
    }
}
