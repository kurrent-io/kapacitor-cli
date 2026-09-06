using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A launch driven through the real ACP factory against an in-process fake agent, so a
/// test can read the transcript the launch produced rather than a runtime built by hand.</summary>
sealed class AcpLaunchNoticeHarness : IAsyncDisposable {
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public FakeAcpAgent                 Fake    { get; }
    public AcpHostedAgentRuntimeFactory Factory { get; }
    public CancellationTokenSource      Cts     { get; } = new();

    Task _fakeRunTask = Task.CompletedTask;

    public AcpLaunchNoticeHarness() {
        Fake = new FakeAcpAgent();

        var connection = new ServerConnection(
            new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance);

        Factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (Fake.ClientWriteStream, Fake.ClientReadStream, new FakeAcpProcess()),
            timeProvider: new FakeTimeProvider());
    }

    public static RuntimeStartContext MakeContext(
            string agentId, string? model = null, string? droppedPick = null,
            PolicySnapshot? policySnapshot = null) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "",
        Model: model, DroppedModelPick: droppedPick, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        ActivityClock: null, PolicySnapshot: policySnapshot);

    public void PublishModels(params string[] modelIds) =>
        Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId, modelIds[0], modelIds.Select(m => (m, m))));

    public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

    public static List<AcpEventEnvelope> Drain(IAcpTranscriptSource transcript) {
        var envelopes = new List<AcpEventEnvelope>();
        while (transcript.Envelopes.TryRead(out var envelope)) envelopes.Add(envelope);
        return envelopes;
    }

    public static IEnumerable<string> SystemNotes(IAcpTranscriptSource transcript) =>
        Drain(transcript).Where(e => e.Kind == AcpEventKind.SystemNote).Select(e => e.Text ?? "");

    public async ValueTask DisposeAsync() {
        Cts.Cancel();
        try { await _fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await Fake.DisposeAsync();
        Cts.Dispose();
    }
}
