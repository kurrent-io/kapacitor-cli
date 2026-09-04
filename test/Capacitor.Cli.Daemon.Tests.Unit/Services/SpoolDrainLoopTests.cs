using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Tests for the periodic spool-drain tick's auth handling.</summary>
public class SpoolDrainLoopTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SpoolDrainLoop MakeLoop(ICapacitorHttpClient http, ILogger logger) =>
        new(Config.Root, http, "https://example.invalid",
            new HookSpool(Config.PathTo("lifecycle")), new TranscriptSpool(Config.PathTo("transcript")), logger);

    [Test]
    public async Task Tick_treats_UnusableServerUrl_as_a_lapse_and_skips_the_pass() {
        var http   = new StatusOnlyHttpClient(AuthStatus.UnusableServerUrl);
        var logger = new CapturingLogger<SpoolDrainLoopTests>();

        await MakeLoop(http, logger).TickAsync(CancellationToken.None);

        await Assert.That(logger.Entries.Any(e => e.Message.Contains("auth lapsed — skipping this pass"))).IsTrue();
    }

    // Credential resolution must run on the loop's own token, not one truncated to the drain
    // budget — a proactive refresh routinely outlives that budget.
    [Test]
    public async Task Tick_resolves_the_credential_on_the_loops_own_token() {
        using var loopCts = new CancellationTokenSource();
        var       http    = new StatusOnlyHttpClient(AuthStatus.Ok);
        var       logger  = new CapturingLogger<SpoolDrainLoopTests>();

        await MakeLoop(http, logger).TickAsync(loopCts.Token);

        await Assert.That(http.ReceivedToken).IsEqualTo(loopCts.Token);
    }

    sealed class StatusOnlyHttpClient(AuthStatus status) : ICapacitorHttpClient {
        public CancellationToken ReceivedToken { get; private set; }

        public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public Task<HttpClient> ForSessionAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default) {
            ReceivedToken = ct;
            return Task.FromResult(new AuthAttempt(new HttpClient(), status, null, null));
        }

        public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());

        public HttpClient Anonymous() => new();

        public HttpClient Loopback() => new();

        public HttpClient Bearer() => new();
    }
}
