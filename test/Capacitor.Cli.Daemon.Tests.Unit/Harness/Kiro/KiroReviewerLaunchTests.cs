using System.IO.Pipelines;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Kiro;

/// <summary>
/// The Kiro reviewer's launch shape, asserted on the ARGV and environment the process would receive
/// — not on a round's outcome. A round that completes proves nothing about whether a tool was
/// trusted if the model never called it.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class KiroReviewerLaunchTests {
    const string InstalledVersion = "2.16.0";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static DaemonConfig EnabledConfig(DaemonStore store) {
        var config = new DaemonConfig {
            KiroUnattendedReviewerEnabled = true,
            Store = store,
            Name = "test-daemon",
            DaemonEpoch = "epoch-1"
        };

        // Seeded exactly as enabling the reviewer does in production: without it every launch is
        // refused over an upgrade that never happened.
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.Kiro.Vendor)
            .Affirm(InstalledVersion);

        return config;
    }

    static RuntimeStartContext Ctx(bool isReviewFlow, string[]? mcpAllowlist = null) => new RuntimeStartContext(
        AgentId: "agent-1", Vendor: "kiro", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: isReviewFlow ? "http://kcap.test" : null,
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap")
        with { McpAllowlist = mcpAllowlist };

    static System.Diagnostics.ProcessStartInfo Psi(
            bool isReviewFlow, DaemonConfig config, string[]? mcpAllowlist = null) =>
        AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Kiro, config, Ctx(isReviewFlow, mcpAllowlist),
            resolveGeminiVersion: _ => InstalledVersion);

    static string TrustValue(System.Diagnostics.ProcessStartInfo psi) {
        var i = psi.ArgumentList.IndexOf("--trust-tools");
        return i >= 0 && i + 1 < psi.ArgumentList.Count ? psi.ArgumentList[i + 1] : "";
    }

    [Test]
    public async Task AReviewLaunch_TrustsReadAndThink_AndNeverWriteOrShell() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var value = TrustValue(Psi(isReviewFlow: true, EnabledConfig(Daemons.Store)));

        await Assert.That(value.Split(',')).Contains("fs_read");
        await Assert.That(value.Split(',')).Contains("thinking");
        await Assert.That(value).DoesNotContain("fs_write");
        await Assert.That(value).DoesNotContain("execute_bash");
    }

    /// <summary>
    /// The case a FIXED trust list fails. Asserting the ARGV rather than a round outcome is what makes
    /// this non-vacuous: a reviewer that never calls an allowlisted tool completes identically whether
    /// or not the tool was trusted.
    /// </summary>
    [Test]
    public async Task AReviewLaunchWithAnAllowlist_TrustsThatServersTools() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var psi   = Psi(isReviewFlow: true, EnabledConfig(Daemons.Store), mcpAllowlist: ["kcap-review"]);
        var value = TrustValue(psi);

        // Assert the exact @server/tool PAIRS. Checking "kcap-review" and "/{tool}" separately would
        // pass a mutation that emitted the server name once and namespaced every tool under the WRONG
        // server — the two substrings would both be present and the reviewer still could not call it.
        var entries = value.Split(',');
        var reviewWire = entries
            .Where(e => e.StartsWith('@') && e.Contains("kcap-review", StringComparison.Ordinal))
            .Select(e => e[1..e.IndexOf('/', StringComparison.Ordinal)])
            .Distinct()
            .Single();

        foreach (var tool in KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-review"])
            await Assert.That(entries).Contains($"@{reviewWire}/{tool}");

        // And the result channel is still there alongside it — widening the gate must not move it.
        foreach (var tool in KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools)
            await Assert.That(value).Contains($"/{tool}");
    }

    /// <summary>The control: an interactive launch gets neither the trust argv nor an isolated home,
    /// because a hosted Kiro the user drives should behave exactly as their own session does.</summary>
    [Test]
    public async Task AnInteractiveLaunch_HasNoTrustArgvAndNoIsolatedHome() {
        // Deliberately NOT skipped on Windows: it asserts the interactive path creates no home and
        // no trust argv, which is exactly the assertion that should hold on a platform where the
        // reviewer is unavailable. Skipping it there would drop the coverage that matters most.

        var psi = Psi(isReviewFlow: false, EnabledConfig(Daemons.Store));

        await Assert.That(psi.ArgumentList.Contains("--trust-tools")).IsFalse();
        await Assert.That(psi.Environment.ContainsKey("KIRO_HOME")).IsFalse();
    }

    [Test]
    public async Task AReviewLaunch_SetsAnEmptyOwnerOnlyKiroHome() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var psi = Psi(isReviewFlow: true, EnabledConfig(Daemons.Store));

        await Assert.That(psi.Environment.ContainsKey("KIRO_HOME")).IsTrue();

        var home = psi.Environment["KIRO_HOME"]!;
        await Assert.That(Directory.Exists(home)).IsTrue();
        await Assert.That(Directory.GetFileSystemEntries(home).Length).IsEqualTo(0);

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(home)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// An EXPLICITLY disabled daemon still refuses at the launch boundary. Note the config now has to
    /// set the flag false on purpose — the default is enabled — which is the point of the test: the
    /// opt-out must still be honoured at the boundary an explicit <c>vendor: "kiro"</c> request reaches
    /// without consulting advertisement.
    /// </summary>
    [Test]
    public async Task AnExplicitlyDisabledDaemon_StillRefusesAReviewLaunch() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var config = new DaemonConfig {
            Store = Daemons.Store, Name = "test-daemon", KiroUnattendedReviewerEnabled = false
        };

        await Assert.That(() => Psi(isReviewFlow: true, config))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("kiro_unattended_reviewer_disabled");
    }

    /// <summary>
    /// The other half, and the one this change exists for: a DEFAULT daemon — nothing configured —
    /// launches. Before, an operator got a refusal here and had to edit a service unit to use a feature
    /// they had explicitly requested.
    /// </summary>
    [Test]
    public async Task ADefaultDaemon_LaunchesAReviewWithoutAnyOptIn() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only.");

        var config = new DaemonConfig { Store = Daemons.Store, Name = "test-daemon", DaemonEpoch = "epoch-1" };

        // Seeded exactly as a real boot seeds it, which is now unconditional.
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.Kiro.Vendor)
            .Affirm(InstalledVersion);

        var psi = Psi(isReviewFlow: true, config);

        await Assert.That(psi.ArgumentList).Contains("--trust-tools");
    }

    /// <summary>
    /// The gate must fire on an upgrade. Asserted with the operator flag ON, so this cannot pass for
    /// the wrong reason (a disabled daemon refuses everything).
    /// </summary>
    /// <summary>
    /// An UPGRADED kiro-cli launches, with no operator action. This test used to assert the opposite,
    /// and inverting it is the point of the change: the recorded version is a minimum, so a routine
    /// vendor release no longer takes the reviewer offline.
    /// </summary>
    [Test]
    public async Task AnUpgradedKiro_LaunchesWithoutAnyOperatorAction() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var config = EnabledConfig(Daemons.Store);

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.Kiro, config, Ctx(isReviewFlow: true),
            resolveGeminiVersion: _ => "2.17.0");

        await Assert.That(psi).IsNotNull();
    }

    /// <summary>
    /// …and the other direction still refuses, which is what keeps "minimum" meaningful rather than
    /// "no gate at all". Without this control the test above would also pass if the version check had
    /// simply been deleted.
    /// </summary>
    [Test]
    public async Task AKiroOlderThanTheRecordedMinimum_StillRefusesAReviewLaunch() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var config = EnabledConfig(Daemons.Store);

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
                AcpVendorDescriptors.Kiro, config, Ctx(isReviewFlow: true),
                resolveGeminiVersion: _ => "2.15.0"))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("kiro_reviewer_version_below_minimum");
    }

    /// <summary>
    /// A peer that is ALIVE and never answers. This is the production shape — measured, an
    /// unauthenticated kiro-cli prints "Opening browser..." and stays alive indefinitely rather than
    /// failing — and it is the one case the two terminating fixtures (an unresolvable binary, a peer
    /// that exits before initialize) structurally cannot produce.
    /// </summary>
    [Test]
    public async Task AnAliveButSilentPeer_HitsTheDeadlineAndIsReaped() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only: its isolated home holds review context and cannot be created owner-only on Windows.");

        var config = EnabledConfig(Daemons.Store);
        config.KiroReviewerLaunchTimeoutSeconds = 1;

        // Streams that never yield a frame: the child is up, the pipe is open, nothing arrives.
        var silentIn  = new Pipe();
        var silentOut = new Pipe();
        var process   = new AliveSilentProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Kiro,
            config: config,
            loggerFactory: NullLoggerFactory.Instance,
            connection: new SilentServerConnection(),
            connectionSource: _ => (silentIn.Writer.AsStream(), silentOut.Reader.AsStream(), process),
            resolveVendorVersion: _ => InstalledVersion);

        var ex = await Assert.That(async () =>
            await factory.StartAsync(Ctx(isReviewFlow: true), CancellationToken.None))
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).StartsWith("kiro_reviewer_launch_timeout");

        // Reaped, not merely abandoned — otherwise the child and its transcript-bearing home outlive
        // the round the server has already given up on.
        await Assert.That(process.Terminated).IsTrue();
    }

    sealed class SilentServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance) { }

    sealed class AliveSilentProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid       => 31337;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public bool Terminated { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            Terminated = true;
            HasExited  = true;
            ExitCode   = 137;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The end-to-end wiring of AllowlistedAutoApprove, through the REAL factory and the REAL runtime.
    ///
    /// <para>This exists because the unit tests either side of it cannot catch the production failure:
    /// the admission predicate's own tests compare two helpers over a local fixture, and would pass
    /// unchanged if the factory stopped supplying <c>admittedToolIds</c> altogether. The mutation that
    /// survives them — <c>admittedToolIds: null</c> — is exactly the one this test kills, because a
    /// null set admits nothing and the frame below would reap instead of being approved.</para>
    ///
    /// <para>The alias is read from what the launch ACTUALLY injected (the fake records
    /// <c>session/new</c>), never reconstructed here: reconstructing it would be the second derivation
    /// the whole design is built to avoid, and the test would then pass against a launch whose real
    /// admitted set was something else.</para>
    /// </summary>
    [Test]
    public async Task AReviewLaunch_ApprovesAPermissionFrameNamingItsOwnInjectedTool() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only.");

        var config = EnabledConfig(Daemons.Store);
        var agent  = new FakeAcpAgent();
        var conn   = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Kiro,
            config: config,
            loggerFactory: NullLoggerFactory.Instance,
            connection: conn,
            connectionSource: _ => (agent.ClientWriteStream, agent.ClientReadStream, new AliveSilentProcess()),
            resolveVendorVersion: _ => InstalledVersion);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = agent.RunAsync(cts.Token);

        var started = await factory.StartAsync(Ctx(isReviewFlow: true), cts.Token)
                                   .WaitAsync(TimeSpan.FromSeconds(30));

        // The wire name THIS launch injected, taken from the session/new it actually sent.
        var sessionNew = agent.ReceivedCalls.First(c => c.Method == "session/new").Params!.Value;
        var injected   = sessionNew.GetProperty("mcpServers")[0].GetProperty("name").GetString()!;

        agent.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: $$"""{"toolCallId":"call-1","title":"Running: @{{injected}}/submit_review_result"}""",
            optionsJson:  """[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]""");

        await started.Runtime.SendUserInputAsync("review").WaitAsync(TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (agent.LastServerRequestResponse is null && DateTime.UtcNow < deadline)
            await Task.Delay(25, cts.Token);

        // Approved locally: selected, never routed to a human, and the reviewer is still alive.
        await Assert.That(agent.LastServerRequestResponse).IsNotNull();
        await Assert.That(agent.LastServerRequestResponse!.Value
                               .GetProperty("outcome").GetProperty("outcome").GetString())
            .IsEqualTo("selected");
        await Assert.That(conn.RequestAcpInteractionAsyncCalled).IsFalse();

        await started.Runtime.DisposeAsync();
    }

    /// <summary>
    /// The NEGATIVE half, and what the positive test alone cannot prove. Round 8's point: a mutation
    /// that WIDENED the production admitted set — or wired Kiro to blanket AutoApprove — would satisfy
    /// "the injected tool is approved" while silently approving everything. This asserts the other
    /// direction through the same real factory and runtime: a frame naming a tool this launch did not
    /// inject is refused and the reviewer is reaped.
    /// </summary>
    [Test]
    public async Task AReviewLaunch_ReapsAPermissionFrameNamingAToolItDidNotInject() {
        Skip.Unless(!OperatingSystem.IsWindows(),
            "The Kiro unattended reviewer is POSIX-only.");

        var config = EnabledConfig(Daemons.Store);
        var agent  = new FakeAcpAgent();
        var conn   = new CaptureServerConnection();
        var child  = new AliveSilentProcess();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Kiro,
            config: config,
            loggerFactory: NullLoggerFactory.Instance,
            connection: conn,
            connectionSource: _ => (agent.ClientWriteStream, agent.ClientReadStream, child),
            resolveVendorVersion: _ => InstalledVersion);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = agent.RunAsync(cts.Token);

        var started = await factory.StartAsync(Ctx(isReviewFlow: true), cts.Token)
                                   .WaitAsync(TimeSpan.FromSeconds(30));

        // kcap-flows is the tool the whole containment design exists to keep away from a reviewer.
        agent.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Running: @kcap-flows/start_flow"}""",
            optionsJson:  """[{"optionId":"allow-once","name":"Yes","kind":"allow_once"}]""");

        await started.Runtime.SendUserInputAsync("review").WaitAsync(TimeSpan.FromSeconds(30));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!child.Terminated && DateTime.UtcNow < deadline)
            await Task.Delay(25, cts.Token);

        await Assert.That(child.Terminated).IsTrue();
        await Assert.That(conn.RequestAcpInteractionAsyncCalled).IsFalse();

        await started.Runtime.DisposeAsync();
    }

    sealed class CaptureServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance) {
        public bool RequestAcpInteractionAsyncCalled { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(
                AcpInteractionRequest request, CancellationToken ct = default) {
            RequestAcpInteractionAsyncCalled = true;

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }
}
