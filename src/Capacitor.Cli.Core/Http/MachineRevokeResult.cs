namespace Capacitor.Cli.Core.Http;

/// <summary>What revoking a machine can mean.</summary>
public abstract record MachineRevokeResult {
    public sealed record Revoked : MachineRevokeResult;

    public sealed record NotFound : MachineRevokeResult;
}
