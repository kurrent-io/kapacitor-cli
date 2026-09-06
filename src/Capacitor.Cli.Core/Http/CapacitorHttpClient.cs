using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Http;

internal sealed class CapacitorHttpClient(
        IHttpClientFactory factory, ICredentialSource credentials, CapacitorServer server) : ICapacitorHttpClient {
    // One hint per process, not one per client build. A command that talks to several endpoints
    // resolves the same lapsed credential for each of them, and repeating the line once per target
    // says nothing the first one did not.
    int _lapseReported;

    public async Task<HttpClient> ForCommandAsync(CancellationToken ct = default) {
        // The only verb that throws, and it is not in a send path: a command with a user present owes
        // them the hint and an exit code, which the other verbs' callers have no way to render.
        if (!server.Usable) throw new UnusableServerUrlException(HttpClientExtensions.SchemeMissingHint);

        // Resolved here only for the hint: the handler applies the bearer itself, since a client the
        // factory builds has no credential at build time.
        await ReportLapseAsync(await credentials.ResolveAsync(ct));

        return factory.CreateClient(CapacitorClients.Default);
    }

    public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) =>
        Task.FromResult(factory.CreateClient(CapacitorClients.Default));

    public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) =>
        AttemptAsync(CapacitorClients.Hook, ct);

    public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
        AttemptAsync(CapacitorClients.Default, ct);

    async Task<AuthAttempt> AttemptAsync(string lane, CancellationToken ct) {
        // Answered before anything is spent: an unusable URL reaches no token store, no discovery and
        // no socket, and the caller's not-usable branch already knows what to do with it.
        if (!server.Usable)
            return new AuthAttempt(
                factory.CreateClient(lane),
                AuthStatus.UnusableServerUrl, HttpClientExtensions.SchemeMissingHint);

        var state = await credentials.ResolveAsync(ct);

        return new AuthAttempt(
            factory.CreateClient(lane), state.Status, state.Problem, state.Resolution?.IssuedServerUrl);
    }

    // Resolves nothing: the hint is the only reason ForCommandAsync does, and the handler applies
    // the bearer on send either way.
    public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) =>
        Task.FromResult(factory.CreateClient(CapacitorClients.Default));

    public HttpClient Anonymous() => factory.CreateClient(CapacitorClients.Anonymous);

    public HttpClient Loopback() => factory.CreateClient(CapacitorClients.Loopback);

    public HttpClient Bearer() => factory.CreateClient(CapacitorClients.Bearer);

    async Task ReportLapseAsync(CredentialState state) {
        var hint = state.Status switch {
            AuthStatus.Expired => "Authentication token has expired. Run 'kcap login' to re-authenticate.",
            // A machine cannot run `kcap login`, so telling it to is worse than saying nothing.
            AuthStatus.NotAuthenticated => state.Problem is { } reason
                ? $"Machine authentication failed: {reason}"
                : "Not authenticated. Run 'kcap login' to authenticate.",
            AuthStatus.WrongServer =>
                $"Stored token was issued by {state.Resolution?.IssuedServerUrl} but this command targets {server.Url}. " +
                $"Run 'kcap login' (or switch profiles with 'kcap use') to authenticate against {server.Url}.",
            _ => null,
        };

        if (hint is null) return;
        if (Interlocked.Exchange(ref _lapseReported, 1) == 1) return;

        await Console.Error.WriteLineAsync(hint);
    }
}
