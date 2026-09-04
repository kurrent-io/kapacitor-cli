using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// An interactive turn that produces nothing says so in the transcript, once, and keeps running. A
/// reviewer launch does not: its first-output deadline already reaps, and two voices on one silence
/// would contradict each other.
/// </summary>
public class AcpTurnSilenceNoticeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    /// <summary>Answers session/prompt only when a test releases it, so a turn can be held silent for
    /// as long as the fake clock is advanced.</summary>
    sealed class SilentProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int     Pid         { get; } = 4242;
        public bool    HasExited   { get; private set; }
        public int?    ExitCode    { get; private set; }
        public string? Diagnostics { get; init; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static RuntimeStartContext MakeContext(string agentId, bool isReviewFlow) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "b", SourceRepo: "/repo"),
        Prompt: "do the thing",
        Model: null, Effort: null, Tools: null,
        IsReview: isReviewFlow, IsReviewFlow: isReviewFlow, Review: null,
        // A review-flow launch builds its result channel from these, and refuses without them.
        Cols: 80, Rows: 24, ServerUrl: "http://kcap.test", DaemonBridgeUrl: null,
        CapacitorPath: "/usr/local/bin/kcap");

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent                 Fake    { get; }
        public FakeTimeProvider             Time    { get; } = new();
        public AcpHostedAgentRuntimeFactory Factory { get; }
        public CancellationTokenSource      Cts     { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness(string? stderr = null) {
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
                connectionSource: _ => (Fake.ClientWriteStream, Fake.ClientReadStream,
                                        new SilentProcess { Diagnostics = stderr }),
                timeProvider: Time);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try { await _fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    static async Task<string?> WaitForSilenceNoteAsync(IAcpTranscriptSource transcript, FakeTimeProvider time) {
        var deadline = DateTime.UtcNow + HangGuard;

        while (DateTime.UtcNow < deadline) {
            while (transcript.Envelopes.TryRead(out var envelope))
                if (envelope.Kind == AcpEventKind.SystemNote && envelope.Text?.Contains("No output") == true)
                    return envelope.Text;

            time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        return null;
    }

    /// A line longer than the cap must not ride into the retained buffer whole — the bound is what
    /// keeps a malformed child from leaving a large string resident for the session.
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Retained_stderr_stays_within_its_cap_for_a_single_enormous_line() {
        Skip.Unless(!OperatingSystem.IsWindows(), "Spawns a POSIX shell.");

        var psi = new ProcessStartInfo("/bin/sh") {
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("awk 'BEGIN { while (i++ < 20000) printf \"x\"; print \"\" }' 1>&2");

        await using var child = new AcpChildProcess(Process.Start(psi)!, NullLogger.Instance);

        await child.WaitForExitAsync(TimeSpan.FromSeconds(10));

        var deadline = DateTime.UtcNow + HangGuard;
        while (child.Diagnostics is null && DateTime.UtcNow < deadline) await Task.Delay(10);

        await Assert.That(child.Diagnostics).IsNotNull();
        await Assert.That(child.Diagnostics!.Length).IsLessThanOrEqualTo(4096);
    }

    [Test]
    public async Task An_interactive_turn_that_produces_nothing_says_so_and_keeps_running() {
        await using var h = new Harness(stderr: "Attempt 1 failed: RATE_LIMIT_EXCEEDED\n");
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        var start = await h.Factory.StartAsync(MakeContext("agent-silent", isReviewFlow: false), h.Cts.Token)
            .WaitAsync(HangGuard);

        var note = await WaitForSilenceNoteAsync(start.Transcript!, h.Time);

        await Assert.That(note).IsNotNull();
        await Assert.That(note!).Contains("3 minutes");
        await Assert.That(start.Runtime.HasExited).IsFalse();

        // Once per turn: the watcher retires on the note, so advancing well past a second window
        // must not produce another. A repeating one would bury the transcript it is trying to explain.
        for (var i = 0; i < 20; i++) {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }

        var extra = 0;
        while (start.Transcript!.Envelopes.TryRead(out var envelope))
            if (envelope.Kind == AcpEventKind.SystemNote && envelope.Text?.Contains("No output") == true)
                extra++;

        await Assert.That(extra).IsEqualTo(0);

        h.Fake.HoldPromptResponses.TrySetResult();
    }

    /// A reviewer launch carries a first-output deadline that reaps on the same silence — a note
    /// beside it would promise the run continues while the reaper ends it.
    [Test]
    public async Task A_reviewer_turn_is_left_to_its_own_deadline() {
        await using var h = new Harness();
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        var start = await h.Factory.StartAsync(MakeContext("agent-reviewer", isReviewFlow: true), h.Cts.Token)
            .WaitAsync(HangGuard);

        for (var i = 0; i < 20; i++) {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }

        var notes = 0;
        while (start.Transcript!.Envelopes.TryRead(out var envelope))
            if (envelope.Kind == AcpEventKind.SystemNote && envelope.Text?.Contains("No output") == true)
                notes++;

        await Assert.That(notes).IsEqualTo(0);

        h.Fake.HoldPromptResponses.TrySetResult();
    }
}
