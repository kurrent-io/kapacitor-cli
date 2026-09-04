using Capacitor.Cli.Core.Auth;
using static Capacitor.Tests.Helpers.AuthFixtures;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The ordered commit boundary itself: the claim hook runs last-cancellable and sees every
/// identity before anything durable exists, then config + stamp + tokens publish to completion
/// even under a cancel. Shares the sink key: WorkOS discovery emits into CliTelemetry's
/// process-global sink.
/// </summary>
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class CommitBoundaryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir  => Config.PathTo("tokens");
    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    const string TwoTenants = """
        [{"org_id":1,"org_login":"acme","origin":"https://acme.kcap.ai"},
         {"org_id":2,"org_login":"contoso","origin":"https://contoso.kcap.ai"}]
        """;

    static AuthHttpScript GitHubDiscoveryScript(Func<HttpRequestMessage, HttpResponseMessage>? tokenExchange = null) =>
        AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""", tenants: TwoTenants, tokenExchange: tokenExchange);

    bool TokenFileExists(string profile) => File.Exists(Path.Combine(TokensDir, $"{profile}.json"));

    [Test]
    public async Task Hook_receives_every_identity_before_anything_durable_exists() {
        using var handler = GitHubDiscoveryScript();

        var       identities  = new List<AuthIdentity>();
        var       configAtHook = true;
        var       tokensAtHook = true;

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst(),
            beforeCommit: (ids, _) => {
                identities.AddRange(ids);
                configAtHook = File.Exists(ConfigPath);
                tokensAtHook = TokenFileExists("acme") || TokenFileExists("contoso");

                return Task.CompletedTask;
            });

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(configAtHook).IsFalse();
        await Assert.That(tokensAtHook).IsFalse();
        await Assert.That(identities.Select(i => i.Profile)).IsEquivalentTo(new[] { "acme", "contoso" });
        await Assert.That(identities.Select(i => i.CanonicalServer))
            .IsEquivalentTo(new[] { "https://acme.kcap.ai:443", "https://contoso.kcap.ai:443" });
    }

    [Test]
    public async Task Hook_failure_aborts_the_commit_with_nothing_durable() {
        using var handler = GitHubDiscoveryScript();

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst(),
            beforeCommit: (_, _) => throw new InvalidOperationException("claim store is unwritable"));

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(((AuthResult.Failed)result).Message).Contains("claim store is unwritable");
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
        await Assert.That(TokenFileExists("contoso")).IsFalse();
    }

    [Test]
    public async Task Hook_cancellation_returns_cancelled_with_nothing_durable() {
        using var handler = GitHubDiscoveryScript();

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst(),
            beforeCommit: (_, _) => throw new OperationCanceledException());

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    [Test]
    public async Task Cancel_signalled_after_the_hook_still_commits_every_publication() {
        using var cts     = new CancellationTokenSource();
        using var handler = GitHubDiscoveryScript();

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst(),
            beforeCommit: (_, _) => { cts.Cancel(); return Task.CompletedTask; });

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsTrue();

        var cfg = ConfigMutator.LoadPure(ConfigPath);
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["acme"].AuthProvider!.Provider).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(cfg.Profiles["contoso"].AuthProvider!.Provider).IsEqualTo(AuthProvider.GitHubApp);
    }

    [Test]
    public async Task Config_and_stamps_are_published_before_the_tokens() {
        var configWhenExchanging = new List<bool>();
        var stampWhenExchanging  = new List<bool>();

        using var handler = GitHubDiscoveryScript(tokenExchange: _ => {
            var cfg = ConfigMutator.LoadPure(ConfigPath);
            configWhenExchanging.Add(File.Exists(ConfigPath));
            stampWhenExchanging.Add(cfg.Profiles.GetValueOrDefault("acme")?.AuthProvider is not null);

            return AuthHttp.Json("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}""");
        });

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst());

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(configWhenExchanging).Count().IsEqualTo(2);
        await Assert.That(configWhenExchanging).DoesNotContain(false);
        await Assert.That(stampWhenExchanging).DoesNotContain(false);
    }

    [Test]
    public async Task Stamp_records_each_identitys_own_canonical_server() {
        using var handler = GitHubDiscoveryScript();

        var facade = NewFacade(
            Config.Root,
            new RecordingAuthProgress(), handler, PickerReturningFirst());

        await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        var cfg = ConfigMutator.LoadPure(ConfigPath);
        await Assert.That(ServerIdentity.SameServer(cfg.Profiles["acme"].AuthProvider!.ServerUrl, "https://acme.kcap.ai")).IsTrue();
        await Assert.That(ServerIdentity.SameServer(cfg.Profiles["contoso"].AuthProvider!.ServerUrl, "https://contoso.kcap.ai")).IsTrue();
    }

    [Test]
    public async Task None_stamp_uses_the_vocabulary_the_start_gate_accepts() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"None"}""");
        var       facade  = NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        await facade.LoginAsync(
            "https://none.example", forceDevice: false, profile: "solo", CancellationToken.None, adoptServer: true);

        var profile = ConfigMutator.LoadPure(ConfigPath).Profiles["solo"];
        var stamp   = profile.AuthProvider;

        // The two comparisons OnboardingGate.EvaluateResolvedAsync makes for a None server.
        await Assert.That(stamp!.Provider).IsEqualTo("None");
        await Assert.That(string.Equals(stamp.Provider, AuthProvider.None, StringComparison.OrdinalIgnoreCase)).IsTrue();
        await Assert.That(ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl)).IsTrue();
    }

    [Test]
    public async Task A_login_cancelled_before_the_boundary_leaves_no_stamp() {
        using var cts   = new CancellationTokenSource();
        var       polls = 0;

        using var handler = AuthHttp.Script(
            authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""",
            devicePoll: () => {
                if (++polls == 2) cts.Cancel();

                return AuthHttp.Json("""{"error":"authorization_pending"}""");
            });

        var facade = NewFacade(Config.Root, new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    public async Task WorkOS_ready_publishes_the_config_before_todays_stored_token_fields() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "u", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "u" }, OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" };

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched));

        await Assert.That(flow).IsTypeOf<WorkOSDiscoveryFlow.Ready>();

        var seen   = new List<AuthIdentity>();
        var result = await WorkOSDiscovery.PublishAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root),
            (WorkOSDiscoveryFlow.Ready)flow, new RecordingAuthProgress(),
            beforeCommit: (ids, _) => { seen.AddRange(ids); return Task.CompletedTask; },
            ct: CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(seen.Select(i => i.Profile)).IsEquivalentTo(new[] { "eventuous" });

        var tokenPath = Path.Combine(TokensDir, "eventuous.json");
        await Assert.That(File.GetLastWriteTimeUtc(ConfigPath)).IsLessThanOrEqualTo(File.GetLastWriteTimeUtc(tokenPath));

        var stored = await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("eventuous");
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");
        await Assert.That(stored.RefreshToken).IsEqualTo("rt2");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(stored.ClientId).IsEqualTo("client_d");
        await Assert.That(stored.GitHubUsername).IsEqualTo("Ada");
        await Assert.That(stored.ServerUrl).IsEqualTo("https://eventuous.kcap.ai:443");

        var cfg = ConfigMutator.LoadPure(ConfigPath);
        await Assert.That(cfg.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(cfg.Profiles["eventuous"].AuthProvider!.Provider).IsEqualTo(AuthProvider.WorkOS);
    }

    [Test]
    public async Task A_token_publication_that_throws_still_answers_Committed_for_what_landed() {
        // A file where the tokens directory belongs: TokenStore.SaveAsync throws out of the boundary.
        Directory.CreateDirectory(Path.GetDirectoryName(TokensDir)!);
        await File.WriteAllTextAsync(TokensDir, "not a directory");

        try {
            var flow     = await ReadyEventuousFlowAsync();
            var progress = new RecordingAuthProgress();

            var result = await WorkOSDiscovery.PublishAsync(Config.Root, AuthFixtures.NewTokenStore(Config.Root), flow, progress, beforeCommit: null, ct: CancellationToken.None);

            // The config commit landed, so the boundary had begun — no torn stop, and the loss is reported.
            await Assert.That(result).IsTypeOf<AuthResult.Committed>();
            await Assert.That(progress.Errors.Any(e => e.Contains("could not be saved"))).IsTrue();
            await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles["eventuous"].AuthProvider!.Provider)
                .IsEqualTo(AuthProvider.WorkOS);
        } finally {
            File.Delete(TokensDir);
        }
    }

    [Test]
    public async Task A_config_commit_that_throws_fails_and_publishes_no_token() {
        // A directory where config.json belongs: the config publish cannot rename over it.
        Directory.CreateDirectory(ConfigPath);

        try {
            var flow     = await ReadyEventuousFlowAsync();
            var progress = new RecordingAuthProgress();

            var result = await WorkOSDiscovery.PublishAsync(Config.Root, AuthFixtures.NewTokenStore(Config.Root), flow, progress, beforeCommit: null, ct: CancellationToken.None);

            // Nothing durable began, so this arm is honestly a failure rather than a partial commit.
            await Assert.That(result).IsTypeOf<AuthResult.Failed>();
            await Assert.That(TokenFileExists("eventuous")).IsFalse();
        } finally {
            Directory.Delete(ConfigPath, recursive: true);
        }
    }

    // A foreign login writes no config at all, so the token IS the whole commit: losing it leaves
    // nothing durable, and Committed (exit 0 + "Logged in as") would be a lie.
    [Test]
    public async Task A_token_only_commit_whose_sole_publication_fails_answers_Failed() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles      = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://other.example" } },
            ActiveProfile = "acme",
        });
        Directory.CreateDirectory(Path.GetDirectoryName(TokensDir)!);
        await File.WriteAllTextAsync(TokensDir, "not a directory");

        try {
            using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
            var       progress = new RecordingAuthProgress();
            var       facade   = NewFacade(Config.Root, progress, handler);

            var result = await facade.LoginAsync(
                "https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None);

            await Assert.That(result).IsTypeOf<AuthResult.Failed>();
            await Assert.That(((AuthResult.Failed)result).Reason).IsEqualTo(AuthFailureReason.Other);
            await Assert.That(progress.Errors.Any(e => e.Contains("could not be saved"))).IsTrue();
            await Assert.That(progress.Notices.Any(n => n.StartsWith("Logged in as", StringComparison.Ordinal))).IsFalse();

            var profile = ConfigMutator.LoadPure(ConfigPath).Profiles["acme"];
            await Assert.That(profile.ServerUrl).IsEqualTo("https://other.example");
            await Assert.That(profile.AuthProvider).IsNull();
        } finally {
            File.Delete(TokensDir);
        }
    }

    // The other arm, unchanged: an adopting login's config commit landed, so the lost token is a
    // warning on top of a real commit rather than a torn stop.
    [Test]
    public async Task An_adopt_login_that_loses_its_token_after_the_config_lands_still_answers_Committed() {
        Directory.CreateDirectory(Path.GetDirectoryName(TokensDir)!);
        await File.WriteAllTextAsync(TokensDir, "not a directory");

        try {
            using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
            var       progress = new RecordingAuthProgress();
            var       facade   = NewFacade(Config.Root, progress, handler);

            var result = await facade.LoginAsync(
                "https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None, adoptServer: true);

            await Assert.That(result).IsTypeOf<AuthResult.Committed>();
            await Assert.That(progress.Errors.Any(e => e.Contains("some credentials could not be saved"))).IsTrue();

            var profile = ConfigMutator.LoadPure(ConfigPath).Profiles["acme"];
            await Assert.That(profile.ServerUrl).IsEqualTo("https://acme.kcap.ai");
            await Assert.That(profile.AuthProvider!.Provider).IsEqualTo(AuthProvider.GitHubApp);
        } finally {
            File.Delete(TokensDir);
        }
    }

    static async Task<WorkOSDiscoveryFlow.Ready> ReadyEventuousFlowAsync() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { User = new() { Id = "u", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" }),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" }));

        return (WorkOSDiscoveryFlow.Ready)flow;
    }

    [Test]
    public async Task WorkOS_ready_with_a_failing_hook_publishes_nothing() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" }));

        var result = await WorkOSDiscovery.PublishAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root),
            (WorkOSDiscoveryFlow.Ready)flow, new RecordingAuthProgress(),
            beforeCommit: (_, _) => throw new IOException("claim not persisted"),
            ct: CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("eventuous")).IsFalse();
    }

    [Test]
    public async Task DiscoverAsync_still_publishes_without_a_hook() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var flow = await WorkOSDiscovery.DiscoverAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { User = new() { Id = "u", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" }),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" }));

        var result = await WorkOSDiscovery.PublishAsync(
            Config.Root, AuthFixtures.NewTokenStore(Config.Root),
            (WorkOSDiscoveryFlow.Ready)flow, new RecordingAuthProgress(), beforeCommit: null, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That((await AuthFixtures.NewTokenStore(Config.Root).LoadAsync("eventuous"))!.AccessToken).IsEqualTo("acc2");
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles["eventuous"].AuthProvider!.Provider)
            .IsEqualTo(AuthProvider.WorkOS);
    }
}
