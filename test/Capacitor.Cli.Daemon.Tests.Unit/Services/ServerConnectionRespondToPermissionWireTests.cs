using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the daemon's local-decision relay against the server's positional
/// <c>RespondToPermission</c> hub method over a real SignalR hub. The protocol layer matches a
/// positional invocation by argument count before the hub body runs, so a
/// <see cref="ServerConnection"/> double that overrides the invoke seam can never observe a
/// mismatch — only a hub declaring the server's parameter list can.
/// </summary>
public class ServerConnectionRespondToPermissionWireTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class Received {
        public TaskCompletionSource<(string SessionId, string RequestId, string Behavior)> Call { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// <c>CapacitorHub.RespondToPermission</c>'s parameter list, verbatim. The trailing defaults
    /// exist for in-process C# callers only: over the wire all seven arguments must be sent.
    /// </summary>
    sealed class SessionsHub(Received received) : Hub {
        public Task RespondToPermission(
                string  sessionId,
                string  requestId,
                string  behavior,
                object? applyPermissions    = null,
                object? updatedInput        = null,
                string? selectedOptionId    = null,
                string? selectedOptionLabel = null
            ) {
            received.Call.TrySetResult((sessionId, requestId, behavior));

            return Task.CompletedTask;
        }
    }

    sealed class TestServerConnection(DaemonConfig config)
        : ServerConnection(config, UnusedTokenStore.Create(), NullLoggerFactory.Instance,
                           NullLogger<ServerConnection>.Instance);

    [Test]
    public async Task Relay_lands_on_a_hub_declaring_the_servers_parameter_list() {
        var received = new Received();
        var builder  = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(received);
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);

        await using var app = builder.Build();
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var config = new DaemonConfig {
            Name       = "test",
            ServerUrl  = app.Urls.Single(),
            ConfigRoot = Config.Root,
            Profiles   = Resolutions.None(Config.Root),
        };
        await using var conn = new TestServerConnection(config);
        using var cts = new CancellationTokenSource(HangGuard);
        await conn.StartHubAsync(cts.Token);

        var outcome = await conn
            .RespondToPermissionAsync("sess-1", "req-1", new PermissionDecision("allow", null, null))
            .WaitAsync(HangGuard);

        await Assert.That(outcome.Reason).IsNull();
        await Assert.That(outcome.Kind).IsEqualTo(ServerConnection.RespondOutcomeKind.Applied);
        var call = await received.Call.Task.WaitAsync(HangGuard);
        await Assert.That(call).IsEqualTo(("sess-1", "req-1", "allow"));

        await app.StopAsync(cts.Token);
    }
}
