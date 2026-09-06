using System.Net;
using System.Net.Sockets;
using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// A 401 from the server means the stored sign-in is unusable — Home routes that to the sign-in
/// dialog, so the outcome must say so in a typed way, not only in the error text.
public class ServerLaunchClientTests {
    static readonly LaunchRequest Request = new("daemon-a", "/repo/a", "claude", "hi");

    static int FreePort() {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Test]
    public async Task A_401_from_the_server_marks_the_outcome_unauthorized() {
        using var tmp = new TempDir();
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () => {
            while (listener.IsListening) {
                try {
                    var ctx = await listener.GetContextAsync();
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                } catch (Exception) {
                    return; // listener stopped — test is done with it
                }
            }
        });

        try {
            var config = new ConfigRoot(tmp.Path);
            await using var client = new ServerLaunchClient(
                Resolutions.At($"http://127.0.0.1:{port}", config), AuthFixtures.NewTokenStore(config));

            var outcome = await client.StartAsync(Request, CancellationToken.None);

            await Assert.That(outcome.Started).IsFalse();
            await Assert.That(outcome.Unauthorized).IsTrue();
        } finally {
            listener.Stop();
        }
    }

    [Test]
    public async Task A_refused_connection_is_a_plain_error_not_unauthorized() {
        using var tmp = new TempDir();
        var config = new ConfigRoot(tmp.Path);
        await using var client = new ServerLaunchClient(
            Resolutions.At($"http://127.0.0.1:{FreePort()}", config), AuthFixtures.NewTokenStore(config));

        var outcome = await client.StartAsync(Request, CancellationToken.None);

        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Unauthorized).IsFalse();
        await Assert.That(outcome.Error).IsNotNull();
    }

    [Test]
    public async Task Unauthorized_is_detected_anywhere_in_the_exception_chain() {
        var wrapped = new InvalidOperationException(
            "outer", new HttpRequestException("401", null, HttpStatusCode.Unauthorized));

        await Assert.That(ServerLaunchClient.IsUnauthorized(wrapped)).IsTrue();
        await Assert.That(ServerLaunchClient.IsUnauthorized(
            new HttpRequestException("403", null, HttpStatusCode.Forbidden))).IsFalse();
        await Assert.That(ServerLaunchClient.IsUnauthorized(new InvalidOperationException("no http"))).IsFalse();
    }
}
