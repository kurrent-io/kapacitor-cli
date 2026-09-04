using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Tests for per-profile TokenStore methods.
/// </summary>
public class TokenStoreProfileTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string LegacyPath => Config.PathTo("tokens.json");

    [Test]
    public async Task SaveAsync_with_profile_writes_to_per_profile_path() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));

        var expected = Path.Combine(TokensDir, "acme.json");
        await Assert.That(File.Exists(expected)).IsTrue();
    }

    [Test]
    public async Task LoadAsync_with_profile_reads_per_profile_file() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));
        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme");

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GitHubUsername).IsEqualTo("alice");
    }

    [Test]
    public async Task LoadAsync_with_unknown_profile_returns_null() {
        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("nonexistent");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task Legacy_tokens_json_is_migrated_on_first_profile_save() {
        // Write a legacy tokens.json in the config base dir
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);

        await File.WriteAllTextAsync(
            LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens)
        );

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));

        await Assert.That(File.Exists(LegacyPath)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json"))).IsTrue();
    }

    [Test]
    public async Task Per_profile_files_are_independent() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("contoso", MakeTokens("bob"));

        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme"))!.GitHubUsername).IsEqualTo("alice");
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("contoso"))!.GitHubUsername).IsEqualTo("bob");
    }

    [Test]
    public async Task DeleteAsync_removes_all_profile_tokens() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("contoso", MakeTokens("bob"));

        await AuthFixtures.NewTokenStore(Config.Root).DeleteAsync();

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme")).IsNull();
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("contoso")).IsNull();
    }

    [Test]
    public async Task Delete_with_profile_removes_only_that_profile() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("contoso", MakeTokens("bob"));

        AuthFixtures.NewTokenStore(Config.Root).Delete("acme");

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme")).IsNull();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("contoso"))!.GitHubUsername).IsEqualTo("bob");
    }

    [Test]
    public async Task SaveAsync_with_invalid_profile_name_throws() {
        await Assert.That(async () => await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("../evil", MakeTokens("x")))
            .Throws<ArgumentException>();

        await Assert.That(async () => await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("", MakeTokens("x")))
            .Throws<ArgumentException>();

        await Assert.That(async () => await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("has/slash", MakeTokens("x")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task LoadAsync_with_corrupt_json_returns_null() {
        Directory.CreateDirectory(TokensDir);
        var valid   = System.Text.Json.JsonSerializer.Serialize(MakeTokens("alice"), CapacitorJsonContext.Default.StoredTokens);
        var corrupt = valid + ",\"provider\":\"workos\"}"; // complete object, then stray comma + tail (the customer's signature)
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json"), corrupt);

        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme");

        await Assert.That(loaded).IsNull();
    }

    [Test]
    public async Task LoadAsync_with_empty_file_returns_null() {
        Directory.CreateDirectory(TokensDir);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json"), "");

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("acme")).IsNull();
    }

    [Test]
    public async Task LoadAsync_legacy_corrupt_file_returns_null() {
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(LegacyPath, "{\"access_token\":\"x\"},garbage");

        // No per-profile file exists, so the default profile falls back to the legacy path.
        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync(ProfileConfig.DefaultName)).IsNull();
    }

    [Test]
    public async Task GetValidTokensAsync_with_corrupt_file_returns_null() {
        // Reproduces the customer crash through the public entry point StatusCommand uses.
        Directory.CreateDirectory(TokensDir);
        var valid   = System.Text.Json.JsonSerializer.Serialize(MakeTokens("alice"), CapacitorJsonContext.Default.StoredTokens);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "default.json"), valid + ",\"x\":1}");

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).GetValidTokensForProfileAsync(ProfileConfig.DefaultName)).IsNull();
    }

    [Test]
    public async Task SaveAsync_concurrent_writes_never_corrupt() {
        // Alternate long (WorkOS-JWT-sized) and short (GitHub-token-sized) payloads so a
        // shorter write landing over a longer one would splice — the byte-492 signature.
        var longTok  = MakeTokens("alice") with { AccessToken = new string('A', 1200) };
        var shortTok = MakeTokens("bob")   with { AccessToken = "gho_short" };

        var writers = Enumerable.Range(0, 64)
            .Select(i => AuthFixtures.NewTokenStore(Config.Root).SaveAsync("race", i % 2 == 0 ? longTok : shortTok));
        await Task.WhenAll(writers);

        // Whoever wrote last wins, but the file must always be a single complete document.
        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("race");
        await Assert.That(loaded).IsNotNull();
    }

    [Test]
    public async Task SaveAsync_leaves_no_temp_residue() {
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));

        var stray = Directory.EnumerateFiles(TokensDir, "*.tmp").ToArray();
        await Assert.That(stray).IsEmpty();
    }

    [Test]
    public async Task LoadAsync_corrupt_active_profile_does_not_fall_back_to_legacy() {
        // A present-but-corrupt active profile means "not authenticated" — it must NOT
        // resurrect stale credentials from a surviving legacy tokens.json.
        Directory.CreateDirectory(TokensDir);
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(
            LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens));
        var valid = System.Text.Json.JsonSerializer.Serialize(MakeTokens("active"), CapacitorJsonContext.Default.StoredTokens);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "default.json"), valid + ",\"x\":1}");

        await Assert.That(await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync(ProfileConfig.DefaultName)).IsNull();
    }

    [Test]
    public async Task LoadAsync_missing_active_profile_still_falls_back_to_valid_legacy() {
        // Genuine pre-upgrade install: no per-profile file, valid legacy file — fallback preserved.
        if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(LegacyPath)!);
        await File.WriteAllTextAsync(
            LegacyPath,
            System.Text.Json.JsonSerializer.Serialize(MakeTokens("legacy"), CapacitorJsonContext.Default.StoredTokens));

        var loaded = await AuthFixtures.NewTokenStore(Config.Root).LoadForProfileAsync(ProfileConfig.DefaultName);

        await Assert.That(loaded).IsNotNull();
        await Assert.That(loaded!.GitHubUsername).IsEqualTo("legacy");
    }

    [Test]
    public async Task DeleteAsync_removes_leaked_temp_files() {
        // Logout must remove ALL token material, including temps leaked by a crash
        // between write and move.
        Directory.CreateDirectory(TokensDir);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "default.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "default.json.999.deadbeef.tmp"), "secret");

        await AuthFixtures.NewTokenStore(Config.Root).DeleteAsync();

        await Assert.That(Directory.EnumerateFiles(TokensDir, "*.tmp").Any()).IsFalse();
    }

    [Test]
    public async Task Delete_profile_removes_only_its_leaked_temp_files() {
        Directory.CreateDirectory(TokensDir);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "acme.json.1.aaaa.tmp"), "secret");
        await File.WriteAllTextAsync(Path.Combine(TokensDir, "contoso.json.2.bbbb.tmp"), "other");

        AuthFixtures.NewTokenStore(Config.Root).Delete("acme");

        await Assert.That(File.Exists(Path.Combine(TokensDir, "acme.json.1.aaaa.tmp"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "contoso.json.2.bbbb.tmp"))).IsTrue();
    }

    [Test]
    public async Task SaveAsync_cleans_up_temp_when_publish_fails() {
        // Force File.Move to fail by making the destination an existing directory; the
        // finally must still remove the temp so no secret-bearing *.tmp is left behind.
        Directory.CreateDirectory(TokensDir);
        Directory.CreateDirectory(Path.Combine(TokensDir, "blocked.json"));

        var threw = false;
        try { await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("blocked", MakeTokens("x")); }
        catch { threw = true; }

        await Assert.That(threw).IsTrue();
        await Assert.That(Directory.EnumerateFiles(TokensDir, "*.tmp").Any()).IsFalse();
    }

    [Test]
    public async Task SaveAsync_sets_owner_only_file_mode_on_unix() {
        if (OperatingSystem.IsWindows()) return; // Unix file-mode behavior only

        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync("acme", MakeTokens("alice"));

        var mode = File.GetUnixFileMode(Path.Combine(TokensDir, "acme.json"));
        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static StoredTokens MakeTokens(string username) => new() {
        AccessToken    = "t",
        ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
        GitHubUsername = username,
        Provider       = AuthProvider.GitHubApp
    };
}
