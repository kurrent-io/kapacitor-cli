using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// End-to-end verification that <c>UpdateCommand</c>'s channel-aware check queries the right npm
/// dist-tag. The registered client is re-pointed at a WireMock-stubbed registry instead of the real
/// <c>registry.npmjs.org</c>, mirroring the harness in
/// <see cref="Config.ServerUrlProbeIntegrationTests"/>.
/// </summary>
public class UpdateChannelQueryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    /// The registered client, not a hand-built one: the 5 s cap these tests reason about is set at
    /// registration, so a client assembled here would not have it. AddHttpClient configuration is
    /// additive, so aiming the base address at the stub keeps that cap.
    readonly ServiceProvider _http;

    NpmRegistryClient Npm => _http.GetRequiredService<NpmRegistryClient>();

    public UpdateChannelQueryTests() {
        _http = new ServiceCollection()
            .AddCapacitorForeignClients()
            .AddHttpClient<NpmRegistryClient>(c => c.BaseAddress = new Uri(_server.Url! + "/"))
            .Services.BuildServiceProvider();

        _server.Given(Request.Create().WithPath("/@kurrent/kcap/latest").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.8.0"}"""));

        _server.Given(Request.Create().WithPath("/@kurrent/kcap/beta").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.9.0-beta.1"}"""));
    }

    public void Dispose() {
        _http.Dispose();
        _server.Stop();
    }

    [Test]
    public async Task Beta_channel_reports_beta_dist_tag_version() {
        var result = await UpdateCommand.CheckForUpdateAsync(forceCheck: true, "beta", Config.Root, Npm);

        await Assert.That(result.Latest).IsEqualTo("0.9.0-beta.1");

        var hits = _server.FindLogEntries(Request.Create().WithPath("/@kurrent/kcap/beta").UsingGet());
        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Latest_channel_reports_latest_dist_tag_version() {
        var result = await UpdateCommand.CheckForUpdateAsync(forceCheck: true, "latest", Config.Root, Npm);

        await Assert.That(result.Latest).IsEqualTo("0.8.0");

        var hits = _server.FindLogEntries(Request.Create().WithPath("/@kurrent/kcap/latest").UsingGet());
        await Assert.That(hits.Count).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Two consecutive fresh coordinator runs (fresh HttpClient/coordinator
    /// state each call, same on-disk <c>KCAP_CONFIG_DIR</c> cache) converge
    /// via the 24h success cache: the response is slower than 300ms but well
    /// inside the passive path's bound, so it completes; the second call
    /// must not touch the network at all.
    /// </summary>
    [Test]
    public async Task Slow_but_completing_response_caches_and_second_run_skips_network() {
        const string channel = "test-slow-success";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.12.0"}""")
                .WithDelay(TimeSpan.FromMilliseconds(400)));

        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        await Assert.That(first.Latest).IsEqualTo("0.12.0");
        await Assert.That(first.FromCache).IsFalse();

        var second = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        await Assert.That(second.Latest).IsEqualTo("0.12.0");
        await Assert.That(second.FromCache).IsTrue();

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A response slower than the passive caller's own cancellation bound is
    /// cancelled at that bound, not at the 5s <c>HttpClient.Timeout</c> — the
    /// fetch is cut off, a backoff record is written, and a subsequent
    /// within-1h passive run makes no further HTTP request.
    /// </summary>
    /// <remarks>
    /// Verified via elapsed time, not <see cref="WireMockServer.FindLogEntries"/>:
    /// WireMock.Net only appends a request log entry once it finishes
    /// composing a response (including its configured delay), so a request
    /// the client itself cancelled mid-flight never gets logged — a 0 hit
    /// count would be true for a genuinely-skipped call AND a
    /// cancelled-before-logging one, so it can't distinguish them. Elapsed
    /// time can: if the second call incorrectly re-hit the network it would
    /// take ~2s (the endpoint's configured delay, uncapped this time since no
    /// short-lived token is passed), whereas a cache/backoff hit returns
    /// near-instantly.
    /// </remarks>
    [Test]
    public async Task Response_slower_than_passive_token_is_cancelled_and_backs_off() {
        const string channel = "test-passive-cancel";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.13.0"}""")
                .WithDelay(TimeSpan.FromSeconds(2)));

        using var passiveBound = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var firstSw = System.Diagnostics.Stopwatch.StartNew();
        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm, passiveBound.Token);
        firstSw.Stop();
        await Assert.That(first.Latest).IsNull();
        await Assert.That(first.FromCache).IsTrue();

        // Cancelled at the ~200ms passive bound, not at the 2s response delay
        // or the 5s HttpClient.Timeout — with slack for CI jitter.
        await Assert.That(firstSw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));

        var secondSw = System.Diagnostics.Stopwatch.StartNew();
        var second = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        secondSw.Stop();
        await Assert.That(second.Latest).IsNull();
        await Assert.That(second.FromCache).IsTrue();

        // Well under the endpoint's 2s configured delay — proves the second
        // call served the backoff record rather than re-hitting the network.
        await Assert.That(secondSw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Pins the 1h backoff policy itself: once a failure has written a
    /// backoff record, even a NOW-fast endpoint is not queried again by a
    /// passive caller inside the window — the skip is driven by the cached
    /// record, not by the endpoint still being slow.
    /// </summary>
    [Test]
    public async Task Failure_pins_one_hour_backoff_even_once_endpoint_recovers() {
        const string channel = "test-backoff-policy";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        await Assert.That(first.Latest).IsNull();

        // The endpoint "recovers" — but the backoff record, not the endpoint,
        // is what should gate the second passive call.
        _server.ResetMappings();
        _server.ResetLogEntries();
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.14.0"}"""));

        var second = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        await Assert.That(second.Latest).IsNull();
        await Assert.That(second.FromCache).IsTrue();

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    /// <summary>
    /// <c>forceCheck: true</c> bypasses the backoff entirely — an explicit
    /// <c>kcap update</c>/<c>--check</c> invocation must always hit the
    /// network rather than silently reusing a retained failure result.
    /// </summary>
    [Test]
    public async Task ForceCheck_bypasses_backoff_and_hits_network() {
        const string channel = "test-force-bypass";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(503));

        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);
        await Assert.That(first.Latest).IsNull();

        _server.ResetMappings();
        _server.ResetLogEntries();
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.15.0"}"""));

        var forced = await UpdateCommand.CheckForUpdateAsync(forceCheck: true, channel, Config.Root, Npm);
        await Assert.That(forced.Latest).IsEqualTo("0.15.0");
        await Assert.That(forced.FromCache).IsFalse();

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    /// <summary>A well-formed answer that names no version has not failed. Arming the backoff here
    /// would suppress the next hour of checks over a reply the registry actually gave us.</summary>
    [Test, NotInParallel("UpdateCommand_RegistryBaseUrl")]
    public async Task A_reply_naming_no_version_does_not_arm_the_backoff() {
        const string channel = "test-no-version";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"dist-tags":{}}"""));

        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);

        await Assert.That(first.Latest).IsNull();
        await Assert.That(first.FromCache).IsFalse();

        await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(2)
            .Because("a backoff armed on this answer would have skipped the second check");
    }

    /// <summary>A body that will not parse is a failure whatever the status line said: no version
    /// can be read from it, so the next passive caller is spared the same round trip.</summary>
    [Test, NotInParallel("UpdateCommand_RegistryBaseUrl")]
    public async Task An_unreadable_body_arms_the_backoff() {
        const string channel = "test-unreadable-body";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("not json"));

        var first = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);

        await Assert.That(first.Latest).IsNull();

        var second = await UpdateCommand.CheckForUpdateAsync(forceCheck: false, channel, Config.Root, Npm);

        await Assert.That(second.FromCache).IsTrue();

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    /// <summary>The registry is a third party we are a guest of, and the agent name is what
    /// identifies us in its logs and its rate limits. Nothing on the calling path reads it, so
    /// only the request as sent can show it is still there.</summary>
    [Test, NotInParallel("UpdateCommand_RegistryBaseUrl")]
    public async Task The_registry_request_names_the_agent() {
        const string channel = "test-agent-name";
        _server.Given(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"version":"0.16.0"}"""));

        await UpdateCommand.CheckForUpdateAsync(forceCheck: true, channel, Config.Root, Npm);

        var sent = _server.FindLogEntries(Request.Create().WithPath($"/@kurrent/kcap/{channel}").UsingGet())
            .Single().RequestMessage;

        await Assert.That(sent.Headers!["User-Agent"].Single()).IsEqualTo("kcap-cli");
    }
}
