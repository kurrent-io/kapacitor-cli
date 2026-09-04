using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

// CliTelemetry's own statics (TestSink, the Initialize-set state) are process-global and stay that
// way — making the facade an instance is a behaviour change, not a path change. So the key names
// them, and every class driving the facade carries it. The telemetry FILES need no key any more:
// they live under this test's own root.
[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class CliTelemetryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // CliTelemetry holds process-global static state (Enabled, TestSink, ...). A prior test
    // elsewhere in the suite (e.g. one that persists `telemetry off`) can leave Enabled=false
    // behind via CliTelemetry.DiscardAndDisable — reset before touching TestSink so every test
    // here starts from pristine state rather than inheriting whatever ran before it. This
    // subsumes the ad hoc CliTelemetry.Reset() call some tests below used to make individually.
    [Before(Test)]
    public void ResetTelemetry() => CliTelemetry.Reset();

    List<TelemetryEvent> StartCapturing(string command = "setup", string? serverUrl = null) {
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize(command, serverUrl, loggedIn: false, Config.Root);

        TelemetryTestGuards.AssertEnabled(command, Config.Root);

        return sink;
    }

    [Test]
    public async Task Capture_records_the_event_with_shared_properties() {
        // StartCapturing() always begins from a brand-new device state file (this test's own root),
        // so Initialize's first-run notice fires and "cli_first_run" lands in the sink too (see
        // First_run_emits_cli_first_run_once_per_device below) — filter by name rather than assume
        // this is the only event, the same way Record_command_emits_cli_command_with_exit_code does.
        var sink = StartCapturing();

        CliTelemetry.Capture("cli_setup_started", new JsonObject { ["no_prompt"] = false });

        var e = sink.Single(x => x.Name == "cli_setup_started");
        await Assert.That(e.Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(e.Properties.ContainsKey("cli_version")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("os")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("arch")).IsTrue();
        await Assert.That(e.Properties.ContainsKey("is_ci")).IsTrue();
        await Assert.That(e.Properties["no_prompt"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Record_command_emits_cli_command_with_exit_code() {
        var sink = StartCapturing("daemon");

        CliTelemetry.RecordCommand("daemon", ["daemon", "start", "--foreground"], exitCode: 0, durationMs: 42);

        var e = sink.Single(x => x.Name == "cli_command");
        await Assert.That(e.Properties["command"]!.GetValue<string>()).IsEqualTo("daemon");
        await Assert.That(e.Properties["subcommand"]!.GetValue<string>()).IsEqualTo("start");
        await Assert.That(e.Properties["exit_code"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(42L);
    }

    // Initialise with a REPORTABLE command so Enabled stays true, which means RecordCommand's own
    // `!CommandEvents.IsReportable(command)` guard — not Initialize's — is what suppresses the
    // event. Load-bearing for Task 10: a long-lived MCP server process calls
    // Initialize("mcp-server", …) once, then RecordCommand may be called per-invocation with a
    // different, potentially denylisted, command string. Initialising with "hook" here would let
    // Initialize's Enabled=false short-circuit RecordCommand before its own guard ever runs.
    // End-to-end version of CommandEventsTests.Unrecognised_tokens_report_unknown: proves the
    // redaction actually reaches the emitted event's `command` property, not just the pure
    // allowlist function. `command` here is NOT denylisted (it's not a real verb at all), so
    // RecordCommand proceeds — CommandEvents.ReportableCommand is what has to catch it.
    [Test]
    public async Task Record_command_redacts_an_unrecognised_verb_to_unknown() {
        var sink = StartCapturing("status");

        CliTelemetry.RecordCommand(
            "/Users/me/work/acme-private", ["/Users/me/work/acme-private"], exitCode: 1, durationMs: 3);

        var e = sink.Single(x => x.Name == "cli_command");
        await Assert.That(e.Properties["command"]!.GetValue<string>()).IsEqualTo("unknown");
    }

    [Test]
    public async Task Denylisted_commands_emit_nothing() {
        var sink = StartCapturing("status");

        CliTelemetry.RecordCommand("hook", ["hook", "--claude"], exitCode: 0, durationMs: 5);

        await Assert.That(sink.Any(x => x.Name == "cli_command")).IsFalse();
    }

    [Test]
    public async Task Disabled_telemetry_captures_nothing() {
        TelemetryState.SetEnabled(false, Config.Root);
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false, Config.Root);

        CliTelemetry.Capture("cli_setup_started", new JsonObject());
        CliTelemetry.RecordCommand("setup", ["setup"], 0, 1);

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    // An uninitialised facade must be inert, not merely non-throwing: a swallowed exception and
    // a correctly-skipped capture look identical from the outside unless state is asserted.
    [Test]
    public async Task Capture_before_initialize_is_inert() {
        // No CliTelemetry.Initialize call in this test — [Before(Test)]'s Reset() above is what
        // makes "uninitialised" actually mean uninitialised, rather than inheriting Enabled=true
        // from whatever the previous test in the run left behind.
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;

        CliTelemetry.Capture("orphan", new JsonObject());
        CliTelemetry.RecordCommand("status", ["status"], 0, 1);
        await CliTelemetry.FlushAndClose();

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    [Test]
    public async Task First_run_emits_cli_first_run_once_per_device() {
        var firstSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = firstSink;
        CliTelemetry.Initialize("setup", null, loggedIn: false, Config.Root);

        var secondSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = secondSink;
        CliTelemetry.Initialize("status", null, loggedIn: false, Config.Root);

        await Assert.That(firstSink.Any(e => e.Name == "cli_first_run")).IsTrue();
        await Assert.That(secondSink.Any(e => e.Name == "cli_first_run")).IsFalse();
    }

    // Task 10: "mcp-server" is the pseudo-command MCP servers re-initialise under (see
    // McpTelemetry). An agent-spawned MCP server's stderr is not watched by a human, and on a
    // fresh machine it can plausibly be the very first kcap-family process ever run — so it must
    // never consume the once-per-device first-run notice on a human's behalf. The first
    // human-invoked, reportable command afterward must still see it.
    [Test]
    public async Task Mcp_server_initialise_does_not_consume_the_first_run_notice() {
        var mcpSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = mcpSink;
        CliTelemetry.Initialize("mcp-server", null, loggedIn: false, Config.Root);

        var humanSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = humanSink;
        CliTelemetry.Initialize("status", null, loggedIn: false, Config.Root);

        await Assert.That(mcpSink.Any(e => e.Name == "cli_first_run")).IsFalse();
        await Assert.That(humanSink.Any(e => e.Name == "cli_first_run")).IsTrue();
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsTrue();
    }

    // The property "never mint a device id while opted out" still holds — only its enforcement
    // point moved. TelemetryDeviceId.GetOrCreate no longer has any notion of state.Enabled to
    // re-check (TelemetryDeviceIdTests.Get_or_create_is_unaffected_by_telemetry_state pins that it
    // now mints even when called directly against a disabled state); Initialize's own
    // `if (!Enabled) return;` is the one gate left, and it must never reach
    // TelemetryDeviceId.GetOrCreate at all when TelemetrySettings.Resolve says disabled.
    [Test]
    public async Task Initialize_never_mints_a_device_id_while_opted_out_via_persisted_config() {
        TelemetryState.SetEnabled(false, Config.Root);
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;

        CliTelemetry.Initialize("status", null, loggedIn: false, Config.Root);

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
    }

    // Regression test for the P2 finding: GetOrCreateDeviceId used to independently veto minting
    // whenever the PERSISTED flag was false, regardless of what TelemetrySettings.Resolve (which
    // already accounts for KCAP_TELEMETRY outranking the persisted config) had decided. That made
    // the documented top-priority env var unable to opt back in once a user had persisted "off" —
    // Initialize would resolve Enabled=true, call GetOrCreateDeviceId, get null back from the
    // stale independent check, and disable itself right back. Mutates the REAL process env var
    // (TelemetrySettings.Resolve(bool?) reads it live), safe under this class's shared
    // [NotInParallel] lock — no other suite in this run touches the real KCAP_TELEMETRY var.
    [Test]
    public async Task Kcap_telemetry_env_var_overrides_a_persisted_off_and_mints_a_device_id() {
        TelemetryState.SetEnabled(false, Config.Root);
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;

        var saved = Environment.GetEnvironmentVariable("KCAP_TELEMETRY");
        Environment.SetEnvironmentVariable("KCAP_TELEMETRY", "1");
        try {
            CliTelemetry.Initialize("status", null, loggedIn: false, Config.Root);

            await Assert.That(CliTelemetry.Enabled).IsTrue();
            await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNotNull();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_TELEMETRY", saved);
        }
    }

    // Regression test for the P1 finding's belt-and-braces half. Program.cs now pre-applies
    // `config set telemetry off` to disk before calling Initialize (see Program.cs), so the plain
    // case never activates telemetry at all. But Initialize CAN still come up live despite a
    // persisted "off" — KCAP_TELEMETRY=1 legitimately overrides it (finding 2) — so this
    // simulates that: telemetry is already live with cli_first_run queued (as if it had resolved
    // enabled for whatever reason) by the time `config set telemetry off` runs, and asserts
    // ConfigCommand.TryApplyTelemetry's CliTelemetry.DiscardAndDisable() tears it down completely
    // in the SAME process — discarding what's queued, disabling the facade, and deleting the id —
    // rather than leaving it to survive to this process's own ProcessExit flush.
    [Test]
    public async Task Opting_out_via_config_tears_down_telemetry_in_the_same_process_and_deletes_the_id() {
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("config", null, loggedIn: false, Config.Root);

        // Sanity: telemetry actually came up live and minted an id — otherwise the assertions
        // below would trivially pass having exercised nothing.
        await Assert.That(CliTelemetry.Enabled).IsTrue();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNotNull();
        await Assert.That(sink.Any(e => e.Name == "cli_first_run")).IsTrue();

        var exit = await new ConfigCommand(Config.Root, new FixedCapacitorHttpClient()).HandleAsync(["config", "set", "telemetry", "off"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink).IsEmpty();
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();

        // The disabled facade must not resurrect anything on the exit-time flush either.
        await CliTelemetry.FlushAndClose();
        await Assert.That(sink).IsEmpty();
    }
}
