using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Services;

/// Reads through an authenticated client built from the profile's server URL and token store, the
/// pair the launch client uses. Reads can overlap, so the client is held by lease: a signed-out
/// result retires it for future borrows and the last borrower disposes it; disposal of the source
/// stops new borrows, cancels active reads, awaits them, then disposes the live client.
public sealed class ServerWorkContextSource : IWorkContextSource, IAsyncDisposable {
    public delegate Task<(HttpClient Client, AuthStatus Status)> ClientFactory(
        ConfigRoot config, ProfileContext profiles, string serverUrl, CancellationToken ct);

    sealed class ClientLease(HttpClient client) {
        public HttpClient Client { get; } = client;
        public int  Borrowers;
        public bool Retired;
        public bool Disposed;
    }

    readonly ConfigRoot _config;
    readonly ProfileContext? _profiles;
    readonly ClientFactory _factory;
    readonly SemaphoreSlim _buildGate = new(1, 1);
    readonly object _lock = new();
    readonly CancellationTokenSource _disposeCts = new();
    readonly List<Task> _active = [];
    ClientLease? _lease;
    ServiceProvider? _lane;
    bool _disposed;

    public ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ClientFactory? factory = null) {
        _config   = config;
        _profiles = profiles;
        _factory  = factory ?? RegisteredLaneAsync;
    }

    /// <summary>
    /// The recovering lane, from a container of this source's own. The app holds no authenticated
    /// container — that lane's handlers need a resolved server, which does not exist when the app
    /// starts — so the one place that does have a server URL builds it, once, and disposes it here.
    /// </summary>
    async Task<(HttpClient Client, AuthStatus Status)> RegisteredLaneAsync(
            ConfigRoot config, ProfileContext profiles, string serverUrl, CancellationToken ct) {
        // Built under _buildGate, which BorrowAsync already holds, so no second lock is needed.
        _lane ??= new ServiceCollection()
            .AddSingleton(config)
            .AddSingleton(profiles)
            .AddSingleton(new CapacitorServer(serverUrl, config, profiles))
            .AddCapacitorHttp()
            .BuildServiceProvider();

        // The waiting lane, not the hook one: this client is leased for as long as the pane is open,
        // so it has to outlive a token that expires under it. The status is what retires the lease.
        var attempt = await _lane.GetRequiredService<ICapacitorHttpClient>().ForWaitAsync(ct).ConfigureAwait(false);

        return (attempt.Client, attempt.Status);
    }

    /// Registered under the lock before it can complete, so a concurrent DisposeAsync either
    /// sees it in the drain or refuses it; Monitor is re-entrant, so the synchronous prefix of
    /// ReadCoreAsync taking the same lock is fine.
    public Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        Task<WorkContextRead> task;
        lock (_lock) {
            if (_disposed) return Task.FromResult(WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed"));
            task = ReadCoreAsync(sessionId, ct);
            _active.Add(task);
        }
        task.ContinueWith(t => { lock (_lock) _active.Remove(t); }, TaskScheduler.Default);
        return task;
    }

    async Task<WorkContextRead> ReadCoreAsync(string sessionId, CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var serverUrl = _profiles?.Resolution.ServerUrl;
        if (_profiles is null || string.IsNullOrEmpty(serverUrl)) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        ClientLease? lease;
        try {
            lease = await BorrowAsync(serverUrl, linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed");
        }
        if (lease is null) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        try {
            var read = await WorkContextReader.ReadAsync(new WorkContextClient(lease.Client, serverUrl), sessionId, linked.Token).ConfigureAwait(false);
            if (read.Kind == WorkContextReadKind.SignedOut) Retire(lease);
            return read;
        } catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed");
        } finally {
            Release(lease);
        }
    }

    async Task<ClientLease?> BorrowAsync(string serverUrl, CancellationToken ct) {
        await _buildGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            lock (_lock) {
                if (_lease is { Retired: false } live) {
                    live.Borrowers++;
                    return live;
                }
            }

            var (client, status) = await _factory(_config, _profiles!, serverUrl, ct).ConfigureAwait(false);
            if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                client.Dispose();
                return null;
            }

            var lease = new ClientLease(client) { Borrowers = 1 };
            lock (_lock) {
                if (_disposed) {
                    client.Dispose();
                    return null;
                }
                _lease = lease;
            }
            return lease;
        } finally {
            _buildGate.Release();
        }
    }

    void Retire(ClientLease lease) {
        lock (_lock) {
            lease.Retired = true;
            if (ReferenceEquals(_lease, lease)) _lease = null;
        }
    }

    void Release(ClientLease lease) {
        bool dispose;
        lock (_lock) {
            lease.Borrowers--;
            dispose = lease.Retired && ClaimDisposal(lease);
        }
        if (dispose) lease.Client.Dispose();
    }

    /// Decided under the lock; the caller disposes outside it. True exactly once per lease, and
    /// only once nobody borrows it.
    static bool ClaimDisposal(ClientLease lease) {
        if (lease.Borrowers != 0 || lease.Disposed) return false;
        lease.Disposed = true;
        return true;
    }

    public async ValueTask DisposeAsync() {
        Task[] active;
        ClientLease? lease;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            active = [.. _active];
            lease  = _lease;
            _lease = null;
        }

        _disposeCts.Cancel();
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch (Exception) { /* each read reported its own outcome; only the drain matters here */ }

        if (lease is not null) {
            bool dispose;
            lock (_lock) {
                lease.Retired = true;
                dispose = ClaimDisposal(lease);
            }
            if (dispose) lease.Client.Dispose();
        }
        // After the lease, never before: disposing the container retires the handler chain the
        // borrowed client sends on.
        if (_lane is not null) await _lane.DisposeAsync().ConfigureAwait(false);

        _disposeCts.Dispose();
        _buildGate.Dispose();
    }
}
