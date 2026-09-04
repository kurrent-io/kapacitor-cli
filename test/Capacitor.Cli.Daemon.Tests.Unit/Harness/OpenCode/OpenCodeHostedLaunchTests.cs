using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.OpenCode;

/// <summary>
/// The hosted OpenCode launch shape, asserted on the argv and ENVIRONMENT the process would receive.
/// The environment is where OpenCode's whole posture lives — <c>opencode acp</c> accepts none of the
/// global flags — so an argv-only assertion would certify nothing about this vendor.
/// </summary>
public class OpenCodeHostedLaunchTests {
    static RuntimeStartContext Ctx(bool isReviewFlow = false, string? model = null) =>
        new RuntimeStartContext(
            AgentId: "agent-1", Vendor: "opencode", SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
            Model: model, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
            Cols: 80, Rows: 24,
            ServerUrl: isReviewFlow ? "http://kcap.test" : null,
            DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    static System.Diagnostics.ProcessStartInfo Psi(
            DaemonConfig? config = null, bool isReviewFlow = false) =>
        AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.OpenCode, config ?? new DaemonConfig(), Ctx(isReviewFlow));

    [Test]
    public async Task Descriptor_MatchesTheProbedSurface() {
        var descriptor = AcpVendorDescriptors.OpenCode;

        await Assert.That(descriptor.Vendor).IsEqualTo("opencode");
        await Assert.That(descriptor.Argv.SequenceEqual(["acp"])).IsTrue();

        // Measured at CALL level (spawn -> initialize -> tools/list -> tools/call, nonce reaching the
        // model), NOT read off the advertisement: OpenCode advertises the same {http, sse} shape as
        // Kiro, Gemini and Copilot, and those three do not agree with each other.
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);

        // set_config_option, whose read half is configOptions — verified at effect level, not just by
        // the echoed success that keeps Gemini on NoOpModelSelector.
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);

        // Unprobed, not measured-ineligible: OpenCode advertises loadSession and a resume session
        // capability, and no crash-resume probe has been run.
        await Assert.That(descriptor.SupportsReconnectResume).IsFalse();
    }

    /// <summary>
    /// OpenCode's launch controls are env-shaped, so an argv trust entry would be a lie about where
    /// the posture comes from. Pinned because the natural way to onboard a vendor here is to reach for
    /// <c>UnattendedTrustArgv</c>, and for this one there is no such flag to reach for.
    /// </summary>
    [Test]
    public async Task Descriptor_CarriesNoArgvTrustVector() {
        var descriptor = AcpVendorDescriptors.OpenCode;

        await Assert.That(descriptor.UnattendedTrustArgv.IsEmpty).IsTrue();
        await Assert.That(descriptor.UnattendedTrustArgvBuilder).IsNull();
        await Assert.That(descriptor.Argv.Contains("--auto")).IsFalse();
    }

    /// <summary>
    /// The dual-capture guarantee. This is the assertion that keeps a daemon-hosted OpenCode session
    /// from being ingested twice — once by the ACP mapper and once by kcap's own live-ingest plugin
    /// loading inside the hosted child (measured, controlled pair, probe §4).
    /// </summary>
    [Test]
    public async Task EveryLaunch_SuppressesTheKcapPlugin() {
        var psi = Psi();

        await Assert.That(psi.Environment[OpenCodeLaunchEnvironment.PureVariable]).IsEqualTo("1");
    }

    /// <summary>
    /// INTERACTIVE too, and that is the point rather than an incidental detail: an interactive hosted
    /// session is exactly as double-captured as a reviewer would be, so scoping the suppression to
    /// review launches — the shape Kiro's isolated home uses, and therefore the easy mistake to
    /// copy — would leave the common case broken.
    /// </summary>
    [Test]
    public async Task PluginSuppression_IsNotScopedToReviewLaunches() {
        var interactive = Psi(isReviewFlow: false);

        await Assert.That(interactive.Environment[OpenCodeLaunchEnvironment.PureVariable]).IsEqualTo("1");
    }

    /// <summary>
    /// The suppression must not leak onto a sibling vendor: it is a statement about OpenCode's plugin
    /// system, and setting it for everyone would be inert today but silently wrong the moment another
    /// vendor read the same variable.
    /// </summary>
    [Test]
    public async Task PluginSuppression_IsNotAppliedToOtherVendors() {
        var cursor = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Cursor, new DaemonConfig(), Ctx() with { Vendor = "cursor" });

        await Assert.That(cursor.Environment.ContainsKey(OpenCodeLaunchEnvironment.PureVariable)).IsFalse();
    }

    /// <summary>
    /// A review-flow launch on a default (non-consenting) daemon is refused BY THE FACTORY, rather than
    /// by trusting the orchestrator's gate to have run first. Unattended support being declared on the
    /// descriptor only makes the vendor eligible — <c>OpenCodeReviewerCapability</c> is what decides,
    /// and this is the launch boundary, which an explicit <c>vendor: "opencode"</c> request reaches
    /// without consulting advertisement. The gate's own arms live in
    /// <c>OpenCodeReviewerCapabilityTests</c>; this asserts the factory actually consults it.
    ///
    /// <para><b>The refusal CODE is platform-dependent, and asserting the consent one unconditionally
    /// fails on Windows for a reason unrelated to what this tests.</b> The gate checks the platform
    /// FIRST and short-circuits, so a Windows host answers `unsupported_platform` before consent is
    /// ever consulted — which is correct behaviour and exactly the trap
    /// <c>KiroReviewerCapability.Decide</c> documents having been caught by. Found on the Windows CI
    /// leg after this passed locally on macOS. Both platforms still assert the factory refuses with a
    /// coded reason; only the consent-specific code is POSIX-scoped.</para>
    /// </summary>
    [Test]
    public async Task AReviewFlowLaunch_IsRefusedOnADaemonThatExplicitlyDisabledIt() {
        var disabled = new DaemonConfig { OpenCodeUnattendedReviewerEnabled = false };

        await Assert.That(() => Psi(disabled, isReviewFlow: true))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_");

        Skip.Unless(!OperatingSystem.IsWindows(),
            "The disabled arm is unreachable on Windows: the gate refuses on platform first, before "
          + "the opt-out is consulted.");

        await Assert.That(() => Psi(disabled, isReviewFlow: true))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("opencode_unattended_reviewer_disabled");
    }

    [Test]
    public async Task Availability_TracksTheConfiguredBinary() {
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.OpenCode,
            config: new DaemonConfig { OpenCodePath = "kcap-opencode-that-does-not-exist" },
            loggerFactory: Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            // Never spawns: this reads a capability property only.
            connectionSource: _ => throw new InvalidOperationException(
                "IsAvailable must not spawn a process."));

        await Assert.That(factory.Vendor).IsEqualTo("opencode");
        await Assert.That(factory.IsAvailable()).IsFalse();
    }

    sealed class StubServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        UnusedTokenStore.Create(),
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
}
