using System.Net;
using System.Net.Http.Headers;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Applies the credential source's bearer, and — unless <paramref name="recover"/> says otherwise —
/// recovers from a single 401 by rotating it and resending once, since a bearer can be locally valid
/// yet already refused. Only ONE component may own 401-retry on a client, or one rejection multiplies
/// into several rotations, so only the client-construction choke point installs it.
///
/// <para>Applying the bearer is not optional: a lane that omits this handler sends anonymously. A
/// lane that wants the credential but not the rotation takes <paramref name="recover"/> false.</para>
/// </summary>
internal sealed class UnauthorizedRecoveryHandler(ICredentialSource source, bool recover = true) : DelegatingHandler {
    // Swapped whole, never mutated in place, so concurrent requests see one bearer or the other.
    string? _current;

    // Memoized so a no-bearer state (NoAuthRequired, Expired, NotAuthenticated, WrongServer) is
    // resolved once per handler instance rather than once per send. IHttpClientFactory rotates the
    // handler chain on its own lifetime, so a credential that appears later still reaches a fresh instance.
    Task<string?>? _seed;

    protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
        // The handler's own bearer, not the client's default header, which still carries the refused one.
        var applied = Volatile.Read(ref _current) ?? await SeedAsync(cancellationToken);

        if (applied is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", applied);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (!recover) return response;

        // A refusal with nothing sent says the credential did not exist when this handler resolved,
        // not that it never will: a session client is built once and held for the process's life, so
        // a login finishing after that would otherwise leave every later send anonymous. Dropping the
        // memo is what lets the next one ask again.
        if (applied is null) Volatile.Write(ref _seed, null);

        if (!CanResend(request)) return response;

        // `applied`, not a re-read: a peer may have rotated already, and blaming its fresh credential
        // would discard one the server never refused. With nothing refused the source re-reads, which
        // is the only repair available when the rejection names no token.
        var rotated = await source.RotateAsync(applied, cancellationToken);

        if (rotated.Bearer is null) return response;

        Volatile.Write(ref _current, rotated.Bearer);
        response.Dispose();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Bearer);

        // base.SendAsync, not recursion: exactly one extra attempt, so a second 401 reaches the caller.
        return await base.SendAsync(request, cancellationToken);
    }

    // A registered client resolves on first send, because the container cannot resolve it at build
    // time. Concurrent first sends may both start a resolve, but only the one that wins the
    // CompareExchange is awaited by every caller, so the source itself is asked at most once.
    async Task<string?> SeedAsync(CancellationToken ct) {
        var seed = Volatile.Read(ref _seed);

        if (seed is null) {
            var started = ResolveOnceAsync(ct);
            seed = Interlocked.CompareExchange(ref _seed, started, null) ?? started;
        }

        try {
            return await seed;
        } catch {
            // A throw is not an answer, and neither is the first caller's cancellation. Memoized, one
            // of them would be this handler's verdict for every later send it serves.
            _ = Interlocked.CompareExchange(ref _seed, null, seed);

            throw;
        }
    }

    async Task<string?> ResolveOnceAsync(CancellationToken ct) {
        var state = await source.ResolveAsync(ct);

        if (state.Bearer is null) return null;

        Interlocked.CompareExchange(ref _current, state.Bearer, null);

        return Volatile.Read(ref _current);
    }

    // Only a body that re-serializes can be replayed; a stream-backed one is consumed by the first
    // attempt. JsonContent must stay listed or the JSON-posting call sites lose recovery.
    static bool CanResend(HttpRequestMessage request) =>
        request.Content is null or ByteArrayContent or System.Net.Http.Json.JsonContent;
}
