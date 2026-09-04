using Capacitor.Cli.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Core;

public static class CapacitorContextServices {
    /// <summary>
    /// Registers the per-process context an entry point has already resolved. Every value is passed
    /// in: nothing here reads the environment, so a host that resolved differently keeps its own
    /// answer.
    /// </summary>
    public static IServiceCollection AddCapacitorContext(
            this IServiceCollection services,
            ConfigRoot config,
            UserHome home,
            DaemonStore daemons,
            ProfileContext profiles) {
        services.AddSingleton(config);
        services.AddSingleton(home);
        services.AddSingleton(daemons);
        services.AddSingleton(profiles);

        return services;
    }
}
