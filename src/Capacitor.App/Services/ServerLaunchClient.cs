using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// Opens the hub connection lazily on first launch and reuses it. URL suffix and access-token
/// provider mirror the daemon's own ServerConnection (same server, same auth) so the two never
/// drift onto separate conventions.
public sealed class ServerLaunchClient(ProfileContext? profiles, TokenStore tokenStore) : ILaunchClient, IAsyncDisposable {
    readonly SemaphoreSlim _gate = new(1, 1);
    HubConnection? _hub;

    /// The gate is held across the INVOKE, not just the connect. GetConnectionAsync disposes and
    /// recreates _hub whenever it is not Connected, so releasing before the invoke would let a
    /// second launch dispose the very connection this one is still using. Launches are a per-click
    /// UI action, so serializing them costs nothing worth having.
    public async Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            var hub = await ConnectLockedAsync(ct);
            var agentId = await hub.InvokeAsync<string>("RequestLaunchAgentV2", LaunchPayload.For(request), ct);
            return new LaunchOutcome(Started: true, AgentId: agentId, Error: null);
        } catch (Exception ex) {
            // HubException carries the server's own rejection text (capacity, unknown vendor,
            // consent denial) — that IS the message Home should show, not a generic wrapper.
            return new LaunchOutcome(false, null, ex.Message, IsUnauthorized(ex));
        } finally {
            _gate.Release();
        }
    }

    /// Walks the chain because SignalR surfaces the negotiate failure wrapped as often as bare.
    internal static bool IsUnauthorized(Exception ex) {
        for (Exception? e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) return true;
        }

        return false;
    }

    /// Caller MUST already hold _gate — see StartAsync. Disposes and rebuilds a connection that
    /// is no longer Connected, which is only safe while nothing else can be mid-invoke on it.
    async Task<HubConnection> ConnectLockedAsync(CancellationToken ct) {
        if (_hub is { State: HubConnectionState.Connected }) return _hub;

        if (_hub is not null) {
            await _hub.DisposeAsync();
            _hub = null;
        }

        var resolved = profiles?.Resolution.ServerUrl
                    ?? throw new InvalidOperationException("No server configured — run kcap setup first.");

        var hub = new HubConnectionBuilder()
            .WithUrl(
                $"{resolved.TrimEnd('/')}/hubs/sessions",
                options => {
                    options.AccessTokenProvider = async () => {
                        var resolution = await tokenStore.GetValidTokensForServerAsync(profiles!.Name, resolved);
                        return resolution.Tokens?.AccessToken;
                    };
                }
            )
            // AOT: point the JSON protocol at the generated context, or the payload
            // serializes reflectively at runtime and NativeAOT publish warns. Shared with
            // the tests via LaunchHubJson so the wire format can't silently diverge.
            .AddJsonProtocol(o => LaunchHubJson.Configure(o.PayloadSerializerOptions))
            .Build();

        // A failed start leaves a live HubConnection nothing else can reach (_hub is still
        // null), so it must be disposed here or its transport/timers leak for the run.
        try {
            await hub.StartAsync(ct);
        } catch {
            await hub.DisposeAsync();
            throw;
        }

        _hub = hub;
        return hub;
    }

    /// Takes the gate first: disposing it out from under an in-flight launch would fault that
    /// launch's Release. Next StartAsync rebuilds the hub so a fresh AccessTokenProvider reads
    /// tokens written by a just-finished sign-in.
    public async Task InvalidateAsync(CancellationToken ct = default) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_hub is null) return;
            await _hub.DisposeAsync().ConfigureAwait(false);
            _hub = null;
        } finally {
            _gate.Release();
        }
    }

    /// Takes the gate first: disposing it out from under an in-flight launch would fault that
    /// launch's Release. A launch arriving after this point fails with ObjectDisposedException,
    /// which StartAsync already turns into a failed LaunchOutcome.
    public async ValueTask DisposeAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        try {
            if (_hub is not null) {
                await _hub.DisposeAsync();
                _hub = null;
            }
        } finally {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
