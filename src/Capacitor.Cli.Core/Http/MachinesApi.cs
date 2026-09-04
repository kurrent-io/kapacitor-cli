using System.Net;
using System.Net.Http.Json;
using System.Text;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Http;

internal sealed class MachinesApi(ICapacitorHttpClient http, CapacitorServer server) : IMachinesApi {
    public async Task<MachineRegistrationResult> RegisterAsync(
            string clientId, string name, string? role, CancellationToken ct = default) {
        // The *WithRetryAsync helpers check this for themselves; this call avoids them by design, so
        // it checks here — a relative URL otherwise throws from inside HttpClient.
        if (!HttpClientExtensions.IsAcceptableUrl(server.Url))
            throw new CapacitorApiException(null, $"Server URL is not usable: '{server.Url}'.");

        using var response = await SendAsync(
            (c, token) => c.PostAsJsonAsync($"{server.Url}/api/admin/machines",
                                   new RegisterMachineRequest(clientId, name, role),
                                   CapacitorJsonContext.Default.RegisterMachineRequest, token), ct);

        if (response.StatusCode is HttpStatusCode.NotFound)  return new MachineRegistrationResult.FeatureDisabled();
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            return new MachineRegistrationResult.NotPermitted();
        if (response.StatusCode is HttpStatusCode.Conflict) return new MachineRegistrationResult.AlreadyRegistered();

        if (!response.IsSuccessStatusCode) {
            throw new CapacitorApiException(
                (int)response.StatusCode,
                $"Registering the machine failed ({(int)response.StatusCode}). "
              + await response.Content.ReadAsStringAsync(ct));
        }

        var machine = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.RegisterMachineResponse, ct);

        return machine is null
            ? throw new CapacitorApiException((int)response.StatusCode, "The server accepted the registration but described no machine.")
            : new MachineRegistrationResult.Registered(machine);
    }

    public async Task<MachinesResult> ListAsync(CancellationToken ct = default) {
        using var response = await SendAsync((c, token) => c.GetWithRetryAsync($"{server.Url}/api/admin/machines", ct: token), ct);

        if (response.StatusCode is HttpStatusCode.NotFound) return new MachinesResult.FeatureDisabled();
        if (!response.IsSuccessStatusCode) throw await CapacitorApiRequests.FailureAsync(response);

        var machines = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.MachineSummaryArray, ct);

        return new MachinesResult.Found(machines ?? []);
    }

    public async Task<MachineRevokeResult> RevokeAsync(string serviceId, CancellationToken ct = default) {
        // The endpoint binds no body, but an empty StringContent would send `Content-Type: text/plain`,
        // which a strict endpoint can reject with a 415.
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await SendAsync(
            (c, token) => c.PostWithRetryAsync($"{server.Url}/api/admin/machines/{Uri.EscapeDataString(serviceId)}/revoke", body, ct: token), ct);

        if (response.StatusCode is HttpStatusCode.NotFound) return new MachineRevokeResult.NotFound();
        if (!response.IsSuccessStatusCode) throw await CapacitorApiRequests.FailureAsync(response);

        return new MachineRevokeResult.Revoked();
    }

    Task<HttpResponseMessage> SendAsync(Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct) =>
        CapacitorApiRequests.SendAsync(http, server, send, ct);
}
