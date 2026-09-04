using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpWorkItemsServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpWorkItemsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient());

    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";

    // Shares ArgParsingTests' NotInParallel key: both suites mutate the same process-global
    // KCAP_SESSION_ID / CODEX_THREAD_ID env vars, so tests in either must not interleave.
    const string SessionEnvVarMutation = "SessionEnvVarMutation";

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Resolve_session_id_prefers_explicit_argument() {
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"explicit1"}"""));

        await Assert.That(id).IsEqualTo("explicit1");
    }

    [Test]
    public async Task Resolve_session_id_strips_dashes_from_explicit_argument() {
        // Matches ArgParsing.ResolveSessionIdFromEnv's normalization so an explicit dashed GUID
        // (e.g. copy-pasted from a UI) resolves to the same dashless key as the ambient env var.
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"1234abcd-56ef-78ab-90cd-1234567890ab"}"""));

        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_falls_back_to_env_when_argument_missing() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "envsess1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var id = McpWorkItemsServer.ResolveSessionId(new JsonObject());

            await Assert.That(id).IsEqualTo("envsess1");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_throws_when_neither_argument_nor_env_present() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var ex = Assert.Throws<ArgumentException>(() => McpWorkItemsServer.ResolveSessionId(new JsonObject()));

            await Assert.That(ex!.Message).IsEqualTo(McpWorkItemsServer.NoSessionIdMessage);
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    public async Task Declare_body_carries_session_id_and_issue_key() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"PROJ-1234"}"""));

        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["issue_key"]!.GetValue<string>()).IsEqualTo("PROJ-1234");
        await Assert.That(body["pr_number"]).IsNull();
        await Assert.That(body["work_item_id"]).IsNull();
        await Assert.That(body["new_title"]).IsNull();
    }

    [Test]
    public async Task Declare_body_carries_pr_number() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":123}"""));

        await Assert.That(body["pr_number"]!.GetValue<int>()).IsEqualTo(123);
    }

    [Test]
    public async Task Declare_body_carries_work_item_id() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","work_item_id":"wi-9"}"""));

        await Assert.That(body["work_item_id"]!.GetValue<string>()).IsEqualTo("wi-9");
    }

    [Test]
    public async Task Declare_body_carries_new_title() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","new_title":"Investigate flaky test"}"""));

        await Assert.That(body["new_title"]!.GetValue<string>()).IsEqualTo("Investigate flaky test");
    }

    [Test]
    public async Task Session_url_escapes_and_resolves_explicit_session_id() {
        var url = McpWorkItemsServer.BuildSessionUrl("http://x", Args("""{"session_id":"sess a/b"}"""));

        await Assert.That(url).IsEqualTo("http://x/api/work-items/session/sess%20a%2Fb");
    }

    // ── flow-review round 2 findings ─────────────────────────────────────────

    [Test]
    public async Task Decode_method_returns_null_for_wrong_shaped_method_instead_of_throwing() {
        // {"id":1,"method":{}} must yield an invalid-request response, never kill the stdio loop.
        var method = McpWorkItemsServer.DecodeMethod(Args("""{"id":1,"method":{}}"""));

        await Assert.That(method).IsNull();
    }

    [Test]
    public async Task Declare_body_rejects_string_pr_number_instead_of_dropping_it() {
        // A malformed two-selector declare (issue_key + string pr_number) must FAIL — silently
        // dropping the wrong-shaped selector would let the server's exactly-one rule pass and
        // perform an attach the caller never validly requested.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"PROJ-1","pr_number":"123"}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_object_shaped_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":{}}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_fractional_pr_number_via_raw_token() {
        // Raw-token validation (JsonElement.TryGetInt32) — a fractional part below double
        // precision must still reject; the lossy double round-trip would have accepted it.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483646.0000000001}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_out_of_range_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483648}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Tools_list_exposes_the_declare_and_breakdown_surface() {
        var tools = McpWorkItemsServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name).ToArray()).IsEquivalentTo(new[] {
            "declare_work_item", "get_session_work_items",
            "declare_work_breakdown", "retract_work_breakdown",
            "declare_work_relation", "retract_work_relation",
            "get_work_item_topology"
        });
    }

    [Test]
    public async Task Server_instructions_steer_agents_to_declare_breakdown_and_relations() {
        // Pin that the preamble names both declare tools + the declared-never-inferred rule, so it
        // can't silently regress to a bare correlation description.
        var instructions = McpWorkItemsServer.ServerInstructions;

        await Assert.That(instructions).Contains("declare_work_breakdown");
        await Assert.That(instructions).Contains("declare_work_relation");
        await Assert.That(instructions).Contains("never");   // "declared, never inferred"
    }

    // ── declared breakdown + relations ───────────────────────────────────────

    [Test]
    public async Task No_tool_advertises_a_server_owned_source_or_declared_by_argument() {
        // Named "advertises", not "accepts" (review correction): without additionalProperties:false a
        // JSON Schema does not forbid a caller supplying these keys, so this test proves only that we
        // never invite them. The guarantee that they are never FORWARDED is a property of the body
        // builders, asserted separately below.
        var tools = McpWorkItemsServer.BuildToolsList();

        // Non-vacuity only: without this the loop body would never run if BuildToolsList returned
        // empty and the test would pass having checked nothing. Deliberately NOT an exact count
        // (review finding) — Tools_list_exposes_the_declare_and_breakdown_surface already pins the
        // exact set, and duplicating it here would make an unrelated new tool fail this test too.
        await Assert.That(tools.Length).IsGreaterThan(0);

        foreach (var tool in tools) {
            await Assert.That(tool.InputSchema.Properties.Keys).DoesNotContain("source")
                .Because($"{tool.Name} must not advertise a server-owned field");
            await Assert.That(tool.InputSchema.Properties.Keys).DoesNotContain("declared_by")
                .Because($"{tool.Name} must not advertise a server-owned field");
        }
    }

    [Test]
    public async Task Body_builders_never_forward_a_caller_supplied_source_or_declared_by() {
        // THIS is the real guarantee: the builders whitelist their output, so even a caller that
        // ignores the schema and sends these keys cannot get them onto the wire. The server resolves
        // both from the authenticated identity and rejects a source of "user" outright.
        const string spoofed = """
                               {"parent_id":"p1","part_ids":["a"],"to_id":"b","relation_kind":"blocks",
                                "source":"user","declared_by":"someone-else"}
                               """;

        var breakdown = McpWorkItemsServer.BuildBreakdownBody(Args(spoofed));
        var relation  = McpWorkItemsServer.BuildRelationBody(Args(spoofed));

        foreach (var body in new[] { breakdown, relation }) {
            await Assert.That(body.ContainsKey("source")).IsFalse();
            await Assert.That(body.ContainsKey("declared_by")).IsFalse();
        }

        // Precondition: the bodies are not empty for an unrelated reason, so the absences above mean
        // "filtered out" rather than "nothing was built".
        await Assert.That(breakdown.ContainsKey("part_ids")).IsTrue();
        await Assert.That(relation.ContainsKey("to_id")).IsTrue();
    }

    [Test]
    public async Task Declared_structure_is_bounded_by_visibility_not_by_repository() {
        // The server accepts a part or a relation whose other end lives in another repository; only
        // visibility to the caller bounds it. Text claiming otherwise makes agents skip declarations
        // the server would take, so the preamble and both declare tools must not restate a repository
        // rule.
        var byName = McpWorkItemsServer.BuildToolsList().ToDictionary(t => t.Name);

        foreach (var name in (string[])["declare_work_breakdown", "declare_work_relation"]) {
            await Assert.That(byName[name].Description).Contains("visible");
            await Assert.That(byName[name].Description).DoesNotContain("same repository");
        }

        await Assert.That(McpWorkItemsServer.ServerInstructions).DoesNotContain("same repository");
    }

    [Test]
    public async Task Every_breakdown_tool_declares_its_ids_required() {
        // Unlike session_id, these ids have no ambient fallback — a schema that marked them optional
        // would invite a call with no id at all.
        var byName = McpWorkItemsServer.BuildToolsList().ToDictionary(t => t.Name);

        await Assert.That(byName["declare_work_breakdown"].InputSchema.Required).IsEquivalentTo(new[] { "parent_id", "part_ids" });
        await Assert.That(byName["retract_work_breakdown"].InputSchema.Required).IsEquivalentTo(new[] { "parent_id", "part_ids" });
        await Assert.That(byName["declare_work_relation"].InputSchema.Required).IsEquivalentTo(new[] { "from_id", "to_id", "relation_kind" });
        await Assert.That(byName["retract_work_relation"].InputSchema.Required).IsEquivalentTo(new[] { "from_id", "to_id", "relation_kind" });
        await Assert.That(byName["get_work_item_topology"].InputSchema.Required).IsEquivalentTo(new[] { "work_item_id" });
    }

    [Test]
    public async Task Array_properties_declare_their_element_type() {
        // An `array` with no `items` is incomplete JSON Schema: a strict client can reject it and a
        // model has to guess the element type.
        foreach (var tool in McpWorkItemsServer.BuildToolsList()) {
            foreach (var (name, property) in tool.InputSchema.Properties) {
                if (property.Type != "array") continue;

                await Assert.That(property.Items).IsNotNull()
                    .Because($"{tool.Name}.{name} is an array and must declare items");
                await Assert.That(property.Items!.Type).IsEqualTo("string");
            }
        }
    }

    [Test]
    public async Task Item_url_builds_the_route_and_escapes_the_id() {
        var url = McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"wi 1/2"}"""), "parent_id", "breakdown");

        // The escape is what stops an id containing a slash from walking out of its path segment into
        // a different route.
        await Assert.That(url).IsEqualTo("http://x/api/work-items/wi%201%2F2/breakdown");
    }

    [Test]
    public async Task Item_url_rejects_a_missing_blank_or_wrong_typed_id() {
        var missing = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", new JsonObject(), "parent_id", "breakdown"));
        await Assert.That(missing!.Message).Contains("parent_id");

        var blank = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"   "}"""), "parent_id", "breakdown"));
        await Assert.That(blank!.Message).Contains("blank");

        var wrongType = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":42}"""), "parent_id", "breakdown"));
        await Assert.That(wrongType!.Message).Contains("string");
    }

    [Test]
    public async Task Breakdown_body_carries_part_ids() {
        var body = McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":["a","b"]}"""));

        // parent_id rides the URL, not the body — sending it twice invites the two copies to diverge.
        await Assert.That(body["parent_id"]).IsNull();
        await Assert.That(body["part_ids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
            .IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task Breakdown_body_rejects_a_wrong_shaped_part_ids_instead_of_dropping_it() {
        // Silently omitting a malformed part_ids would turn a bad declare into a differently-shaped
        // request whose rejection reads as though the caller had sent nothing.
        var notArray = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":"a"}""")));
        await Assert.That(notArray!.Message).Contains("array");

        var notStrings = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":[1,2]}""")));
        await Assert.That(notStrings!.Message).Contains("strings");

        var blankEntry = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":["a","  "]}""")));
        await Assert.That(blankEntry!.Message).Contains("blank");
    }

    [Test]
    public async Task Breakdown_body_leaves_an_absent_part_ids_to_the_server_to_reject() {
        // Deliberate pass-through: the server owns the "empty parts" rule and names it in a coded 400.
        var body = McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1"}"""));

        await Assert.That(body["part_ids"]).IsNull();
    }

    [Test]
    public async Task Relation_body_carries_to_id_and_relation_kind() {
        var body = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a","to_id":"b","relation_kind":"blocks"}"""));

        await Assert.That(body["from_id"]).IsNull(); // rides the URL
        await Assert.That(body["to_id"]!.GetValue<string>()).IsEqualTo("b");
        await Assert.That(body["relation_kind"]!.GetValue<string>()).IsEqualTo("blocks");
    }

    [Test]
    public async Task Breakdown_body_rejects_an_explicit_null_part_ids() {
        // A PRESENT null is a wrong shape, not absence. An earlier revision's `is { } node` form read
        // it as absence and silently omitted the field (review finding).
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildBreakdownBody(Args("""{"parent_id":"p1","part_ids":null}""")));

        await Assert.That(ex!.Message).Contains("part_ids");
    }

    [Test]
    public async Task Relation_body_forwards_an_explicitly_empty_string_rather_than_dropping_it() {
        // Dropping "" would make the server answer "relation_kind is required" when the caller DID
        // supply it — the wrong diagnosis. Forwarding it gets the server's invalid-value error, which
        // is the one that helps.
        var body = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a","to_id":"","relation_kind":""}"""));

        await Assert.That(body.ContainsKey("to_id")).IsTrue();
        await Assert.That(body["to_id"]!.GetValue<string>()).IsEqualTo("");
        await Assert.That(body["relation_kind"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task Relation_body_leaves_absent_keys_absent_and_rejects_present_wrong_types() {
        var absent = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a"}"""));
        await Assert.That(absent.ContainsKey("to_id")).IsFalse();
        await Assert.That(absent.ContainsKey("relation_kind")).IsFalse();

        var nullKind = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildRelationBody(Args("""{"to_id":"b","relation_kind":null}""")));
        await Assert.That(nullKind!.Message).Contains("relation_kind");

        var numericKind = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildRelationBody(Args("""{"to_id":"b","relation_kind":7}""")));
        await Assert.That(numericKind!.Message).Contains("relation_kind");
    }

    [Test]
    public async Task Item_url_rejects_dot_segment_ids_that_escaping_alone_would_not_contain() {
        // `.` is unreserved in RFC 3986, so EscapeDataString leaves it alone and URI normalization
        // then REMOVES the segment: "." would reach /api/work-items/breakdown and ".." would reach
        // /api/breakdown — a different route whose response would be attributed to the id passed.
        // The slash test does not cover this, because the hazard is normalization, not escaping.
        foreach (var id in new[] { ".", ".." }) {
            var ex = Assert.Throws<ArgumentException>(
                () => McpWorkItemsServer.ItemUrl("http://x", Args($$"""{"parent_id":"{{id}}"}"""), "parent_id", "breakdown"));

            await Assert.That(ex!.Message).Contains("parent_id").Because($"id {id} must be rejected");
        }

        // And ONLY those two (review correction — an earlier revision rejected any all-dot id, and
        // this test pinned that over-broad behaviour). "..." is an ordinary path segment, not a dot
        // segment, so refusing it would reject an id the server might accept.
        var threeDots = McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"..."}"""), "parent_id", "breakdown");
        await Assert.That(threeDots).IsEqualTo("http://x/api/work-items/.../breakdown");

        // A dot INSIDE an otherwise-real id is fine too — the guard must not ban the character.
        var ok = McpWorkItemsServer.ItemUrl("http://x", Args("""{"parent_id":"wi.1"}"""), "parent_id", "breakdown");
        await Assert.That(ok).IsEqualTo("http://x/api/work-items/wi.1/breakdown");
    }

    [Test]
    public async Task Relation_body_does_not_enumerate_the_relation_kind_vocabulary() {
        // The server owns the vocabulary. Passing an unknown kind through means the caller gets the
        // server's coded rejection naming the real reason, rather than a client-side guess that could
        // drift from the server as kinds are added.
        var body = McpWorkItemsServer.BuildRelationBody(Args("""{"from_id":"a","to_id":"b","relation_kind":"depends_on"}"""));

        await Assert.That(body["relation_kind"]!.GetValue<string>()).IsEqualTo("depends_on");
    }

    // ── dispatch: the route/method/body pairing itself ────────────────────────

    /// <summary>
    /// Review finding: the helper tests above verify URL and body construction INDEPENDENTLY, so none
    /// of them would catch a switch entry that used GET instead of POST, picked the wrong suffix, or
    /// paired a route with the wrong body builder. These drive the real dispatch through a fake
    /// transport and assert what would actually go on the wire.
    /// </summary>
    sealed class CapturingHandler : HttpMessageHandler {
        public HttpMethod? Method  { get; private set; }
        public string?     Url     { get; private set; }
        public string?     Body    { get; private set; }
        public int         Calls   { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Calls++;
            Method = request.Method;
            Url    = request.RequestUri?.ToString();
            Body   = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    async Task<CapturingHandler> DispatchAsync(string toolName, string argsJson) {
        var handler = new CapturingHandler();
        using var client = new HttpClient(handler);

        // Built as nodes rather than an interpolated raw string: the trailing brace run in a
        // hand-written JSON-RPC envelope collides with raw-string interpolation delimiters.
        var request = new JsonObject {
            ["params"] = new JsonObject {
                ["name"]      = toolName,
                ["arguments"] = JsonNode.Parse(argsJson)
            }
        };

        await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x");

        return handler;
    }

    [Test]
    public async Task Dispatch_declare_work_breakdown_posts_part_ids_to_the_breakdown_route() {
        var h = await DispatchAsync("declare_work_breakdown", """{"parent_id":"p1","part_ids":["a","b"]}""");

        await Assert.That(h.Calls).IsEqualTo(1);
        await Assert.That(h.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Url).IsEqualTo("http://x/api/work-items/p1/breakdown");
        await Assert.That(h.Body).IsEqualTo("""{"part_ids":["a","b"]}""");
    }

    [Test]
    public async Task Dispatch_retract_work_breakdown_targets_the_retract_route_with_the_same_body() {
        var h = await DispatchAsync("retract_work_breakdown", """{"parent_id":"p1","part_ids":["a"]}""");

        await Assert.That(h.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Url).IsEqualTo("http://x/api/work-items/p1/breakdown/retract");
        await Assert.That(h.Body).IsEqualTo("""{"part_ids":["a"]}""");
    }

    [Test]
    public async Task Dispatch_declare_work_relation_posts_the_relation_body_not_the_breakdown_body() {
        var h = await DispatchAsync("declare_work_relation", """{"from_id":"a","to_id":"b","relation_kind":"blocks"}""");

        await Assert.That(h.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Url).IsEqualTo("http://x/api/work-items/a/relations");
        await Assert.That(h.Body).IsEqualTo("""{"to_id":"b","relation_kind":"blocks"}""");
    }

    [Test]
    public async Task Dispatch_retract_work_relation_targets_the_relation_retract_route() {
        var h = await DispatchAsync("retract_work_relation", """{"from_id":"a","to_id":"b","relation_kind":"blocked_by"}""");

        await Assert.That(h.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Url).IsEqualTo("http://x/api/work-items/a/relations/retract");
        await Assert.That(h.Body).IsEqualTo("""{"to_id":"b","relation_kind":"blocked_by"}""");
    }

    [Test]
    public async Task Dispatch_get_work_item_topology_is_a_GET_with_no_body() {
        var h = await DispatchAsync("get_work_item_topology", """{"work_item_id":"wi-1"}""");

        await Assert.That(h.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(h.Url).IsEqualTo("http://x/api/work-items/wi-1/topology");
        await Assert.That(h.Body).IsNull();
    }

    [Test]
    public async Task Dispatch_of_a_breakdown_tool_with_a_missing_id_never_reaches_the_network() {
        // The local validation must fail BEFORE a request is built — otherwise a malformed call would
        // hit some other route and the error would describe the wrong thing.
        var h = await DispatchAsync("declare_work_breakdown", """{"part_ids":["a"]}""");

        await Assert.That(h.Calls).IsEqualTo(0);
    }

    // ── telemetry: a real dispatch failure must read back as ok=false ─────────────────────────
    //
    // Regression coverage for the systematic MCP telemetry bug: every server's TimedDispatchToolCallAsync
    // used to set ok=true merely because DispatchToolCallAsync returned a string, even when that
    // string was a JSON-RPC result carrying isError:true (HandleToolCallAsync catches its own
    // exceptions and returns such a result rather than throwing). This drives a REAL failure
    // through the actual dispatch method — an unrecognized tool name, which never reaches the
    // network — and feeds its actual output into McpTelemetry.ResponseOk, the same function the
    // wrapper now calls, so a reversion of either half would fail this test.
    [Test]
    public async Task ResponseOk_reads_false_from_an_unknown_tool_dispatch_error() {
        using var client = new HttpClient(new CapturingHandler());
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = "not_a_real_tool", ["arguments"] = new JsonObject() }
        };

        var response = await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x");

        await Assert.That(McpTelemetry.ResponseOk(response)).IsFalse();
    }

    // Sanity check for the same wiring: a genuine success must still read back as ok=true, so a
    // bug that made ResponseOk (or the dispatch it reads) unconditionally false would not slip
    // past the test above alone.
    [Test]
    public async Task ResponseOk_reads_true_from_a_successful_dispatch() {
        using var client = new HttpClient(new CapturingHandler());
        var request = new JsonObject {
            ["params"] = new JsonObject {
                ["name"]      = "get_work_item_topology",
                ["arguments"] = JsonNode.Parse("""{"work_item_id":"wi-1"}""")
            }
        };

        var response = await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x");

        await Assert.That(McpTelemetry.ResponseOk(response)).IsTrue();
    }
}
