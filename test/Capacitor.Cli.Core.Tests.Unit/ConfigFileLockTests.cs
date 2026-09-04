using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Exercises the REAL kernel objects ConfigFileLock creates, on every platform the suite
/// runs on. The Windows branch of ConfigFileLock is the only caller of MutexAcl/MutexSecurity in
/// the product, and nothing else in the suite reaches it: the types resolve from the framework
/// reference assemblies, so a build compiles whether or not they are actually loadable at run time
/// on Windows. Only running this code on the Windows CI leg proves it.</summary>
public class ConfigFileLockTests {
    [Test]
    public async Task The_lock_is_acquirable_released_and_reacquirable() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);

        // On Windows this is the MutexAcl.Create path: a DACL'd Global\ mutex. A missing or
        // unloadable System.Threading.AccessControl surfaces here and nowhere earlier.
        await Assert.That(() => {
            ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5)).Dispose();
            ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5)).Dispose();
        }).ThrowsNothing();
    }

    /// <summary>A lease may be released by a thread other than the one that took it, which is what
    /// a holder spanning an <c>await</c> does — its continuation resumes on a pool thread.</summary>
    [Test]
    public async Task A_lease_is_released_by_whichever_thread_disposes_it() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);

        var        acquiredOn = Environment.CurrentManagedThreadId;
        var        disposedOn = acquiredOn;
        Exception? failure    = null;

        var lease   = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));
        var release = new Thread(() => {
            disposedOn = Environment.CurrentManagedThreadId;
            try { lease.Dispose(); } catch (Exception ex) { failure = ex; }
        });

        release.Start();
        release.Join();

        // Without this the test would pass on a release that never crossed a thread at all.
        await Assert.That(disposedOn).IsNotEqualTo(acquiredOn);
        await Assert.That(failure).IsNull();

        // Re-acquiring is what proves the mutex was released, rather than that Dispose stayed quiet.
        using var reacquired = ConfigFileLock.Acquire(path, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>The realistic shape: the lock is held across an await, as a read-modify-write around
    /// a network call must be.</summary>
    [Test]
    public async Task A_lease_survives_an_await_inside_its_scope() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);

        using (ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5))) {
            await Task.Run(() => { });
        }

        using var reacquired = ConfigFileLock.Acquire(path, TimeSpan.FromMilliseconds(500));
    }

    [Test]
    public async Task A_second_acquirer_of_the_same_path_times_out_while_the_lock_is_held() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);

        using var held = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));

        await Assert.That(() => ConfigFileLock.Acquire(path, TimeSpan.FromMilliseconds(250)))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task Distinct_config_paths_do_not_share_a_lock() {
        using var tmp = new TempDir();

        using var held = ConfigFileLock.Acquire(tmp.PathTo("held"), TimeSpan.FromSeconds(5));

        await Assert.That(() => ConfigFileLock.Acquire(tmp.PathTo("other"), TimeSpan.FromMilliseconds(250)).Dispose())
            .ThrowsNothing();
    }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task On_Windows_the_mutex_is_Global_and_its_DACL_grants_the_current_user_only() {
        await AssertGlobalMutexDaclAsync();
    }

    /// Attributed rather than guarded inline: CA1416's flow analysis does not follow an
    /// OperatingSystem.IsWindows() guard across a lambda boundary.
    [SupportedOSPlatform("windows")]
    static async Task AssertGlobalMutexDaclAsync() {
        using var tmp = TempDir.WithPathTo("config.json", out var path);
        // Deliberately recomputed rather than shared with ConfigFileLock: the name IS the
        // cross-process, cross-VERSION contract (the class doc records a past rename that silently
        // lost mutual exclusion), and a shared helper would make this test agree with whatever the
        // product does. Opening this exact Global\ name from outside the lock is itself the proof
        // that the mutex is cross-session rather than Local\.
        var name = @"Global\kcap-cfg-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))).ToLowerInvariant();

        using var held = ConfigFileLock.Acquire(path, TimeSpan.FromSeconds(5));

        using var opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions);
        var rules = opened.GetAccessControl()
                          .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                          .Cast<MutexAccessRule>()
                          .ToArray();

        // Exactly one rule: anything else means another identity was granted access to a lock whose
        // whole point is that a different local user cannot squat or hold it.
        await Assert.That(rules.Length).IsEqualTo(1);
        await Assert.That(rules[0].IdentityReference).IsEqualTo(WindowsIdentity.GetCurrent().User!);
        await Assert.That(rules[0].MutexRights).IsEqualTo(MutexRights.FullControl);
        await Assert.That(rules[0].AccessControlType).IsEqualTo(AccessControlType.Allow);
    }
}
