using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Option B task 5 (the capstone) — GATED live E2E that drives a REAL TOOL-USING turn
/// against a REAL <c>cursor-agent acp</c> process, through the FULL daemon pipeline built by tasks
/// 1–4: <see cref="AcpHostedAgentRuntimeFactory"/> (real process spawn, no <c>FakeAcpAgent</c>) →
/// <see cref="AcpHostedAgentRuntime"/>'s ACP handshake + chunk aggregation (task 2) →
/// <see cref="AcpEventTranslator"/> (task 1) → <see cref="IAcpTranscriptSource.Envelopes"/> (task
/// 2/4's bind-handoff shape) — the same shape task 3/4's orchestrator wiring reads from, just read
/// directly here instead of through the (SignalR-backed) forwarder.
///
/// Also exercises the permission bridge for real: the python probe backing
/// <c>docs/ai-688-cursor-prototype-findings.md</c>'s "Tool-using turn" section
/// showed Cursor DOES send a real <c>session/request_permission</c> before running an
/// un-allowlisted shell command, so this test's <see cref="AutoApproveServerConnection"/> answers
/// it with a genuine "selected" decision (unlike <c>AcpHostedAgentRuntimeFactoryLiveTests</c>'s
/// HELLO-only gap-1 test, whose <c>CaptureServerConnection</c> always cancels because that prompt
/// never triggers a permission request) — proving <see cref="AcpInteractionBridge"/>'s
/// already-implemented request/response mapping round-trips correctly against the real agent,
/// through the daemon's own code path (not just the python probe).
///
/// Gated behind <c>KCAP_ACP_LIVE=1</c> for the same reason as
/// <c>AcpHostedAgentRuntimeFactoryLiveTests</c>: no <c>cursor-agent</c> binary/account in CI, and no
/// ordinary local run should silently spend a real Cursor turn.
/// </summary>
public class AcpHostedAgentRuntimeFactoryToolUseLiveTests {
    const string LiveGateEnvVar = "KCAP_ACP_LIVE";

    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan LiveTurnTimeout  = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A real (non-connecting) <see cref="ServerConnection"/> subclass — same
    /// established-pattern shape as <c>AcpHostedAgentRuntimeFactoryTests</c>/
    /// <c>AcpHostedAgentRuntimeFactoryLiveTests</c>'s <c>CaptureServerConnection</c> — that
    /// AUTO-APPROVES any ACP interaction request (permission or elicitation) so a real tool-using
    /// turn can complete unattended, instead of just recording-and-cancelling. Records every
    /// request it saw for this test's assertions/report.
    /// </summary>
    sealed class AutoApproveServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            UnusedTokenStore.Create(),
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance
        ) {
        public List<AcpInteractionRequest> Requests { get; } = [];

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            Requests.Add(request);

            var options = request.Options ?? [];
            Console.WriteLine(
                $"[ai-688-task5-live] RequestAcpInteractionAsync: kind={request.Kind} tool={request.ToolName} " +
                $"options=[{string.Join(",", options.Select(o => $"{o.OptionId}:{o.Kind}"))}]");

            // Best-effort pick of a one-shot "allow" option — matches the real Cursor shape the
            // task 5 probe observed ({optionId:"allow-once", kind:"allow_once"}, see the findings
            // doc's "Tool-using turn" section) but doesn't hard-require that exact vocabulary, in
            // case a different tool call offers differently-worded options.
            var chosen = options.FirstOrDefault(o => string.Equals(o.Kind, "allow_once", StringComparison.OrdinalIgnoreCase));
            if (chosen.OptionId is null)
                chosen = options.FirstOrDefault(o => (o.Kind ?? "").Contains("allow", StringComparison.OrdinalIgnoreCase));
            if (chosen.OptionId is null && options.Length > 0)
                chosen = options[0];

            // AcpInteractionBridge.MapPermissionDecision fails closed unless BOTH (a)
            // Outcome is on its AffirmativeOutcomes allowlist AND (b) SelectedOptionId matches one
            // of the OFFERED options' OptionId. "allow_once" is always on that allowlist, so it's
            // used as Outcome regardless of the chosen option's own Kind string — what actually
            // resolves WHICH option gets selected is SelectedOptionId, set below to the option we
            // just picked (or null/no options → "cancel", which the bridge also maps safely).
            var outcome = options.Length > 0 ? "allow_once" : "cancel";

            return Task.FromResult(new AcpInteractionDecision(
                Outcome: outcome,
                SelectedOptionId: chosen.OptionId,
                SelectedOptionLabel: chosen.Label,
                SelectedIndex: null,
                FreeText: null,
                UpdatedToolInput: null));
        }
    }

    [Test]
    public async Task StartAsync_AgainstRealCursorAgentAcp_ToolUsingTurnProducesTranscriptPipeline() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' tool-using turn — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Team-tier `cursor-agent` on PATH).");

        using var worktreeDir = new TempDir();

        // A real (console) logger factory, same rationale as the gap-1 live test: AcpHostedAgentRuntime
        // logs warnings on non-fatal model-selection failures that would otherwise be silently swallowed.
        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        var connection = new AutoApproveServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig(), // CursorPath="cursor-agent"
            loggerFactory: liveLoggerFactory,
            connection: connection,
            connectionSource: null // real cursor-agent acp spawn — gap 1/task 5's production path
        );

        var ctx = new RuntimeStartContext(
            AgentId: "ai-688-task5-live",
            Vendor: "cursor",
            SourceRepoPath: worktreeDir.Path,
            Worktree: WorktreeInfo.Borrowed(worktreeDir.Path),
            Prompt: "Use your shell/command tool to run exactly `echo kcap-e2e-marker` and report the output.",
            Model: "claude-sonnet-4-5", // resolved against session/new's availableModels by AcpModelResolver
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 80,
            Rows: 24,
            ServerUrl: null,
            DaemonBridgeUrl: null,
            CapacitorPath: "/usr/local/bin/kcap");

        using var startCts = new CancellationTokenSource();

        var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);

        // AcpHostedAgentRuntimeFactory.StartAsync always sets HostedRuntimeStart.Transcript to
        // the runtime itself for the "cursor" vendor (task 4's bind-handoff shape) — reading
        // through this interface, rather than downcasting Runtime, exercises exactly the seam
        // task 3/4's orchestrator wiring uses.
        var transcript = started.Transcript ?? throw new InvalidOperationException(
            "AcpHostedAgentRuntimeFactory.StartAsync did not set HostedRuntimeStart.Transcript — task 4's bind-handoff shape regressed.");

        try {
            var envelopes = await CollectEnvelopesAsync(transcript.Envelopes, LiveTurnTimeout);

            Console.WriteLine($"[ai-688-task5-live] observed {envelopes.Count} AcpEventEnvelope(s):");
            foreach (var e in envelopes)
                Console.WriteLine(
                    $"[ai-688-task5-live]   seq={e.Seq} kind={e.Kind} text={Truncate(e.Text)} " +
                    $"toolCallId={e.ToolCallId} toolName={e.ToolName} toolInput={Truncate(e.ToolInputJson)} " +
                    $"toolResult={Truncate(e.ToolResult)} toolIsError={e.ToolIsError}");

            Console.WriteLine($"[ai-688-task5-live] AcpInteractionRequest(s) seen: {connection.Requests.Count}");
            foreach (var r in connection.Requests)
                Console.WriteLine($"[ai-688-task5-live]   kind={r.Kind} tool={r.ToolName} options=[{string.Join(",", (r.Options ?? []).Select(o => o.OptionId))}]");

            // Resilient assertions (real-model non-determinism — this is a manual/gated E2E,
            // not a CI gate; see this class's remarks): every serialized prompt turn — tool-using
            // or not — produces a UserMessage (ProcessTurnAsync emits it unconditionally before
            // sending session/prompt) and at least one AssistantText (the aggregated
            // agent_message_chunk run). Whether a ToolCall/ToolResult pair ALSO appears depends
            // on whether the model actually exercised the tool for THIS run.
            await Assert.That(envelopes.Any(e => e.Kind == AcpEventKind.UserMessage)).IsTrue();
            await Assert.That(envelopes.Any(e => e.Kind == AcpEventKind.AssistantText)).IsTrue();

            var toolCalls   = envelopes.Where(e => e.Kind == AcpEventKind.ToolCall).ToList();
            var toolResults = envelopes.Where(e => e.Kind == AcpEventKind.ToolResult).ToList();

            if (toolCalls.Count > 0) {
                Console.WriteLine("[ai-688-task5-live] turn used a tool — ToolCall envelope(s) present.");
                await Assert.That(toolCalls[0].ToolCallId).IsNotNull();
            } else {
                Console.WriteLine("[ai-688-task5-live] turn did NOT surface a ToolCall envelope (model chose not to use a tool this run).");
            }

            if (toolResults.Count > 0)
                Console.WriteLine("[ai-688-task5-live] turn produced a ToolResult envelope — a tool_call_update reached a terminal status with extractable content.");

            Console.WriteLine($"[ai-688-task5-live] PROJ-686 permission path fired: {connection.Requests.Count > 0}");
        } finally {
            startCts.Cancel();
            await started.Runtime.DisposeAsync();
        }
    }

    /// <summary>
    /// Drains <paramref name="envelopes"/> until <paramref name="timeout"/> elapses or the channel
    /// completes (the runtime disposing closes it). There is no single reliable "the turn is done"
    /// signal on this channel alone (a ToolResult isn't guaranteed — the model might not use a tool;
    /// a terminal AssistantText isn't a distinct marker from a mid-turn one), so this drains for the
    /// whole window rather than trying to detect turn-end early — matching the reference "run
    /// exploratory E2E for a bounded ~60-90s wall clock" pattern the task brief calls for.
    /// </summary>
    static async Task<List<AcpEventEnvelope>> CollectEnvelopesAsync(ChannelReader<AcpEventEnvelope> envelopes, TimeSpan timeout) {
        var collected = new List<AcpEventEnvelope>();
        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await envelopes.WaitToReadAsync(timeoutCts.Token)) {
                while (envelopes.TryRead(out var envelope))
                    collected.Add(envelope);
            }
        } catch (OperationCanceledException) {
            // Timed out draining — fall through with whatever was collected so far so the caller can
            // still log/assert on a partial sequence instead of losing it to an unhandled exception.
        }

        return collected;
    }

    static string Truncate(string? s, int max = 160) =>
        s is null ? "null" : s.Length <= max ? s : s[..max] + "...";
}
