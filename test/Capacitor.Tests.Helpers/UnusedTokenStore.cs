using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A store for a collaborator that never consults one — a server connection built against an
/// unreachable URL and never started. Its root is a path that cannot be created, so a call that
/// unexpectedly reaches disk fails loudly instead of reading a real config directory.
/// </summary>
public static class UnusedTokenStore {
    public static TokenStore Create() => AuthFixtures.NewTokenStore(new ConfigRoot("/dev/null/kcap-unused"));
}
