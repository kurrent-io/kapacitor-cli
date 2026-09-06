using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Daemon;

public static class DaemonHttpServices {
    /// <summary>
    /// The same lanes the CLI gets. The daemon reaches the same server with the same credential, so a
    /// second way of building a client here is a second place for the 401 recovery and the observation
    /// headers to drift out of step.
    /// </summary>
    public static IServiceCollection AddDaemonHttp(
            this IServiceCollection services, ConfigRoot configRoot, DaemonConfig config) {
        services.AddSingleton(_ => new CapacitorServer(config.ServerUrl, configRoot, config.Profiles));
        services.AddCapacitorHttp();

        return services;
    }
}
