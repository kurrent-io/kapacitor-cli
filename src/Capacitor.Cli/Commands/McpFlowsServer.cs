using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

class McpFlowsServer(
        ConfigRoot config, ProfileContext profiles, TokenStore store, ICapacitorHttpClient http) {
    public async Task<int> RunAsync(string? driverArg = null) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Requester context is resolved ONCE here, from the running harness rather than from the
        // environment this process inherited — see HarnessRequesterContext for why an inherited
        // KCAP_SESSION_ID / process cwd names the launching session instead of this driver. Both the
        // session id and the working directory come from the same resolution, so a flow can never be
        // attributed to one session while being reviewed in another session's checkout.
        var requester    = HarnessRequesterContext.Resolve();
        var cwd          = requester.ProjectDir ?? Directory.GetCurrentDirectory();
        var repoRoot     = GitRepository.FindRoot(cwd);
        // Prefer the `--driver` stamp from this server's own registration (deterministic for the JSON
        // harnesses); fall back to env inference for Claude/Codex, whose registrations are unstamped.
        var driverVendor = DriverVendor.Infer(driverArg);
        var tools        = BuildToolsList();

        var repository = new CwdRepository(config, cwd);

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — re-initialise under the reportable pseudo-command
        // "mcp-server" so per-tool-call events actually leave. Best-effort: a stale token on
        // disk must never block the server from starting.
        var loggedIn = false;
        try { loggedIn = await store.LoadForProfileAsync(profiles.Name) is not null; } catch { }
        CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn, config);

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup: kcap-flows
        // auto-registers, so Claude Code spawns `kcap mcp flows` for every session —
        // deferring keeps startup local-only (no GET /auth/config, token load, or stderr re-auth
        // hint) for sessions that never invoke a flows tool. Created on demand into a nullable
        // field (rather than a Lazy<Task>) so a transient creation failure leaves it null and the
        // next call retries, instead of a faulted task sticking for the rest of the session. Safe
        // without locking: the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                if (client is null) {
                    client = await http.ForSessionAsync();
                    // the review-flow endpoints long-poll (start_review_flow /
                    // submit_review_round block server-side up to ~10 min while the reviewer runs).
                    // The default 100s timeout would abort the POST, which the server sees as a
                    // cancel and tears the reviewer down — so disable the client-side deadline and
                    // let the server's FlowResultWaiter + the harness MCP tool timeout bound it.
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                }

                return await HandleToolCallAsync(
                    callId, callRequest, client, baseUrl, cwd, repoRoot, await repository.GetAsync(),
                    requestingSessionId: requester.SessionId, driverVendor: driverVendor,
                    reviewerVendorPreference: () => LoadReviewerVendorPreferenceAsync());
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp flows: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
        }

        // Records which MCP tools agents actually reach for. Never touches the response path:
        // the result (or the exception) is returned exactly as DispatchToolCallAsync produced it.
        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = Stopwatch.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                McpTelemetry.ToolCalled("kcap-flows", tool, ok, CommandTiming.ElapsedMs(start));
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        try {
            while (await reader.ReadLineAsync() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;

                try {
                    request = JsonNode.Parse(line)?.AsObject();
                } catch {
                    continue;
                }

                if (request is null) continue;

                var id     = request["id"];
                var method = request["method"]?.GetValue<string>();

                // Notifications have no id — don't send a response
                if (id is null) continue;

                var response = method switch {
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await TimedDispatchToolCallAsync(id, request),
                    _            => McpProtocol.TryHandleStandardMethod(method, id)
                                    ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };

                await writer.WriteLineAsync(response);
            }
        } finally {
            if (client is not null) {
                try { client.Dispose(); } catch {
                    /* swallow — best-effort cleanup */
                }
            }
        }

        return 0;
    }

    // Internal (not private) so unit tests can drive the tool-call dispatch directly against a
    // WireMock stub, without spawning the real stdio JSON-RPC process (that full-process path is
    // Capacitor.Cli.Tests.Integration's job).
    internal async Task<string> HandleToolCallAsync(
            JsonNode            id,
            JsonObject          request,
            HttpClient          client,
            string              baseUrl,
            string              cwd,
            string?             repoRoot,
            RepositoryPayload?  repoInfo,
            FlowRetryClock?     clock = null,
            SettlementBackoff?  backoff = null,
            // The requesting session, resolved by RunAsync from the running harness (never read from
            // the environment down here — see HarnessRequesterContext). Optional so the tests that
            // exercise routing/retry/error paths, where requester identity is irrelevant, can omit
            // it; the production dispatch in RunAsync always supplies it.
            string?             requestingSessionId = null,
            // How the saved reviewer-vendor preference is read. RunAsync supplies the real read —
            // a fresh one per consultation, never a cached value (see
            // LoadReviewerVendorPreferenceAsync). Absent means "none saved", so a test that does not
            // care cannot accidentally consult the developer's own config file.
            Func<Task<SavedReviewerVendor>>? reviewerVendorPreference = null,
            // The driver harness, inferred once by RunAsync from the running harness's env, so the
            // reviewer-vendor lookup can echo driver_vendor without this handler reading the env.
            string? driverVendor = null
        ) {
        clock                    ??= FlowRetryClock.System;
        backoff                  ??= SettlementBackoff.Default;
        reviewerVendorPreference ??= () => Task.FromResult(new SavedReviewerVendor(null, ProfileConfig.DefaultName));
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var apiRoot = baseUrl.TrimEnd('/');

            // The start/submit tools (and their generic aliases) may need async poll — handle separately.
            if (toolName is "start_review_flow" or "start_flow" or "submit_review_round" or "send_to_participant") {
                // Dynamic flows: whether THIS call carried an inline definition_yaml. Uncoded
                // (non-JSON `{"error":...}`) failures on such a start get the "server may not
                // support dynamic flows" hint — coded rejections never do (new-server signal).
                var wasDynamicStart = toolName is "start_flow" && arguments?["definition_yaml"] is not null;

                // Reviewer-model override: a model-bearing start goes to the protocol-v3 route
                // (StartFlowAsync). Computed BEFORE the dispatch because a v3 start must NOT be wrapped
                // in the SETTLEMENT retry: each POST to /review/start/v3 mints AND launches a run, so
                // re-POSTing a retryable settlement 409 (flow_settlement_busy /
                // reviewer_launch_incarnation_superseded) would violate exactly-one-v3-POST and churn
                // reviewer launches. The coded 409 surfaces to the caller, who retries the whole start.
                // It DOES keep the lane's own 401 recovery: a 401 is rejected at the auth layer
                // BEFORE any run is created, so its single re-send is the only POST that reaches
                // business logic — exactly-one-EFFECTIVE-v3-POST holds while a long-lived MCP process
                // can still recover a token that expired after startup. So a model start drops ONLY
                // the settlement re-POST. Every v2 (no-model) start and every round keeps the full
                // settlement retry unchanged.
                var wasModelStart = !wasDynamicStart
                    && toolName is "start_review_flow" or "start_flow"
                    && !string.IsNullOrWhiteSpace(arguments?["model"]?.GetValue<string>());

                // When this call sends TWICE (the preference fallback below), both sends share ONE
                // settlement budget measured from here — see budgetStartedAt on
                // SendWithSettlementRetryAsync. Taken before the first send so the second can never
                // extend the total past the single-call deadline.
                var settlementStartedAt = clock.UtcNow;

                var sendResult = toolName switch {
                    "start_review_flow"   => wasModelStart
                        ? new SettlementSendResult.Response(await StartFlowAsync(client, apiRoot, arguments, cwd, repoRoot, repoInfo, kindArgName: "kind", requestingSessionId: requestingSessionId))
                        : await SendWithSettlementRetryAsync(client, apiRoot, (c, ct) => StartFlowAsync(c, apiRoot, arguments, cwd, repoRoot, repoInfo, kindArgName: "kind", requestingSessionId: requestingSessionId, ct: ct), clock, backoff),
                    "start_flow"          => wasModelStart
                        ? new SettlementSendResult.Response(await StartFlowAsync(client, apiRoot, arguments, cwd, repoRoot, repoInfo, kindArgName: "definition_id", requestingSessionId: requestingSessionId))
                        : await SendWithSettlementRetryAsync(client, apiRoot, (c, ct) => StartFlowAsync(c, apiRoot, arguments, cwd, repoRoot, repoInfo, kindArgName: "definition_id", requestingSessionId: requestingSessionId, ct: ct), clock, backoff),
                    // Round submission also retries the coded participant_unreachable 409 (see
                    // ParticipantUnreachableCode) — never a start, which can't return it.
                    "submit_review_round" => await SendWithSettlementRetryAsync(client, apiRoot, (c, ct) => SubmitRoundAsync(c, apiRoot, arguments, contextArgName: "context", participant: null, async: true, ct: ct), clock, backoff, extraRetryableCode: ParticipantUnreachableCode),
                    _                     => await SendWithSettlementRetryAsync(client, apiRoot, (c, ct) => SubmitRoundAsync(c, apiRoot, arguments, contextArgName: "message", participant: GetRequiredArg(arguments, "participant"), async: ParseAsyncArg(arguments), ct: ct), clock, backoff, extraRetryableCode: ParticipantUnreachableCode)
                };

                // Settlement-admission design (§3.2 G): the elapsed deadline is mapped HERE, at the
                // only place that builds tool results, into a normal MCP tool error carrying the last
                // coded rejection plus attempt/elapsed diagnostics. It must never escape as an
                // unhandled stdio fault, and must never turn a retryable busy into something a caller
                // reads as fatal — the guidance is explicitly "retry".
                if (sendResult is SettlementSendResult.DeadlineExhausted exhausted)
                    return BuildToolResult(id, FormatSettlementDeadlineError(exhausted), isError: true);

                using var postResponse = ((SettlementSendResult.Response)sendResult).Value;

                var postBody = await postResponse.Content.ReadAsStringAsync();

                if (postResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), isError: true);

                // Catalog-start protocol-v2 skew seam (404 means an old server, before any run
                // started) plus an explicit-vendor echo check once the route matched.
                var requestedVendor = NormalizeVendor(arguments?["vendor"]?.GetValue<string>());

                if (wasModelStart) {
                    // The model-bearing start went to the v3 route — its own protocol/ack gate REPLACES
                    // (and precedes) the vendor-override skew gate, which would misreport a v3 404 as a
                    // "protocol v2" skew. The model ack (applied_reviewer_model + equivalence key) is the
                    // authoritative MODEL echo here.
                    if (CheckReviewerModelResult(toolName, postResponse.StatusCode, postResponse.IsSuccessStatusCode, postBody, out var modelRunIdToClose) is { } modelCheck) {
                        // Only the 2xx-missing-ack case salvages a run id; the skew cases start nothing.
                        if (modelRunIdToClose is not null)
                            await BestEffortCloseAsync(client, apiRoot, modelRunIdToClose);

                        return BuildToolResult(id, modelCheck.Message, modelCheck.IsError);
                    }

                    // Valid MODEL ack — but the model/key are opaque and don't prove which VENDOR was
                    // applied. A model start always carries a vendor, so ALSO run the ordinal vendor-echo
                    // check (the same helper a v2 start uses — no vendor->model knowledge is introduced).
                    // A mismatch salvages + defensively closes the run and returns the error.
                    if (CheckVendorOverrideResult(toolName, requestedVendor, postResponse.StatusCode, postResponse.IsSuccessStatusCode, postBody, out var modelVendorRunIdToClose) is { } modelVendorCheck) {
                        if (modelVendorRunIdToClose is not null)
                            await BestEffortCloseAsync(client, apiRoot, modelVendorRunIdToClose);

                        return BuildToolResult(id, modelVendorCheck.Message, modelVendorCheck.IsError);
                    }
                    // Both acks valid — fall through to the shared success/round rendering below, which
                    // echoes the reviewer-model audit fields alongside the result.
                } else if (!wasDynamicStart && CheckVendorOverrideResult(toolName, requestedVendor, postResponse.StatusCode, postResponse.IsSuccessStatusCode, postBody, out var flowRunIdToClose) is { } vendorCheck) {
                    // Best-effort: we have the run id from this same response (echo mismatch only —
                    // the 404 case never has one) — close it defensively rather than leave a
                    // wrongly-vendored reviewer running unattended.
                    if (flowRunIdToClose is not null)
                        await BestEffortCloseAsync(client, apiRoot, flowRunIdToClose);

                    return BuildToolResult(id, vendorCheck.Message, vendorCheck.IsError);
                }

                // The saved-preference fallback. The server refuses a start it cannot resolve a
                // reviewer for (explicit → definition-authored → refuse); the LAST rung is local, so
                // a user who told us once which reviewer they want isn't asked again every run. It
                // runs at most ONCE per tool call and only for a refusal that provably started
                // nothing — see ShouldPreferenceRetry for why every conjunct of that gate matters.
                if (ShouldPreferenceRetry(toolName, wasDynamicStart, wasModelStart, requestedVendor, postResponse.IsSuccessStatusCode, postBody)) {
                    // Normalized exactly like an explicit argument would be: the canonical token is
                    // what the server echoes back, so a preference saved as "Codex" must not read as
                    // a vendor mismatch and close the run it just started. Blank is re-checked here
                    // rather than trusted from the accessor, because NormalizeVendor throws on one
                    // and an empty preference must degrade to "none saved", never to a crash.
                    var saved      = await reviewerVendorPreference();
                    var preference = string.IsNullOrWhiteSpace(saved.Vendor) ? null : NormalizeVendor(saved.Vendor);

                    if (preference is null)
                        return BuildToolResult(
                            id,
                            FormatFlowStartError((int)postResponse.StatusCode, postBody, wasDynamicStart)
                                + PreferenceMissingGuidance(saved.ProfileName),
                            isError: true);

                    // Injected as a real argument rather than passed alongside, so the retry is
                    // indistinguishable from an explicit request: StartFlowAsync sends it AND
                    // CheckVendorOverrideResult asserts the echo against it. arguments is non-null
                    // here — a null one throws out of the required-argument reads before any POST.
                    arguments!["vendor"] = preference;

                    // On the FIRST send's budget, not a fresh one: two 3-minute windows plus the poll
                    // cap would outlast the harness tool timeout, and the way that ends is the worst
                    // one available — this retry succeeds, a paid reviewer launches, the harness has
                    // already timed the call out, and the driver starts the flow a second time. With
                    // the budget shared, an exhausted window returns before POSTing at all.
                    var retryResult = await SendWithSettlementRetryAsync(
                        client, apiRoot,
                        (c, ct) => StartFlowAsync(
                            c, apiRoot, arguments, cwd, repoRoot, repoInfo,
                            kindArgName: toolName == "start_review_flow" ? "kind" : "definition_id",
                            requestingSessionId: requestingSessionId, ct: ct),
                        clock, backoff, budgetStartedAt: settlementStartedAt);

                    if (retryResult is SettlementSendResult.DeadlineExhausted retryExhausted)
                        return BuildToolResult(id, FormatSettlementDeadlineError(retryExhausted), isError: true);

                    using var retryResponse = ((SettlementSendResult.Response)retryResult).Value;

                    var retryBody = await retryResponse.Content.ReadAsStringAsync();

                    // Same ordering as the first POST: an expired token (the refresh retry inside the
                    // send already had its go) is an auth problem, not a vendor one — say so, rather
                    // than printing a raw HTTP 401 the caller would read as a flow rejection.
                    if (retryResponse.StatusCode == HttpStatusCode.Unauthorized)
                        return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), isError: true);

                    if (CheckVendorOverrideResult(toolName, preference, retryResponse.StatusCode, retryResponse.IsSuccessStatusCode, retryBody, out var retryRunIdToClose) is { } retryVendorCheck) {
                        if (retryRunIdToClose is not null)
                            await BestEffortCloseAsync(client, apiRoot, retryRunIdToClose);

                        return BuildToolResult(id, retryVendorCheck.Message, retryVendorCheck.IsError);
                    }

                    // Terminal either way: the retry is the whole budget. A failure that indicts the
                    // saved vendor itself gets the re-ask-and-re-save remedy; anything else surfaces
                    // as itself, because blaming the preference for an unrelated fault would send the
                    // user to change a setting that was never wrong.
                    if (!retryResponse.IsSuccessStatusCode) {
                        var stale = TryParseCodedError(retryBody, out var retryCode, out _)
                                    && StalePreferenceCodes.Contains(retryCode!);

                        return BuildToolResult(
                            id,
                            FormatFlowStartError((int)retryResponse.StatusCode, retryBody, wasDynamicStart)
                                + (stale ? StalePreferenceGuidance(preference, saved.ProfileName) : ""),
                            isError: true);
                    }

                    var (retryPayload, retryIsError) = await ResolveRoundResultAsync(client, apiRoot, retryBody, toolName, wasDynamicStart, clock, backoff, settlementStartedAt);

                    return BuildToolResult(id, $"{PreferenceAppliedPrefix(preference)}\n{retryPayload}", retryIsError);
                }

                if (!postResponse.IsSuccessStatusCode)
                    return BuildToolResult(id, FormatFlowStartError((int)postResponse.StatusCode, postBody, wasDynamicStart), isError: true);

                var (payload, isError) = await ResolveRoundResultAsync(client, apiRoot, postBody, toolName, wasDynamicStart, clock, backoff, settlementStartedAt);
                return BuildToolResult(id, payload, isError);
            }

            // `wait: true` blocks via bounded repeated GETs instead of the single GET below. Absent or
            // false never reaches this branch — that untouched single-GET path IS the backwards-compat
            // contract for every existing caller.
            if (toolName is "get_review_flow_status" or "get_flow_status" && ParseWaitArg(arguments)) {
                var waitFlowRunId = arguments?["flow_run_id"]?.GetValue<string>()
                    ?? throw new ArgumentException("Missing required argument: flow_run_id");
                var waitResult = await PollStatusUntilTerminalAsync(client, apiRoot, waitFlowRunId, toolName, clock, backoff);
                return BuildToolResult(id, waitResult.Payload, waitResult.IsError);
            }

            // Read-only availability lookup: reads GET /api/daemons and computes, client-side, which
            // reviewer vendors can actually run an unattended review for THIS repo (see
            // ReviewerVendorLookup). Its own branch — it hits /api/daemons, not a flow URL, and never
            // mutates anything.
            if (toolName is "list_reviewer_vendors") {
                // Surface a NON-sensitive repo id (owner/repo), never the local repoRoot path, which
                // must not leak to the model — repoRoot is used only for the on-disk hosting match.
                var repoIdentity = repoInfo is { Owner.Length: > 0, RepoName.Length: > 0 }
                    ? $"{repoInfo.Owner}/{repoInfo.RepoName}"
                    : null;
                // Read-only: never MachineId.Get() here — that would persist machine.json on first
                // use, breaking this tool's read-only/side-effect-free contract. A null id (no
                // machine.json yet) just drops the same-machine filter for this call.
                var machineId = new MachineId(config).ReadPersisted();

                // repo_unresolved is a local precondition AND the highest-precedence reason — settle it
                // before any network call, so an unresolved repo can never surface as an auth/lookup
                // error from GET /api/daemons instead of the contractual repo_unresolved result.
                if (string.IsNullOrEmpty(repoRoot))
                    return BuildToolResult(id, JsonSerializer.Serialize(
                        ReviewerVendorLookup.Aggregate(null, repoRoot, machineId, driverVendor, repoIdentity: repoIdentity),
                        McpJsonContext.Default.ReviewerVendorsResult));

                using var daemonsResp = await client.GetAsync(apiRoot + "/api/daemons");

                if (daemonsResp.StatusCode == HttpStatusCode.Unauthorized)
                    return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), isError: true);

                ReviewerVendorsResult result;
                if (!daemonsResp.IsSuccessStatusCode) {
                    result = ReviewerVendorLookup.Aggregate(null, repoRoot, machineId, driverVendor, repoIdentity: repoIdentity);
                } else {
                    var daemonsBody = await daemonsResp.Content.ReadAsStringAsync();
                    var (records, skipped, skew) = ReviewerVendorLookup.ParseDaemons(daemonsBody);
                    result = ReviewerVendorLookup.Aggregate(
                        records, repoRoot, machineId, driverVendor,
                        schemaSkew: skew, skippedRecords: skipped, repoIdentity: repoIdentity);
                }

                return BuildToolResult(id, JsonSerializer.Serialize(result, McpJsonContext.Default.ReviewerVendorsResult));
            }

            using var httpResponse = toolName switch {
                "get_review_flow_status" or "get_flow_status" => await client.GetAsync(BuildFlowUrl(apiRoot, arguments)),
                "close_review_flow"      or "close_flow"      => await client.PostAsync(BuildFlowUrl(apiRoot, arguments) + "/close", null),
                _                                             => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                // Decode coded envelopes here too (status/close previously printed the raw
                // body) — FormatFlowStartError falls back to the raw HTTP line for uncoded bodies.
                return BuildToolResult(id, FormatFlowStartError((int)httpResponse.StatusCode, body, wasDynamicStart: false), isError: true);
            }

            string statusPayload;

            if (toolName is "get_review_flow_status" or "get_flow_status") {
                statusPayload = FormatStatusResponse(body, out var pendingIds);

                // E-c: ack exactly the ids that were actually rendered into the text
                // above, after the text is fully built — never before, never a superset.
                var flowRunId = arguments?["flow_run_id"]?.GetValue<string>();
                if (flowRunId is not null)
                    await AckRenderedMessagesAsync(client, apiRoot, flowRunId, pendingIds, clock);
            } else if (toolName is "close_review_flow" or "close_flow") {
                // E-c: render pending_messages but never ack them — the server delivers
                // them atomically with the close, so there is nothing left to redeliver.
                statusPayload = FormatCloseResponse(body, out _);
            } else {
                statusPayload = FormatRoundResponse(body);
            }

            return BuildToolResult(id, statusPayload);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// The no-progress window: how long the start/submit POST lane keeps transparently retrying a
    /// settlement-layer coded 409 WITHOUT seeing the daemon's sequenced-lane watermark
    /// (<c>last_processed_seq</c> on the 409 body) advance, measured as ELAPSED time — including
    /// each request's own duration, not just the sum of the backoff delays. That distinction is the
    /// whole point: a settlement-aware server absorbs the wait by HOLDING the request open (up to a
    /// per-launch admission wait on the order of a minute), so a delay-only budget would let
    /// worst-case wall-clock blow past the MCP tool timeout the kcap plugin pins for its MCP servers
    /// (MCP_TOOL_TIMEOUT, 10 minutes) and surface as a harness-level timeout instead of a clean
    /// tool result. Three minutes fits roughly two full server-side admission waits plus backoff
    /// while staying far under that ceiling. If the harness pin ever changes, re-derive this.
    ///
    /// <para>Liveness-supervision spec §5: a retryable 409's <c>last_processed_seq</c> re-arms this
    /// window from the moment of that response when it is the FIRST seq observed, or STRICTLY higher
    /// than the previous one. An equal or lower seq is not progress (a frozen lane must still exhaust
    /// one full window after its single observation; a lower one is a daemon reconnect resetting its
    /// watermark, not a drain). A missing/null seq is "no evidence" — never a reset, and never a
    /// reason to tell the caller anything is out of date; it simply keeps the flat window. Clipped by
    /// <see cref="SettlementAbsoluteDeadline"/>.</para>
    ///
    /// <para>A tool call spends AT MOST ONE such window in total, which is what keeps that
    /// derivation valid: the one caller that sends twice (the preference fallback) threads
    /// <c>budgetStartedAt</c> so both sends share this budget rather than opening a second. The
    /// settlement lane and the round-poll lane that follows it likewise share ONE
    /// <see cref="ToolCallBudget"/> — a second independent window would push the call past the
    /// harness pin, and precisely in the shape that matters, with a paid reviewer launched by a POST
    /// whose result nobody is still waiting for.</para>
    /// </summary>
    internal static readonly TimeSpan SettlementElapsedDeadline = TimeSpan.FromMinutes(3);

    /// <summary>The hard absolute ceiling on the whole settlement-retry lane, measured from the
    /// FIRST attempt — continuous daemon-lane progress can keep resetting
    /// <see cref="SettlementElapsedDeadline"/>'s rolling window indefinitely, so this is what
    /// actually bounds that lane. It bounds the settlement lane ALONE; the end-to-end bound on a tool
    /// call is <see cref="ToolCallBudget"/>, which this must stay under.</summary>
    internal static readonly TimeSpan SettlementAbsoluteDeadline = TimeSpan.FromMinutes(8);

    /// <summary>The ONE end-to-end budget for a tool call that sends and then polls, anchored at its
    /// first POST attempt. The settlement lane (<see cref="SettlementAbsoluteDeadline"/>) and the
    /// round-poll lane (<see cref="PollCap"/>) run SEQUENTIALLY, so bounding them separately bounds
    /// the call at 8m + 8m against the ~10m MCP tool timeout the kcap plugin pins — the harness would
    /// kill the call mid-poll with the reviewer already launched and paid for. Sharing this budget
    /// means whatever settlement spends, the poll no longer has.
    ///
    /// <para>Applied as a CLIP on <see cref="PollCap"/>, never a replacement: a call whose settlement
    /// lane returned immediately (the overwhelming majority, and every existing fixture) is bounded by
    /// <c>PollCap</c> exactly as before. If the harness pin changes, re-derive this ONE value.</para>
    /// </summary>
    internal static readonly TimeSpan ToolCallBudget = TimeSpan.FromMinutes(9);

    static readonly HashSet<string> SettlementRetryableCodes =
        new(StringComparer.Ordinal) { "flow_settlement_busy", "reviewer_launch_incarnation_superseded" };

    /// <summary>The coded, eventually-retryable 409 a round-submit POST returns when a role's prior
    /// reviewer agent isn't durably proven absent yet (e.g. inactivity-stopped) — the server declares
    /// it retryable and converges once an attributable daemon unregister or the reconcile sweep
    /// proves the old agent gone. Passed as <see cref="SendWithSettlementRetryAsync"/>'s
    /// <c>extraRetryableCode</c> only by round-submit call sites, never start_review_flow/start_flow:
    /// the server can only return this for a PREVIOUSLY-ASSIGNED role with a completed settlement, a
    /// shape a start never has. Not in <see cref="SettlementRetryableCodes"/> — unlike those two, it
    /// carries no sequenced-lane watermark to observe progress from.</summary>
    internal const string ParticipantUnreachableCode = "participant_unreachable";

    /// <summary>Parses the coded-rejection envelope: a JSON object with a non-empty string
    /// "error" code and a string "message". Returns false for an uncoded/unparseable body.
    /// Shared by <see cref="FormatFlowStartError"/> and the settlement-retry gate below so both
    /// agree on what counts as "coded".</summary>
    internal static bool TryParseCodedError(string body, out string? code, out string? message) {
        code = null;
        message = null;

        try {
            var node = JsonNode.Parse(body) as JsonObject;
            if (node?["error"] is JsonValue ev && ev.TryGetValue<string>(out var c) && c.Length > 0
                    && node["message"] is JsonValue mv && mv.TryGetValue<string>(out var m)) {
                code = c;
                message = m;
                return true;
            }
        } catch (JsonException) {
            // not JSON — reads as uncoded
        }

        return false;
    }

    /// <summary>Parses the optional <c>last_processed_seq</c> a settlement 409 body may carry — the
    /// daemon's sequenced-lane watermark at rejection time. ABSENT (an old server) and PRESENT-but-JSON-
    /// null (a daemon that has never reported) both yield null, and callers must treat both the same
    /// way: "no progress evidence", never anything to warn the caller about.</summary>
    internal static long? TryParseLastProcessedSeq(string body) {
        try {
            if (JsonNode.Parse(body) is JsonObject node
                    && node["last_processed_seq"] is JsonValue v
                    && v.TryGetValue<long>(out var seq))
                return seq;
        } catch (JsonException) {
            // not JSON — no evidence either
        }

        return null;
    }

    /// <summary>
    /// Bounded, code-aware auto-retry for the two settlement-layer coded 409s a start/round POST
    /// can return: flow_settlement_busy (a settlement CAS append exhausted its own retry budget)
    /// and reviewer_launch_incarnation_superseded (the launch's incarnation was superseded by a
    /// concurrent settlement transition). Both are documented server-side as retryable: retrying
    /// the originating request re-resolves against the settlement layer's current state.
    ///
    /// For start_review_flow/start_flow, retrying re-POSTs the start — which mints a FRESH
    /// flow_run_id and abandons the superseded attempt (see StartFlowAsync), so it's
    /// unconditionally safe. For submit_review_round/send_to_participant, the server invariant
    /// that no path appends FlowRoleAgentAssigned/FlowRoundSubmitted/FlowIntentCompleted unless
    /// IsCurrentSettlementCompletion is true means a coded rejection here never recorded a round
    /// — so retrying the same flow_run_id can't double-submit.
    ///
    /// Only these two codes (via <see cref="TryParseCodedError"/>) trigger a retry, plus whatever
    /// single code the caller names in <paramref name="extraRetryableCode"/> (today only
    /// <see cref="ParticipantUnreachableCode"/>). Every other coded 4xx, and an uncoded failure,
    /// passes through untouched — an older server that has never heard of the extra code simply
    /// never sends it, so this is additive, not a capability negotiation.
    ///
    /// <para><see cref="ParticipantUnreachableCode"/> never carries a <c>last_processed_seq</c>, so
    /// the rolling no-progress window never re-arms for it and it always exhausts at the flat
    /// <see cref="SettlementElapsedDeadline"/> (3 minutes). That bound is load-bearing, not
    /// redundant with a faster server-side signal: the incarnation-cleared append that would let a
    /// retry succeed lands in the reviewer gateway / settlement service, not in the reconciler, so
    /// 3 minutes is sized to give that reconcile sweep (<c>FlowReconcilerService.SweepInterval</c>)
    /// one full cycle to prove the prior agent's absence.</para>
    ///
    /// <para>Bounded by <see cref="SettlementElapsedDeadline"/> rather than an attempt count, on the
    /// <see cref="SettlementBackoff"/> schedule, with the deadline's remaining time propagated as a
    /// per-request <see cref="CancellationToken"/> so a held POST is abandoned instead of
    /// overshooting. Each retried POST re-enters a fresh server-side admission wait, which is what
    /// lets a burst of concurrent launches against one daemon absorb serially.</para>
    ///
    /// <para>Returns a discriminated result: <c>Response</c> carries the live response (the caller
    /// owns it, exactly as before); <c>DeadlineExhausted</c> carries only the last observed coded
    /// error plus attempt/elapsed diagnostics — this helper disposes every superseded failing
    /// response, so an exhausted result never carries a live one. The deadline CTS is linked to the
    /// caller's token; in production that token is <see cref="CancellationToken.None"/> (the stdio
    /// loop has none), and caller-token cancellation is rethrown untouched — only this helper's OWN
    /// deadline firing produces <c>DeadlineExhausted</c>.</para>
    ///
    /// <para>Sits above the lane's own 401 recovery, which still applies on every attempt. All
    /// timing is injectable so unit tests run instantly on a virtual clock.</para>
    ///
    /// <para><paramref name="budgetStartedAt"/> lets a caller that sends TWICE within one tool call
    /// (the preference fallback: a refused vendor-less start, then one re-send naming the saved
    /// vendor) share ONE elapsed budget instead of opening a second full one. Two independent
    /// budgets would put the worst case at 3m + 3m of settlement plus the poll cap — past the
    /// harness MCP tool timeout this deadline was sized to stay under, with the specific hazard that
    /// the second POST succeeds (a run minted, a paid reviewer launched) after the harness has
    /// already given up, and the driver starts the flow again. Omitted, the budget starts now, which
    /// is every other caller's behavior unchanged.</para>
    /// </summary>
    internal static async Task<SettlementSendResult> SendWithSettlementRetryAsync(
            HttpClient                                                    client,
            string                                                        apiRoot,
            Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
            FlowRetryClock                                                clock,
            SettlementBackoff                                             backoff,
            CancellationToken                                             callerToken = default,
            DateTimeOffset?                                               budgetStartedAt = null,
            // A single additional coded 409 this call treats as retryable, alongside the two
            // settlement codes — see the class remarks above and ParticipantUnreachableCode's own
            // doc. Null (every caller except round-submit) preserves today's behavior exactly.
            string?                                                       extraRetryableCode = null
        ) {
        var startedAt         = budgetStartedAt ?? clock.UtcNow;
        var absoluteDeadline  = startedAt + SettlementAbsoluteDeadline;
        // The rolling no-progress deadline. Starts as the flat window from startedAt — identical to
        // today's behavior — and is pushed out (never past absoluteDeadline; see EffectiveDeadline)
        // only when a 409 carries a last_processed_seq STRICTLY greater than the one before it.
        var noProgressDeadline = startedAt + SettlementElapsedDeadline;
        // The most recently OBSERVED seq (whatever it was — a regression updates this too), so the
        // comparison is always "vs. the previous 409", never "vs. the historical high". Null means
        // no evidence has been seen yet; the transition out of null is itself progress (see below).
        long? lastSeq = null;

        string? lastCode    = null;
        string? lastMessage = null;

        DateTimeOffset EffectiveDeadline() => noProgressDeadline < absoluteDeadline ? noProgressDeadline : absoluteDeadline;

        for (var attempt = 1;; attempt++) {
            var deadline  = EffectiveDeadline();
            var remaining = deadline - clock.UtcNow;

            if (remaining <= TimeSpan.Zero)
                return new SettlementSendResult.DeadlineExhausted(lastCode, lastMessage, attempt - 1, clock.UtcNow - startedAt);

            using var scope = clock.CreateDeadline(remaining, callerToken);

            HttpResponseMessage response;
            try {
                response = await send(client, scope.Token);
            } catch (OperationCanceledException) when (scope.DeadlineFired && !callerToken.IsCancellationRequested) {
                // OUR deadline cut an in-flight attempt short — that is an exhausted budget, not a
                // failure to report. Caller cancellation deliberately falls through and rethrows.
                return new SettlementSendResult.DeadlineExhausted(lastCode, lastMessage, attempt, clock.UtcNow - startedAt);
            }

            if (response.IsSuccessStatusCode) return new SettlementSendResult.Response(response);

            // Responses arrive fully buffered (the default completion option), so this read can't
            // block on the network and needs no token of its own.
            var body = await response.Content.ReadAsStringAsync();

            // extraRetryableCode requires an actual 409 — the server only ever raises it as a
            // Conflict, so a same-coded body on another status must surface immediately.
            // SettlementRetryableCodes stays status-agnostic (pre-existing).
            if (!TryParseCodedError(body, out var code, out var message)
                    || !(SettlementRetryableCodes.Contains(code!)
                         || (code == extraRetryableCode && response.StatusCode == HttpStatusCode.Conflict)))
                return new SettlementSendResult.Response(response);

            lastCode    = code;
            lastMessage = message;
            response.Dispose();

            // Progress evidence re-arms the rolling window from THIS instant. Two cases qualify: the
            // FIRST seq ever observed (evidence just arrived — a first 409 that took 2m30s to come
            // back must not leave only 30s of window), and any STRICT increase over the previously
            // observed one. An equal seq (the lane is genuinely stalled, not merely reporting) or a
            // lower one (a restart/regression, most likely a daemon reconnect) is not progress and
            // leaves the window where it is — so a frozen lane still exhausts one full window after
            // its single observation. A missing/null seq never updates lastSeq at all, so an old
            // server (or one whose daemon has never reported) keeps the flat window from startedAt.
            var seq = TryParseLastProcessedSeq(body);
            if (seq.HasValue) {
                if (!lastSeq.HasValue || seq.Value > lastSeq.Value)
                    noProgressDeadline = clock.UtcNow + SettlementElapsedDeadline;

                lastSeq = seq;
            }

            var left = EffectiveDeadline() - clock.UtcNow;

            if (left <= TimeSpan.Zero)
                return new SettlementSendResult.DeadlineExhausted(lastCode, lastMessage, attempt, clock.UtcNow - startedAt);

            await clock.DelayAsync(backoff.Delay(attempt, left), callerToken);
        }
    }

    /// <summary>The outcome of <see cref="SendWithSettlementRetryAsync"/>: either a response the
    /// caller owns and disposes, or an exhausted elapsed deadline carrying only diagnostics. Closed
    /// hierarchy (private base constructor) so every consumer must handle both arms.</summary>
    internal abstract record SettlementSendResult {
        SettlementSendResult() { }

        internal sealed record Response(HttpResponseMessage Value) : SettlementSendResult;

        internal sealed record DeadlineExhausted(
            string? LastCode, string? LastMessage, int Attempts, TimeSpan Elapsed) : SettlementSendResult;
    }

    /// <summary>
    /// Pure decision for the protocol-v2 skew seam (404 = old server) + explicit-vendor echo check
    /// on a start response. Returns null to proceed normally after the route matches (no explicit
    /// vendor, or the vendor was echoed correctly); otherwise returns the tool error, with <paramref name="flowRunIdToClose"/>
    /// set (echo-mismatch case only) for the caller's best-effort close. Pure (no HttpClient) so it
    /// is unit-testable and the close side effect stays with the caller.
    /// </summary>
    internal static (string Message, bool IsError)? CheckVendorOverrideResult(
            string toolName, string? requestedVendor, HttpStatusCode statusCode, bool isSuccess, string postBody,
            out string? flowRunIdToClose
        ) {
        flowRunIdToClose = null;

        if (toolName is not ("start_review_flow" or "start_flow")) return null;

        // Primary seam: the versioned route either exists (server supports the feature) or
        // doesn't (clean 404, no run started, no agent launched — see StartFlowAsync's
        // route-selection logic).
        if (statusCode == HttpStatusCode.NotFound)
            return (
                "Error: this server does not support flow catalog-start protocol v2 — " +
                "upgrade the kcap server before starting a review flow.",
                true);

        if (requestedVendor is null) return null;

        // Defense in depth: the route existed (matched, non-404), so a run may already be
        // starting/started — assert the applied vendor actually matches what was requested.
        if (!isSuccess) return null;

        // Parse defensively: a malformed / non-object / wrong-typed body must NOT throw past this
        // method (the outer catch would turn it into a generic error and SKIP the close). Any
        // missing/invalid applied-vendor echo is treated as a hard mismatch; a valid flow_run_id is
        // still salvaged so the best-effort close can run.
        JsonObject? node = null;
        try { node = JsonNode.Parse(postBody) as JsonObject; } catch (JsonException) { /* leave null → mismatch */ }

        var applied = TryGetString(node, "applied_reviewer_vendor");

        if (string.Equals(applied, requestedVendor, StringComparison.Ordinal)) return null;

        flowRunIdToClose = TryGetString(node, "flow_run_id");

        return (
            $"Error: requested reviewer vendor '{requestedVendor}' but the server applied " +
            $"'{applied ?? "(none)"}' — closed the run defensively. This should not happen " +
            "when the versioned start route matched; please report it.",
            true);
    }

    /// <summary>
    /// The saved-preference retry trigger. A flow start is NON-IDEMPOTENT — an accepted POST mints a
    /// run and launches a paid reviewer — so the only retryable failure is one the server provably
    /// refused before doing anything: the STRUCTURED reviewer_vendor_required code, which the
    /// vendor-resolution ladder returns after exhausting explicit → definition-authored and before
    /// any run exists.
    ///
    /// <para>Every conjunct is load-bearing. A vendor-less AND model-less catalog start is the only
    /// shape that means "I never named a vendor": the v3 (model-bearing) route rejects a blank vendor
    /// with the SAME code, where retrying would let a saved preference shadow the vendor a pinned
    /// model belongs to. A dynamic (definition_yaml) start declares vendors per participant and
    /// rejects a top-level override outright. A non-start tool never resolves a reviewer at all.</para>
    ///
    /// <para>Ambiguity can never reach here: a timeout, a cancelled POST or a dropped connection
    /// produces no (status, body) pair — the settlement lane returns DeadlineExhausted and the
    /// caller has already returned, or the exception unwinds to the tool-error catch. What this
    /// function CAN see and refuses: success, any other code (server_catching_up,
    /// reviewer_vendor_unavailable, the send-surface reviewer_vendor_unresolvable, …), and uncoded
    /// bodies — including the text-prefixed 400s (no_daemon_available:, daemon_outdated:) that carry
    /// a code's words without its structure. Codes match ordinally and whole, never by prefix.</para>
    /// </summary>
    internal static bool ShouldPreferenceRetry(
            string toolName, bool wasDynamicStart, bool wasModelStart,
            string? requestedVendor, bool isSuccess, string body) =>
        toolName is "start_review_flow" or "start_flow"
        && !wasDynamicStart
        && !wasModelStart
        && requestedVendor is null
        && !isSuccess
        && TryParseCodedError(body, out var code, out _)
        && code == "reviewer_vendor_required";

    /// <summary>Appended to the server's own coded rejection when nothing is saved: the driver must
    /// ask a human rather than pick a reviewer itself — the whole point of the server refusing is
    /// that no one has said which vendor should review.
    ///
    /// <para>It names the profile this start actually consulted, because the two are resolved
    /// differently: the flows lane reads the profile the repo/URL/env resolution selected, while
    /// `kcap config set` writes to the config's ACTIVE profile. When they differ, a save the driver
    /// dutifully performs lands somewhere this lane never reads, and without the name in the message
    /// the symptom is a preference that "does not work" with nothing to look at.</para></summary>
    internal static string PreferenceMissingGuidance(string profileName) =>
        $"\nNo saved reviewer-vendor preference (profile: {profileName}). Ask the user which reviewer " +
        $"vendor to use ({ReviewerVendors.Tokens}), pass it as 'vendor', and offer to save it: " +
        "kcap config set flows.reviewer_vendor <vendor> — that writes to the ACTIVE profile, so if " +
        $"'{profileName}' is not the active one (check with kcap config show), the saved value will not " +
        "be read back here.";

    /// <summary>Appended when the retry's OWN failure says the saved vendor is the problem — the
    /// preference is stale (uninstalled, decertified, renamed), so re-asking and re-saving is the
    /// fix, not another retry. Names the consulted profile for the same reason as above: that is
    /// where the replacement has to land to be seen.</summary>
    internal static string StalePreferenceGuidance(string preference, string profileName) =>
        $"\nYour saved preference '{preference}' (flows.reviewer_vendor, profile: {profileName}) no longer " +
        "works — ask the user for a reviewer vendor and update it: " +
        $"kcap config set flows.reviewer_vendor <vendor>, in profile '{profileName}' — the one this start resolved.";

    /// <summary>Prefixed to a preference-retry success so the driver never reports a reviewer the
    /// user did not name in this conversation as though they had.</summary>
    internal static string PreferenceAppliedPrefix(string preference) =>
        $"reviewer vendor '{preference}' applied from your saved preference (flows.reviewer_vendor)";

    /// <summary>The retry's own coded failures that indict the SAVED vendor rather than the request:
    /// the vendor is not launchable on any eligible daemon, or the server does not know the token at
    /// all (a preference saved before a rename, or simply mistyped).</summary>
    static readonly HashSet<string> StalePreferenceCodes =
        new(StringComparer.Ordinal) { "reviewer_vendor_unavailable", "unknown_vendor" };

    /// <summary>What a preference lookup found, and the profile it looked in — the name travels with
    /// the value because every message built from a lookup needs it, including the one built when
    /// the value is null.</summary>
    internal readonly record struct SavedReviewerVendor(string? Vendor, string ProfileName);

    /// <summary>
    /// Reads the saved reviewer vendor FROM DISK, every time it is asked. Not via the profile
    /// deserialized at process start: `kcap mcp flows` is long-lived (the harness spawns it once per
    /// session and keeps it), while the `kcap config set` this feature's own guidance asks the driver
    /// to run is a different process writing that file. Against a start-time snapshot the
    /// ask-the-user-once loop would never close inside a session — refuse, ask, save, and the very
    /// next start still sees nothing and asks again.
    ///
    /// <para>Profile selection matches the rest of this process (the repo/URL/env resolution that
    /// picked the server this start is talking to), falling back to the config's active profile when
    /// a URL override made the resolver skip profile selection.</para>
    ///
    /// <para>Injectable at the dispatch seam (like the retry clock) because reading it for real would
    /// make a unit test depend on the developer's own config file; the production binding is covered
    /// end-to-end by the KCAP_CONFIG_DIR-isolated integration test.</para>
    /// </summary>
    async Task<SavedReviewerVendor> LoadReviewerVendorPreferenceAsync() {
        // Read from disk per consultation, not from the startup snapshot: this server outlives the
        // invocation that started it, and the error it returns TELLS the user to go and save this
        // very setting from another process. Which profile to read is still fixed at startup — only
        // the setting needs currency, and this is the only reader in the CLI that does.
        var current = await AppConfig.LoadProfileConfig(config);

        return new(
            current.Profiles.GetValueOrDefault(profiles.Name)?.EffectiveReviewerVendorPreference(),
            profiles.Name);
    }

    /// <summary>Reads a string property without throwing on a missing key, a null, or a
    /// wrong-typed (e.g. numeric) value — a wrong-typed applied-vendor echo must read as "no valid
    /// echo" (→ hard mismatch), never crash the defensive close path.</summary>
    static string? TryGetString(JsonObject? obj, string key) =>
        obj is not null && obj.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s)
            ? s
            : null;

    // The LOCAL reviewer-model transport errors. Both are self-inflicted (never a
    // server body) and both carry the reviewer_model_protocol_required code so a caller can react
    // to a single machine-readable signal, whether the skew showed up as a missing route or a
    // response that lacked the model ack. Neither ever downgrades to v2 — the request stays failed.
    internal const string ReviewerModelProtocolRequiredMessage =
        "Error (reviewer_model_protocol_required): this server does not support the reviewer model " +
        "override protocol (v3). Upgrade the kcap server, or omit 'model' to use the server's default " +
        "reviewer model. The request was NOT downgraded to an older protocol — no review flow was started.";

    internal const string ReviewerModelAckMissingMessage =
        "Error (reviewer_model_protocol_required): the server accepted the start but did not acknowledge " +
        "the reviewer model override (missing applied_reviewer_model / reviewer_model_equivalence_key) — " +
        "closed the run defensively. Upgrade the kcap server so the model override is honored.";

    // Coded rejections that mean the server can't speak the v3 model protocol (as opposed to a
    // genuine v3 rejection like reviewer_model_invalid / reviewer_model_unavailable, which must
    // surface verbatim). reviewer_model_protocol_required is what every non-v3 route returns when
    // it receives a Model; the two flow_client_protocol_* codes are the v3 route's own version guard.
    static readonly HashSet<string> ReviewerModelProtocolSkewCodes = new(StringComparer.Ordinal) {
        "reviewer_model_protocol_required",
        "flow_client_protocol_required",
        "flow_client_protocol_unsupported",
    };

    /// <summary>
    /// Pure decision for the reviewer-MODEL start response (the v3 route). Runs only
    /// for a model-bearing start (the caller gates on that). Returns null to proceed to normal
    /// rendering (success with a valid ack); otherwise the tool error, with
    /// <paramref name="flowRunIdToClose"/> set (the 2xx-missing-ack case only) for the caller's
    /// best-effort defensive close. Pure (no HttpClient) so the close side effect stays with the caller.
    ///
    /// <para>Old-server / skew mapping (→ <see cref="ReviewerModelProtocolRequiredMessage"/>, never a
    /// v2 retry): a 404/405 on the versioned route, or a coded body whose code is a protocol-skew
    /// code. A genuine coded v3 rejection OR an uncoded (legacy / 5xx / proxy) body returns null so
    /// the caller's <see cref="FormatFlowStartError"/> surfaces the real status/body verbatim.</para>
    ///
    /// <para>Ack validation on success requires a nonempty <c>applied_reviewer_model</c> AND
    /// <c>reviewer_model_equivalence_key</c> — it NEVER string-compares requested/applied/resolved
    /// (an alias validly resolves to a dated concrete id; the server already validated the
    /// equivalence key). A 2xx missing either field is a legacy/malformed ack →
    /// <see cref="ReviewerModelAckMissingMessage"/> + salvaged run id.</para>
    /// </summary>
    internal static (string Message, bool IsError)? CheckReviewerModelResult(
            string toolName, HttpStatusCode statusCode, bool isSuccess, string postBody,
            out string? flowRunIdToClose
        ) {
        flowRunIdToClose = null;

        if (toolName is not ("start_review_flow" or "start_flow")) return null;

        // Primary skew seam: the versioned v3 route either exists or doesn't. An old server returns
        // a clean 404 (or 405) before any handler runs — no run started, no agent launched.
        if (statusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            return (ReviewerModelProtocolRequiredMessage, true);

        if (!isSuccess) {
            // A coded protocol-skew rejection also means the server can't do v3; any OTHER coded
            // rejection is a genuine v3 verdict and passes through to FormatFlowStartError.
            if (TryParseCodedError(postBody, out var code, out _))
                return ReviewerModelProtocolSkewCodes.Contains(code!)
                    ? (ReviewerModelProtocolRequiredMessage, true)
                    : null;

            // Qodo #2: an UNCODED non-success body (e.g. a 5xx / proxy HTML-or-text error) is NOT an
            // old-server signal — only a clean 404/405 is (handled above). Return null so the caller's
            // FormatFlowStartError surfaces the real status/body, instead of masking a genuine
            // failure as a protocol-version skew.
            return null;
        }

        // Success: validate the model ack. Parse defensively — a malformed / non-object body must
        // not throw past this method (the outer catch would skip the defensive close).
        JsonObject? node = null;
        try { node = JsonNode.Parse(postBody) as JsonObject; } catch (JsonException) { /* leave null → treated as missing ack */ }

        var applied = TryGetString(node, "applied_reviewer_model");
        var key     = TryGetString(node, "reviewer_model_equivalence_key");

        if (!string.IsNullOrEmpty(applied) && !string.IsNullOrEmpty(key)) return null;

        // A 2xx that lacks the model ack fields is a legacy/malformed body — salvage the run id (if
        // any) so the caller can close defensively, and fail closed rather than render a
        // half-applied override.
        flowRunIdToClose = TryGetString(node, "flow_run_id");
        return (ReviewerModelAckMissingMessage, true);
    }

    /// <summary>Best-effort defensive close of a run a start response told us to
    /// abandon (a vendor echo mismatch, or a missing model ack). Bounded by its own short deadline —
    /// the shared flows client uses Timeout.InfiniteTimeSpan (flow starts long-poll), so an
    /// unbounded close would wedge the single-threaded stdio MCP loop and the error would never be
    /// delivered. Swallows every failure (incl. the timeout); the run still surfaces in the Flows
    /// tab / stale-reviewer sweep either way.</summary>
    static async Task BestEffortCloseAsync(HttpClient client, string apiRoot, string flowRunId) {
        try {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var closeResponse = await client.PostAsync(
                $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}/close", null, closeCts.Token);
        } catch {
            // best-effort (incl. the timeout above)
        }
    }

    /// <summary>
    /// Routes a review-flow start to the versioned start endpoint. §2.7 B3: a vendor-bearing,
    /// non-dynamic start POSTs to /api/flows/review/start/v4 (protocol 4, the resumable-park tier),
    /// falling back on HTTP 404 (old server) to /v3 when a model was given else /v2; a vendor-less or
    /// dynamic (definition_yaml) start keeps the pre-B3 route (/v2 or the legacy generic route). Shared
    /// by start_review_flow (reads the flow kind from the "kind" arg) and its generic alias start_flow
    /// (reads it from "definition_id" — the server treats kind == definition id, phase C).
    /// start_flow additionally accepts an inline "definition_yaml" (dynamic flows): the MCP
    /// schema can't express the xor, so exactly-one is enforced here, BEFORE any HTTP call;
    /// start_review_flow stays catalog-only (kind remains required there). Internal (not private)
    /// so unit tests can drive it directly against a WireMock stub.
    /// <para><paramref name="requestingSessionId"/> is supplied by the caller (resolved once in
    /// <see cref="RunAsync"/> from the running harness) and deliberately NOT read from the
    /// environment here — an inherited <c>KCAP_SESSION_ID</c> names the session that launched this
    /// one. It is a required parameter so no call site can acquire it ambiently by accident.</para>
    /// </summary>
    internal async Task<System.Net.Http.HttpResponseMessage> StartFlowAsync(
            HttpClient         client,
            string             apiRoot,
            JsonObject?        arguments,
            string             cwd,
            string?            repoRoot,
            RepositoryPayload? repoInfo,
            string             kindArgName,
            string?            requestingSessionId,
            CancellationToken  ct = default
        ) {
        string? kind;
        string? definitionYaml = null;

        if (kindArgName == "definition_id") {
            kind           = arguments?[kindArgName]?.GetValue<string>();
            definitionYaml = arguments?["definition_yaml"]?.GetValue<string>();

            if ((kind is null) == (definitionYaml is null))
                throw new ArgumentException(
                    "provide exactly one of definition_id (catalog flow) or definition_yaml (dynamic flow).");
        } else {
            kind = GetRequiredArg(arguments, kindArgName);
        }

        var targetKind   = GetRequiredArg(arguments, "target_kind");
        var targetRef    = GetRequiredArg(arguments, "target_ref");
        var targetTitle  = GetRequiredArg(arguments, "target_title");
        var context      = GetRequiredArg(arguments, "context");
        var instructions = arguments?["instructions"]?.GetValue<string>();
        var mode         = arguments?["mode"]?.GetValue<string>();
        var vendor       = NormalizeVendor(arguments?["vendor"]?.GetValue<string>());

        // An optional reviewer MODEL override. When present it forces the
        // protocol-v3 route (below) and is fail-closed locally BEFORE any HTTP call:
        //  - a model is meaningless without a vendor (the server's v3 route requires one, and the
        //    CLI never guesses a vendor from a model — there's no vendor→model table here), and
        //  - a model is structurally invalid for a dynamic (definition_yaml) flow, where every
        //    participant already declares its own model inline.
        // Both throw ArgumentException, which HandleToolCallAsync turns into a clean tool error
        // WITHOUT a POST (so a rejected pairing never mints a run and never retries v2).
        var model = NormalizeModel(arguments?["model"]?.GetValue<string>());

        if (model is not null) {
            if (definitionYaml is not null)
                throw new ArgumentException(
                    "a reviewer model override is not supported for dynamic (definition_yaml) flows — " +
                    "every participant already declares its own model in the embedded definition.");
            if (vendor is null)
                throw new ArgumentException(
                    "vendor is required when a reviewer model override is requested. " +
                    "Pass the lowercase canonical vendor token (e.g. 'claude', 'codex') the model belongs to.");
        }

        // B2: this machine's stable id, matched server-side against each connected daemon's
        // registration id to prove the reviewer would run on the SAME host as this requester. Same
        // call the daemon reports at registration (ServerConnection), so the ids are identical — the
        // last piece that lets the server pick the borrow path instead of a mirrored worktree.
        // requester_machine_id is optional on the wire: if resolving it throws (e.g. an unwritable
        // config dir on first-run create), degrade to null so the server just falls back to the
        // mirror rather than aborting the whole flow-start.
        string? machineId;
        try {
            machineId = new MachineId(config).Get();
        } catch (Exception e) {
            await Console.Error.WriteLineAsync(
                $"kcap mcp flows: could not resolve machine id ({e.Message}); starting review flow without requester_machine_id (server falls back to mirror)");
            machineId = null;
        }

        var body = new StartReviewFlowDto(
            Kind:                 kind,
            TargetKind:           targetKind,
            TargetRef:            targetRef,
            TargetTitle:          targetTitle,
            Context:              context,
            Instructions:         instructions,
            RequestingSessionId:  requestingSessionId,
            RequestingCwd:        cwd,
            RequestingRepoRoot:   repoRoot,
            RepoOwner:            repoInfo?.Owner,
            RepoName:             repoInfo?.RepoName,
            DaemonName:           null,
            RepoPath:             repoRoot,
            Mode:                 mode,
            Async:                true,
            RequesterMachineId:   machineId,
            DefinitionYaml:       definitionYaml,
            Vendor:               vendor,
            // The BASE-body protocol, used only on the pre-B3 fallback routes below: a reviewer-model
            // override is 3 (v3), every other catalog start is 2 (v2), a dynamic start sends none. The
            // §2.7 B3 primary attempt overrides this to 4 for the /v4 route (see the routing below).
            ClientFlowProtocolVersion: model is not null ? 3 : definitionYaml is null ? 2 : null,
            Model:                model
        );

        // §2.7 B3: a participant_parked-capable client routes every non-dynamic, vendor-bearing
        // start through /review/start/v4 (protocol 4), so the server returns the version-gated
        // `participant_parked` result when a resumable park crosses a round. An older server lacks the
        // route and 404s it → fall back to the pre-B3 endpoint (protocol 3/2), where a park reports the
        // legacy `participant_stopped`; both are resubmit triggers, so the crossed round never stalls
        // across the rollout. Only a 404 falls back — any other response (incl. a real 4xx) is returned
        // as-is, never a silent downgrade. A dynamic (definition_yaml) or vendor-less start keeps its
        // pre-B3 route (v4 requires a vendor).
        if (vendor is not null && definitionYaml is null) {
            var v4Resp = await client.PostAsync(
                $"{apiRoot}/api/flows/review/start/v4",
                JsonContent.Create(body with { ClientFlowProtocolVersion = 4 }, McpJsonContext.Default.StartReviewFlowDto),
                ct);
            if (v4Resp.StatusCode != System.Net.HttpStatusCode.NotFound) return v4Resp;
            v4Resp.Dispose(); // old server without /v4 — fall through to the pre-B3 route below
        }

        // Pre-B3 route selection (and the /v4 fallback), the server-capability signal that fails closed:
        //  - a reviewer-model override → POST /review/start/v3 (an old server 404s it — mapped to
        //    reviewer_model_protocol_required, never downgraded to v2);
        //  - every other catalog start → POST /review/start/v2 (protocol 2);
        //  - a dynamic (definition_yaml) start → the legacy generic route with its inline contract.
        var startPath = model is not null
            ? $"{apiRoot}/api/flows/review/start/v3"
            : definitionYaml is null
                ? $"{apiRoot}/api/flows/review/start/v2"
                : $"{apiRoot}/api/flows/review/start";

        return await client.PostAsync(
            startPath,
            JsonContent.Create(body, McpJsonContext.Default.StartReviewFlowDto),
            ct
        );
    }

    /// <summary>
    /// Posts to POST /api/flows/{id}/rounds. Shared by submit_review_round (reads the round
    /// context from the "context" arg, never sends a participant) and its generic alias
    /// send_to_participant (reads context from "message" and always sends a participant,
    /// which the server validates against the flow definition).
    /// </summary>
    static Task<System.Net.Http.HttpResponseMessage> SubmitRoundAsync(
            HttpClient  client,
            string      apiRoot,
            JsonObject? arguments,
            string      contextArgName,
            string?     participant,
            bool        async,
            CancellationToken ct = default
        ) {
        var flowRunId    = arguments?["flow_run_id"]?.GetValue<string>();
        var context      = GetRequiredArg(arguments, contextArgName);
        var instructions = arguments?["instructions"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(flowRunId)) {
            throw new ArgumentException(
                "Missing required argument: flow_run_id. " +
                "Pass the flow_run_id returned by start_review_flow or start_flow."
            );
        }

        var body = new SubmitReviewRoundDto(Context: context, Instructions: instructions, Async: async, Participant: participant);

        return client.PostAsync(
            $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}/rounds",
            JsonContent.Create(body, McpJsonContext.Default.SubmitReviewRoundDto),
            ct
        );
    }

    static string? NormalizeVendor(string? vendor) {
        if (vendor is null) return null;
        var normalized = vendor.Trim().ToLowerInvariant();
        if (normalized.Length == 0) throw new ArgumentException("vendor must not be blank.");
        return normalized;
    }

    /// <summary>Trims a reviewer model override, treating an absent (null) value as
    /// "no override". A present-but-blank value throws — the server would reject it as
    /// reviewer_model_required, so fail locally before the POST. Deliberately NOT case-folded and
    /// NOT provider-prefix-stripped: model ids are vendor-specific and case-sensitive (the server's
    /// ReviewerModelInput.Normalize does the authoritative hygiene — the CLI passes the value
    /// through so no vendor→model knowledge lives in the MCP layer).</summary>
    static string? NormalizeModel(string? model) {
        if (model is null) return null;
        var normalized = model.Trim();
        if (normalized.Length == 0) throw new ArgumentException("model must not be blank when provided.");
        return normalized;
    }

    static string BuildFlowUrl(string apiRoot, JsonObject? arguments) {
        var flowRunId = arguments?["flow_run_id"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required argument: flow_run_id");

        return $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}";
    }

    static readonly TimeSpan PollInterval   = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollCap        = TimeSpan.FromMinutes(8);   // safely below MCP_TOOL_TIMEOUT
    static readonly TimeSpan PerGetTimeout  = TimeSpan.FromSeconds(20);
    static readonly TimeSpan NotFoundGrace  = TimeSpan.FromSeconds(10);
    // E-c final review, Important: the shared client has Timeout = InfiniteTimeSpan (the
    // review-flow endpoints long-poll), so without a per-attempt bound a hung ack POST would block
    // indefinitely — stalling the tool response the driver is waiting on. Mirrors PerGetTimeout's
    // per-attempt bounding of PollUntilTerminalAsync's GETs.
    static readonly TimeSpan PerAckPostTimeout = TimeSpan.FromSeconds(15);

    static readonly HashSet<string> TerminalRoundStatuses =
        new(StringComparer.Ordinal) { "findings", "clean", "waiting", "unclear", "failed", "cancelled" };

    // Structured result from the poll path so callers can propagate isError correctly.
    record PollResult(string Payload, bool IsError);

    // Maximum consecutive transient failures (5xx / network / TLS) before giving up.
    const int MaxTransientRetries = 5;

    /// <summary> E-c: a multi-participant start records the run only — no round exists to
    /// poll, so the old poll path must not run. Returns null unless the body is exactly that shape
    /// (round-full starts and old servers fall through to the existing logic unchanged). Today's
    /// server never puts pending_messages on a round-less start (a brand-new run has no
    /// participants), but the path renders + exposes them anyway so every returned response obeys
    /// the same format-then-ack rule with no carve-outs (Qodo review on #278).</summary>
    internal static string? TryFormatRoundlessStart(string postBody, out IReadOnlyList<string> pendingIds) {
        pendingIds = [];

        try {
            var node = JsonNode.Parse(postBody)?.AsObject();
            if (node is null) return null;

            var flowRunId = node["flow_run_id"]?.GetValue<string>();
            var status    = node["status"]?.GetValue<string>();

            if (flowRunId is null || status != "running") return null;
            if (node["round_id"] is not null || node["round_number"] is not null) return null;

            var sb = new StringBuilder();
            sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
            sb.AppendLine("status: running");
            sb.AppendLine();
            sb.Append("Multi-participant flow started — no round is in flight yet. Address a role with " +
                      "send_to_participant(flow_run_id, participant, message); each role's agent launches " +
                      "lazily on its first message.");
            sb.AppendLine();
            AppendWorkspaceDiagnostics(sb, node);
            AppendBudgetDisclosure(sb, node);
            pendingIds = AppendPendingMessages(sb, node);
            return sb.ToString();
        } catch {
            return null;
        }
    }

    /// <summary>One canonical guidance line for the server's coded server_catching_up rejection,
    /// shared by every surface that renders it (start/submit/poll/status/close here, plus both
    /// sidecar branches in McpFlowResultServer) so the advice can never drift between tools.</summary>
    internal const string ServerCatchingUpGuidance =
        "The server is catching up after a read-model rebuild — try again in a few minutes, or ask the user what to do.";

    /// <summary>Renders an exhausted settlement elapsed deadline as tool-error text, in the same
    /// "Error (code): message" shape <see cref="FormatFlowStartError"/> uses for a coded rejection —
    /// the caller sees the real server code it kept hitting, plus how hard the CLI tried, plus the
    /// fact that retrying is still the right move. Only the POST lane can produce this; the poll
    /// lane has its own graceful-cap message and its own budget.</summary>
    internal static string FormatSettlementDeadlineError(SettlementSendResult.DeadlineExhausted exhausted) {
        var attempts = exhausted.Attempts == 1 ? "1 attempt" : $"{exhausted.Attempts} attempts";
        var elapsed  = FormatElapsed(exhausted.Elapsed);
        var detail   = string.IsNullOrWhiteSpace(exhausted.LastMessage) ? "" : $" Last server message: {exhausted.LastMessage}";

        // Distinct cause from the busy-lane wording below: this is absence-not-proven-yet, not
        // settlement contention.
        if (exhausted.LastCode == ParticipantUnreachableCode) {
            return $"Error ({ParticipantUnreachableCode}): gave up after {attempts} over {elapsed} — the " +
                   "server has not yet proven the previous reviewer agent for this role is gone. This is " +
                   "retryable: try again in a minute or two, once the daemon reports it absent or the " +
                   $"server's periodic sweep confirms it.{detail}";
        }

        // LastCode is null when the deadline cancelled an in-flight request before ANY coded response
        // was parsed — the very first attempt can time out. Claiming "the daemon is still settling"
        // there states a cause this client never observed, and printing the default code in the
        // Error(...) position reads as though the server sent it. Keep the code token stable (agents
        // match on it) but say plainly where it came from.
        var code  = exhausted.LastCode ?? "flow_settlement_busy";
        var cause = exhausted.LastCode is null
            ? "no coded response arrived before the deadline, so the code above is this client's " +
              "default rather than something the server reported"
            : "the daemon is still settling a prior launch and could not admit this one in time";

        return $"Error ({code}): gave up after {attempts} over {elapsed} — {cause}. This is retryable: " +
               $"try again in a minute, or check for another review flow already running against the " +
               $"same daemon.{detail}";
    }

    /// <summary>Compact, stable elapsed rendering for the deadline message (e.g. "3m", "2m 30s",
    /// "45s") — no locale- or tick-dependent formatting in a string an agent may match on.</summary>
    static string FormatElapsed(TimeSpan elapsed) {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        var minutes = (int)elapsed.TotalMinutes;
        var seconds = elapsed.Seconds;

        if (minutes == 0) return $"{Math.Max(seconds, 0)}s";

        return seconds == 0 ? $"{minutes}m" : $"{minutes}m {seconds}s";
    }

    /// <summary>Maps a non-2xx start/submit (or poll) response body to the tool error text.
    /// Status-agnostic contract (dynamic flows): ANY body carrying a string "error" code plus a
    /// "message" is a coded rejection from a dynamic-flows-aware server — surface the server
    /// message verbatim, prefixed with the code, and never add the old-server hint. Only an
    /// UNCODED failure on a start that included definition_yaml gets the "may not support
    /// dynamic flows" hint (the coded body is the new-server capability signal), keeping the
    /// raw body either way. Decodes via <see cref="TryParseCodedError"/>, the same parse
    /// SendWithSettlementRetryAsync's retry gate uses, so the two can never disagree about what
    /// counts as "coded".</summary>
    internal static string FormatFlowStartError(int status, string body, bool wasDynamicStart) {
        if (TryParseCodedError(body, out var code, out var message)) {
            if (code == "server_catching_up")
                return $"Error ({code}): {message}\n{ServerCatchingUpGuidance}";

            return $"Error ({code}): {message}";
        }

        var hint = wasDynamicStart
            ? "dynamic start failed — this server may not support dynamic flows (upgrade the server or use a catalog definition). "
            : "";

        return $"Error: {hint}HTTP {status} — {body}";
    }

    /// <summary>If the POST already carries a terminal result (old/blocking server), return it.
    /// Otherwise poll GET /api/flows/{id} until the started round is terminal.
    /// <paramref name="toolName"/> is the tool that initiated the round (one of
    /// start_review_flow/submit_review_round/start_flow/send_to_participant) — threaded through
    /// so the graceful-cap timeout message can point back at the matching status tool.
    /// <paramref name="toolCallStartedAt"/> is this call's single <see cref="ToolCallBudget"/> anchor
    /// (the instant before its first POST), so the poll lane shares that budget with the settlement
    /// lane rather than starting fresh. Null from a call site with no settlement lane in front of it,
    /// which then falls back to <see cref="PollCap"/> alone.</summary>
    async Task<PollResult> ResolveRoundResultAsync(HttpClient client, string apiRoot, string postBody, string toolName, bool wasDynamicStart, FlowRetryClock clock, SettlementBackoff backoff, DateTimeOffset? toolCallStartedAt = null) {
        if (TryFormatRoundlessStart(postBody, out var roundlessPendingIds) is { } roundless) {
            if (roundlessPendingIds.Count > 0 &&
                JsonNode.Parse(postBody)?.AsObject()?["flow_run_id"]?.GetValue<string>() is { } roundlessRunId)
                await AckRenderedMessagesAsync(client, apiRoot, roundlessRunId, roundlessPendingIds, clock);

            return new(roundless, false);
        }

        var node      = JsonNode.Parse(postBody)?.AsObject();
        var status    = node?["status"]?.GetValue<string>();
        var flowRunId = node?["flow_run_id"]?.GetValue<string>();
        var roundNum  = node?["round_number"]?.GetValue<int>();

        if (status != "running" || flowRunId is null || roundNum is null) {
            // terminal-in-POST (old server) or unparseable body.
            var formatted = FormatRoundResponse(postBody, out var pendingIds);

            // flowRunId may be null here (unparseable body) — nothing to ack against.
            if (flowRunId is not null)
                await AckRenderedMessagesAsync(client, apiRoot, flowRunId, pendingIds, clock);

            return new(formatted, false);
        }

        return await PollUntilTerminalAsync(client, apiRoot, flowRunId, roundNum.Value, toolName, wasDynamicStart, clock, backoff, toolCallStartedAt);
    }

    /// <summary>Tool family that started the round determines which status tool the graceful-cap
    /// message points callers back to: the review aliases (start_review_flow/submit_review_round)
    /// suggest get_review_flow_status; the generic tools (start_flow/send_to_participant) suggest
    /// get_flow_status. Both hit the exact same endpoint, so this only affects wording.</summary>
    static string StatusToolNameFor(string toolName) =>
        toolName is "start_review_flow" or "submit_review_round" ? "get_review_flow_status" : "get_flow_status";

    async Task<PollResult> PollUntilTerminalAsync(HttpClient client, string apiRoot, string flowRunId, int roundNumber, string toolName, bool wasDynamicStart, FlowRetryClock clock, SettlementBackoff backoff, DateTimeOffset? toolCallStartedAt = null) {
        var url                   = $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}";
        var pollStartedAt         = clock.UtcNow;
        // The poll lane's cap, CLIPPED to what remains of the shared ToolCallBudget. Absent an anchor
        // there was no settlement lane to share with, so PollCap stands alone.
        var pollLaneDeadline      = pollStartedAt + PollCap;
        var deadline              = toolCallStartedAt is { } anchor && anchor + ToolCallBudget < pollLaneDeadline
            ? anchor + ToolCallBudget
            : pollLaneDeadline;
        // Fix #3: anchor the 404 grace window to poll start, not to first-seen-404.
        var notFoundGraceDeadline = pollStartedAt + NotFoundGrace;
        var consecutiveTransient  = 0;
        string? lastTransientError;
        // Retry ordinal for the two settlement-layer coded 409s, feeding the shared jittered backoff
        // schedule — distinct from the network/5xx transient budget above, which uses the full 3s
        // PollInterval. Reset on any successful GET, so a late busy starts the schedule over.
        var settlementRetriesUsed = 0;

        while (clock.UtcNow < deadline) {
            using var getCts = clock.CreateTimeoutSource(PerGetTimeout);
            HttpResponseMessage resp;
            try {
                resp = await client.GetAsync(url, getCts.Token);
            } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                // Fix #4: count network/TLS/timeout as transient; stop after budget.
                consecutiveTransient++;
                lastTransientError = ex.Message;
                if (consecutiveTransient > MaxTransientRetries)
                    return new($"Error: poll failed after {MaxTransientRetries} consecutive network errors: {lastTransientError}", true);
                await clock.DelayAsync(PollInterval); continue;
            }

            using (resp) {
                if (resp.StatusCode == HttpStatusCode.NotFound) {
                    // Fix #3: 404 only gets the grace window anchored to poll start.
                    if (clock.UtcNow > notFoundGraceDeadline)
                        return new($"Error: flow_run_id {flowRunId} not found.", true);
                    await clock.DelayAsync(PollInterval); continue;
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new(await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), true);

                // Fix #4: non-transient 4xx (e.g. 400, 403, 409 budget_unverifiable) fail
                // immediately — coded bodies surface via FormatFlowStartError like the POST path.
                var statusCode = (int)resp.StatusCode;
                if (statusCode is >= 400 and < 500) {
                    var errBody = await resp.Content.ReadAsStringAsync();

                    // The same two settlement-layer coded 409s can also surface on the poll GET
                    // (the server-side backstop mapping an escaped settlement conflict). This lane
                    // shares the POST lane's jittered backoff SCHEDULE but keeps its own budget: the
                    // retry is bounded by the loop's remaining PollCap, not by an attempt count, and
                    // the policy delay is truncated so it can never overshoot that cap. Every other
                    // coded/uncoded 4xx is untouched.
                    if (TryParseCodedError(errBody, out var code, out _) &&
                            SettlementRetryableCodes.Contains(code!)) {
                        settlementRetriesUsed++;
                        await clock.DelayAsync(backoff.Delay(settlementRetriesUsed, deadline - clock.UtcNow));
                        continue;
                    }

                    return new(FormatFlowStartError(statusCode, errBody, wasDynamicStart), true);
                }

                // Fix #4: 5xx / other non-success counts toward the transient budget.
                if (!resp.IsSuccessStatusCode) {
                    consecutiveTransient++;
                    lastTransientError = $"HTTP {statusCode}";
                    if (consecutiveTransient > MaxTransientRetries)
                        return new($"Error: poll failed after {MaxTransientRetries} consecutive server errors: {lastTransientError}", true);
                    await clock.DelayAsync(PollInterval); continue;
                }

                // Successful response — reset transient counters.
                consecutiveTransient  = 0;
                lastTransientError    = null;
                settlementRetriesUsed = 0;

                var body      = await resp.Content.ReadAsStringAsync();
                var node      = JsonNode.Parse(body)?.AsObject();
                var rn        = node?["round_number"]?.GetValue<int>();
                var rs        = node?["round_status"]?.GetValue<string>();
                var runStatus = node?["status"]?.GetValue<string>();

                // Fix #1: run-level terminal stops the loop, but only return round result
                // when the projected round matches the one we submitted.
                if (runStatus is "closed" or "failed") {
                    if (rn == roundNumber && rs is not null && TerminalRoundStatuses.Contains(rs)) {
                        var formatted = FormatPolledRoundResult(node!, flowRunId, out var pendingIds);
                        await AckRenderedMessagesAsync(client, apiRoot, flowRunId, pendingIds, clock);
                        return new(formatted, false);
                    }
                    // Run became terminal before our round produced a result — explicit error.
                    return new($"Error: review run {runStatus} before round {roundNumber} produced a result.", true);
                }

                // Only act on OUR round; an earlier projection may still show a prior round.
                if (rn == roundNumber && rs is not null && TerminalRoundStatuses.Contains(rs)) {
                    var formatted = FormatPolledRoundResult(node!, flowRunId, out var pendingIds);
                    await AckRenderedMessagesAsync(client, apiRoot, flowRunId, pendingIds, clock);
                    return new(formatted, false);
                }
            }
            await clock.DelayAsync(PollInterval);
        }

        // Graceful cap reached — PollCap, or the shared ToolCallBudget if the settlement lane ate into
        // it (including the degenerate case where it left nothing, so the loop above never ran a single
        // GET). Either way the round is still legitimately running and this is not an error.
        var statusToolName = StatusToolNameFor(toolName);
        return new(
            $"Flow still running for flow_run_id {flowRunId} (round {roundNumber}). " +
            $"Call {statusToolName} to retrieve the result when ready.",
            false
        );
    }

    /// <summary>Implements `wait: true` on the two status tools (liveness design §6): repeated bounded
    /// GETs against the SAME endpoint the plain call hits, sharing <see cref="PollUntilTerminalAsync"/>'s
    /// per-attempt timeout, cadence, transient budget and 409 backoff, until the run is terminal, the
    /// current round reaches a <see cref="TerminalRoundStatuses"/> value, or <see cref="PollCap"/> elapses.
    ///
    /// <para>Deliberately NOT a reuse of <see cref="PollUntilTerminalAsync"/>: it must render through
    /// <see cref="FormatStatusResponse(string, out IReadOnlyList{string})"/> — the same envelope a <c>wait:false</c> call renders — never
    /// <see cref="FormatPolledRoundResult(JsonObject, string, out IReadOnlyList{string})"/>'s round-submission shape. Reusing it would make the tool's
    /// response shape depend on whether <c>wait</c> was set.</para></summary>
    async Task<PollResult> PollStatusUntilTerminalAsync(
            HttpClient client, string apiRoot, string flowRunId, string toolName, FlowRetryClock clock, SettlementBackoff backoff) {
        var url                   = $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}";
        var pollStartedAt         = clock.UtcNow;
        var deadline              = pollStartedAt + PollCap;
        var consecutiveTransient  = 0;
        string? lastTransientError;
        var settlementRetriesUsed = 0;

        while (clock.UtcNow < deadline) {
            using var getCts = clock.CreateTimeoutSource(PerGetTimeout);
            HttpResponseMessage resp;
            try {
                resp = await client.GetAsync(url, getCts.Token);
            } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                consecutiveTransient++;
                lastTransientError = ex.Message;
                if (consecutiveTransient > MaxTransientRetries)
                    return new($"Error: poll failed after {MaxTransientRetries} consecutive network errors: {lastTransientError}", true);
                await clock.DelayAsync(PollInterval); continue;
            }

            using (resp) {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new(await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot), true);

                var statusCode = (int)resp.StatusCode;
                if (statusCode is >= 400 and < 500) {
                    var errBody = await resp.Content.ReadAsStringAsync();

                    // Same settlement-layer coded 409s the round-submission poll lane retries
                    // transparently — this GET hits the identical endpoint, so the server-side
                    // backstop that can surface them there can surface them here too.
                    if (TryParseCodedError(errBody, out var code, out _) &&
                            SettlementRetryableCodes.Contains(code!)) {
                        settlementRetriesUsed++;
                        await clock.DelayAsync(backoff.Delay(settlementRetriesUsed, deadline - clock.UtcNow));
                        continue;
                    }

                    // Every other coded/uncoded 4xx (including 404) fails immediately — exactly
                    // what the plain wait:false status call already does for the same response.
                    return new(FormatFlowStartError(statusCode, errBody, wasDynamicStart: false), true);
                }

                if (!resp.IsSuccessStatusCode) {
                    consecutiveTransient++;
                    lastTransientError = $"HTTP {statusCode}";
                    if (consecutiveTransient > MaxTransientRetries)
                        return new($"Error: poll failed after {MaxTransientRetries} consecutive server errors: {lastTransientError}", true);
                    await clock.DelayAsync(PollInterval); continue;
                }

                consecutiveTransient  = 0;
                lastTransientError    = null;
                settlementRetriesUsed = 0;

                var body        = await resp.Content.ReadAsStringAsync();
                var node        = JsonNode.Parse(body)?.AsObject();
                var runStatus   = node?["status"]?.GetValue<string>();
                var roundStatus = node?["round_status"]?.GetValue<string>();

                var runTerminal   = runStatus is "closed" or "failed";
                var roundTerminal = roundStatus is not null && TerminalRoundStatuses.Contains(roundStatus);

                if (runTerminal || roundTerminal) {
                    var formatted = FormatStatusResponse(body, out var pendingIds);
                    await AckRenderedMessagesAsync(client, apiRoot, flowRunId, pendingIds, clock);
                    return new(formatted, false);
                }
            }
            await clock.DelayAsync(PollInterval);
        }

        // Genuine 8-min cap: the same benign text the round-submission poll lane returns, minus the
        // round number (a bare status wait has none pinned) — callers already treat this string as
        // benign, non-error "try the status tool again" guidance, per the backwards-compat design.
        return new(
            $"Flow still running for flow_run_id {flowRunId}. Call {toolName} to retrieve the result when ready.",
            false
        );
    }

    /// <summary>Formats the terminal GET /api/flows/{id} response into the same envelope+text as FormatRoundResponse.</summary>
    internal static string FormatPolledRoundResult(JsonObject node, string flowRunId) =>
        FormatPolledRoundResult(node, flowRunId, out _);

    /// <summary> E-c: id-exposing overload — see <see cref="AppendPendingMessages"/>.</summary>
    internal static string FormatPolledRoundResult(JsonObject node, string flowRunId, out IReadOnlyList<string> pendingIds) {
        var roundNumber = node["round_number"]?.GetValue<int>();
        var resultKind  = node["round_result_kind"]?.GetValue<string>() ?? node["round_status"]?.GetValue<string>() ?? "";
        var resultText  = node["round_result_text"]?.GetValue<string>();

        var sb = new StringBuilder();
        sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
        if (roundNumber.HasValue) { sb.Append("round_number: "); sb.AppendLine(roundNumber.Value.ToString()); }
        sb.Append("status: ");      AppendLine(sb, node["status"]?.GetValue<string>() ?? "");
        sb.Append("result_kind: "); AppendLine(sb, resultKind);
        if (TryGetString(node, "requested_reviewer_vendor") is { } requestedVendor) {
            sb.Append("requested_reviewer_vendor: "); AppendLine(sb, requestedVendor);
        }
        if (TryGetString(node, "applied_reviewer_vendor") is { } appliedVendor) {
            sb.Append("applied_reviewer_vendor: "); AppendLine(sb, appliedVendor);
        }
        if (TryGetString(node, "reviewer_vendor_source") is { } vendorSource) {
            sb.Append("reviewer_vendor_source: "); AppendLine(sb, vendorSource);
        }
        AppendReviewerModelAudit(sb, node);
        AppendWorkspaceDiagnostics(sb, node);
        AppendBudgetDisclosure(sb, node);
        // Before the result text: the driver should read the warning before the (suspect) result.
        AppendReviewerVendorMismatchWarning(sb, node);
        if (!string.IsNullOrEmpty(resultText)) { sb.AppendLine(); sb.Append(resultText); }

        pendingIds = AppendPendingMessages(sb, node);
        return sb.ToString();
    }

    /// <summary>
    /// Formats a ReviewFlowRoundResponse or ReviewFlowStatusResponse (from start/submit) into a
    /// compact envelope followed by the result text.
    /// </summary>
    internal static string FormatRoundResponse(string body) => FormatRoundResponse(body, out _);

    /// <summary> E-c: id-exposing overload — see <see cref="AppendPendingMessages"/>.</summary>
    internal static string FormatRoundResponse(string body, out IReadOnlyList<string> pendingIds) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) { pendingIds = []; return body; }

            var flowRunId   = node["flow_run_id"]?.GetValue<string>()   ?? "";
            var roundId     = node["round_id"]?.GetValue<string>()     ?? "";
            var status      = node["status"]?.GetValue<string>()      ?? "";
            var resultKind  = node["result_kind"]?.GetValue<string>()  ?? "";
            var resultText  = node["result_text"]?.GetValue<string>();
            var requestedVendor = TryGetString(node, "requested_reviewer_vendor");
            var appliedVendor = TryGetString(node, "applied_reviewer_vendor");
            var vendorSource = TryGetString(node, "reviewer_vendor_source");

            var sb = new StringBuilder();
            sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
            sb.Append("round_id: ");    AppendLine(sb, roundId);
            sb.Append("status: ");      AppendLine(sb, status);
            sb.Append("result_kind: "); AppendLine(sb, resultKind);
            if (requestedVendor is not null) { sb.Append("requested_reviewer_vendor: "); AppendLine(sb, requestedVendor); }
            if (appliedVendor is not null) { sb.Append("applied_reviewer_vendor: "); AppendLine(sb, appliedVendor); }
            if (vendorSource is not null) { sb.Append("reviewer_vendor_source: "); AppendLine(sb, vendorSource); }
            AppendReviewerModelAudit(sb, node);
            AppendWorkspaceDiagnostics(sb, node);
            AppendBudgetDisclosure(sb, node);

            if (!string.IsNullOrEmpty(resultText)) {
                sb.AppendLine();
                sb.Append(resultText);
            }

            pendingIds = AppendPendingMessages(sb, node);
            return sb.ToString();
        } catch {
            pendingIds = [];
            return body;
        }
    }

    internal static string FormatStatusResponse(string body) => FormatStatusResponse(body, out _);

    /// <summary> E-c: id-exposing overload — see <see cref="AppendPendingMessages"/>.</summary>
    internal static string FormatStatusResponse(string body, out IReadOnlyList<string> pendingIds) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) { pendingIds = []; return body; }

            var flowRunId      = node["flow_run_id"]?.GetValue<string>()      ?? "";
            var status         = node["status"]?.GetValue<string>()         ?? "";
            var definitionId   = node["definition_id"]?.GetValue<string>()   ?? "";
            var targetTitle    = node["target_title"]?.GetValue<string>()    ?? "";
            var roundCount     = node["round_count"]?.GetValue<int>();
            var lastResultKind = node["last_result_kind"]?.GetValue<string>();
            var lastResultText = node["last_result_text"]?.GetValue<string>();

            var sb = new StringBuilder();
            sb.Append("flow_run_id: ");   AppendLine(sb, flowRunId);
            sb.Append("status: ");        AppendLine(sb, status);
            sb.Append("definition_id: "); AppendLine(sb, definitionId);
            sb.Append("target_title: ");  AppendLine(sb, targetTitle);

            if (roundCount.HasValue) {
                sb.Append("round_count: ");
                sb.AppendLine(roundCount.Value.ToString());
            }

            var requestedVendor = TryGetString(node, "requested_reviewer_vendor");
            var appliedVendor = TryGetString(node, "applied_reviewer_vendor");
            var vendorSource = TryGetString(node, "reviewer_vendor_source");
            if (requestedVendor is not null) { sb.Append("requested_reviewer_vendor: "); AppendLine(sb, requestedVendor); }
            if (appliedVendor is not null) { sb.Append("applied_reviewer_vendor: "); AppendLine(sb, appliedVendor); }
            if (vendorSource is not null) { sb.Append("reviewer_vendor_source: "); AppendLine(sb, vendorSource); }
            AppendReviewerModelAudit(sb, node);
            AppendWorkspaceDiagnostics(sb, node);
            AppendBudgetDisclosure(sb, node);

            if (!string.IsNullOrEmpty(lastResultKind)) {
                sb.Append("result_kind: "); AppendLine(sb, lastResultKind);
            }

            if (!string.IsNullOrEmpty(lastResultText)) {
                sb.AppendLine();
                sb.Append(lastResultText);
            }

            AppendParticipants(sb, node);
            AppendReviewerVendorMismatchWarning(sb, node);

            pendingIds = AppendPendingMessages(sb, node);
            return sb.ToString();
        } catch {
            pendingIds = [];
            return body;
        }
    }

    static void AppendParticipants(StringBuilder sb, JsonObject node) {
        if (node["participants"] is not JsonArray participants || participants.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("participants:");
        foreach (var item in participants) {
            if (item is not JsonObject participant) continue;
            var role = StringField(participant, "role");
            var vendor = StringField(participant, "vendor");
            var model = StringField(participant, "model");
            var stopped = participant["stopped"] is JsonValue value && value.TryGetValue<bool>(out var b) && b;
            sb.Append("- "); sb.Append(role); sb.Append(": vendor="); sb.Append(vendor);
            sb.Append(" model="); sb.Append(model); sb.Append(" status=");
            sb.AppendLine(stopped ? "stopped" : "running");
        }
    }

    /// <summary>Warns when the "reviewer" participant's vendor disagrees with the run-level
    /// <c>applied_reviewer_vendor</c> in the same body, or the server set
    /// <c>reviewer_vendor_mismatch</c> (covers a lagged participant list; the local comparison
    /// covers older servers). A warning, not a tool error — the response still carries results
    /// and pending messages, and the driver decides whether to close.</summary>
    static void AppendReviewerVendorMismatchWarning(StringBuilder sb, JsonObject node) {
        var applied = TryGetString(node, "applied_reviewer_vendor");

        string? participantVendor = null;
        if (applied is not null && node["participants"] is JsonArray participants)
            foreach (var item in participants) {
                if (item is not JsonObject p) continue;
                if (!string.Equals(StringField(p, "role"), "reviewer", StringComparison.Ordinal)) continue;
                // A stopped entry is historical — a rotated-out reviewer must not raise a false
                // alarm against the ACTIVE vendor. The server flag still covers stopped-row
                // divergence it can see.
                if (p["stopped"] is JsonValue stopped && stopped.TryGetValue<bool>(out var s) && s) continue;
                var vendor = StringField(p, "vendor");
                // An absent vendor in a partial body is not evidence of disagreement.
                if (vendor.Length > 0 && !string.Equals(vendor, applied, StringComparison.Ordinal)) {
                    participantVendor = vendor;
                    break;
                }
            }

        var serverFlagged =
            node["reviewer_vendor_mismatch"] is JsonValue flag && flag.TryGetValue<bool>(out var b2) && b2;

        if (participantVendor is null && !serverFlagged) return;

        sb.AppendLine();
        sb.Append("⚠ reviewer vendor mismatch: ");
        sb.AppendLine(participantVendor is not null
            ? $"participant 'reviewer' is '{participantVendor}' but applied_reviewer_vendor is '{applied}'."
            : "the server flagged that the active reviewer's vendor disagrees with applied_reviewer_vendor.");
        sb.AppendLine("  A different reviewer than this run reported may be doing the work — treat its " +
                      "results as suspect: close the flow and report this.");
    }

    /// <summary>
    /// Formats a CloseReviewFlowResponse into a compact envelope.
    /// The server returns only <c>flow_run_id</c> and <c>status</c>.
    /// </summary>
    internal static string FormatCloseResponse(string body) => FormatCloseResponse(body, out _);

    /// <summary> E-c: id-exposing overload — see <see cref="AppendPendingMessages"/>.</summary>
    internal static string FormatCloseResponse(string body, out IReadOnlyList<string> pendingIds) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) { pendingIds = []; return body; }

            var flowRunId = node["flow_run_id"]?.GetValue<string>() ?? "";
            var status    = node["status"]?.GetValue<string>()      ?? "";

            var sb = new StringBuilder();
            sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
            sb.Append("status: ");      AppendLine(sb, status);
            // Close is often the LAST thing a caller reads, so it is the surface most likely to be
            // the only record of what the reviewer actually saw.
            AppendWorkspaceDiagnostics(sb, node);
            AppendBudgetDisclosure(sb, node);

            pendingIds = AppendPendingMessages(sb, node);
            return sb.ToString();
        } catch {
            pendingIds = [];
            return body;
        }
    }

    /// <summary> E-c: renders the fold-computed undelivered sidecar messages carried on a
    /// status/round/close response. Returns the rendered ids so the caller can ack exactly what
    /// the driver will actually see (never more).</summary>
    internal static IReadOnlyList<string> AppendPendingMessages(StringBuilder sb, JsonObject node) {
        if (node["pending_messages"] is not JsonArray arr || arr.Count == 0) return [];

        // Render entries into a scratch buffer first so the header count reflects what actually
        // got rendered, not the raw array length — a malformed (non-object) entry is skipped
        // below, and the header must not overcount past what the driver will see (E-c
        // final review, Minor: header used arr.Count while entries were filtered).
        var ids     = new List<string>();
        var entries = new StringBuilder();
        var count   = 0;

        foreach (var item in arr) {
            if (item is not JsonObject o) continue;

            // Type-safe extraction: a wrong-typed field degrades to "", never throws —
            // FormatPolledRoundResult has no exception boundary, so a throw here would turn a
            // successful terminal poll into a generic internal error (Qodo review on #278).
            var id   = StringField(o, "message_id");
            var from = StringField(o, "from_participant");
            var text = StringField(o, "text");

            entries.Append("- from "); entries.Append(from); entries.Append(" ["); entries.Append(id); entries.Append("]: ");
            entries.AppendLine(text);
            count++;

            if (id.Length > 0) ids.Add(id);
        }

        if (count == 0) return [];

        sb.AppendLine();
        sb.Append("pending_messages ("); sb.Append(count); sb.AppendLine("):");
        sb.Append(entries);

        return ids;
    }

    static string StringField(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Renders the reviewer-MODEL override audit trail (requested / applied /
    /// resolved model + the model source) when present, mirroring the vendor audit block. Each line
    /// is conditional so a legacy / vendor-only / no-override response renders nothing extra. It
    /// NEVER string-compares the three values — they legitimately differ (a requested alias resolves
    /// to a dated concrete id; the server already validated equivalence). They are surfaced for the
    /// caller to read, not to police.</summary>
    static void AppendReviewerModelAudit(StringBuilder sb, JsonObject node) {
        if (TryGetString(node, "requested_reviewer_model") is { } requested) { sb.Append("requested_reviewer_model: "); AppendLine(sb, requested); }
        if (TryGetString(node, "applied_reviewer_model")   is { } applied)   { sb.Append("applied_reviewer_model: ");   AppendLine(sb, applied); }
        if (TryGetString(node, "resolved_reviewer_model")  is { } resolved)  { sb.Append("resolved_reviewer_model: ");  AppendLine(sb, resolved); }
        if (TryGetString(node, "reviewer_model_source")    is { } source)    { sb.Append("reviewer_model_source: ");    AppendLine(sb, source); }
    }

    /// <summary>Renders the run's reviewer workspace decision. ONE helper, reached from every surface
    /// that formats a flow response, so no path can disagree with another about what the reviewer read.
    /// <para>An absent decision renders <c>unknown</c> and NEVER <c>borrowed</c> — guessing borrowed is
    /// the one wrong answer that reads as reassurance. Reasons render verbatim: nothing here
    /// enumerates them, so a reason added server-side reaches the user unchanged.</para></summary>
    static void AppendWorkspaceDiagnostics(StringBuilder sb, JsonObject node) {
        var mode = TryGetString(node, "workspace_mode");

        switch (mode) {
            case "borrowed":
                sb.AppendLine("workspace: borrowed (the reviewer saw this session's project directory in place, uncommitted changes included)");
                break;
            case "fallback": {
                var reason = TryGetString(node, "fallback_reason");
                sb.Append("workspace: fallback");
                if (reason is not null) { sb.Append(" ("); sb.Append(reason); sb.Append(')'); }
                sb.AppendLine();
                // Says only what is true for EVERY reason, and only about the WORKING TREE.
                //
                // Two earlier wordings were both wrong in the same direction — asserting more than the
                // CLI can know. "read a checkout at the last commit" is false for
                // context_only_requested (no repository is read at all). "did not see uncommitted
                // work" is ALSO false there: the caller may have inlined an uncommitted diff into the
                // submitted context, so the reviewer saw uncommitted work — just not from the tree.
                //
                // The only claim that holds for every reason is about the automatic channel: the tree
                // was not borrowed, so nothing uncommitted arrived that way. Naming reasons
                // individually would fix each case and reintroduce the allowlist this feature exists
                // without.
                sb.AppendLine("  ⚠ Your working tree was NOT borrowed — uncommitted changes were not " +
                              "included automatically. Anything uncommitted the reviewer saw came from " +
                              "the context you submitted.");
                break;
            }
            case null:
                sb.AppendLine("workspace: unknown");
                break;
            default:
                // An owned worktree, or a mode this build has not heard of. Report it verbatim rather
                // than collapsing it into one of the cases above — inventing a category is how a
                // caller ends up trusting a decision the server never made.
                sb.Append("workspace: "); AppendLine(sb, mode);
                break;
        }
    }

    /// <summary>Render the additive budget-enforcement disclosure a dynamic run carries.
    /// ABSENT (both keys omitted) on the wire for catalog / budget-irrelevant runs and against an
    /// older server — nothing is rendered, byte-identical to before. "partial" names the roles whose
    /// spend is rounds/time-governed rather than dollar-metered; "full" (or any future level) is
    /// reported verbatim.</summary>
    static void AppendBudgetDisclosure(StringBuilder sb, JsonObject node) {
        var enforcement = TryGetString(node, "budget_enforcement");
        if (enforcement is null) return;   // catalog / no dynamic budget / old server → render nothing

        if (enforcement == "partial") {
            sb.Append("budget enforcement: partial");
            if (node["unmetered_roles"] is JsonArray roles) {
                // Skip malformed/hostile elements (see IsRenderableRole); open the parenthetical
                // only once a valid role is found.
                var open = false;
                foreach (var role in roles) {
                    if (role is not JsonValue v || !v.TryGetValue<string>(out var name) || !IsRenderableRole(name))
                        continue;
                    sb.Append(open ? ", " : " (unmetered roles: ");
                    sb.Append(name);
                    open = true;
                }
                if (open) sb.Append(')');
            }
            sb.AppendLine();
        } else {
            sb.Append("budget enforcement: "); AppendLine(sb, enforcement);
        }
    }

    /// <summary>A role name is renderable only if it is non-blank and free of control characters —
    /// a server-supplied name with a newline/CR would otherwise forge lines in this line-oriented
    /// output.</summary>
    static bool IsRenderableRole(string name) {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var c in name) if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary> E-c: deliver-once ack for pending messages. Callers must invoke this
    /// AFTER the response text has been fully formatted, passing only the ids that were actually
    /// rendered into that text — never before, never a superset. No-op (no HTTP call at all) when
    /// <paramref name="messageIds"/> is empty, which keeps this byte-compatible with servers that
    /// predate the ack endpoint. Best-effort: one retry after 2s (on the injected <paramref name="clock"/>) on any
    /// failure (non-2xx or exception), then swallows and logs to stderr — the next status/round/
    /// close call will see the same messages still pending and re-render + re-ack them, so a lost
    /// ack only delays cleanup, it never drops a message.</summary>
    internal static async Task AckRenderedMessagesAsync(
            HttpClient            client,
            string                apiRoot,
            string                flowRunId,
            IReadOnlyList<string> messageIds,
            FlowRetryClock        clock
        ) {
        if (messageIds.Count == 0) return;

        var url  = $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}/messages/ack";
        var body = new AckFlowMessagesDto(messageIds);

        async Task<bool> TryPostAsync() {
            try {
                // E-c final review, Important: bound each attempt — the shared client has
                // no client-side deadline (Timeout = InfiniteTimeSpan, needed for the long-polling
                // round endpoints), so an unbounded ack POST could hang the tool response the
                // driver is waiting on. A timeout surfaces as OperationCanceledException, which
                // falls into the existing swallow-and-retry-once path below.
                using var postCts = clock.CreateTimeoutSource(PerAckPostTimeout);
                using var response = await client.PostAsync(
                    url, JsonContent.Create(body, McpJsonContext.Default.AckFlowMessagesDto), postCts.Token);
                return response.IsSuccessStatusCode;
            } catch {
                return false;
            }
        }

        if (await TryPostAsync()) return;

        await clock.DelayAsync(TimeSpan.FromSeconds(2));

        if (await TryPostAsync()) return;

        await Console.Error.WriteLineAsync(
            $"kcap mcp flows: failed to ack {messageIds.Count} rendered message(s) for flow_run_id {flowRunId}; will redeliver on next call.");
    }

    static void AppendLine(StringBuilder sb, string value) => sb.AppendLine(value);

    static string GetRequiredArg(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();
        return value ?? throw new ArgumentException($"Missing required argument: {name}");
    }

    /// <summary>
    /// Parses the optional "async" argument for send_to_participant. A missing key and an
    /// explicit JSON null both surface as a null JsonNode from the indexer (JsonNode has no
    /// "null" leaf type), so both default to true — matching submit_review_round's hardcoded
    /// Async: true. A JSON boolean is used as-is. Anything else (e.g. an LLM caller passing the
    /// string "yes") throws ArgumentException, which HandleToolCallAsync's catch turns into a
    /// clean tool error instead of an uncaught GetValue&lt;bool&gt;() crash.
    /// </summary>
    static bool ParseAsyncArg(JsonObject? arguments) =>
        arguments?["async"] switch {
            null                                              => true,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _                                                 => throw new ArgumentException("Invalid argument: async must be a boolean")
        };

    /// <summary>
    /// Parses the optional "wait" argument on get_review_flow_status/get_flow_status (§6 of the
    /// liveness design). A missing key and an explicit JSON null both default to false — the
    /// single-GET behavior every existing caller already gets, unchanged. A JSON boolean is used
    /// as-is; anything else throws ArgumentException, turned into a clean tool error by
    /// HandleToolCallAsync's catch, mirroring ParseAsyncArg's contract.
    /// </summary>
    static bool ParseWaitArg(JsonObject? arguments) =>
        arguments?["wait"] switch {
            null                                              => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _                                                 => throw new ArgumentException("Invalid argument: wait must be a boolean")
        };

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-flows", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    static string BuildErrorResponse(JsonNode id, int code, string message) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        };
        return envelope.ToJsonString();
    }

    static string ToResponse<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        };
        return envelope.ToJsonString();
    }

    internal static McpTool[] BuildToolsList() => [
        new(
            "start_review_flow",
            "Start a new review flow. This hands the work to a SEPARATE hosted reviewer agent and iterates to sign-off — it is NOT how you review something yourself. " +
            "COST: this spawns a PAID hosted reviewer (a real model running to completion), so only start one when a review flow is genuinely wanted. " +
            "Only call this when the user explicitly asked for a review *flow* / to submit for review; for an ordinary 'review my PR' or 'code review' request, review directly and do NOT call this tool. " +
            "Returns findings (same UX); the server runs the reviewer asynchronously and the CLI polls internally. " +
            "Returns a flow_run_id that identifies this review session — save it to call submit_review_round or get_review_flow_status later. " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["kind"]         = new("string", "Review flow kind. Valid values: 'spec-review' (for specs and design documents), 'code-review' (for code changes and PRs)."),
                    ["target_kind"]  = new("string", "What is being reviewed: 'pr', 'branch', 'file', 'spec', 'plan', etc."),
                    ["target_ref"]   = new("string", "A reference to the target (PR URL, branch name, file path, etc.)."),
                    ["target_title"] = new("string", "Human-readable title for the target (PR title, spec name, etc.)."),
                    ["context"]      = new("string", "Background context for the reviewer: what to focus on, constraints, definition of done. State where the changes live — the reviewer sees a mirror of THIS SESSION's project directory, not of the directory you are working in; if your changeset is elsewhere or incomplete there, say so, give an explicit commit range, and inline the relevant diffs. Whether it sees your UNCOMMITTED work is conditional: only when the run actually borrows your checkout. Responses report this as workspace: borrowed | fallback (<reason>) | unknown — for the reserved review aliases; other flow kinds report unknown. On fallback your checkout was NOT borrowed, so inline anything uncommitted that matters."),
                    ["instructions"] = new("string", "Optional additional instructions for the reviewer agent."),
                    ["mode"]         = new("string", "Optional. Pass 'context-only' to have the reviewer treat the submitted context/diff as authoritative rather than reading the repository. By default, on the same machine, it reviews a worktree mirrored from THIS SESSION's project directory — not from the directory you are working in — with uncommitted changes only when your checkout is borrowed."),
                    ["vendor"]       = new("string", "Optional reviewer vendor for the reserved alias, independent of the driver harness. Omit to use the flow definition's authored vendor; if the definition names none, your saved flows.reviewer_vendor preference is applied, else the server asks for one. The selected vendor must be installed and certified unattended on an eligible daemon; there is no silent fallback. Pass the lowercase canonical vendor token (e.g. 'claude', 'codex')."),
                    ["model"]        = new("string", "Optional reviewer model override for this review. REQUIRES 'vendor' — the model is interpreted against that vendor (there is no vendor->model table here), so passing model without vendor is rejected locally. Omit to use the vendor's default reviewer model. The chosen model must be resolvable and certified on the selected daemon; there is no silent fallback. Pass the vendor's own model id or alias verbatim (case-sensitive) — do not translate or guess it. Requires a server that supports the v3 flow-start protocol.")
                },
                ["kind", "target_kind", "target_ref", "target_title", "context"]
            )
        ),
        new(
            "submit_review_round",
            "Submit a follow-up round to an existing review flow. Returns findings (same UX); the server runs the reviewer asynchronously and the CLI polls internally. Use this to ask for clarifications, provide additional context, or request a re-review after addressing feedback. " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["flow_run_id"]  = new("string", "Flow run ID returned by start_review_flow."),
                    ["context"]      = new("string", "Updated context or response to the reviewer's previous findings."),
                    ["instructions"] = new("string", "Optional instructions for this round.")
                },
                ["flow_run_id", "context"]
            )
        ),
        new(
            "get_review_flow_status",
            "Get the current status of a review flow: running, waiting, completed, or failed. Also surfaces the last result kind and result text. " +
            "Long rounds are normal — a reviewer round can legitimately run well past a single check. " +
            "Optional wait: true blocks (bounded, internally retried GETs — never a raw long-poll) until the round is terminal or roughly 8 minutes pass, instead of returning the current snapshot immediately; on the 8-minute cap it returns the same benign still-running text as an unset/false wait, so re-enter with wait: true again rather than treating that as an error. " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_review_flow."),
                    ["wait"]        = new("boolean", "Optional, defaults to false. When true, block until the round is terminal or roughly 8 minutes elapse, instead of returning immediately.")
                },
                ["flow_run_id"]
            )
        ),
        new(
            "close_review_flow",
            "Close a review flow, marking it as complete. Call this after the review is done and the findings have been addressed. " +
            "The close response may carry final pending_messages — read them; they are delivered with the close and will not be shown again.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_review_flow.")
                },
                ["flow_run_id"]
            )
        ),
        new(
            "start_flow",
            "COST / EXPLICIT INTENT: call only after the user explicitly asks to start or run an agent flow. The participants execute paid hosted models. " +
            "Start a new agent flow from the server's flow-definition catalog (definition_id) or from an inline YAML definition (definition_yaml — dynamic flows). This hands the work to a SEPARATE hosted agent and iterates to sign-off — it is NOT how you do the work yourself. " +
            "Returns findings (same UX); the server runs the flow asynchronously and the CLI polls internally. " +
            "Returns a flow_run_id that identifies this flow run — save it to call send_to_participant or get_flow_status later. " +
            "Multi-participant definitions start round-less — the response carries no round; address each role with send_to_participant (roles launch lazily on first message). " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["definition_id"]   = new("string", "Flow definition id from the catalog (e.g. 'code-review', or a custom definition). Provide exactly one of definition_id or definition_yaml — never both, never neither."),
                    ["definition_yaml"] = new("string", "Inline flow-definition YAML document for a dynamic (non-catalog) flow — the full definition, same schema as catalog definitions. Provide exactly one of definition_id or definition_yaml — never both, never neither. Requires a server with dynamic flows enabled. Every participant MUST declare 'workspace: none' (the parser rejects a missing workspace) and a concrete model id (no 'default')."),
                    ["target_kind"]    = new("string", "What is being reviewed: 'pr', 'branch', 'file', 'spec', 'plan', etc."),
                    ["target_ref"]     = new("string", "A reference to the target (PR URL, branch name, file path, etc.)."),
                    ["target_title"]   = new("string", "Human-readable title for the target (PR title, spec name, etc.)."),
                    ["context"]        = new("string", "Background context for the agent: what to focus on, constraints, definition of done. State where the changes live — the participant sees a mirror of THIS SESSION's project directory, not of the directory you are working in; if your changeset is elsewhere or incomplete there, say so, give an explicit commit range, and inline the relevant diffs."),
                    ["instructions"]   = new("string", "Optional additional instructions for the agent."),
                    ["mode"]           = new("string", "Optional. Pass 'context-only' to have the agent treat the submitted context/diff as authoritative rather than reading the repository. By default, on the same machine, it works in a worktree mirrored from THIS SESSION's project directory — not from the directory you are working in — with uncommitted changes only when your checkout is borrowed."),
                    ["vendor"]         = new("string", "Optional reviewer vendor. Reserved spec-review/code-review aliases use it independently of the driver. Omit to use the flow definition's authored vendor; if the definition names none, your saved flows.reviewer_vendor preference is applied, else the server asks for one. Custom single-participant catalog definitions accept an explicit override. Rejected for multi-participant and definition_yaml flows. The selected vendor must be certified unattended on an eligible daemon; no silent fallback. Pass a lowercase canonical token."),
                    ["model"]          = new("string", "Optional reviewer model override for a single-participant catalog review definition. REQUIRES 'vendor' — the model is interpreted against that vendor (there is no vendor->model table here), so passing model without vendor is rejected locally. Rejected for definition_yaml (dynamic) and multi-participant flows. Omit to use the vendor's default reviewer model. The chosen model must be resolvable and certified on the selected daemon; there is no silent fallback. Pass the vendor's own model id or alias verbatim (case-sensitive) — do not translate or guess it. Requires a server that supports the v3 flow-start protocol.")
                },
                ["target_kind", "target_ref", "target_title", "context"]
            )
        ),
        new(
            "send_to_participant",
            "Send a follow-up message to a participant in an existing flow. Returns findings (same UX); the server runs the flow asynchronously and the CLI polls internally. Use this to ask for clarifications, provide additional context, or request a re-review after addressing feedback. " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["flow_run_id"]  = new("string", "Flow run ID returned by start_flow."),
                    ["participant"]  = new("string", "The participant role to send to, as declared by the flow definition's participants map (single-participant definitions use 'reviewer'). The server rejects an unknown role, naming the valid ones."),
                    ["message"]      = new("string", "Updated context or response to the participant's previous findings."),
                    ["instructions"] = new("string", "Optional instructions for this round."),
                    ["async"]        = new("boolean", "Optional. Defaults to true.")
                },
                ["flow_run_id", "participant", "message"]
            )
        ),
        new(
            "get_flow_status",
            "Get the current status of a flow run: running, waiting, completed, or failed. Also surfaces the last result kind and result text. " +
            "Long rounds are normal — a participant round can legitimately run well past a single check. " +
            "Optional wait: true blocks (bounded, internally retried GETs — never a raw long-poll) until the round is terminal or roughly 8 minutes pass, instead of returning the current snapshot immediately; on the 8-minute cap it returns the same benign still-running text as an unset/false wait, so re-enter with wait: true again rather than treating that as an error. " +
            "Responses may carry pending_messages — out-of-band notes from participants. React to each message_id ONCE, when first shown: a message normally never reappears, but a failed delivery acknowledgment redelivers it on a later call — never react to the same message_id twice.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_flow."),
                    ["wait"]        = new("boolean", "Optional, defaults to false. When true, block until the round is terminal or roughly 8 minutes elapse, instead of returning immediately.")
                },
                ["flow_run_id"]
            )
        ),
        new(
            "close_flow",
            "Close a flow run, marking it as complete. Call this after the work is done and the findings have been addressed. " +
            "The close response may carry final pending_messages — read them; they are delivered with the close and will not be shown again.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_flow.")
                },
                ["flow_run_id"]
            )
        ),
        new(
            "list_reviewer_vendors",
            "List the reviewer vendors that can ACTUALLY run an unattended review flow for THIS repo right now — installed and certified on a connected daemon that hosts this repository. " +
            "Read-only and side-effect-free: safe to call before offering a review, so you recommend a reviewer that will not be rejected. " +
            "Returns reviewers[] (each with the canonical lowercase vendor token; empty when none, and diagnostics.reason then names why: repo_unresolved | schema_skew | lookup_failed | no_daemons_connected | no_repo_hosting_daemon | no_unattended_reviewer), " +
            "driver_vendor (the harness running THIS session, or absent when it cannot be determined — treat absent as unknown, and do not claim a different model), " +
            "and diagnostics counts. This does NOT start a review — it only reports availability; use start_review_flow to run one. " +
            "Availability is a snapshot: a vendor listed here can still be rejected by start_review_flow if the daemon dropped in between.",
            new("object", new(), [])
        )
    ];
}

