using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Captures the one-way sequenced CommandAck/CommandRejected sends (synchronously, so an
/// awaited SubmitAsync has already recorded them) and no-ops everything else the capacity-reject path
/// touches. IsReady is forced true so the base sends aren't short-circuited before our override.
///
/// <para>§3.3: also overrides <see cref="AgentRegisteredAsync"/> so a genuinely SUCCESSFUL
/// sequenced launch (one that reaches <c>RegisterAgentAsync</c>'s awaited, un-guarded
/// <c>_server.AgentRegisteredAsync(...)</c> call) doesn't fault on the base implementation's
/// <c>_hub.InvokeAsync</c> against a never-started HubConnection — no pre-Task-5 test ever drove a
/// full success through THIS server double, since every existing capacity/malformed-tuple test is
/// rejected before reaching registration.</para></summary>
internal sealed class SeqCaptureServerConnection() : ServerConnection(
    new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
    UnusedTokenStore.Create(),
    NullLoggerFactory.Instance,
    NullLogger<ServerConnection>.Instance
) {
    internal override bool                                  IsReady       => true;
    public            List<CommandRejected>                 Rejects       { get; } = [];
    public            List<CommandAck>                      Acks          { get; } = [];
    public            List<(string AgentId, string Reason)> LaunchFaileds { get; } = [];

    public override Task LaunchFailedAsync(string             agentId, string reason) { lock (LaunchFaileds) LaunchFaileds.Add((agentId, reason)); return Task.CompletedTask; }
    public override Task CommandRejectedAsync(CommandRejected rej) { lock (Rejects) Rejects.Add(rej); return Task.CompletedTask; }
    public override Task CommandAckAsync(CommandAck           ack)           { lock (Acks)    Acks.Add(ack);    return Task.CompletedTask; }

    public override Task AgentRegisteredAsync(
        string  agentId,              string? prompt, string? model, string? effort, string? repoPath,
        string? sandboxPolicy = null, string? approvalPolicy = null, string? permissionPreset = null,
        string? runtimeTransport = null) => Task.CompletedTask;
}
