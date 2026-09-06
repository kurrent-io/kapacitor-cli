namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/admin/machines*</c> endpoints — see <see cref="ISessionsApi"/> for
/// the no-URL-composition, no-status-inspection contract every API interface in this namespace
/// follows.</summary>
public interface IMachinesApi {
    /// <summary>
    /// Registers an already-provisioned machine by its PUBLIC client id. Deliberately not retried:
    /// the server 409s a duplicate, so a lost response followed by a retry would report failure for a
    /// registration that landed — while the operator holds a freshly printed secret and is being told
    /// to destroy the credential. A failure before the request lands is the operator's to retry,
    /// knowing what happened.
    /// </summary>
    Task<MachineRegistrationResult> RegisterAsync(
        string clientId, string name, string? role, CancellationToken ct = default);

    Task<MachinesResult> ListAsync(CancellationToken ct = default);

    /// <summary>Revocation is idempotent server-side, so this one is retried: a lost response
    /// followed by a retry converges on the intended state rather than a false conflict.</summary>
    Task<MachineRevokeResult> RevokeAsync(string serviceId, CancellationToken ct = default);
}
