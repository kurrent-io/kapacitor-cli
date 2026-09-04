using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Reads published versions from an npm registry. Carries neither our credential nor our observation
/// headers: the registry is not our server. Degrades rather than throws, on
/// <see cref="Auth.TenantProvisioningClient"/>'s convention — an update check must never be able to
/// break the command it is attached to.
///
/// <para>The request path is relative, so the client it is given must carry the registry's base
/// address — the registration sets it.</para>
/// </summary>
public sealed class NpmRegistryClient(HttpClient http) {
    /// <summary>The version a dist-tag currently points at.</summary>
    public async Task<NpmDistTag> GetDistTagAsync(string channel, CancellationToken ct) {
        try {
            using var resp = await http.GetAsync($"@kurrent/kcap/{channel}", ct);
            if (!resp.IsSuccessStatusCode) return new(false, null);

            var body = await resp.Content.ReadAsStringAsync(ct);

            return new(true, JsonNode.Parse(body)?["version"]?.GetValue<string>());
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) {
            return new(false, null);
        }
    }
}
