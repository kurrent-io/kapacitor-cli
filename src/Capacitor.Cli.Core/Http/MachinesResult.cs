using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Http;

/// <summary>What listing machines can mean. <see cref="FeatureDisabled"/> is the server having no
/// machine credentials at all — distinct from an empty <see cref="Found"/>, which is a server that
/// has them and none registered.</summary>
public abstract record MachinesResult {
    public sealed record Found(MachineSummary[] Machines) : MachinesResult;

    public sealed record FeatureDisabled : MachinesResult;
}
