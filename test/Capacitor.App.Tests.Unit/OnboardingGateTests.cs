using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// The decision-1 gate matrix (design doc §2 decision 1 / §4's <c>OnboardingGate</c> bullet),
/// pinned against <see cref="TokenStore"/>'s REAL refresh/binding rules rather than a
/// reimplementation of them — each test below cites the TokenStore rule it mirrors.
/// </summary>
public class OnboardingGateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    OnboardingGate Gate() => new(Config.Root, AuthFixtures.NewTokenStore(Config.Root));

    const string ProfileName = "acme";
    const string ServerUrl = "https://acme.example";

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);
    string TokensDir  => Config.PathTo("tokens");

    // ── ValidServerUrl (the shared validator) ───────────────────────────────

    [Test]
    [Arguments("https://acme.example", true)]
    [Arguments("http://acme.example", true)]
    [Arguments("https://acme.example:8443/base", true)]
    [Arguments("file:///tmp/x", false)]
    [Arguments("not-a-url", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task ValidServerUrl_accepts_only_absolute_http_or_https(string? url, bool expected) {
        await Assert.That(OnboardingGate.ValidServerUrl(url)).IsEqualTo(expected);
    }

    // ── Gate matrix ──────────────────────────────────────────────────────────

    [Test]
    public async Task No_resolvable_profile_yields_NoProfile() {
        // active_profile names a profile that does not exist in `profiles` — ProfileResolver's
        // ResolveByName returns Profile: null, ProfileName: null (ProfileResolver.cs:85-96).
        WriteConfig(new ProfileConfig { ActiveProfile = "ghost", Profiles = new() });

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.NoProfile);
    }

    [Test]
    public async Task Corrupt_config_json_lands_on_InvalidServerUrl_not_a_crash() {
        // AppConfig.LoadProfileConfig catches JsonException and synthesizes a default profile
        // (ServerUrl: null) rather than throwing — the gate must ride that degrade to
        // InvalidServerUrl, not crash or misreport NoProfile (a "default" profile DOES resolve,
        // it's just unconfigured).
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, "{not valid json");

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.InvalidServerUrl);
    }

    [Test]
    public async Task Invalid_server_url_rejects_file_scheme_and_App_ValidProfileName_agrees() {
        const string fileUrl = "file:///tmp/x";
        var profile = new Profile { ServerUrl = fileUrl };
        WriteConfig(SingleProfileConfig(profile));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.InvalidServerUrl);

        // Decision 2: the gate and App.ValidProfileName must share one validator — before this
        // task, App.ValidProfileName accepted any absolute URI (including file://).
        await Assert.That(OnboardingGate.ValidServerUrl(fileUrl)).IsFalse();
        var resolved = new ResolvedProfile(fileUrl, ProfileName, profile, null);
        await Assert.That(App.ValidProfileName(resolved)).IsNull();
    }

    [Test]
    public async Task No_token_file_yields_NoToken() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task WorkOS_expired_with_refresh_token_and_client_id_is_Complete() {
        // TokenStore.GetValidTokensForProfileAsync (TokenStore.cs:398): WorkOS refreshes only
        // when BOTH RefreshToken and ClientId are present.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(
            AuthProvider.WorkOS, expired: true, serverUrl: ServerUrl, refreshToken: "rt", clientId: "cid"));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task WorkOS_expired_missing_client_id_is_TokenUnusableExpired() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(
            AuthProvider.WorkOS, expired: true, serverUrl: ServerUrl, refreshToken: "rt", clientId: null));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.TokenUnusableExpired);
    }

    [Test]
    public async Task GitHubApp_expired_is_always_refresh_capable_and_Complete() {
        // TokenStore.cs:403-405 / DecideProactiveRefresh: GitHubApp always refreshes via the
        // server's /auth/refresh, independent of RefreshToken (normally null for this provider).
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: true, serverUrl: ServerUrl));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task Wrong_server_token_is_TokenUnusableBinding_even_when_unexpired() {
        // TokenStore.BoundToTarget (TokenStore.cs:339-340) is checked BEFORE expiry — an
        // unexpired-but-wrong-server token must still be refused, never silently accepted.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(
            AuthProvider.GitHubApp, expired: false, serverUrl: "https://other.example"));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.TokenUnusableBinding);
    }

    [Test]
    public async Task Legacy_unbound_token_null_server_url_is_treated_as_usable() {
        // Pinned per TokenStore's REAL treatment: BoundToTarget (TokenStore.cs:339-340) —
        // "tokens.ServerUrl is null || SameServer(...)" — a pre-upgrade token with no stamp has
        // nothing to contradict and is let through to ANY server. The gate must agree, not
        // invent a stricter rule that would strand every pre-upgrade machine behind the wizard.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: null));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task Corrupt_token_file_yields_NoToken_not_a_crash() {
        // TokenStore.ReadTokenFileAsync (TokenStore.cs:94-108): a JsonException degrades to
        // Unusable → null, never throws — the wizard is the recovery path, not a crash.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        Directory.CreateDirectory(TokensDir);
        var valid = JsonSerializer.Serialize(MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: ServerUrl),
            CapacitorJsonContext.Default.StoredTokens);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, $"{ProfileName}.json"), valid + ",\"x\":1}");

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task None_stamp_matching_current_server_is_Complete_without_any_token_file() {
        // Deliberately the raw lowercase literal, not AuthProvider.None: the gate's provider
        // compare must be case-insensitive, proven by this exact-lowercase row still reaching
        // Complete alongside the AuthProvider.None-constant row below.
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp("none", ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));
        // Deliberately no tokens/ directory at all — the stamp must short-circuit the token read.

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
        await Assert.That(Directory.Exists(TokensDir)).IsFalse();
    }

    // Blocker (final review): the stamp WRITER emits AuthProvider.None ("None", capitalized)
    // verbatim, not a lowercased literal — an ordinal-exact "none" compare would silently never
    // satisfy the gate for a real stamp. This is the actual production shape the fix targets.
    [Test]
    public async Task None_constant_stamp_matching_current_server_is_Complete_without_any_token_file() {
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp(AuthProvider.None, ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));
        // Deliberately no tokens/ directory at all — the stamp must short-circuit the token read.

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
        await Assert.That(Directory.Exists(TokensDir)).IsFalse();
    }

    [Test]
    public async Task Stale_none_stamp_after_server_url_change_requires_a_token() {
        // The stamp names a DIFFERENT server than the profile's current one (a server_url edit
        // since the stamp was written) — SameServer fails, so the stamp is ignored, not honored.
        var profile = new Profile {
            ServerUrl    = ServerUrl,
            AuthProvider = new AuthProviderStamp(AuthProvider.None, "https://old.example")
        };
        WriteConfig(SingleProfileConfig(profile));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task Non_none_stamp_does_not_short_circuit_token_rules() {
        // Only a "none" stamp bypasses the token read (see None_stamp_matching_current_server_is_
        // Complete_without_any_token_file). A "workos" stamp — even matching the current server —
        // must still go through the normal expiry/refresh rules: an expired WorkOS token with no
        // RefreshToken/ClientId is TokenUnusableExpired, stamp or no stamp.
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp("workos", ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(
            AuthProvider.WorkOS, expired: true, serverUrl: ServerUrl, refreshToken: null, clientId: null));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await AssertIncomplete(result, GateReason.TokenUnusableExpired);
    }

    [Test]
    public async Task Legacy_profile_without_a_stamp_still_requires_and_accepts_a_valid_token() {
        // No auth_provider stamp at all (the common case for every profile before this task):
        // the gate must fall all the way through to the real token evaluation — proven here by
        // giving it a genuinely valid token and expecting Complete via THAT path, not some
        // stamp-shaped shortcut.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl, AuthProvider = null }));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: ServerUrl));

        var result = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    // ── EvaluateResolvedAsync: the shared-resolution seam (Codex P1) ────────

    // App.StartAsync resolves ONCE (EvaluateAsync's own resolve, which the daemon graph is then
    // built from) and hands that SAME identity to EvaluateResolvedAsync — proving it never
    // re-resolves is what rules out the race the P1 finding described: a concurrent active-profile
    // change between two independent resolves evaluating the gate against a different profile than
    // the one the daemon graph built.
    [Test]
    public async Task EvaluateResolvedAsync_never_re_resolves_ignoring_a_config_change_after_capture() {
        WriteConfig(SingleProfileConfig(
            new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp(AuthProvider.None, ServerUrl) }));
        var resolved = (await AppConfig.ResolveActiveProfile([], Config.Root)).Resolution;

        // Mutates the identity underneath the already-captured resolution — a fresh self-resolving
        // EvaluateAsync call at this point would see NoProfile instead.
        WriteConfig(new ProfileConfig { ActiveProfile = "ghost", Profiles = new() });

        var result = await Gate().EvaluateResolvedAsync(resolved.ProfileName, resolved.Profile, CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    /// A failed EVALUATION must not cost the caller the resolution. The daemon it attaches to and
    /// the profile a sign-in writes to are both read off it, so losing it silently repoints them at
    /// the OS username and "default" — a token file this process cannot read moves both.
    [Test]
    public async Task An_unreadable_token_file_degrades_the_verdict_but_keeps_the_resolution() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        // A directory where the token file goes: the read throws something LoadForProfileAsync does
        // not catch (it handles only missing-file and malformed-JSON).
        Directory.CreateDirectory(Path.Combine(TokensDir, $"{ProfileName}.json"));

        var (result, profiles) = await Gate().EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.EvaluationFailed);
        await Assert.That(profiles).IsNotNull();
        await Assert.That(profiles.Name).IsEqualTo(ProfileName);
        await Assert.That(profiles.Resolution.ServerUrl).IsEqualTo(ServerUrl);
    }

    static async Task AssertIncomplete(GateResult result, GateReason expected) {
        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(expected);
    }

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));

    static StoredTokens MakeToken(
            string provider, bool expired, string? serverUrl, string? refreshToken = null, string? clientId = null) =>
        new() {
            AccessToken    = "t",
            ExpiresAt      = expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "u",
            Provider       = provider,
            ServerUrl      = serverUrl,
            RefreshToken   = refreshToken,
            ClientId       = clientId
        };
}
