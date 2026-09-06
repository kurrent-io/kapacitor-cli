using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Tests.Helpers;

/// <summary>Hands back a fresh, unauthenticated <see cref="HttpClient"/> per call — for driving an
/// API implementation (<c>FeedbackApi</c>, <c>SessionsApi</c>, ...) directly against a stub server,
/// bypassing the auth-discovery pipeline <c>AddCapacitorHttp</c> wires in production.</summary>
public sealed class FixedCapacitorHttpClient : ICapacitorHttpClient {
    public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

    public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) =>
        Task.FromResult(new HttpClient());

    public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Ok, null, null));

    public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Ok, null, null));

    public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) =>
        Task.FromResult(new HttpClient());

    public HttpClient Anonymous() => new();

    public HttpClient Loopback() => new();

    public HttpClient Bearer() => new();
}
