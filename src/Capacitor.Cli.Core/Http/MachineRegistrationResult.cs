using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// What registering a machine can mean. Every case but <see cref="Registered"/> leaves a credential
/// that exists in WorkOS and is unusable here, so each carries its own remedy and none may be
/// collapsed into a generic failure.
/// </summary>
public abstract record MachineRegistrationResult {
    public sealed record Registered(RegisterMachineResponse Machine) : MachineRegistrationResult;

    /// <summary>The server has no machine credentials, so the name is now taken by an orphan.</summary>
    public sealed record FeatureDisabled : MachineRegistrationResult;

    public sealed record NotPermitted : MachineRegistrationResult;

    /// <summary>The server already holds this client id — a genuine duplicate, or an earlier attempt
    /// of this command that landed and lost its response.</summary>
    public sealed record AlreadyRegistered : MachineRegistrationResult;
}
