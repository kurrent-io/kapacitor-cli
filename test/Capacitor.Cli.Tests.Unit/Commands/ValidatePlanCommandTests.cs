using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <see cref="ValidatePlanCommand"/>'s two-call flow — <c>GET .../plan-artifacts?chain=true</c>
/// for the discovered plan set, then the existing <c>GET .../recap?chain=true</c> for current-session
/// work rows and "what's done" summaries. A 404 on the artifacts route (old server / non-visible
/// session) falls back to the original recap-only rendering unchanged.
///
/// <para><c>HandleCore</c> is driven with a <see cref="SessionsApi"/> built against a
/// <see cref="FixedCapacitorHttpClient"/> pointed at the WireMock stub — no auth-discovery
/// pipeline, mirroring <c>FeedbackCommandTests</c>'s <c>HandleCore</c> seam.</para>
///
/// Every test is <c>[NotInParallel]</c> with NO group key (globally sequential), mirroring
/// <c>ClaudeHookStdoutTests</c>: the SUT writes to the process-global <see cref="Console.Out"/>,
/// and a group key alone would not stop a different file's test from leaking into the capture.
/// </summary>
public class ValidatePlanCommandTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string SessionId = "9dc2775376454e4691ecc2d69973c152";

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    ISessionsApi Api() =>
        new SessionsApi(new FixedCapacitorHttpClient(), new CapacitorServer(_server.Url!, Config.Root, Resolutions.At(_server.Url!, Config.Root)));

    static async Task<string> CaptureStdoutAsync(Func<Task> action) {
        using var capture = ConsoleOutput.StartCapture();
        await action();
        return capture.GetCapturedOutput();
    }

    static string RecapJson(string extraEntries = "") => $$"""
        [
          {"type":"write","session_id":"{{SessionId}}","agent_id":null,"agent_type":null,"content":"","file_path":"src/Foo.cs","timestamp":"2026-07-01T00:00:00Z"},
          {"type":"whats_done","session_id":null,"agent_id":null,"agent_type":null,"content":"Implemented Foo.","file_path":null,"timestamp":"2026-07-01T00:05:00Z"}{{extraEntries}}
        ]
        """;

    [Test, NotInParallel]
    public async Task Primary_artifact_renders_under_plan_and_recap_supplies_work_and_whats_done() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-1",
                    "kind": "plan",
                    "title": "Plan A",
                    "source": "native_plan",
                    "session_id": "{{SessionId}}",
                    "content": "Step 1\nStep 2",
                    "content_state": "ok",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "content_hash": "abc123",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "high",
                    "reason": "native plan tool",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var exitCode = -1;
        var stdout = await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("## Plan");
        await Assert.That(stdout).Contains("Step 1\nStep 2");
        await Assert.That(stdout).Contains("## What's Done");
        await Assert.That(stdout).Contains("Implemented Foo.");
        await Assert.That(stdout).Contains("- Write: src/Foo.cs");
        await Assert.That(exitCode).IsEqualTo(0);

        var hits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(hits.Count).IsEqualTo(1); // recap is still consulted for work/whats_done
    }

    [Test, NotInParallel]
    public async Task Empty_response_prints_no_plan_found_and_skips_recap() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""
                { "primary": null, "artifacts": [], "diagnostics": [] }
                """));

        var exitCode = -1;
        var stdout = await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout.Trim()).IsEqualTo("No plan found for this session.");
        await Assert.That(exitCode).IsEqualTo(0); // absence of a plan is a valid answer

        var recapHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(recapHits.Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task NotFound_falls_back_to_legacy_recap_only_rendering() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        // Legacy path: a single recap call carrying a "plan" entry plus the usual work/whats_done.
        var extra = $$""", {"type":"plan","session_id":"{{SessionId}}","agent_id":null,"agent_type":null,"content":"Legacy plan text","file_path":null,"timestamp":"2026-06-30T00:00:00Z"}""";
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson(extra)));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("## Plan");
        await Assert.That(stdout).Contains("Legacy plan text");
        await Assert.That(stdout).Contains("## What's Done");
        await Assert.That(stdout).Contains("Implemented Foo.");
        await Assert.That(stdout).DoesNotContain("plan truncated");
        await Assert.That(stdout).DoesNotContain("plan content unavailable");

        // Both routes were attempted: plan-artifacts first (404), then the recap fallback.
        var artifactHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet());
        await Assert.That(artifactHits.Count).IsEqualTo(1);
        var recapHits = _server.FindLogEntries(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(recapHits.Count).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task NotFound_with_no_plan_entries_prints_no_plan_found() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout.Trim()).IsEqualTo("No plan found for this session.");
    }

    [Test, NotInParallel]
    public async Task Truncated_primary_prefixes_the_plan_section_with_a_byte_count_marker() {
        const string content = "Truncated prefix of the plan...";
        var byteCount        = Encoding.UTF8.GetByteCount(content);

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-2",
                    "kind": "plan",
                    "title": "Plan B",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": "{{content}}",
                    "content_state": "truncated",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": true,
                    "original_bytes": 5000,
                    "content_hash": "def456",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "medium",
                    "reason": "repo file over size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains($"[plan truncated: first {byteCount} of 5000 bytes]");
        await Assert.That(stdout).Contains(content);
    }

    [Test, NotInParallel]
    public async Task Unavailable_primary_renders_placeholder_and_states_validation_is_not_possible() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-3",
                    "kind": "plan",
                    "title": "Plan C",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": null,
                    "content_state": "unavailable",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "original_bytes": 999999,
                    "content_hash": "ghi789",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "low",
                    "reason": "content exceeds hard size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var exitCode = -1;
        var stdout = await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("[plan content unavailable due to size bounds]");
        await Assert.That(stdout).Contains("Validation is not possible");
        // review finding 3: distinguishable from success (0) and from a generic error (1).
        await Assert.That(exitCode).IsEqualTo(2);
    }

    [Test, NotInParallel]
    public async Task Truncated_primary_with_null_original_bytes_falls_back_to_unknown_total_marker() {
        // review finding 2: OriginalBytes is nullable — a malformed/edge response could
        // omit it even on a "truncated" artifact. The marker must stay well-formed ("of ? bytes"),
        // never "of  bytes".
        const string content = "Truncated prefix with no known original size...";

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-no-total",
                    "kind": "plan",
                    "title": "Plan F",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": "{{content}}",
                    "content_state": "truncated",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": true,
                    "original_bytes": null,
                    "content_hash": "notot1",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "medium",
                    "reason": "repo file over size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var exitCode = -1;
        var stdout = await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        var byteCount = Encoding.UTF8.GetByteCount(content);
        await Assert.That(stdout).Contains($"[plan truncated: first {byteCount} of ? bytes]");
        await Assert.That(stdout).DoesNotContain("of  bytes"); // malformed: double space, no total
        await Assert.That(stdout).Contains(content);
        await Assert.That(exitCode).IsEqualTo(0); // content did render — this isn't the unavailable case
    }

    [Test, NotInParallel]
    public async Task Truncated_primary_with_null_content_renders_like_unavailable() {
        // review finding 2: content_state=="truncated" with Content == null is an edge
        // shape the server contract doesn't normally produce, but the CLI must not print
        // "first 0 of {n} bytes" — treat it like "unavailable" (placeholder + exit 2 since this
        // is the primary).
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-null-content",
                    "kind": "plan",
                    "title": "Plan G",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": null,
                    "content_state": "truncated",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": true,
                    "original_bytes": 5000,
                    "content_hash": "nullc1",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "medium",
                    "reason": "repo file over size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var exitCode = -1;
        var stdout = await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).DoesNotContain("first 0");
        await Assert.That(stdout).Contains("[plan content unavailable due to size bounds]");
        await Assert.That(stdout).Contains("Validation is not possible");
        await Assert.That(exitCode).IsEqualTo(2);
    }

    [Test, NotInParallel]
    public async Task Degraded_primary_prefixes_the_plan_section_with_the_degraded_marker() {
        // CRITICAL: is_complete == false on the primary — a newer
        // revision exists but hasn't resolved yet — must render the
        // "unresolved newer revision" marker before the content. This mirrors the
        // server's PlanRowRendering.DegradedText byte-for-byte (em-dash, spacing).
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-degraded",
                    "kind": "plan",
                    "title": "Plan D",
                    "source": "native_plan",
                    "session_id": "{{SessionId}}",
                    "content": "Step 1\nStep 2",
                    "content_state": "ok",
                    "is_complete": false,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "content_hash": "deg111",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "high",
                    "reason": "native plan tool",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("[plan state: unresolved newer revision — last known complete text]");
        await Assert.That(stdout).Contains("Step 1\nStep 2");

        // Marker precedes the content.
        var markerIndex  = stdout.IndexOf("[plan state: unresolved newer revision", StringComparison.Ordinal);
        var contentIndex = stdout.IndexOf("Step 1\nStep 2", StringComparison.Ordinal);
        await Assert.That(markerIndex).IsLessThan(contentIndex);
    }

    [Test, NotInParallel]
    public async Task Degraded_and_truncated_primary_renders_both_markers_degraded_first() {
        // Degraded composes WITH truncated: degraded line first, then the truncation
        // line, mirroring the server's PlanRowRendering ordering.
        const string content = "Truncated prefix of a degraded plan...";
        var byteCount        = Encoding.UTF8.GetByteCount(content);

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-degraded-truncated",
                    "kind": "plan",
                    "title": "Plan E",
                    "source": "repo_file",
                    "session_id": "{{SessionId}}",
                    "content": "{{content}}",
                    "content_state": "truncated",
                    "is_complete": false,
                    "is_confirmed": true,
                    "is_truncated": true,
                    "original_bytes": 5000,
                    "content_hash": "deg222",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "medium",
                    "reason": "repo file over size bound",
                    "is_primary": true
                  },
                  "artifacts": [],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        var degradedIndex  = stdout.IndexOf("[plan state: unresolved newer revision", StringComparison.Ordinal);
        var truncatedIndex = stdout.IndexOf($"[plan truncated: first {byteCount} of 5000 bytes]", StringComparison.Ordinal);
        var contentIndex   = stdout.IndexOf(content, StringComparison.Ordinal);

        await Assert.That(degradedIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(truncatedIndex).IsGreaterThan(degradedIndex);
        await Assert.That(contentIndex).IsGreaterThan(truncatedIndex);
    }

    [Test, NotInParallel]
    public async Task Primary_not_first_in_artifacts_array_still_renders_first_then_others_in_server_order() {
        // MINOR: the primary artifact can appear anywhere in the
        // "artifacts" array (the server's discovery order is newest-first, not
        // primary-first). Rendering must still put the primary's section first,
        // followed by the OTHER artifacts in the order the server returned them.
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                {
                  "primary": {
                    "artifact_id": "art-primary",
                    "kind": "plan",
                    "title": "Primary Plan",
                    "source": "native_plan",
                    "session_id": "{{SessionId}}",
                    "content": "PRIMARY-CONTENT",
                    "content_state": "ok",
                    "is_complete": true,
                    "is_confirmed": true,
                    "is_truncated": false,
                    "content_hash": "p1",
                    "version": 1,
                    "discovered_at": "2026-07-01T00:00:00Z",
                    "confidence": "high",
                    "reason": "native plan tool",
                    "is_primary": true
                  },
                  "artifacts": [
                    {
                      "artifact_id": "art-other-1",
                      "kind": "plan",
                      "title": "Other Plan 1",
                      "source": "repo_file",
                      "session_id": "{{SessionId}}",
                      "content": "OTHER-ONE-CONTENT",
                      "content_state": "ok",
                      "is_complete": true,
                      "is_confirmed": true,
                      "is_truncated": false,
                      "content_hash": "o1",
                      "version": 1,
                      "discovered_at": "2026-07-01T00:00:00Z",
                      "confidence": "low",
                      "reason": "candidate",
                      "is_primary": false
                    },
                    {
                      "artifact_id": "art-primary",
                      "kind": "plan",
                      "title": "Primary Plan",
                      "source": "native_plan",
                      "session_id": "{{SessionId}}",
                      "content": "PRIMARY-CONTENT",
                      "content_state": "ok",
                      "is_complete": true,
                      "is_confirmed": true,
                      "is_truncated": false,
                      "content_hash": "p1",
                      "version": 1,
                      "discovered_at": "2026-07-01T00:00:00Z",
                      "confidence": "high",
                      "reason": "native plan tool",
                      "is_primary": true
                    },
                    {
                      "artifact_id": "art-other-2",
                      "kind": "plan",
                      "title": "Other Plan 2",
                      "source": "repo_file",
                      "session_id": "{{SessionId}}",
                      "content": "OTHER-TWO-CONTENT",
                      "content_state": "ok",
                      "is_complete": true,
                      "is_confirmed": true,
                      "is_truncated": false,
                      "content_hash": "o2",
                      "version": 1,
                      "discovered_at": "2026-07-01T00:00:00Z",
                      "confidence": "low",
                      "reason": "candidate",
                      "is_primary": false
                    }
                  ],
                  "diagnostics": []
                }
                """));

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        var primaryIndex = stdout.IndexOf("PRIMARY-CONTENT", StringComparison.Ordinal);
        var other1Index  = stdout.IndexOf("OTHER-ONE-CONTENT", StringComparison.Ordinal);
        var other2Index  = stdout.IndexOf("OTHER-TWO-CONTENT", StringComparison.Ordinal);

        await Assert.That(primaryIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(primaryIndex).IsLessThan(other1Index);
        await Assert.That(other1Index).IsLessThan(other2Index);

        // The primary's content is rendered exactly once (not duplicated because it
        // also appears in the "artifacts" array).
        var occurrences = System.Text.RegularExpressions.Regex.Count(stdout, "PRIMARY-CONTENT");
        await Assert.That(occurrences).IsEqualTo(1);
    }
}
