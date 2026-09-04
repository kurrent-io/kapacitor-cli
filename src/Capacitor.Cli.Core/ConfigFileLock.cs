using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// Cross-process mutual exclusion among kcap writers of one shared config file. For a file under a
/// <see cref="ConfigRoot"/>, go through <see cref="ConfigRoot.AcquireLock"/> instead — this entry
/// point is for the foreign paths that have no root (Claude's <c>~/.claude.json</c>, Codex's
/// <c>config.toml</c>). EVERY kcap writer of such a file must
/// acquire this lock for its read-modify-write — an in-process <c>lock</c> serializes only one
/// process, and a writer outside the lock can commit between another writer's re-read and
/// rename, losing its update.
///
/// <para><b>Naming/security.</b> On Windows a bare mutex name is session-local
/// (<c>Local\</c>), which misses the real topology here: a service-installed daemon runs in
/// session 0 while the CLI runs in the login session. The lock therefore uses <c>Global\</c>
/// explicitly, created with a DACL granting access to the CURRENT USER only, so another local
/// user cannot squat or hold the name (an existing mutex we cannot open surfaces as an
/// exception → callers fail closed). On non-Windows, .NET named mutexes are already
/// machine-wide (per-user shared-memory files), so the plain name suffices. The name hashes
/// the canonical config path, which itself contains the user's home — distinct users get
/// distinct names even before the DACL.</para>
///
/// <para>Note: kcap versions predating this helper used a bare, differently-prefixed name for
/// the Codex config lock, so mutual exclusion across a version transition is best-effort —
/// accepted: the lock guards rare, explicit admin operations.</para>
/// </summary>
public static class ConfigFileLock {
    /// <summary>Acquires the lock for <paramref name="configPath"/>, waiting up to
    /// <paramref name="timeout"/> (default 10s). Dispose to release. Throws on timeout or an
    /// unopenable (foreign-owned) mutex — callers treat that as a failed, no-write operation.</summary>
    public static IDisposable Acquire(string configPath, TimeSpan? timeout = null) {
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(configPath)))).ToLowerInvariant();
        var mutex = CreateMutex("kcap-cfg-" + hash);
        try {
            return new MutexLease(
                mutex, timeout ?? TimeSpan.FromSeconds(10),
                $"Timed out waiting for another kcap update of {configPath}.");
        } catch {
            mutex.Dispose();
            throw;
        }
    }

    static Mutex CreateMutex(string name) {
        if (!OperatingSystem.IsWindows()) return new Mutex(false, name);

        // Global\ = cross-session (service daemon in session 0 vs. the login-session CLI);
        // the current-user-only DACL keeps other local users from squatting the name.
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Allow));
        return MutexAcl.Create(initiallyOwned: false, @"Global\" + name, out _, security);
    }

    /// <summary>
    /// Holds the mutex on a thread of its own, and releases it there. A <see cref="Mutex"/> belongs
    /// to the thread that waited on it, so a holder that awaits anything resumes on a pool thread
    /// and its <c>Dispose</c> throws <see cref="ApplicationException"/> instead of releasing —
    /// leaving the lock held until the process exits. Owning the thread is what makes a lease safe
    /// to hold across an <c>await</c>.
    /// </summary>
    sealed class MutexLease : IDisposable {
        readonly ManualResetEventSlim _acquired = new(false);
        readonly ManualResetEventSlim _release  = new(false);
        readonly Thread               _thread;

        // Written before _acquired is set and read after the wait on it, so the event's barriers
        // are what publish it.
        Exception? _failure;

        int _disposed;

        /// <summary>Returns once the mutex is held, or throws what the wait produced — a timeout, or
        /// a mutex this user cannot open.</summary>
        internal MutexLease(Mutex mutex, TimeSpan timeout, string timeoutMessage) {
            _thread = new Thread(() => Own(mutex, timeout, timeoutMessage)) {
                IsBackground = true,
                Name         = "kcap-config-lock",
            };

            _thread.Start();
            _acquired.Wait();

            if (_failure is null) return;

            _thread.Join();
            _acquired.Dispose();
            _release.Dispose();

            throw _failure;
        }

        /// <summary>The owner thread's whole life: take the mutex, report the outcome, wait for the
        /// lease to be disposed, release. It reports failures rather than throwing, because nothing
        /// could catch a throw here and it would end the process instead of reaching the caller.</summary>
        void Own(Mutex mutex, TimeSpan timeout, string timeoutMessage) {
            try {
                try {
                    if (!mutex.WaitOne(timeout)) _failure = new TimeoutException(timeoutMessage);
                } catch (AbandonedMutexException) {
                    // The prior writer died while holding the lock; ownership transfers to us.
                }
            } catch (Exception ex) {
                _failure = ex;
            }

            var held = _failure is null;

            _acquired.Set();

            if (!held) return;

            _release.Wait();

            // A release that fails would abandon the mutex, which the next waiter recovers from — so
            // swallowing it costs a recovery, while letting it escape a background thread ends the
            // process.
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* already not ours */ }
            finally { mutex.Dispose(); }
        }

        /// <summary>Returns once the mutex is actually released, not once the release is requested:
        /// a caller that re-acquires immediately would otherwise race its own lease.</summary>
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _release.Set();
            _thread.Join();
            _acquired.Dispose();
            _release.Dispose();
        }
    }
}
