using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Auth;

public interface IAuthProxyClient {
    Task<ProxyConfigResponse?> GetConfigAsync(string proxyUrl, CancellationToken ct = default);
    Task<DiscoveryResult>      DiscoverTenantsAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default);
    Task<DiscoveryResult>      DiscoverWorkOSTenantsAsync(string proxyUrl, string workosAccessToken, CancellationToken ct = default);

    /// <summary>
    /// Provisions a machine application against the operator's own token. Never throws: the caller
    /// prints one message per outcome, and an unreadable success body degrades to no application
    /// rather than to an exception the command has no wording for.
    /// </summary>
    Task<MachineProvisioningResult> CreateMachineApplicationAsync(
        string proxyUrl, string bearer, string name, CancellationToken ct = default);

    /// <summary>Asks the proxy to prepare a browser pick. Null on any failure — the caller falls back.</summary>
    Task<CliPickerPrepareResponse?> PreparePickAsync(
        string proxyUrl, string bearer, string secretHash, CancellationToken ct = default);

    /// <summary>
    /// Polls for the browser's answer. Presents the secret rather than the bearer: a WorkOS access
    /// token lives about five minutes, so a poll spanning a human's decision would expire partway
    /// and drag token refresh into this seam.
    /// </summary>
    Task<CliPickerResultResponse?> PollPickAsync(
        string proxyUrl, string handle, string secret, CancellationToken ct = default);

    /// <summary>
    /// Releases a handle this CLI has stopped waiting on. Best effort: the TTL collects it anyway,
    /// and the point is only that the page stops saying "all set" for an answer nobody will read.
    /// </summary>
    Task AbandonPickAsync(string proxyUrl, string handle, CancellationToken ct = default);
}

public class AuthProxyClient(HttpClient http) : IAuthProxyClient {
    public async Task<ProxyConfigResponse?> GetConfigAsync(string proxyUrl, CancellationToken ct = default) {
        try {
            using var response = await http.GetAsync($"{proxyUrl}/config", ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ProxyConfigResponse, ct);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return null;
        }
    }

    public async Task<DiscoveryResult> DiscoverTenantsAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/discover-tenants");
            request.Headers.Authorization = new("Bearer", githubAccessToken);
            using var response = await http.SendAsync(request, ct);

            return response.StatusCode switch {
                HttpStatusCode.OK                                       => new(await ReadTenants(response, ct), DiscoveryError.None),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new([], DiscoveryError.TokenRejected),
                _                                                       => new([], DiscoveryError.UpstreamError)
            };
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    public async Task<DiscoveryResult> DiscoverWorkOSTenantsAsync(string proxyUrl, string workosAccessToken, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/discover-tenants-workos");
            request.Headers.Authorization = new("Bearer", workosAccessToken);
            using var response = await http.SendAsync(request, ct);

            return response.StatusCode switch {
                HttpStatusCode.OK                                       => new(await ReadTenants(response, ct), DiscoveryError.None),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new([], DiscoveryError.TokenRejected),
                _                                                       => new([], DiscoveryError.UpstreamError)
            };
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new([], DiscoveryError.ProxyUnreachable);
        }
    }

    public async Task<MachineProvisioningResult> CreateMachineApplicationAsync(
            string proxyUrl, string bearer, string name, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/connect/m2m-applications") {
                Content = JsonContent.Create(
                    new CreateMachineApplicationRequest(name),
                    CapacitorJsonContext.Default.CreateMachineApplicationRequest)
            };
            request.Headers.Authorization = new("Bearer", bearer);

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized) return new(null, MachineProvisioningError.Unauthorized);
            if (response.StatusCode is HttpStatusCode.Forbidden)    return new(null, MachineProvisioningError.Forbidden);

            if (!response.IsSuccessStatusCode) {
                return new(null, MachineProvisioningError.Rejected,
                    (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
            }

            return new(
                await response.Content.ReadFromJsonAsync(
                    CapacitorJsonContext.Default.CreateMachineApplicationResponse, ct),
                MachineProvisioningError.None);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            return new(null, MachineProvisioningError.Unreachable, Detail: e.Message);
        } catch (System.Text.Json.JsonException) {
            return new(null, MachineProvisioningError.None);
        }
    }

    public async Task<CliPickerPrepareResponse?> PreparePickAsync(
            string proxyUrl, string bearer, string secretHash, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{proxyUrl}/cli/v1/picker/prepare") {
                Content = JsonContent.Create(
                    new CliPickerPrepareRequest { SecretHash = secretHash },
                    CapacitorJsonContext.Default.CliPickerPrepareRequest)
            };
            request.Headers.Authorization = new("Bearer", bearer);

            using var attempt = Bounded(ct);
            using var response = await http.SendAsync(request, attempt.Token);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.CliPickerPrepareResponse, ct);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) {
            return null;
        }
    }

    public async Task<CliPickerResultResponse?> PollPickAsync(
            string proxyUrl, string handle, string secret, CancellationToken ct = default) {
        try {
            using var attempt = Bounded(ct);
            using var response = await http.PostAsJsonAsync(
                $"{proxyUrl}/cli/v1/picker/{handle}/result",
                new CliPickerResultRequest { Secret = secret },
                CapacitorJsonContext.Default.CliPickerResultRequest, attempt.Token);

            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.CliPickerResultResponse, ct);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) {
            return null;
        }
    }

    public async Task AbandonPickAsync(string proxyUrl, string handle, CancellationToken ct = default) {
        try {
            using var attempt = Bounded(ct);
            using var response = await http.DeleteAsync($"{proxyUrl}/cli/v1/picker/{handle}", attempt.Token);
        } catch (Exception e) when (e is HttpRequestException or OperationCanceledException) {
            // Nothing to do about it and nothing to tell the user: the pick is already abandoned
            // locally, and the TTL is what actually guarantees the handle goes away.
        }
    }

    /// <summary>
    /// Bounds one picker attempt. The shared client's default is 100 seconds, which on an
    /// unreachable proxy would hold the "press a key to choose here" escape for that long and make
    /// the advertised way out look broken. Every one of these calls degrades to the terminal picker,
    /// so giving up early costs nothing.
    /// </summary>
    static CancellationTokenSource Bounded(CancellationToken ct) {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        return cts;
    }

    static async Task<DiscoveredTenant[]> ReadTenants(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DiscoveredTenantArray, ct) ?? [];
}
