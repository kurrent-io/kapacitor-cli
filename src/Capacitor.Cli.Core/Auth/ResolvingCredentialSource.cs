using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Picks the credential source this process needs, on first use. Discovery is async and a container
/// cannot construct asynchronously, so the choice happens inside the call and is memoized for the
/// instance's life — except a None verdict, which stays open to re-discovery.
/// </summary>
internal sealed class ResolvingCredentialSource(
        CapacitorServer      server,
        TokenStore           tokens,
        WorkOSClient         workos,
        AuthProviderDiscovery discovery,
        MachineTokenProvider  minter) : ICredentialSource {
    ICredentialSource? _chosen;

    public async Task<CredentialState> ResolveAsync(CancellationToken ct) =>
        await (await ChooseAsync(ct)).ResolveAsync(ct);

    public async Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) =>
        await (await ChooseAsync(ct)).RotateAsync(refused, ct);

    async Task<ICredentialSource> ChooseAsync(CancellationToken ct) {
        if (Volatile.Read(ref _chosen) is { } already) return already;

        var picked = await PickAsync(ct);

        // A None verdict is the one answer that may be an outage fallback rather than the server's:
        // discovery deliberately does not cache that, so pinning it here would leave a daemon or MCP
        // process unauthenticated for its whole life once the server recovers or a login writes a
        // credential. An authoritative None is answered from discovery's own memo, so re-picking it
        // costs a dictionary lookup.
        if (ReferenceEquals(picked, NoCredentials.Instance)) return picked;

        // Two concurrent first calls may both pick. The sources hold no state of their own — the
        // caches they read are elsewhere — so the loser's instance is simply dropped.
        return Interlocked.CompareExchange(ref _chosen, picked, null) ?? picked;
    }

    async Task<ICredentialSource> PickAsync(CancellationToken ct) {
        if (await discovery.DiscoverAsync(server.Url, server.Config, server.Profiles, tokens, ct) == "None")
            return NoCredentials.Instance;

        // Before the token store: a runner has no profile, so that path would find nothing and advise
        // `kcap login`, which a runner cannot do. Gated on Intended, not on both variables, so a
        // half-configured one is told which is missing.
        if (MachineAuth.Intended)
            return MachineAuth.TryRead(out var problem) is { } credential
                ? new MachineCredentials(minter, workos, credential)
                : new Unusable(problem!);

        return new TokenStoreCredentials(tokens, server.Profiles.Name, server.Url);
    }

    sealed class Unusable(string problem) : ICredentialSource {
        public Task<CredentialState> ResolveAsync(CancellationToken ct) =>
            Task.FromResult(new CredentialState(null, AuthStatus.NotAuthenticated, Problem: problem));

        public Task<CredentialState> RotateAsync(string? refused, CancellationToken ct) => ResolveAsync(ct);
    }
}
