namespace Capacitor.Cli.Core.Http;

/// <summary>
/// How a caller asks for a client, expressed as intent rather than options. Each verb pairs a named
/// client with the messaging that suits its caller.
/// </summary>
public interface ICapacitorHttpClient {
    /// <summary>
    /// For an interactive command: a ready client, with the re-auth hint already on stderr when the
    /// credential could not be resolved. The caller may dispose it — that returns it to the factory
    /// without tearing down the shared handler chain.
    /// </summary>
    Task<HttpClient> ForCommandAsync(CancellationToken ct = default);

    /// <summary>
    /// For a watcher, a teardown, anything running unattended: the same client and the same 401
    /// recovery as an interactive command, minus the re-auth hint. Nobody reads that stderr, and a
    /// background loop earns one "Run 'kcap login'" per flush there.
    /// </summary>
    Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default);

    /// <summary>
    /// For a long-lived stdio server: one client built on the first tool call and held for the
    /// process's life. Held is the whole point — and why the lane rotates its pooled connections,
    /// since a socket kept for hours outlives the DNS answer that opened it.
    /// </summary>
    Task<HttpClient> ForSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// For a hook or a spool drain: the client plus the auth outcome, and nothing on stderr. Vendors
    /// read hook stderr as the hook's own result, so a message there is not advice but corruption;
    /// the caller decides what a lapsed credential means and whether to skip the send entirely.
    ///
    /// <para>Its lane carries the credential but reports a 401 rather than rotating against it: the
    /// caller sends once inside a budget it cannot extend, and it spools what a rotation would have
    /// resent. A hook leg that wants the rotation — the memory index, whose fetch the caller can
    /// afford to retry — asks for a background client instead.</para>
    /// </summary>
    Task<AuthAttempt> ForHookAsync(CancellationToken ct = default);

    /// <summary>
    /// For a leg that waits on something outside this process — a human in a browser, a flow the
    /// server answers later: the auth outcome, so the caller can decline to start rather than block
    /// on a request that can only 401, and the same 401 recovery a command gets, because a wait can
    /// outlive the short-lived token it began with.
    /// </summary>
    Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default);

    /// <summary>
    /// A client with no bearer and no token-store read, for a request that must not mint or spend a
    /// credential: probing a server the caller is still deciding to adopt, or refreshing at the URL
    /// that issued the token rather than the one configured. Resolves nothing, so it is synchronous.
    /// </summary>
    HttpClient Anonymous();

    /// <summary>
    /// The lane for a daemon-minted 127.0.0.1 capability URL, where the URL IS the credential. Its own
    /// verb because the containment is the point: a borrowed reviewer's sandbox redirects HOME, so
    /// there is no token store to reach for, and the grant must not travel anywhere but the loopback
    /// address it was minted for.
    /// </summary>
    HttpClient Loopback();

    /// <summary>
    /// For a caller that supplies its own bearer and must send exactly that one: no rotation, since
    /// rotating would mutate auth state the caller is in the middle of judging, and no redirect,
    /// since the hop strips a hand-set Authorization and the stripped 401 reads as the server's
    /// verdict on the token.
    /// </summary>
    HttpClient Bearer();
}
