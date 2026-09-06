using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Commands;
// TUnit ships a DiscoveryResult of its own, and both are in scope here.
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The browser pick, and the many ways it has to give up without taking <c>kcap setup</c> with it.
/// </summary>
public class BrowserTenantPickerTests {
    static DiscoveredTenant WorkOS(string org, string slug) => new() {
        Provider = AuthProvider.WorkOS, OrganizationId = org, Slug = slug,
        Origin = $"https://{slug}.kcap.ai", DisplayName = slug
    };

    static DiscoveredTenant GitHub(string login) => new() {
        Provider = AuthProvider.GitHubApp, OrgLogin = login, Origin = $"https://{login}.kcap.ai"
    };

    static readonly DiscoveredTenant[] Two = [WorkOS("org_a", "acme"), WorkOS("org_b", "globex")];

    sealed class StubProxy : IAuthProxyClient {
        public CliPickerPrepareResponse? Prepared { get; set; }
        public Queue<CliPickerResultResponse?> Polls { get; } = new();
        public int Abandons { get; private set; }
        public string? PreparedWithBearer { get; private set; }

        public Task<ProxyConfigResponse?> GetConfigAsync(string u, CancellationToken ct = default) =>
            Task.FromResult<ProxyConfigResponse?>(null);
        public Task<DiscoveryResult> DiscoverTenantsAsync(string u, string t, CancellationToken ct = default) =>
            Task.FromResult(new DiscoveryResult([], DiscoveryError.None));
        public Task<DiscoveryResult> DiscoverWorkOSTenantsAsync(string u, string t, CancellationToken ct = default) =>
            Task.FromResult(new DiscoveryResult([], DiscoveryError.None));
        public Task<MachineProvisioningResult> CreateMachineApplicationAsync(string u, string b, string n, CancellationToken ct = default) =>
            throw new NotSupportedException("the picker never provisions");

        public Task<CliPickerPrepareResponse?> PreparePickAsync(string u, string bearer, string hash, CancellationToken ct = default) {
            PreparedWithBearer = bearer;
            return Task.FromResult(Prepared);
        }

        public string? PolledWithSecret { get; private set; }
        public int Polls_ { get; private set; }

        public Task<CliPickerResultResponse?> PollPickAsync(string u, string h, string s, CancellationToken ct = default) {
            PolledWithSecret = s;
            Polls_++;
            return Task.FromResult(Polls.Count > 0 ? Polls.Dequeue() : new CliPickerResultResponse { Status = "pending" });
        }

        public Task AbandonPickAsync(string u, string h, CancellationToken ct = default) {
            Abandons++;
            return Task.CompletedTask;
        }
    }

    sealed class StubKeys(bool available) : IKeyWatcher {
        public bool CanWatch => true;
        public bool KeyAvailable => available;
        public char ReadKey() => 'x';
        public void Drain() { }
    }

    sealed class SilentKeys : IKeyWatcher {
        public bool CanWatch => false;
        public bool KeyAvailable => false;
        public char ReadKey() => '\0';
        public void Drain() { }
    }

    sealed class StubLauncher(bool opens = true) : IBrowserLauncher {
        public string? Opened { get; private set; }
        public bool TryOpen(string url) { Opened = url; return opens; }
    }

    static CliPickerPrepareResponse Ready(DateTimeOffset expires) => new() {
        Handle = "h1", PollIntervalSeconds = 1, ExpiresAt = expires,
        Tenants = [new() { Key = "org_a", Slug = "acme", Origin = "https://acme.kcap.ai" },
                   new() { Key = "org_b", Slug = "globex", Origin = "https://globex.kcap.ai" }]
    };

    static TenantPickContext Context(StubProxy proxy, bool viaLoopback = true, int version = 1) =>
        new(Bearer: "tok", Proxy: proxy, ProxyUrl: "https://auth.test", ViaLoopback: viaLoopback, PickerVersion: version);

    static (BrowserTenantPicker Picker, FakeTimeProvider Time) Build(
            StubProxy proxy, IKeyWatcher? keys = null, StubLauncher? launcher = null, Func<bool>? canPrompt = null) {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        // Non-interactive fallback: it returns null rather than opening a Spectre prompt the test
        // host cannot answer, which makes "fell back" observable as a null.
        var picker = new BrowserTenantPicker(
            launcher ?? new StubLauncher(), new SpectreTenantPicker(isInteractive: () => false),
            new SilentProgress(), keys ?? new SilentKeys(), time, canPrompt);

        return (picker, time);
    }

    /// <summary>
    /// Runs a pick that has to sleep between polls. The fake clock only moves when told, so a test
    /// that awaits the pick outright waits forever on the first interval.
    /// </summary>
    static async Task<DiscoveredTenant?> Drive(Task<DiscoveredTenant?> pick, FakeTimeProvider time) {
        while (!pick.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(2));
            await Task.Yield();
        }

