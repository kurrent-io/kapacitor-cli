using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Names the lane a caller asked for, and answers every verb over the same handler. The choice is
/// otherwise invisible to a unit test: the verbs differ only in the handler chain the production
/// factory attaches, which no stub has.
///
/// <para><paramref name="status"/> is what the hook verb reports, for a caller that decides whether
/// to send at all.</para>
/// </summary>
public sealed class RecordingCapacitorHttpClient(
        HttpMessageHandler? handler = null, AuthStatus status = AuthStatus.Ok) : ICapacitorHttpClient {
    public List<string> Lanes { get; } = [];

    public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) =>
        Task.FromResult(Take(nameof(ForCommandAsync)));

    public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) =>
        Task.FromResult(Take(nameof(ForBackgroundAsync)));

    public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) =>
        Task.FromResult(Take(nameof(ForSessionAsync)));

    public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(Take(nameof(ForHookAsync)), status, null, null));

    public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
        Task.FromResult(new AuthAttempt(Take(nameof(ForWaitAsync)), status, null, null));

    public HttpClient Anonymous() => Take(nameof(Anonymous));

    public HttpClient Loopback() => Take(nameof(Loopback));

    public HttpClient Bearer() => Take(nameof(Bearer));

    HttpClient Take(string lane) {
        Lanes.Add(lane);

        return handler is null ? new() : new(handler, disposeHandler: false);
    }
}
