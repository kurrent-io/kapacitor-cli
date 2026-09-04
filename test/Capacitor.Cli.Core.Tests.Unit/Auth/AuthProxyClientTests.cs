using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class AuthProxyClientTests {
    [Test]
    public async Task GetConfigAsync_returns_client_id_only_when_exchange_url_absent() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"Iv1.abc"}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.GitHubClientId).IsEqualTo("Iv1.abc");
        await Assert.That(config.GitHubCodeExchangeUrl).IsNull();
    }

    [Test]
    public async Task GetConfigAsync_returns_exchange_url_when_present() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"Iv1.abc","github_code_exchange_url":"https://auth.example/auth/github/code-exchange"}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.GitHubClientId).IsEqualTo("Iv1.abc");
        await Assert.That(config.GitHubCodeExchangeUrl).IsEqualTo("https://auth.example/auth/github/code-exchange");
    }

    [Test]
    public async Task GetConfigAsync_reads_workos_fields() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"gh","workos_client_id":"client_d","workos_authkit_domain":""}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config!.WorkOSClientId).IsEqualTo("client_d");
        await Assert.That(config.WorkOSAuthKitDomain).IsEqualTo("");
    }

    [Test]
    public async Task GetConfigAsync_returns_null_on_proxy_unreachable() {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMilliseconds(200);
        var client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync("http://127.0.0.1:1");

        await Assert.That(config).IsNull();
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_tenants_on_200() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""[{"org_id":100,"org_login":"acme","origin":"https://a.example"}]""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.None);
        await Assert.That(result.Tenants.Length).IsEqualTo(1);
        await Assert.That(result.Tenants[0].OrgLogin).IsEqualTo("acme");
        await Assert.That(result.Tenants[0].OrgId).IsEqualTo(100L);
        await Assert.That(result.Tenants[0].Origin).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_TokenRejected_on_401() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_TokenRejected_on_403() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_UpstreamError_on_502() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.UpstreamError);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_ProxyUnreachable_on_connection_refused() {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMilliseconds(200);
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync("http://127.0.0.1:1", "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.ProxyUnreachable);
    }

    [Test]
    public async Task DiscoverWorkOSTenantsAsync_parses_provider_aware_rows() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants-workos").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""[{"provider":"WorkOS","organization_id":"org_a","slug":"eventuous","display_name":"Eventuous","origin":"https://eventuous.kcap.ai"}]""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverWorkOSTenantsAsync(server.Urls[0], "wos.tok.en");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.None);
        await Assert.That(result.Tenants[0].OrganizationId).IsEqualTo("org_a");
        await Assert.That(result.Tenants[0].Slug).IsEqualTo("eventuous");
        await Assert.That(result.Tenants[0].Origin).IsEqualTo("https://eventuous.kcap.ai");
    }

    [Test]
    public async Task DiscoverWorkOSTenantsAsync_maps_401_to_TokenRejected() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants-workos").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverWorkOSTenantsAsync(server.Urls[0], "bad");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task CreateMachineApplicationAsync_carries_the_operators_own_bearer() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/connect/m2m-applications").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"application_id":"app_1","client_id":"cid","client_secret":"sec","organization_id":"org_1","created":true}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.CreateMachineApplicationAsync(server.Urls[0], "wos-token", "runner");

        await Assert.That(result.Error).IsEqualTo(MachineProvisioningError.None);
        await Assert.That(result.Application!.ClientId).IsEqualTo("cid");
        await Assert.That(result.Application.ClientSecret).IsEqualTo("sec");
        await Assert.That(result.Application.Created).IsTrue();

        var sent = server.LogEntries.Single().RequestMessage;

        await Assert.That(sent.Headers!["Authorization"].Single()).IsEqualTo("Bearer wos-token");
        await Assert.That(sent.Body).Contains("runner");
    }

    [Test]
    public async Task CreateMachineApplicationAsync_separates_a_rejected_sign_in_from_a_missing_role() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/connect/m2m-applications").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var http = new HttpClient();

        var unauthorized = await new AuthProxyClient(http).CreateMachineApplicationAsync(server.Urls[0], "t", "runner");

        server.Reset();
        server.Given(Request.Create().WithPath("/connect/m2m-applications").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        var forbidden = await new AuthProxyClient(http).CreateMachineApplicationAsync(server.Urls[0], "t", "runner");

        await Assert.That(unauthorized.Error).IsEqualTo(MachineProvisioningError.Unauthorized);
        await Assert.That(forbidden.Error).IsEqualTo(MachineProvisioningError.Forbidden)
            .Because("one tells the operator to sign in again, the other that they need a role no sign-in will grant");
    }

    [Test]
    public async Task CreateMachineApplicationAsync_reports_the_proxys_own_status_and_body() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/connect/m2m-applications").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502).WithBody("upstream said no"));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.CreateMachineApplicationAsync(server.Urls[0], "t", "runner");

        await Assert.That(result.Error).IsEqualTo(MachineProvisioningError.Rejected);
        await Assert.That(result.Status).IsEqualTo(502);
        await Assert.That(result.Detail).IsEqualTo("upstream said no")
            .Because("a bare \"provisioning failed\" leaves the operator nothing to act on");
    }

    [Test]
    public async Task CreateMachineApplicationAsync_reports_an_unreachable_proxy() {
        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.CreateMachineApplicationAsync("http://127.0.0.1:1", "t", "runner");

        await Assert.That(result.Error).IsEqualTo(MachineProvisioningError.Unreachable);
        await Assert.That(result.Detail).IsNotNull();
    }

    /// The command prints one message per outcome and has no wording for an exception; a success
    /// whose body will not parse has to arrive as "no credential disclosed", not as a crash.
    [Test]
    public async Task CreateMachineApplicationAsync_degrades_an_unreadable_success_body() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/connect/m2m-applications").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("not json")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.CreateMachineApplicationAsync(server.Urls[0], "t", "runner");

        await Assert.That(result.Error).IsEqualTo(MachineProvisioningError.None);
        await Assert.That(result.Application).IsNull();
    }
}
