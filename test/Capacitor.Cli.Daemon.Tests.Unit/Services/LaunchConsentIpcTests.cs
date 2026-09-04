using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// End-to-end coverage of the consent frames (ConsentSubscribe/ConsentResolve/
/// ConsentRulesGet/ConsentRulesPut) over a REAL Unix-domain socket — the same
/// LocalControlServer.HandleConnectionAsync routing switch a real `kcap` client talks to.
/// The harness mirrors AgentOrchestratorLocalAttachTests's real-socket tests (temp
/// per-test daemons directory, socket-file poll, Windows guard) but builds its own minimal
/// AgentOrchestrator, since none of these tests exercise Spawn/Attach/List/Stop — the
/// orchestrator only needs to exist to satisfy LocalControlServer's constructor.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LaunchConsentIpcTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40
            ) => throw new NotSupportedException("LaunchConsentIpcTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(
        TempDaemonStore Daemons, LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection,
        LaunchConsentStore Store, LaunchConsentBroker Broker, LaunchConsentGate Gate, string SockPath);

    async Task<Harness> StartAsync(
            string daemonName, LaunchConsentDefault def, int promptTimeoutSeconds, CancellationToken ct
        ) {
        var daemons = new TempDaemonStore();
        var stateRoot = daemons.Store.StateDirectory(daemonName);
        var store = new LaunchConsentStore(stateRoot, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(def, promptTimeoutSeconds, []), out _);
        var broker = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };
        var consentIpc = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, tokens, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, Home, connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate);

        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, new DaemonStatusNotifier());
        var restart = RestartCoordinator.ForTest(daemons.Store, daemonName, daemonName, new NoopRestartStrategy());
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = daemons.Store.SocketPath(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(daemons, server, orchestrator, connection, store, broker, gate, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
        h.Daemons.Dispose();
    }

    /// Wraps a test body with the harness lifecycle, mirroring
    /// AgentOrchestratorLocalAttachTests's real-socket tests. The harness owns its own daemons
    /// directory, so no test here shares state with another; each [Test] still carries its own
    /// Windows guard, which must be visible on the test method itself.
    async Task RunAsync(
            string daemonName, LaunchConsentDefault def, int promptTimeoutSeconds,
            Func<Harness, CancellationToken, Task> body
        ) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, def, promptTimeoutSeconds, cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    /// Waits for the daemon's accept loop to actually process a just-sent ConsentSubscribe frame
    /// (i.e. for LaunchConsentIpc.HandleSubscribeAsync to call broker.Subscribe()) — a bounded poll
    /// bridging the gap between "frame written to the socket" and "server-side subscription live".
    static async Task SpinUntilSubscribedAsync(LaunchConsentBroker broker, CancellationToken ct) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!broker.HasSubscriber && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
    }

    // ══ v2 helpers: a fresh connection per resolve (mirrors the file's existing one-shot-resolve
    // tests), routed through the real LocalControlServer switch so the ConsentResolveV2/
    // ConsentSubscribeV2 routing added for the v2 consent surface is exercised, not bypassed. ═════

    static async Task<ConsentAckDto> ResolveAsync(Harness h, string json, bool requireEcho, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        var frameType = requireEcho ? FrameType.ConsentResolveV2 : FrameType.ConsentResolve;
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(frameType, json), ct);
        var resp = await FrameCodec.ReadAsync(s, ct);
        return JsonSerializer.Deserialize(resp!.Text, ConsentIpcJsonContext.Default.ConsentAckDto)!;
    }

    /// Builds a ConsentResolveDto payload via the shared source-gen context (rather than a
    /// hand-typed literal) so the helper can't drift from the wire shape it's meant to exercise.
    /// `action` overrides the save_rule's action independently of `decision` — needed to drive a
    /// store-side rejection (an invalid action) while still resolving a valid allow/deny.
    static string ResolveJson(string requestId, string decision, bool saveRule, string? promptId, string? action = null) {
        var rule = saveRule ? new ConsentRuleDto(action ?? decision, "github:1", "agent", null, null) : null;
        return JsonSerializer.Serialize(new ConsentResolveDto(requestId, decision, rule, promptId),
            ConsentIpcJsonContext.Default.ConsentResolveDto);
    }

    static IReadOnlyList<LaunchConsentRule> StoreRules(Harness h) => h.Store.Current.Rules;

    static bool PendingStillLive(Harness h, string requestId) =>
        h.Broker.PendingSnapshot().Any(p => p.RequestId == requestId);

    static async Task<ConsentPendingDto> FirstPendingFrom(Stream subscribeStream, CancellationToken ct) {
        var frame = await FrameCodec.ReadAsync(subscribeStream, ct);
        return JsonSerializer.Deserialize(frame!.Text, ConsentIpcJsonContext.Default.ConsentPendingDto)!;
    }

    [Test]
    public async Task RulesGet_returns_current_policy_and_RulesPut_replaces_it() {
        await RunAsync("test-consent-rules", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            // Act 1: RulesGet reports the current (default, empty) policy.
            await using (var s1 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s1, new LocalFrame(FrameType.ConsentRulesGet), ct);
                var resp = await FrameCodec.ReadAsync(s1, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentRules);
                var dto = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentPolicyDto);
                await Assert.That(dto!.Default).IsEqualTo("allow");
                await Assert.That(dto.Rules.Count).IsEqualTo(0);
            }

            // Act 2: RulesPut replaces the policy; the store reflects the new default.
            await using (var s2 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s2, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                    """{"default":"deny","prompt_timeout_seconds":30,"rules":[]}"""), ct);
                var resp = await FrameCodec.ReadAsync(s2, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(h.Store.Current.Default).IsEqualTo(LaunchConsentDefault.Deny);
            }

            // Act 3: an invalid rule action is rejected with an explanatory error.
            await using (var s3 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s3, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                    """{"default":"allow","prompt_timeout_seconds":45,"rules":[{"action":"bogus","requester":null,"kind":null,"repo":null,"vendor":null}]}"""), ct);
                var resp = await FrameCodec.ReadAsync(s3, ct);
                await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsFalse();
                await Assert.That(ack.Error).Contains("action");
            }
        });
    }

    [Test]
    public async Task Subscribe_receives_pending_and_Resolve_unblocks_the_gate() {
        await RunAsync("test-consent-subscribe", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            // Subscribe FIRST and wait for it to actually land server-side before starting the
            // background decide. The gate no longer short-circuits synchronously on HasSubscriber —
            // DecideAsync now runs a bounded grace wait (WaitForSubscriberAsync) that would tolerate
            // a subscriber arriving a little late — but this test still wants the subscription
            // GUARANTEED live up front, both to stay deterministic and to avoid burning part of that
            // grace window. Writing the frame only queues it; the daemon-side broker.Subscribe() call
            // (what actually flips HasSubscriber) happens asynchronously once the accept loop reads
            // it off the socket.
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude", null);
            var decideTask = h.Gate.DecideAsync("a9", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);
            var pendingDto = JsonSerializer.Deserialize(pending.Text, ConsentIpcJsonContext.Default.ConsentPendingDto);
            await Assert.That(pendingDto!.RequestId).IsEqualTo("a9");

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a9","decision":"allow","save_rule":null}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                await Assert.That(ackFrame!.Type).IsEqualTo(FrameType.ConsentAck);
                var ack = JsonSerializer.Deserialize(ackFrame.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
            }

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsTrue();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }

    [Test]
    public async Task Resolve_with_save_rule_appends_to_policy() {
        await RunAsync("test-consent-saverule", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "review-flow", "/tmp/repo", "claude", null);
            var decideTask = h.Gate.DecideAsync("a10", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a10","decision":"deny","save_rule":{"action":"deny","kind":"review-flow","requester":null,"repo":null,"vendor":null}}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                var ack = JsonSerializer.Deserialize(ackFrame!.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(ack.Error).IsNull(); // the save succeeded — no partial-failure warning to report
            }

            await Assert.That(h.Store.Current.Rules
                .Any(r => r.Action == "deny" && r.Kind == "review-flow")).IsTrue();

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsFalse();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }

    [Test]
    public async Task Resolve_unknown_request_acks_false() {
        await RunAsync("test-consent-unknown", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                """{"request_id":"nope","decision":"allow","save_rule":null}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
        });
    }

    // ══ Code-review follow-up: STJ source-gen does NOT enforce non-nullable members — a
    // syntactically valid payload missing a required field deserializes with that field left
    // null rather than throwing JsonException. Before the fix, both handlers reached code that
    // dereferenced/used the null value directly (dto.Rules.Select(...), broker.TryResolve(null,
    // ...)), throwing an UNCAUGHT exception (only JsonException was caught) that dropped the
    // connection with no ConsentAck reply at all. These tests pin the fixed behavior: a
    // malformed-but-parseable payload always gets a ConsentAck(false, ...) reply, never a
    // dropped connection. ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task RulesPut_missing_rules_field_acks_false_without_dropping_the_connection() {
        await RunAsync("put-norules", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // No "rules" key at all.
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                """{"default":"allow","prompt_timeout_seconds":45}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
            await Assert.That(ack.Error).Contains("malformed");
        });
    }

    [Test]
    public async Task RulesPut_null_rules_element_acks_false_without_dropping_the_connection() {
        // "rules":[null] is valid JSON — STJ source-gen deserializes it into a List<ConsentRuleDto>
        // containing a null element despite the non-nullable C# declaration. Any(r => r.Action is
        // null) would throw an uncaught NullReferenceException on that element (only JsonException
        // is caught), dropping the connection with no ConsentAck at all. Pins the fixed guard
        // (r is null || r.Action is null).
        await RunAsync("put-nullrule", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPut,
                """{"default":"allow","prompt_timeout_seconds":45,"rules":[null]}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
            await Assert.That(ack.Error).Contains("malformed");
        });
    }

    [Test]
    public async Task Resolve_missing_request_id_acks_false_without_dropping_the_connection() {
        await RunAsync("resolve-noid", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            // No "request_id" key at all.
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                """{"decision":"allow","save_rule":null}"""), ct);
            var resp = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(resp).IsNotNull(); // the connection must NOT have been dropped
            await Assert.That(resp!.Type).IsEqualTo(FrameType.ConsentAck);
            var ack = JsonSerializer.Deserialize(resp.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
            await Assert.That(ack!.Ok).IsFalse();
        });
    }

    // ══ Code-review follow-up: ack conflation fix. Ok now reflects the RESOLUTION outcome only;
    // a rejected save_rule is a secondary, partial failure that rides along as Error even when
    // Ok=true, rather than being indistinguishable from "no pending request with that id". ══════

    [Test]
    public async Task Resolve_with_an_invalid_save_rule_still_resolves_but_reports_the_save_error() {
        await RunAsync("saverule-bad", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribe), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude", null);
            var decideTask = h.Gate.DecideAsync("a11", input, ct);

            var pending = await FrameCodec.ReadAsync(subscriber, ct);
            await Assert.That(pending!.Type).IsEqualTo(FrameType.ConsentPending);

            await using (var resolver = await ConnectAsync(h.SockPath, ct)) {
                // The save_rule's action is invalid — the store rejects it — but the resolution
                // itself (the owner's "allow" decision) must still apply.
                await FrameCodec.WriteAsync(resolver, LocalFrame.ConsentJson(FrameType.ConsentResolve,
                    """{"request_id":"a11","decision":"allow","save_rule":{"action":"bogus","requester":null,"kind":null,"repo":null,"vendor":null}}"""), ct);
                var ackFrame = await FrameCodec.ReadAsync(resolver, ct);
                var ack = JsonSerializer.Deserialize(ackFrame!.Text, ConsentIpcJsonContext.Default.ConsentAckDto);
                // Ok=true: the resolution applied. Error carries the save's partial failure —
                // NOT the same shape as the "unknown id" (Ok=false) case asserted above.
                await Assert.That(ack!.Ok).IsTrue();
                await Assert.That(ack.Error).Contains("action");
            }

            // The invalid rule was never persisted.
            await Assert.That(h.Store.Current.Rules.Count).IsEqualTo(0);

            var outcome = await decideTask;
            await Assert.That(outcome.Allowed).IsTrue();
            await Assert.That(outcome.Source).IsEqualTo("prompt_user");
        });
    }

    // ══ v2 consent surface (ConsentSubscribeV2/ConsentResolveV2): a mandatory prompt_id identity
    // echo, rule_saved reported on both ack branches, and ToDto stamping pinned against the
    // broker's own ground-truth PromptId/RequesterDisplay. ═══════════════════════════════════════

    [Test]
    public async Task V2_resolve_without_prompt_id_acks_invalid_payload() {
        await RunAsync("v2-resolve-noecho", LaunchConsentDefault.Allow, 45, async (h, ct) => {
            // A v2 resolve missing prompt_id entirely never reaches the broker.
            await using (var s1 = await ConnectAsync(h.SockPath, ct)) {
                await FrameCodec.WriteAsync(s1, LocalFrame.ConsentJson(FrameType.ConsentResolveV2,
                    """{"request_id":"a1","decision":"allow","save_rule":null}"""), ct);
                var resp = await FrameCodec.ReadAsync(s1, ct);
                var ack = JsonSerializer.Deserialize(resp!.Text, ConsentIpcJsonContext.Default.ConsentAckDto)!;
                await Assert.That(ack.Ok).IsFalse();
                await Assert.That(ack.Error).Contains("prompt_id");
            }

            // Nor does an empty-string prompt_id.
            var emptyEchoAck = await ResolveAsync(h, ResolveJson("a1", "allow", saveRule: false, ""), requireEcho: true, ct);
            await Assert.That(emptyEchoAck.Ok).IsFalse();
            await Assert.That(emptyEchoAck.Error).Contains("prompt_id");
        });
    }

    [Test]
    public async Task Rule_saved_is_populated_on_both_ok_branches() {
        await RunAsync("v2-rule-saved", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribeV2), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude", null);

            // (1) save_rule + live pending + matching echo → Ok=true, RuleSaved=true.
            var decideTask1 = h.Gate.DecideAsync("a1", input, ct);
            var pending1 = await FirstPendingFrom(subscriber, ct);
            var okAck = await ResolveAsync(h, ResolveJson("a1", "allow", saveRule: true, pending1.PromptId), requireEcho: true, ct);
            await Assert.That(okAck.Ok).IsTrue();
            await Assert.That(okAck.RuleSaved).IsTrue();
            await decideTask1;

            // (2) save_rule + NO pending → the rule is still persisted (save-before-resolve is
            // deliberate — "Allow & remember" is a durable trust statement, not conditioned on this
            // particular launch still being live), Ok=false, RuleSaved=true.
            var nopAck = await ResolveAsync(h, ResolveJson("ghost", "allow", saveRule: true, "p-x"), requireEcho: true, ct);
            await Assert.That(nopAck.Ok).IsFalse();
            await Assert.That(nopAck.RuleSaved).IsTrue();
            await Assert.That(StoreRules(h).Any(r => r.Requester == "github:1")).IsTrue(); // persisted despite Ok=false

            // (3) save_rule rejected by the store (invalid action) → RuleSaved=false on BOTH the
            // still-resolves-fine branch and the no-pending branch.
            var decideTask3 = h.Gate.DecideAsync("a1", input, ct); // successor prompt reusing "a1" — case (1)'s was already claimed
            var pending3 = await FirstPendingFrom(subscriber, ct);
            var rejectedButLiveAck = await ResolveAsync(
                h, ResolveJson("a1", "allow", saveRule: true, pending3.PromptId, action: "bogus"), requireEcho: true, ct);
            await Assert.That(rejectedButLiveAck.Ok).IsTrue(); // the resolution itself still applies
            await Assert.That(rejectedButLiveAck.RuleSaved).IsFalse();
            await decideTask3;

            var rejectedNoPendingAck = await ResolveAsync(
                h, ResolveJson("ghost2", "allow", saveRule: true, "p-y", action: "bogus"), requireEcho: true, ct);
            await Assert.That(rejectedNoPendingAck.Ok).IsFalse();
            await Assert.That(rejectedNoPendingAck.RuleSaved).IsFalse();

            // (4) no save_rule → RuleSaved=null.
            var decideTask4 = h.Gate.DecideAsync("a1", input, ct);
            var pending4 = await FirstPendingFrom(subscriber, ct);
            var plainAck = await ResolveAsync(h, ResolveJson("a1", "deny", saveRule: false, pending4.PromptId), requireEcho: true, ct);
            await Assert.That(plainAck.RuleSaved).IsNull();
            await decideTask4;
        });
    }

    [Test]
    public async Task V2_resolve_with_mismatching_echo_acks_no_pending_and_leaves_the_request_live() {
        await RunAsync("v2-resolve-mismatch", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribeV2), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude", null);
            var decideTask = h.Gate.DecideAsync("a1", input, ct);
            var pending = await FirstPendingFrom(subscriber, ct);

            var ack = await ResolveAsync(h, ResolveJson("a1", "allow", saveRule: false, "WRONG"), requireEcho: true, ct);
            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(PendingStillLive(h, "a1")).IsTrue();

            // Resolve for real with the correct echo, so the DecideAsync task doesn't outlive the test.
            var cleanupAck = await ResolveAsync(h, ResolveJson("a1", "allow", saveRule: false, pending.PromptId), requireEcho: true, ct);
            await Assert.That(cleanupAck.Ok).IsTrue();
            await decideTask;
        });
    }

    [Test]
    public async Task Subscribe_pushes_prompt_id_and_requester_display_on_pending_frames() {
        await RunAsync("v2-subscribe-stamped", LaunchConsentDefault.Prompt, 30, async (h, ct) => {
            await using var subscriber = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(subscriber, new LocalFrame(FrameType.ConsentSubscribeV2), ct);
            await SpinUntilSubscribedAsync(h.Broker, ct);

            var input = new LaunchConsentInput("user_x", RequesterIsOwner: false, "agent", "/tmp/repo", "claude", "Mathias");
            var decideTask = h.Gate.DecideAsync("a1", input, ct);

            var dto = await FirstPendingFrom(subscriber, ct);
            // Assert against the broker's own ground-truth request, not just a non-null check —
            // this is the pin for ToDto's stamping (previously untested).
            var live = h.Broker.PendingSnapshot().Single(p => p.RequestId == "a1");
            await Assert.That(dto.PromptId).IsEqualTo(live.PromptId);
            await Assert.That(dto.RequesterDisplay).IsEqualTo(live.RequesterDisplay);
            await Assert.That(dto.RequesterDisplay).IsEqualTo("Mathias");

            var ack = await ResolveAsync(h, ResolveJson("a1", "allow", saveRule: false, dto.PromptId), requireEcho: true, ct);
            await Assert.That(ack.Ok).IsTrue();
            await decideTask;
        });
    }
}
