using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Request body for the proxy's <c>POST /connect/m2m-applications</c>. Carries no organization.</summary>
public sealed record CreateMachineApplicationRequest(
        [property: JsonPropertyName("name")] string Name
    );

/// <summary>
/// The proxy's provisioning result.
///
/// <para><c>client_secret</c> is present ONLY on a create. WorkOS discloses a secret exactly once, so
/// an idempotent hit (the machine already existed) returns null — there is nothing left to disclose,
/// and pretending otherwise would be worse than saying so.</para>
/// </summary>
public sealed record CreateMachineApplicationResponse {
    [JsonPropertyName("application_id")]  public string  ApplicationId  { get; init; } = "";
    [JsonPropertyName("client_id")]       public string  ClientId       { get; init; } = "";
    [JsonPropertyName("client_secret")]   public string? ClientSecret   { get; init; }
    [JsonPropertyName("organization_id")] public string  OrganizationId { get; init; } = "";
    [JsonPropertyName("created")]         public bool    Created        { get; init; }
}

/// <summary>Request body for the tenant's <c>POST /api/admin/machines</c>. Public data only.</summary>
public sealed record RegisterMachineRequest(
        [property: JsonPropertyName("workos_client_id")] string  WorkOsClientId,
        [property: JsonPropertyName("display_name")]     string  DisplayName,
        [property: JsonPropertyName("role")]             string? Role
    );

/// <summary>One machine as the tenant reports it. Carries no secret — the tenant never sees one.</summary>
public sealed record MachineSummary {
    [JsonPropertyName("service_id")]       public string          ServiceId      { get; init; } = "";
    [JsonPropertyName("user_id")]          public string          UserId         { get; init; } = "";
    [JsonPropertyName("workos_client_id")] public string          WorkOsClientId { get; init; } = "";
    [JsonPropertyName("display_name")]     public string          DisplayName    { get; init; } = "";
    [JsonPropertyName("role")]             public string          Role           { get; init; } = "";
    [JsonPropertyName("created_by")]       public string          CreatedBy      { get; init; } = "";
    [JsonPropertyName("created_at")]       public DateTimeOffset  CreatedAt      { get; init; }
    [JsonPropertyName("revoked_at")]       public DateTimeOffset? RevokedAt      { get; init; }
    [JsonPropertyName("usable")]           public bool            Usable         { get; init; }
}

/// <summary>The tenant's register response — the derived principal id.</summary>
public sealed record RegisterMachineResponse {
    [JsonPropertyName("service_id")] public string ServiceId { get; init; } = "";
    [JsonPropertyName("user_id")]    public string UserId    { get; init; } = "";
}

/// <summary>Why provisioning produced no application.</summary>
public enum MachineProvisioningError {
    None,
    Unauthorized,
    Forbidden,
    Rejected,
    Unreachable
}

/// <summary>
/// The proxy's answer to a provisioning request. <c>Rejected</c> carries the proxy's own status and
/// body, because the operator cannot act on "it failed" alone; <c>Unreachable</c> carries the
/// transport message. A <c>None</c> with no application means the proxy answered but disclosed no
/// credential — which is the same dead end as a create that returns an empty secret.
/// </summary>
public sealed record MachineProvisioningResult(
        CreateMachineApplicationResponse? Application,
        MachineProvisioningError          Error,
        int                               Status = 0,
        string?                           Detail = null
    );