/// <summary>CLI-side DTO for POST /api/flows/review/start — mirrors the server's StartReviewFlowRequest fields.
/// Kind and DefinitionYaml are mutually exclusive (server-enforced too): a catalog start carries kind and
/// null-omits definition_yaml; a dynamic start carries definition_yaml and null-omits kind — the
/// WhenWritingNull context config keeps the absent one off the wire entirely, so catalog starts stay
/// byte-compatible with servers that predate dynamic flows.</summary>
record StartReviewFlowDto(
    [property: JsonPropertyName("kind")]                   string? Kind,
    [property: JsonPropertyName("target_kind")]            string  TargetKind,
    [property: JsonPropertyName("target_ref")]             string  TargetRef,
    [property: JsonPropertyName("target_title")]           string  TargetTitle,
    [property: JsonPropertyName("context")]                string  Context,
    [property: JsonPropertyName("instructions")]           string? Instructions,
    [property: JsonPropertyName("requesting_session_id")] string? RequestingSessionId,
    [property: JsonPropertyName("requesting_cwd")]         string? RequestingCwd,
    [property: JsonPropertyName("requesting_repo_root")]   string? RequestingRepoRoot,
    [property: JsonPropertyName("repo_owner")]             string? RepoOwner,
    [property: JsonPropertyName("repo_name")]              string? RepoName,
    [property: JsonPropertyName("daemon_name")]            string? DaemonName,
    [property: JsonPropertyName("repo_path")]              string? RepoPath,
    [property: JsonPropertyName("mode")]                   string? Mode,
    [property: JsonPropertyName("async")]                  bool    Async,
    [property: JsonPropertyName("requester_machine_id")]  string? RequesterMachineId = null,
    [property: JsonPropertyName("definition_yaml")]        string? DefinitionYaml = null,
    // Reviewer vendor override: optional, single-participant catalog flow kinds only. Omitted
    // (null) leaves the server's existing no-override behavior byte-identical on any server
    // version — see StartFlowAsync's route-selection logic, which only posts this alongside a
    // request to the versioned start route.
    [property: JsonPropertyName("vendor")]                 string? Vendor = null,
    [property: JsonPropertyName("client_flow_protocol_version")] int? ClientFlowProtocolVersion = null,
    // Reviewer MODEL override — optional, catalog single-participant review flow
    // kinds only, and ONLY ever sent alongside client_flow_protocol_version 3 to the /review/start/v3
    // route (see StartFlowAsync). Omitted (null via WhenWritingNull) keeps a no-model start
    // byte-identical to the v2 wire on any server version. A model requires a non-null Vendor
    // (StartFlowAsync rejects the pairing locally) and is invalid for dynamic (definition_yaml) flows.
    [property: JsonPropertyName("model")]                  string? Model = null
);

/// <summary>
/// CLI-side DTO for POST /api/flows/{flowRunId}/rounds — mirrors the server's SubmitReviewRoundRequest.
/// Participant is optional (D-b): the review alias (submit_review_round) always leaves it
/// null, which the WhenWritingNull context config omits from the wire entirely, keeping the alias
/// byte-compatible with servers that predate the field. The generic alias (send_to_participant)
/// always supplies it; the server validates it against the flow definition.
/// </summary>
record SubmitReviewRoundDto(
    [property: JsonPropertyName("context")]      string  Context,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("async")]        bool    Async,
    [property: JsonPropertyName("participant")]  string? Participant = null
);

/// <summary>CLI-side DTO for POST /api/flows/{flowRunId}/messages/ack — E-c deliver-once ack.</summary>
record AckFlowMessagesDto(
    [property: JsonPropertyName("message_ids")] IReadOnlyList<string> MessageIds
);
