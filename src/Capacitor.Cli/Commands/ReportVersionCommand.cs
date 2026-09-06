using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Hidden command spawned by the npm wrapper right after <c>kcap update</c> installs a new
/// binary: makes one authenticated GET against <see cref="WhoamiCommand.ProbePath"/> (read-only,
/// no side effects — unlike <see cref="SetupCommand"/>'s cli-setup POST) so the server's
/// endpoint-agnostic version-observer middleware sees the new <see cref="HttpClientExtensions.CliVersionHeader"/>
/// immediately. Fail-open: never throws, never prints, always returns 0, bounded to
/// <see cref="Budget"/> total (discovery + request).
/// </summary>
public sealed class ReportVersionCommand(CapacitorServer server, ICapacitorHttpClient http) {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public async Task<int> HandleAsync() {
        try {
            using var cts = new CancellationTokenSource(Budget);

            var (client, status) =
                await http.ForHookAsync(cts.Token);

            using (client) {
                // Reached with no server configured — it is an offline command — and the status says
                // so before a URL is needed, which is also what keeps the silent return honest.
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return 0;

                var url = AppConfig.NormalizeUrl(server.Url) + WhoamiCommand.ProbePath;

                using var _ = await client.GetOnceAsync(url, Budget, cts.Token);
            }
        } catch {
            // Fail-open — see class doc.
        }

        return 0;
    }
}
