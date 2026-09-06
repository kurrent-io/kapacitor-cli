using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

/// The decision-2 carve-out: App.StartAsync evaluates the onboarding gate FIRST (one resolve, via
/// OnboardingGate.EvaluateAsync) and branches — Complete builds the daemon graph with auto-actions
/// open, Incomplete opens the wizard instead and, if it is still Incomplete when the wizard closes,
/// builds that same graph with auto-actions closed permanently. StartAsync itself needs a real
/// daemon/profile — not a unit-test seam, same reason AppStartupTests drives extracted statics
/// instead (see that file's own header comment) — so this exercises the two pure seams App exposes
/// for the carve-out: AutoActionsPermanentlyClosed (the gate→flag switch) and
/// ResolveConsentFlipIdentity (the ConsentFlipCoordinator identity delegate, MUST-WIRE 1).
/// WizardStartupTests owns the wizard-mode half; DaemonLifecycleControllerTests covers the
/// controller-level ctor param behavior (fake lane, no gate involved).
public class AppStartupCarveOutTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    OnboardingGate Gate() => new(Config.Root, AuthFixtures.NewTokenStore(Config.Root));

    const string ProfileName = "acme";
    const string ServerUrl = "https://acme.example";

    // ---- AutoActionsPermanentlyClosed: the pure gate→flag switch ----

    [Test]
    public async Task Complete_gate_keeps_auto_actions_open() {
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete())).IsFalse();
    }

    [Test]
    [Arguments(GateReason.NoProfile)]
    [Arguments(GateReason.InvalidServerUrl)]
    [Arguments(GateReason.NoToken)]
    [Arguments(GateReason.TokenUnusableBinding)]
    [Arguments(GateReason.TokenUnusableExpired)]
    [Arguments(GateReason.EvaluationFailed)]
    public async Task Incomplete_gate_closes_auto_actions_for_every_reason(GateReason reason) {
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Incomplete(reason))).IsTrue();
    }

    // ---- End-to-end against a REAL OnboardingGate.EvaluateAsync() ----
    // The two cases that apply without a wizard: valid URL + no token, and an invalid/non-HTTP URL.

    [Test]
    public async Task ValidUrl_noToken_fixture_closes_auto_actions() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));

        var gate = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)gate).Reason).IsEqualTo(GateReason.NoToken);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();
    }

    [Test]
    public async Task InvalidNonHttpUrl_fixture_closes_auto_actions() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var gate = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)gate).Reason).IsEqualTo(GateReason.InvalidServerUrl);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();
    }

    // Symmetric control: a genuinely Complete fixture must NOT close auto-actions.
    [Test]
    public async Task Complete_fixture_keeps_auto_actions_open() {
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp("none", ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));

        var gate = (await Gate().EvaluateAsync(CancellationToken.None)).Result;

        await Assert.That(gate).IsTypeOf<GateResult.Complete>();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsFalse();
    }

    // ---- ResolveConsentFlipIdentity: MUST-WIRE 1's ConsentFlipCoordinator identity delegate ----

    [Test]
    public async Task ResolveConsentFlipIdentity_resolves_active_profile_server_and_daemon_name() {
        var profile = new Profile { ServerUrl = ServerUrl, Daemon = new DaemonSettings { Name = "acme-daemon" } };
        WriteConfig(SingleProfileConfig(profile));

        var (resolvedProfile, server, daemonName) = AppUnderTest.ResolveConsentFlipIdentity(Config.Root);

        await Assert.That(resolvedProfile).IsEqualTo(ProfileName);
        await Assert.That(server).IsEqualTo(ServerIdentity.Canonicalize(ServerUrl));
        await Assert.That(daemonName).IsEqualTo("acme-daemon");
    }

    // Mirrors ConsentFlipCoordinatorTests' own unparseable-server fallback: Canonicalize(...) is null here.
    [Test]
    public async Task ResolveConsentFlipIdentity_falls_back_to_the_raw_server_when_unparseable() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "not a url" }));

        var (_, server, _) = AppUnderTest.ResolveConsentFlipIdentity(Config.Root);

        await Assert.That(server).IsEqualTo("not a url");
    }

    // No config.json at all: ConfigMutator.LoadPure degrades to a fresh default rather than throwing.
    [Test]
    public async Task ResolveConsentFlipIdentity_no_config_file_yields_the_default_profile_with_no_server() {
        var (resolvedProfile, server, daemonName) = AppUnderTest.ResolveConsentFlipIdentity(Config.Root);

        await Assert.That(resolvedProfile).IsEqualTo("default");
        await Assert.That(server).IsEqualTo("");
        await Assert.That(daemonName).IsNotEmpty(); // DaemonNameResolver's OS-username/machine/"daemon" fallback chain
    }

    // ---- EvaluateGateSafelyAsync: round-1 review — a gate exception must not brick startup ----

    [Test]
    public async Task EvaluateGateSafelyAsync_passes_a_successful_result_through_unchanged() {
        var complete = new GateResult.Complete();

        var (result, profiles) = await AppUnderTest.EvaluateGateSafelyAsync(
            _ => Task.FromResult<(GateResult, ProfileContext)>((complete, Resolutions.None(Config.Root))),
            CancellationToken.None);

        await Assert.That(result).IsSameReferenceAs(complete);
        // The resolution rides along with the verdict, so the graph cannot build on a second one.
        await Assert.That(profiles).IsNotNull();
    }

    [Test]
    public async Task EvaluateGateSafelyAsync_degrades_an_unexpected_exception_to_incomplete() {
        var (result, profiles) = await AppUnderTest.EvaluateGateSafelyAsync(
            _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(GateReason.EvaluationFailed);
        // Nothing resolved: every consumer of this reads null as "stay fail-closed".
        await Assert.That(profiles).IsNull();
    }

    // A cancellation matching the caller's OWN token is shutdown, not a gate failure — it must
    // propagate rather than be swallowed into a fabricated Incomplete result.
    [Test]
    public async Task EvaluateGateSafelyAsync_rethrows_a_cancellation_matching_the_callers_token() {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AppUnderTest.EvaluateGateSafelyAsync(_ => throw new OperationCanceledException(), cts.Token));
    }

    // An OperationCanceledException NOT tied to the caller's token (ct never cancelled) is just
    // another unexpected exception — it degrades exactly like InvalidOperationException above.
    [Test]
    public async Task EvaluateGateSafelyAsync_degrades_an_unrelated_cancellation_to_incomplete() {
        var (result, profiles) = await AppUnderTest.EvaluateGateSafelyAsync(
            _ => throw new OperationCanceledException("unrelated"), CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(GateReason.EvaluationFailed);
        // Nothing resolved: every consumer of this reads null as "stay fail-closed".
        await Assert.That(profiles).IsNull();
    }

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root), JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
}