        return await pick;
    }

    sealed class SilentProgress : IAuthProgress {
        public void Notice(string message) { }
        public void Error(string message) { }
        public void BrowserOpening(string url) { }
        public void DeviceCode(string userCode, string verificationUri, string? provider, bool prefilled) { }
        public void PollTick() { }
    }

    [Test]
    public async Task A_browser_choice_resolves_to_the_row_discovery_handed_over() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, _) = Build(proxy);
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.OrganizationId).IsEqualTo("org_b");
        await Assert.That(proxy.PreparedWithBearer).IsEqualTo("tok");
    }

    /// <summary>
    /// Prepare's list and discovery's can differ — a membership changes, or an org is skipped on a
    /// bad hostname. The rest of the flow builds profiles from discovery's array alone, so a key
    /// that maps only onto prepare's would name a profile nobody created.
    /// </summary>
    [Test]
    public async Task A_key_absent_from_discovery_s_own_list_falls_back_rather_than_being_returned() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_vanished" });

        var (picker, _) = Build(proxy);
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNull();
    }

    /// <summary>Opening a browser nobody can see would leave the user watching a poll for ten minutes.</summary>
    [Test]
    public async Task A_device_grant_login_never_opens_a_browser() {
        var proxy    = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        var launcher = new StubLauncher();

        var (picker, _) = Build(proxy, launcher: launcher);
        await picker.PickAsync(Two, Context(proxy, viaLoopback: false), CancellationToken.None);

        await Assert.That(launcher.Opened).IsNull();
        await Assert.That(proxy.PreparedWithBearer).IsNull();
    }

    /// <summary>A proxy predating the routes must not be polled to a deadline on a 404.</summary>
    [Test]
    public async Task A_proxy_that_does_not_advertise_the_picker_is_not_asked() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };

        var (picker, _) = Build(proxy);
        await picker.PickAsync(Two, Context(proxy, version: 0), CancellationToken.None);

        await Assert.That(proxy.PreparedWithBearer).IsNull();
    }

    /// <summary>The proxy resolves these rows from a WorkOS bearer, and a GitHub caller has none.</summary>
    [Test]
    public async Task Github_rows_go_to_the_terminal() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };

        var (picker, _) = Build(proxy);
        await picker.PickAsync([GitHub("acme"), GitHub("globex")], Context(proxy), CancellationToken.None);

        await Assert.That(proxy.PreparedWithBearer).IsNull();
    }

    [Test]
    public async Task A_prepare_that_fails_falls_back_instead_of_failing_the_setup() {
        var proxy = new StubProxy { Prepared = null };

        var (picker, _) = Build(proxy);
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        // Null is the non-interactive fallback declining, not an exception escaping.
        await Assert.That(picked).IsNull();
    }

    /// <summary>
    /// Polled, never blocked on: the keypress has to be able to win a race the poll is also in, and
    /// it has to be seen before the interval sleep rather than after it.
    /// </summary>
    [Test]
    public async Task A_keypress_takes_the_pick_back_to_the_terminal_and_releases_the_handle() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, _) = Build(proxy, keys: new StubKeys(available: true));
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNull();
        await Assert.That(proxy.Abandons).IsEqualTo(1);
    }

    /// <summary>
    /// The CLI adopts the proxy's deadline rather than timing independently, so the two cannot
    /// disagree about whether a late choice still counts.
    /// </summary>
    [Test]
    public async Task A_deadline_already_past_gives_up_without_polling() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(-1)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, _) = Build(proxy);
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNull();
        await Assert.That(proxy.Polls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_expired_handle_stops_the_poll() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "expired" });

        var (picker, _) = Build(proxy);
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNull();
    }

    [Test]
    public async Task The_browser_is_sent_to_the_prepared_handle() {
        var proxy    = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        var launcher = new StubLauncher();
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_a" });

        var (picker, _) = Build(proxy, launcher: launcher);
        await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(launcher.Opened).IsEqualTo("https://auth.test/cli/v1/picker/h1");
    }

    /// <summary>
    /// A browser pick is still a prompt. Under <c>--no-prompt</c> it stops an unattended run just as
    /// dead as a terminal one, so the same gate has to cover both.
    /// </summary>
    [Test]
    public async Task An_unattended_run_is_never_sent_to_a_browser() {
        var proxy    = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        var launcher = new StubLauncher();

        var (picker, _) = Build(proxy, launcher: launcher, canPrompt: () => false);
        await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(launcher.Opened).IsNull();
        await Assert.That(proxy.PreparedWithBearer).IsNull();
    }

    /// <summary>
    /// The poll presents the CLI's own secret, never its bearer: a WorkOS access token lives about
    /// five minutes and would expire partway through a human's decision.
    /// </summary>
    [Test]
    public async Task The_poll_presents_the_generated_secret_rather_than_the_bearer() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_a" });

        var (picker, _) = Build(proxy);
        await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(proxy.PolledWithSecret).IsNotNull();
        await Assert.That(proxy.PolledWithSecret).IsNotEqualTo("tok");
        await Assert.That(proxy.PolledWithSecret!.Length).IsGreaterThan(20);
    }

    /// <summary>
    /// A choice posted inside the last poll interval is already stored and the page has told the
    /// user it was saved, so exiting without collecting it would ask them again for an answer they
    /// have already given.
    /// </summary>
    [Test]
    public async Task A_choice_made_just_before_the_deadline_is_still_collected() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddSeconds(1)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "pending" });
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, time) = Build(proxy);
        var pick = picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(5));

        await Assert.That((await pick)?.OrganizationId).IsEqualTo("org_b");
    }

    [Test]
    public async Task A_prepare_returning_no_workspaces_falls_back() {
        var proxy = new StubProxy {
            Prepared = new CliPickerPrepareResponse {
                Handle = "h1", PollIntervalSeconds = 1,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(10), Tenants = []
            }
        };

        var (picker, _) = Build(proxy);

        await Assert.That(await picker.PickAsync(Two, Context(proxy), CancellationToken.None)).IsNull();
        await Assert.That(proxy.Polls_).IsEqualTo(0);
    }

    /// <summary>
    /// A Spectre prompt cannot be cancelled, so falling through on a cancelled token would open one
    /// for a run the user has already abandoned.
    /// </summary>
    [Test]
    public async Task A_cancelled_run_does_not_open_a_terminal_prompt() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // An interactive fallback, so reaching it would be observable as a throw rather than a null.
        var picker = new BrowserTenantPicker(
            new StubLauncher(), new SpectreTenantPicker(isInteractive: () => true),
            new SilentProgress(), new SilentKeys(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

        await Assert.That(await picker.PickAsync(Two, Context(proxy), cts.Token)).IsNull();
    }

    /// <summary>
    /// A poll that did not happen is not a pending answer. Treating them alike holds the run for the
    /// whole handle lifetime when the proxy goes away after prepare, and the terminal picker is right
    /// there.
    /// </summary>
    [Test]
    public async Task A_proxy_that_stops_answering_does_not_hold_the_run_to_the_deadline() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        for (var i = 0; i < 20; i++) proxy.Polls.Enqueue(null);

        var (picker, time) = Build(proxy);
        var picked = await Drive(picker.PickAsync(Two, Context(proxy), CancellationToken.None), time);

        await Assert.That(picked).IsNull();
        await Assert.That(proxy.Polls_).IsLessThanOrEqualTo(3);
    }

    /// <summary>A blip is not an outage: the run carries on and still collects the answer.</summary>
    [Test]
    public async Task A_single_failed_poll_does_not_abandon_the_browser() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(null);
        proxy.Polls.Enqueue(null);
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "pending" });
        proxy.Polls.Enqueue(null);
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, time) = Build(proxy);

        await Assert.That((await Drive(picker.PickAsync(Two, Context(proxy), CancellationToken.None), time))?
            .OrganizationId).IsEqualTo("org_b");
    }

    /// <summary>
    /// The initializer on <c>Tenants</c> does not survive a proxy sending <c>"tenants": null</c>, and
    /// a throw here would take the whole of setup with it rather than falling back.
    /// </summary>
    [Test]
    public async Task A_prepare_response_with_a_null_tenant_list_falls_back() {
        var proxy = new StubProxy {
            Prepared = new CliPickerPrepareResponse {
                Handle = "h1", PollIntervalSeconds = 1,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(10), Tenants = null!
            }
        };

        var (picker, _) = Build(proxy);

        await Assert.That(await picker.PickAsync(Two, Context(proxy), CancellationToken.None)).IsNull();
        await Assert.That(proxy.Polls_).IsEqualTo(0);
    }

    /// <summary>
    /// A launcher saying false means no browser handler exists here, so nobody can reach the page.
    /// Polling on regardless holds the run to the deadline for a choice that cannot be made.
    /// </summary>
    [Test]
    public async Task A_browser_that_cannot_be_launched_falls_back_at_once() {
        var proxy = new StubProxy { Prepared = Ready(DateTimeOffset.UnixEpoch.AddMinutes(10)) };
        proxy.Polls.Enqueue(new CliPickerResultResponse { Status = "selected", Key = "org_b" });

        var (picker, _) = Build(proxy, launcher: new StubLauncher(opens: false));
        var picked = await picker.PickAsync(Two, Context(proxy), CancellationToken.None);

        await Assert.That(picked).IsNull();
        await Assert.That(proxy.Polls_).IsEqualTo(0);
        await Assert.That(proxy.Abandons).IsEqualTo(1);
    }
}
