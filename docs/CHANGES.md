# Change notes

Why each feature is the shape it is — the reasoning a reader would otherwise reconstruct from a
diff. `CLAUDE.md` holds the invariants; `docs/superpowers/specs/` holds the full designs.

Not release notes. Each entry is written as of the change that produced it and is not revised as the
code moves on; where an entry disagrees with the code, the code wins.

## The SessionStart index names the repo's projects

An agent can only land a memory at project scope by passing a slug, and nothing in a session told it
which project the cwd repo is in — so cross-repo learnings went to org, the only cross-repo scope
reachable without one. The index fragment now opens with a line per project naming that slug.

**The projects ride the index call, negotiated by `include=projects`.** The body was a bare array and
a CLI that predates this drops the whole fragment when it is not one, so the shape could not simply
change. A sibling endpoint or a superseding one costs a second round trip inside a hook budget that
is already tight on Cursor and Claude; a response header cannot carry a non-ASCII project name
without an encoding nobody else in the wire needs. The parameter costs nothing and degrades in both
directions: an older server ignores it and answers with the array, a newer one answers with an
object, and the CLI decides which it got by sniffing the opening token. Sniffing rather than
attempting the array parse matters — a failed deserialize is indistinguishable from a corrupt body,
which is retried, and an old server would never answer differently.

**The parameter goes out even with no repo resolved.** It declares that this CLI can read the object
body, not that a repo is in hand; making it conditional would leave the server answering one request
with two shapes.

**Projects alone are enough to emit a fragment**, where entries alone are not. A project holding no
memories yet is exactly the state the agent is being asked to fix, and it cannot without the slug.

## The flow can enable the daemon as a service

`kcap daemon service ensure` existed with no caller in the product. Making the browser able to ask for it
needed the ladder separated from the verb: `EvaluateEnsureAsync` decides and mutates, `Ensure` reports,
and both the terminal and the flow reach the machine through the one ladder — so the outcome a screen
renders cannot diverge from the outcome the verb would have printed.

**The ladder's vocabulary does not fit the wire, and the collapse is load-bearing rather than tidy.** The
flow's outcome and reason sets are closed and the server refuses an unrecognised token outright, after
which the report is retried for ever and the request stays outstanding — so a screen waits on an answer
that already happened. `EnsureFlowMap` collapses roughly twenty tokens into a handful under one rule:
`refused` means nothing was mutated, `failed` means a transaction ran and did not land, which is the
ladder's own attention-versus-failing-arm split.

Three rows are not what a first pass would write. **Viability refuses rather than fails** — it is proven
before anything destructive, so routing it to `failed` would claim a transaction that never ran and offer
a retry against an unusable pinned URL that cannot succeed. **`package_inconsistent` is keyed on the
reason and never on the exit**, because the start gate raises the same token as the viability abort, and
keying on the exit puts a retry button on a broken install. And **`plain_failure` is not "busy"**: off
launchd, exit 1 is lock contention *or* a manager error, and only one of those retries into working.

**`installed` and `started` collapse; `verified` does not.** No copy distinguishes install from start, and
`already_enabled` already separates "nothing needed" from "we did something" — but only launchd runs the
verified transaction, so off it nothing proves the daemon came up and answered. Two success tokens keep
"reachable now" a claim the ladder is willing to make.

**Nothing is reported about the service, and that is not a simplification.** The offer was going to be
conditioned on whether the daemon already ran as a service, reported on the flow create. But the create
runs before login selects a tenant, and the service *id* comes from the daemon name that setup does not
collect until after the browser leg — so the fact would have been a claim about a service whose identity
is chosen later. There is no point in the flow at which it can be made honest. The offer is therefore
unconditional and the ladder decides at action time, where the profile is known: someone who already has
it gets `already_enabled`, which reads honestly, and someone running `setup` has very likely not set it
up.

**The wait spinner comes down for this capability and not for the shim.** The shim prompts through
osascript and writes nothing; the verify transaction writes its coded lines, and two live renderables
cannot share a console — the same reason the import takes it down.

**And it is performed after setup's own steps, not in the browser leg's poll loop.** A unit bakes the
profile name, the expected server URL and the daemon name; the leg runs before the step that commits any
of them. Acting there installs a unit for the profile the process started with — the wrong server after a
login that adopted another tenant, or a service named for a daemon the user is about to rename, leaving
the one they do use uninstalled. So the leg does not advertise the capability at all (an unadvertised one
is left outstanding rather than reported, which is exactly the "asked, waiting" state the screen already
renders), and the request is finished afterwards against the profile setup just *wrote* rather than a
re-resolution of it — CLI, environment and repository precedence can each land somewhere else. The
report is retried a bounded number of times there, because unlike the poll loop there is no next tick.

**The deferred client's auth status is checked, not assumed.** The client factory hands one back whether
or not the stored token is usable — that is what its `AuthStatus` return is for — and an expired or
missing token leaves a client whose poll 401s, answering an empty body that reads as nothing having been
asked. So an unchecked status reproduces the silent no-op a raw `HttpClient` would. An unusable status names the
recovery instead, which by then is `kcap login` and the verb itself: the browser is gone and nothing will
retry the request.

**`NoAuthRequired` is usable and runs it**, unlike in the browser leg, which skips there because it has
nothing left to do. Treating the two the same would leave the request outstanding on a server that would
have answered it.

## Service install derives the Antigravity ADC trio

Hosted `agy` runs under a per-launch isolated `HOME`, which hides both the interactive OAuth login and
ADC's well-known location — so the daemon can only authenticate it through three unit-carried variables,
and an operator with a fully working interactive `agy` still saw `antigravity_reviewer_auth_unavailable`
after every reinstall because two of the three were exported nowhere. Install now completes the trio
silently, the same capture every other key gets: the credential path from ADC's well-known file (only when
it exists), `AGY_ADC_AUTH=1` riding the credential (the flag alone is a broken half-configuration), the
project from the environment or gcloud's active config. Exported values always win.

Two boundaries hold. Derivation lives in the installer the operator is running, never the daemon — the
daemon still reads no credential location of its own accord. And the flag lands in the UNIT only: exported
interactively, `AGY_ADC_AUTH=1` disables agy's hook capture, so the shell is exactly where it must not go.

## The import outcome reaches the first-run flow

The flow's `import-outcome` route folded a report into `FirstRunFlowState.ImportOutcome` and had no
caller, so the Done screen's counts caption was unreachable — and since the outcome is also the signal
that the run finished, the screen could not tell a working import from a stalled one.

`FinalCounts` already carried the route's three counts, so nothing is re-derived. Two things it did not
carry: a session the `--private` preflight held back never reaches the upload and so appears in none of
the three, and folds into `failed` (re-running retries exactly it); and a pass that threw leaves its
sessions unaccounted, which **three counts cannot express**, so nothing at all is reported for that run.
Sending the surviving pass's figures would state a clean import over a run that lost one.

**Null is not `(0,0,0)`.** Three zeroes are also a clean run over an already-loaded history, so a refusal
carries a coded token — `no_readable_agents`, or `decision_unreadable` — and the server rejects a token
on an outcome that moved something.

**An empty answer has two causes and only one is a decline.** `Choices` is empty both when the user asked
for nothing and when every level in the decision was unreadable here; `IsDecline` already drew that line,
and reporting the second as a clean zero would tell the screen "you chose not to" about a user who chose
otherwise. Relatedly, `HandleImport` had three "found nothing" exits that returned 0 without reaching
`onFinished`, so a clean run over an out-of-scope selection looked like a lost pass and reported nothing —
they now report a measured zero, the rule the discovery report in the same file already followed.

The retry is **not** credited against the poll budget, unlike the scan and the import. It runs on every
tick for as long as the report is refused, so crediting it would let a server that never accepts it
stretch the flow's own 30-minute backstop into hours.

That second token had no producer, and the branch that should raise it was worse than silent: a decision
naming a window this build cannot map returned early **without stamping the cursor**, re-evaluating the
same answer on every tick for the life of the flow. Polling cannot make a newer server's vocabulary
readable, so it is reported and the cursor moves.

`decided_at` cannot be wrong inside the lane — the answer is built from the view's own stamp, so they are
one value. The reachable hazard is the retry: the report is held across ticks, and `DeliverOutcomeAsync`
takes no view, so it cannot re-stamp a held report with whatever is standing.

**Retrying a retryable status is opt-in.** `SendWithRetryAsync` retried transport faults but returned any
completed response, so one 503 cost a session with no second attempt while an unreachable server got
thirty seconds of trying — which is what made `failed` too weak to show. It is now retried like a
transport fault (408, 429, 5xx; `Retry-After` honoured, capped by the remaining budget) but only where the
call site asks. Every hook, watch, daemon and MCP path shares that helper and their budgets assume one
attempt; changing it for all of them in service of one caption is not the trade. An exhausted budget
returns the status rather than throwing — the call sites catch `HttpRequestException` only, so a throw
would turn a 503 into a crash mid-import.

## Secret redaction is structural

`SecretRedactor.RedactLine` walks the line token by token and rewrites one JSON value at a time.
Scanning the serialized line cannot be made safe: `AuthHeaderRegex`'s value class excludes `"` and
`\` but not `{`, `}`, `[`, `]`, `,` or `:`, so a header-named key carrying an object or a number had
the match run past the value and swallow the structure after it. The server drops a line it cannot
parse without saying so, which is what made the damage invisible.

Decoding first keeps a serialized tool result carried as a string in scope, and lets the key
vocabularies run against a real JSON property name — which no text pattern can see, the key and the
value being separate tokens. A secret-bearing key arms every leaf beneath it, so
`{"auth":["b1","b2"]}` redacts both elements and keeps the array. Numbers are exempt whatever the
key: the keyword vocabulary matches anywhere in a name, so `token_count` and `input_tokens` — read
as metrics, and present on nearly every model turn — would otherwise be rewritten. The all-digit
credential is the deliberate price, and a header value arrives as a string. A name that is itself a
credential is replaced outright and numbered, since two siblings sharing one marker would collide
into a duplicate key.

Every token goes straight back out through a `Utf8JsonWriter`, so a mangled document is not
representable. The reader's depth limit is System.Text.Json's own ceiling, which is also the
writer's: anything the reader accepts the writer can emit, leaving the whole-line pipeline only
input no reader would take — where re-checking the result would mean re-parsing what just failed to
parse. A comment is dropped rather than re-emitted, since strict JSON has none; the drop counts as
a change on its own, or a line whose values are clean would ride the unchanged path still carrying
it.

A line whose values all survive is handed back as it arrived rather than as the writer re-encoded
it, so the common case reaches the wire byte for byte. Once anything is redacted that no longer
holds: the whole line is the writer's, escaping and spacing normalised.

## The Agents screen's visibility answer reaches the profile

The flow asked who may read future sessions, recorded it on `FirstRunAgentsDecidedEvent`, served it on
the poll as `default_visibility` — and no CLI read it. The field was absent from the wire models
entirely, so it was dropped at deserialisation and `kcap setup`'s step 3 prompted unconditionally and
wrote its own answer over it. The one place in the flow that asked a question and discarded the answer.

It rides the Agents decision, so it is read off the same answer and gated the same way, and it is
validated against `AppConfig.ValidVisibilities` rather than forwarded: the value lands in profile
config and is stamped on every session afterwards, so a stop a newer server invented would be written
to a file this build owns and read back by something that may not mean the same by it. A dropped value
degrades to null, which leaves the profile as it was — the same outcome as never having asked.

**The two nulls are not the same.** The field is null both when the step is unanswered and when it was
answered and left unset, and only the first should reach the prompt: the prompt's cursor starts on
`org_public`, so a Return on a re-run would widen an existing `private` on a question the user had
already answered. An answered-but-unset screen therefore re-writes what the profile already holds,
which is the lane's contract for a null answer and a no-op for everything downstream. Whether the step
settled is what separates them, and `SetupCommand.DecideVisibility` is the one place that decides.

Declining every harness while still choosing an audience is coherent, so `IsDecline` says nothing about
the visibility. No precedence question against `--default-visibility` arises: that flag is read only
under `--no-prompt`, where the browser leg never runs.

## `--private` stamps a value

An omitted `default_visibility` is not "no default": the server's generated column reads
`COALESCE(default_visibility,'org_public') = 'org_public'`, so a session-start that says nothing
lands as `default:org` — a class two `VisibilitySql` arms admit, one of them provider-independent.
Six of the nine import sources omitted the field under `--private` and left privacy to the closing
`SetVisibilityNoneForAll` pass, which meant minutes of org-visibility on a large import and
permanent exposure for any session whose PUT failed, since those failures are swallowed by design.
The other three stamped `"private"` in their own payload builder, which is why checking one source
found it correct.

`ImportContext.VisibilityStampFor(status)` is now the only place that decides the stamp, and the
chain path resolves the same rule into `chainDefaultVisibility`. The Step-3 default lands on `New`
alone, while `private` is sent on every status because it costs nothing.

**A stamp only decides visibility at creation.** The read model's import-overlap branch — the one a
re-import of an already-closed session takes — omits `default_visibility` from its update, so
re-asserting `private` on a session that already exists is discarded. For anything a run merely
revisits, the closing `visibility=none` pass is the only mechanism, which is why membership in it is
now the in-scope classification set rather than whatever the import concluded: `importedSessionIds`
gains a session only where new work happened, and `privateScopeSessionIds` excludes Copilot, Kiro, Pi
and OpenCode, so a failed routed replay or a chain resume whose session-end POST failed was
privatised by nothing. The bound is status — the scope filter runs before classification and an
excluded source has its status flipped — so `New | Partial | AlreadyLoaded` is the selected-and-
present set and a too-short session is left alone.

**And it happens before the content, not after.** A closing pass guarantees a revisited session ends
up owner-only; it does not stop what this run uploads into it being readable meanwhile, which is the
window the defect is named after. So the in-scope `Partial` and `AlreadyLoaded` sessions are narrowed
ahead of both import phases — `New` is excluded, having nothing to narrow and no row to name — and the
closing pass becomes recovery for a session created during the run.

That pass is fail-closed per session: the write logs and swallows its failures, so a session it could
not narrow is dropped from `chains` and `routed` and counted as a failure, rather than replayed into
while still carrying the audience the user just excluded.

The 2026-07-20 unified-import spec scoped this expansion out while already arguing that post-hoc
privatisation is unsafe for a session that fails mid-stream; this is that argument applied to the
eight other paths.

## The first-run flow's import lane

`kcap setup`'s browser leg now feeds and reads the Import screen. Discovery reports per repository AND
per window, because "how many sessions will this selection import" is a cell and neither margin of a
table gives you one; `ImportDiscoverySummary` buckets both from one pass, and windows are keyed off the
same constant the report travels under, so `--discover`'s own windows and the screen's picker are one
list.

The vendor filter is applied to the sources scanned rather than to the counts afterwards, which is what
makes every reported figure already scoped. **Only an explicit refusal drops a vendor:** the server
normalises an untouched harness out of the decision, so refused and never-offered look identical on the
wire, but this machine knows what it reported — `FirstRunMachineReport.Detected` is the set the screen
could offer, and anything outside it was never offered to refuse.

The scan is gated on the Agents step settling, since its answer is the filter. It runs once; the POST
is retried until the server takes it. The decision then runs two passes, because `--private` is per
invocation, with the shared one followed by an explicit `visibility=org` write — the profile default
produces `default:org`, which is admitted only where the repository owner matches the configured org,
so the default route promises a team can read this and delivers owner-only nearly everywhere.

Polling stops while the import runs, because two live Spectre renderables cannot share a terminal, and
both lanes add their elapsed time back to the poll budget: that budget catches a terminal nobody is
sitting at, and a scan or an upload is work. The decision's timestamp is a cursor rather than a flag,
so widening the window on a second answer runs the wider import while re-confirming runs nothing.
`FirstRunImportAnswer.NoReadableVendors` covers the one otherwise-silent failure — repositories chosen
but no vendor this build can read, where running would report success for history that never moved.

Whether a pass succeeded is read off `ImportRunOutcome`, not the exit code: `HandleImport` returns 0
for a run whose sessions failed, because import is best-effort and the Done grid is where that is
reported. The outcome carries the run's counts plus lost explicit-visibility writes, since a session
the user chose an audience for that still carries the old one is a failure of what they asked for.

## Claude SessionEnd hand-off

Claude Code computes the grace it gives SessionEnd hooks from `settings.json` timeouts only; a
plugin's `hooks.json` timeout is used for matching but never for that computation, so kcap's
SessionEnd hook gets the 1.5 s floor and is killed — after it has already killed the watcher whose
parent-exit watchdog would otherwise have ended the session. The hook therefore reads its payload,
re-invokes itself with `--detached`, pipes the payload to that child and exits, all before the
server-URL git probes and the global spool drain that `Program.cs` runs ahead of every hook. The
continuation runs the unchanged session-end path — spool fallback and `ended_at` idempotency
included — under the 15 s `HookBudget` that used to be the hook's, with its output in the session
log and its own session so neither Claude's abort nor a closing terminal can reach it. Only
SessionEnd is handed off: SubagentStop is already `async` in `hooks.json`, and the others honour
their timeouts.

## Review flows and reviewer selection

Review flows use a vendor-neutral catalog-start v2 protocol: reserved `spec-review`/`code-review`
aliases select an explicit or server-default reviewer independently of the driver. Codex setup
registers `kcap-flows` without auto-approval and tracks only newly-created global TOML entries in
`mcp-ownership-v1.json`, so uninstall preserves manual/customized MCP configuration. Daemons retain
the string unattended-vendor list for compatibility and additionally advertise structured
per-vendor CLI/launcher-policy capabilities. Cursor serves borrowed review context from a
daemon-owned snapshot (dirty tracked and non-ignored untracked files, refreshed between rounds),
because its zero-interaction modes may write; Claude has no borrowed-review containment strategy
and therefore fails closed to an owned worktree.

## Borrowed review: containment

Copilot also borrows from a daemon-owned snapshot, but its read boundary is an **OS sandbox**
(`BorrowedReviewSandbox`, `sandbox-exec`, `(deny default)`) rather than a tool clamp, because the
read tools it needs to see the snapshot are the same tools that could be pointed elsewhere. The
profile grants nothing under the user's home: a per-launch `HOME`/`TMPDIR` state directory replaces
the vendor's own config and cache grants, `BorrowedReviewAuthBroker` replaces the keychain grant with
a token from the daemon's own environment, and `BorrowedReviewRuntimeRoots` replaces whole-prefix
grants with software subdirectories derived from the vendor binary — never the prefix's `etc`/`var`. Support is
conjunctive: macOS/ARM64 **and** `sandbox-exec` **and** a brokerable token, or the capability is not
advertised and the server answers `vendor_containment_unreadable` with the `context-only` remedy.
Enforcement is asserted by tests that run a real process under the profile, because a model-layer
refusal is not containment evidence.

## Borrowed review: credentials

The daemon never *looks* for a credential: no keychain read, no prompt, no cache, no persistence, no
default command. It forwards a token the operator exported, or — for a supervised daemon, whose unit
file must not hold a secret — runs the single command the operator configured in
`KCAP_COPILOT_TOKEN_CMD`, and only when an actual borrowed launch needs one. Availability is
deliberately passive (configuration presence, never execution): probing by running the command at
startup would mint a credential nobody asked for, so a configured-but-broken command instead fails at
spawn with the coded `borrowed_review_auth_unavailable`. Service units are written owner-only, and
installation fails rather than leaving one readable.

## Borrowed review: capability advertisement

Borrowed-review capability is **trust-by-default**: a vendor advertises it whenever its runtime
factory declares a containment strategy, for whatever build of the vendor CLI is installed and on
every platform. It is deliberately not gated on the installed binary matching a validated-build
record — a vendor auto-update would then silently withdraw the capability and reviewers would fall
back to a stale committed base. The daemon logs the CLI version it probed at startup (a startup
observation, not a launch-time fact) and does no automated drift detection; a defective vendor
release is handled by a human report and a corrected record. See
`docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md` in kcap-server.

## Launch consent

A daemon-owned launch-consent gate (AI-1623, `LaunchConsent*` in `Capacitor.Cli.Daemon/Services`)
sits in front of every SERVER-driven launch: `LaunchConsentStore` owns `{stateDir}/consent.json` as
the single writer, degrading a missing/corrupt file to the upgrade-safe default (`allow`, so no
pre-existing daemon bricks on update) rather than failing closed. Every decision — rule-matched or
human — is appended to `consent-decisions.jsonl` (1MB rotation, 0600 from first byte) for the
`kcap daemon consent log` verb and the eventual desktop Activity feed, and a non-owner denial
surfaces to the server as the coded `launch_denied_by_owner` reason. The policy is queried/edited
live over the local control socket via the append-only `FrameType` values 11–14
(`ConsentSubscribe`/`ConsentResolve`/`ConsentRulesGet`/`ConsentRulesPut`) and 72–74
(`ConsentPending`/`ConsentRules`/`ConsentAck`); `kcap daemon consent {show,set-default,allow,deny,remove,log}`
is the CLI surface and never blocks a launch waiting on a terminal prompt.

## Local control IPC

**AI-1648** hardens the local control IPC ahead of the desktop supervisor app (spec:
`docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`). A versioned **hello**
frame pair (`HelloIpc.cs`) lets a client discover daemon capabilities before assuming any protocol
shape: `Hello = 15` (client→daemon, optional `ClientHelloDto` — diagnostics only, never trusted)
draws `HelloReply = 75` (`HelloReplyDto`: protocol/daemon version + a `capabilities` list) from
`LocalControlServer`, answered and closed like `List`. `LocalControlCapabilities.Current` sits next
to the routing switch so an entry can never be advertised without a live handler — this PR ships
`["consent/1"]` only, `"status/1"` is reserved for AI-1649's `StatusSubscribe` handler. A pre-hello
daemon can't decode frame 15 at all, so down-level discovery is hello-then-EOF, not an `Error` reply.
The `prompt_no_ui` instant-deny race is closed by a bounded **subscriber grace** in
`LaunchConsentGate`/`LaunchConsentBroker`: `min(5s, PromptTimeoutSeconds)` burned from one monotonic
absolute deadline (injectable `TimeProvider`) fixed at prompt-path entry — every later wait
recomputes `deadline − now` immediately before use rather than accumulating elapsed time, zero
remaining settles as the existing `prompt_timeout` denial (no special case), and a generational
subscriber-arrival waiter in the broker (one shared `TaskCompletionSource` per zero-subscriber
period) lets concurrent waiters converge with arrival winning ties. Cancellation (the launch's own
shutdown token) aborts the wait and the launch together — no consent decision is ever fabricated.

## Desktop onboarding: mutation safety

**AI-1655 Plan B** (spec: `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` §4/§6)
is the desktop app's mutation-safety substrate. Every daemon mutation the app performs routes through
ONE app-lifetime `DaemonMutationLane` (`Capacitor.App/Services/Mutation/`): per-action CLI pinning
(validated login-shell resolver, strict `0.12.0-beta.1` floor probe per mutation), an action-scoped
`KcapCli` executor overlaying `KCAP_CONSENT_SEED_DEFAULT`/`KCAP_EXPECT_SERVER_URL`/
`KCAP_APP_SPAWN_NO_TELEMETRY`/`KCAP_BOOT_ATTEMPT`, instance-bound evidence classification (one
shared predicate; misclassification-toward-success is the cardinal sin), boot-refusal-marker
attribution by attempt id, and a leased FIFO outcome channel whose single consumer owns ALL
actionable presentation (waiter results are state-only; requeue-exactly-once, second abandonment =
logged consume). `ConsentFlipClaims` (durable, `ConfigFileLock`-mutated, two-lock conditional clear,
quarantine-aside) + `ConsentFlipCoordinator` (factory guard → `ConsentRulesPutV2`) cover
pre-existing daemons. `OnboardingGate` (provider-aware, mirrors `TokenStore`'s real refresh rules,
shared URL validator with `App.ValidProfileName`) drives the decision-2 startup carve-out:
gate-incomplete machines build the graph with lifecycle auto-actions permanently closed and the
shim auto-offer suppressed (item stays visible). Wizard UI + the Core auth façade are Plan C.

## Desktop onboarding: wizard and auth façade

**AI-1655 Plan C** (spec §5/§3) is the Core façade and the full wizard. `OnboardingFacade`
(`Capacitor.Cli.Core/Auth/`) drives login/discover/create through one ordered commit boundary —
claims (decision 7) → config + provider stamp → tokens — behind a totalized `AuthResult`:
`Committed`/`Cancelled`/`Failed(AuthFailureReason)`/`Retarget(ServerInput)`. `LoginAsync`'s
`adoptServer` flag separates `kcap login` (never repoints `server_url`) from `kcap setup`/the
wizard (adopts — the write that reaches gate-complete); `kcap setup`/`kcap login` re-plumb onto the
façade as thin Spectre adapters, behavior-preserving. `WizardComposition.BuildGraph`
(`Capacitor.App/Services/Onboarding/`) composes the 8-step wizard (Shim/Connect/Sign-in/Defaults/
Agents/Import/Daemon/Done) over that SAME façade via `WizardAuthService` and its decision-7
`ArmingHook`; `App.RunWizardModeAsync` runs it wizard-first on a gate-incomplete machine (no
daemon graph, no tray) and hands the outcome channel to the normal graph's consumer via
`OutcomeChannel.TransferConsumer` once the sign-in lane cancels/quiesces, closing auto-actions
permanently past the quiesce cap (decision 2/§6a). The §7 streaming `IProcessRunner` backs the
Import step's live, bounded-tail log pane.

## Session workspace terminal

**AI-2195** (spec: `docs/superpowers/specs/2026-08-24-ai2195-session-workspace-terminal-design.md`)
attaches a live terminal to the session workspace. `TerminalTabViewModel` opens every workspace in
`Resolving` and **never constructs an attach client until the session's first matching
`AgentStatusDto` arrives**: attaching optimistically would race the has_terminal gate and show a
spurious "no such agent" flash before a genuinely no-terminal session's note could render. `has_terminal`
is authoritative when the daemon sends it; `HostedHarnessCatalog`'s vendor-transport map is only the
fallback for an older daemon that sent null. `AgentAttachClient` linearizes every termination race
through one atomic cause slot, and **detach intent is recorded, never itself a cause**: a terminal
frame the pump already read wins even with a detach pending, so a daemon `Exited` racing a client
`Detach` still resolves `Exited`, not `Detached`; only EOF with detach intent pending settles
`Detached`. Teardown spends at most its first second on the (best-effort, unacknowledged) `Detach`
write, then force-closes the socket regardless of whether that write landed — the tmux-style PTY
dimension clamp is guaranteed to release by roughly that one-second mark on every exit path, not
contingent on graceful pump completion. `WorkspaceTeardownTracker` seals atomically at the shutdown
drain (registration and seal cannot race past the final snapshot); a post-seal `Track` is executed
and observed rather than refused, so a workspace a coordinator builds between the two shutdown passes
still cannot hold a socket open past the drain. The companion guard lives in `NavigationGate`: its
first shutdown pass latches (which also bumps the generation), so `OpenSession` — card click or launch
auto-open alike — rejects from then on in every window, current or later-built.
The feed into the embedded emulator is rewritten first (`TerminalFeedSanitizer`): XTerm.NET
dispatches a CSI on its final byte alone, so xterm's modifyOtherKeys set — `CSI > 4 ; 2 m`, which
Claude Code sends on every return to raw mode — reaches the SGR handler as "underline on, dim on",
and agent renderers close styles one at a time and never send the full reset that would clear it;
every private-parameter sequence ending in `m` is dropped. The same handler has no arm for the
underline-colour selectors 58/59, whose arguments are read as attribute codes, and drops any
parameter with colon sub-parameters, losing `4:0` and the colon truecolour form.
`KCAP_APP_PTY_DUMP=<file>` appends every fed frame as received, the only record of what the
emulator was given.

## Session chat

**AI-2196** (spec: `docs/superpowers/specs/2026-08-26-ai2196-chat-for-pty-harnesses-design.md`)
renders a Claude or interactive Codex session's own transcript as the workspace's Chat tab and sends
composer text to the PTY. **The daemon, not the app, knows where the transcript is**: every PTY launch
runs the same transcript discovery the server-driven path used, and the link-resolved path rides
`AgentStatusDto.transcript_path` — link-resolved because the per-worktree Claude project dir is a
symlink the launcher deletes at cleanup. Discovery runs until the *path* is known and pulses the
status notifier before any server report. Every transcript open shares read/write/delete; the tail
promises only length-regression reset. **Composer sends are accepted, never acknowledged**, and one
at a time: bracketed paste, a 150 ms wait past Codex's post-paste Enter suppression, then one CR —
only if the terminal's opening token is unchanged. The token advances only through `BeginAttempt`
(after the attach lane is won) and `Invalidate` (detach, teardown, removal, every terminal outcome);
an attempt's own `Connecting`/`Attached` publishes never advance it, and a stale token discards a
late `Attached`, so a queued attach callback cannot reopen a terminal the daemon already dropped.
`TerminalHost` stays laid out under the Chat tab (faded, disabled, reported offscreen) so the PTY
clamp sees the real pane size; everything else collapses with `IsVisible`. Links open only through
`LinkPolicy` (absolute http/https) via one tab-level command. **Markdown never emits a `LineBreak`
inline or a newline inside a `Run`**: Avalonia 12's line breaker never finishes laying one out under a
height-unconstrained parent (any `StackPanel` or `ScrollViewer`), so a soft break is a space and a
hard break splits the paragraph into stacked text blocks; the pipeline carries precise source
locations only so an unmapped inline can degrade to its own source text rather than a type name.

## Permission prompts in the desktop app

**AI-2308** (spec: `docs/superpowers/specs/2026-08-28-ai2308-permission-prompts-daemon-bridge-design.md`)
surfaces a PTY-hosted Claude/Codex session's permission prompt as a card on the Chat tab, with the
rail pip and tray Attention derived from the same cache. The local control socket gains the
append-only frames `PermissionSubscribe = 20` / `PermissionResolve = 21` and `PermissionPending = 77` /
`PermissionResolved = 78` / `PermissionAck = 79`, advertised as `permission/1`. **The daemon's
`PermissionPromptBroker` is the one claim point**: the app's resolve, the server's push, an agent's
withdrawal, the no-UI deny and the shutdown claim all settle a request through `TrySettle`, and the
hook's answer, the ack, the log record and the `Resolved` push all derive from the claimed
settlement — so `Ok=true` is the decision the hook receives. The bridge registers the request
locally BEFORE the server leg dials; the leg feeds the server's decision into the same claim, and a
local win is relayed through the hub's own `RespondToPermission` so the web card clears. A settled
request's server invoke is kept off the wire by a predicate the invoke lambda reads synchronously
(`PermissionRequestAbandonedException`, deliberately not one of the exception types
`ConnectionRetry` retries). The bridge drains admitted handlers before closing its listener; the
tracked wrapper is scheduled with no cancellation token, because a delegate cancelled before it
starts never runs its `finally`. Every caller-controlled wire string is bounded (`PermissionWire`),
ids are canonicalized by GUID parse, and a request kept for a subscriber that then leaves has no
clock — it lives until the agent exits, the same stale card a TUI answer leaves.

## Elicitation question cards for PTY sessions

A PTY Claude session's `AskUserQuestion` already reaches the app as a pending permission entry
(the `PermissionRequest` hook rides the permission lane end to end), so the desktop renders it
as an answerable question card instead of a broken Allow/Deny card — classification is app-side,
on the entry it already receives, and the answer is the existing resolve frame with `allow` plus
the documented `updatedInput` answers shape. No wire, daemon, or CLI change: the feature works
against any daemon advertising `permission/1`, and AI-2197 defines its own frames later for the
structured ACP vendors. Core's `ClaudeElicitation` owns the contract: a strict, capped,
parser-created immutable model and a composer that validates every answer against it, which is
what bounds the outgoing resolve frame by arithmetic. An unparseable or oversized payload falls
back to the permission card (Allow = let the TUI ask) with "Allow always" hidden for the
question tool.

## Compact tool calls in the Chat tab

**AI-2418** (spec: `docs/superpowers/specs/2026-09-02-ai2418-compact-tool-calls-design.md`) folds a
run of consecutive tool calls into one `ToolGroupItem` row: settled calls collapse to a summary line
("Read files, ran a command"), live calls stay listed beneath it, and any prose (user turn, assistant
text, system note) closes the run. **The fold is uniform** — a lone settled call still reads "Ran a
command" — and **folding never hides an error**: a failed call inside a folded group puts the danger
`✕` on the summary line. The group binds ONE inner list whose source swaps on toggle, because a
hidden `ItemsControl` keeps its containers; expanding a group realizes every row and folding releases
them. Expanding holds follow-tail once, so the clicked summary stays in view. Summary wording keys on
the transcript's tool name (Codex's rollout says `shell`, its hook says `Bash`), with Codex shell
commands classified by `CodexCommandClassifier`, ported verbatim from the server into Core so the
server can delete its copy on the next submodule bump. A row waiting on a permission shows an accent
`?` in the outcome slot: `PermissionPendingDto` gains an optional `tool_use_id` the daemon reads from
the hook body (Claude's PermissionRequest hook carries it; Codex's deliberately does not), and the
view-model recomputes the marks from pending requests and running calls on every change, diffing
against the last marks so a call that settles while its request is still pending is cleared rather
than masked by its `✓`. A request without an id marks the agent's sole running call and abstains
when two or more are running.

## Launch and stop command routing

The receive pump no longer awaits launch/stop EXECUTION for either command format: arrival order is
preserved by routing sequenced AND un-sequenced server-origin launch/stop traffic through the ONE
existing serial lane (`RunLaneAsync`). Un-sequenced commands commit via a typed, no-ack entry point
— `SequencedCommandProcessor.SubmitUnsequenced(UnsequencedItem)` — whose admissibility check,
active-launch-instance tracking, and lane commit all happen inside one critical section before the
call returns. Active launch instances are reference-counted per agent id, so a launch dequeued and
parked at the consent gate stays an admissible stop target; admissible targets are `_agents` ∪
durable PID records ∪ active instances — the PID-record arm is load-bearing (it's how the server's
registry-independent physical stop reclaims a prior incarnation's survivor), not belt-and-braces. A
per-boot publication barrier makes "no dual domain, ever" structural: one lock guards both handler
admission and the processor's single null→live transition. Stop coalescing is launch-aware and
identity-guarded (a launch commit clears all of its id's pending-stop keys; a same-payload retry
after a faulted stop always commits fresh), and the queued-stop count backs an edge-triggered,
hysteresis-gated alarm exposed via `QueuedStopDepth`/`QueuedStopHighWater` accessors — additive, with
no production consumer yet (AI-1649's supervision IPC is the natural one).

## Approval policy rules never widen silently

`.kcap/approvals.yaml` (repo) and `~/.config/kcap/approvals.yaml` (user) let a rule allow, deny, or
force-ask a tool call with identical semantics at every seam that already existed — Claude's
`PreToolUse`/`PermissionRequest` hooks, the hosted-Claude permission lane, and the ACP
`request_permission` bridge ahead of kcap's own launch presets. Each invariant below exists because
the alternative is a policy that reads tighter than it behaves.

**An unanalyzed shell command can never be allowed** — not through a coarse `{ kind: shell }`
matcher, not through a substring hit. `ShellCommandAnalyzer`'s allowlist grammar (literal tokens,
only `&&`/`;`/`|` joins) decides "analyzed"; anything with redirection, expansion, a glob, compound
syntax, a **known** shell name in any argv position, or an `env -S`/`--split-string` form (which
re-splits one token into a command line, hiding a name inside it) keeps its raw string as a
restriction-only component with an empty coverage set, so deny/ask can still fire on it but allow
has nothing to cover. The shell-name check is a maintained list, so an interpreter outside it stays
analyzable — and allow-eligible exactly when the analyzed command is fully covered by the policy's
ordinary allow patterns, whatever form the covering pattern takes: naming the interpreter, a glob
spanning its position, or a wrapper rule granting it argv (`env *`, `sudo *`). Every such pattern is
a grant visible in the policy file; what the maintained list guarantees is narrower and absolute — a
**known** shell name in any argv position is never allow-eligible under any pattern. Widening the
grammar without re-deriving the coverage-set argument would quietly make obfuscated commands
allow-eligible.

**Allow requires full coverage at an exact token count.** `git status` allows only `git status`; the
trailing bare `*` in `git status *` is the one thing that opts a rule into arbitrary extra argv, and
a multi-segment command line needs every segment covered or the action is unmatched, not allowed.
Deny/ask stay loose — `git push --force*` matches a contiguous token run anywhere — because
over-triggering there only tightens, never widens.

**`caps` and `enforcement` are server-scope fields**, and `PolicyDocumentBinder` rejects a repo or
user document that sets either: those two keys are how a wider scope puts a ceiling on repo/user
policy, so a local file that could declare them could grant itself the wider scope's own authority.

**No match means silence, not a default.** An action nothing here decides passes straight through to
the vendor's native behavior — turning this feature on is never itself a source of new prompts.

**A rendered session's local seams are tighten-only**: deny/ask apply, allow is never computed
there, because the daemon still runs one full evaluation for that same call. A local allow would let
a rendered session auto-approve something the daemon's pass was meant to be the sole judge of.

**A document outside the supported YAML subset, or one that otherwise fails to bind, is dropped
whole and reported — never half-applied.** `PolicySnapshotBuilder` records the failure as a
degradation and it reaches the user as a `[kcap] approval policy degraded: …` `systemMessage` at
session start; losing one rule silently (a stray tab, an unsupported construct) would be worse than
losing the whole file loudly.

**Policy-decision events never go out as an inline HTTP call from a hook.** They append to the same
spool every other lifecycle event drains through, snapshot first so the server can resolve the
snapshot id a decision names before the decision itself arrives — a hook runs on a few seconds'
budget and the vendor only acts on stdout once the process exits, so a round trip in-line could
outlive the hook and lose a `deny` that had already been decided.

**Only `PreToolUse` is decided in the Claude hook's degraded (no-client) arm.** It is answered before
any client is built, so an unreachable server cannot disable a deny; `PermissionRequest` is answered
inside `HandleCore`, which that arm never reaches, so a prompt already raised simply stands. The
asymmetry is deliberate — a standing prompt is the safe outcome for the seam whose job is to answer a
question a human is already looking at, and moving it earlier would auto-answer prompts during the
very outage that made the evaluation least trustworthy.

## Desktop shell: the checkout on the status wire

**AI-2320** adds three trailing members to `AgentStatusDto` — `worktree_path`, `work_location`,
`borrowed_from` — and makes `repo_path` the repository for every agent. Before, one field carried two
conventions: a primary reported the repository it was launched for while running in its owned
worktree, and a borrowed reviewer reported the worktree it borrowed. The rail filed the two one group
apart, and nothing on the wire said they shared a checkout.

**The daemon resolves the repository; the app never reads a path's shape.** `RepoLabel` recognised
`.claude/worktrees` and `.capacitor/worktrees` tails — a guess that a reviewer borrowing a subdirectory,
or any other layout, defeated. The daemon holds the `WorktreeInfo` and can read the `.git` entries, so
`AgentCheckout` resolves once per agent: the source checkout's root (a borrowed cwd may sit below it),
the main repository behind that root (a linked worktree's `.git` file names it), and the snapshot root
for a runtime that runs in its own copy. The rail keeps `ResolveMainRepoRoot` only so an older
daemon's checkout-shaped `repo_path` still groups; against such a daemon a reviewer's tray and card
labels show the worktree leaf, the price of dropping the guess. The consent prompt and the activity
log pay it too: they label a launch request's path, a wire that carries no repository behind it, so
they name the checkout the request is for.

**Both a marker and a path go on the wire, and the path is the grouping key.** A Cursor or Copilot
reviewer runs in a private snapshot, so `worktree_path` alone would file it under a node nobody else
shares; `borrowed_from` names the checkout it reviews, which is where it belongs — in the rail and in
the workspace subtitle alike, through one `CheckoutLabel` so the two cannot drift. The chat relativizes
tool paths against `worktree_path`, the checkout the agent actually runs in. `work_location` is derived from `borrowed_from` at the one stamping site,
so the two cannot disagree, and a client that only needs the marker reads the token instead of
comparing paths.

## Desktop launcher: permission mode

The new-session composer gains a Claude permission-mode chip (Manual, Accept edits, Auto, Bypass
permissions) beside the effort chip, and Start becomes a round arrow button. Manual sends nothing —
the `permission_mode` key is omitted, not null — so an unchanged launch is byte-identical to one
predating the chip and an older server or daemon behaves exactly as before. Any other choice rides
one name-bound field through `RequestLaunchAgentV2`, the server's `LaunchAgentCommand` and the
daemon's `LauncherContext` to a `--permission-mode` argument on the interactive Claude arm only. The
tokens live once, in Core's `ClaudePermissionModes`, so the chip and the daemon guard cannot drift.

**The mode is a preference, never a containment override.** A reviewer's `bypassPermissions` and a
borrowed checkout's prompting are guarantees, so the server's `ClaudePermissionModeRequestPolicy` and
the daemon's `ClaudePermissionModePolicy` refuse a mode on a review-flow, PR-review, borrowed or
non-Claude launch with a coded reason — before consent and before any worktree work, mirroring the
Codex posture and ACP preset guards rather than silently dropping or honouring it. Only the four
offered tokens pass; `plan` and `dontAsk` are Claude's but not the product's. The daemon advertises
`PermissionModeVendors` on connect and the server refuses a mode toward a daemon that does not list
the vendor, so an older daemon can never quietly run Manual under a Bypass selection.

**An interactive bypass launch keeps the single-Enter submit.** `DisablesApprovalPrompts` stays true
only for owned review-flow reviewers: bypass silences permission prompts, not question cards or the
one-time bypass consent dialog, and the multi-CR spray would answer either. The chip is withheld for
every vendor but Claude, the mode is session-scoped like the effort, and the harness picker no
longer offers "Remember" — a chosen harness is always persisted for the repository.

## A 401 spools

Every hook path classed an HTTP 401 with the payload-rejecting 4xxs and dropped the event, so a
credential rejected mid-session lost the lifecycle event that hit it: `kcap login` resumed recording
from the next event, but a dropped session-start left the session without a server-side record and a
dropped session-end left it stuck active. A 401 is now retryable, alongside 5xx, 408 and 429.

**The classification is one helper, not eight copies.** The live posts (Claude's three bounded arms,
`AgentHookPoster.PostOrSpoolAsync` for the other vendors) and the drain posters (`LifecycleSpoolDrain`,
Claude's and Cursor's inline replays) each restated the rule, and Cursor showed why that cannot hold:
its live post already spooled any non-2xx, so a 401'd entry survived to the next drain and was dropped
there — a visible loss turned into a delayed silent one. Spooling at the live post is only safe once
every drain agrees, so both sides read `HookSpool.IsRetryable`.

**Retention is the spool's own.** A backlog that can only land after a human logs in is bounded the
way an outage backlog is: the per-session byte cap and the 30-day reap. The pre-flight auth check
still skips the drain while the token store knows the credential is dead, so a spooled 401 costs
nothing until the login, and the drain that follows the login replays the backlog in order. On the
vendor path a server 401 is now `Spooled` rather than `Failed` — the hook exits 0, as Claude's already
did, and the stderr line still names `kcap login`.

## Repo-aware MCP servers: the working directory's repository

Every kcap MCP server is spawned at session start by each harness that registers it, so its startup
path runs once per server per session whether or not a tool is ever called. The sessions, memory,
analytics and flows servers resolved the working directory's repository there with pull-request
detection on — a live `gh pr view` / `glab` round-trip that is roughly the whole startup on a GitHub
checkout (about 0.8 s per server) for a value none of them reads: they scope requests by owner and
name only. `CwdRepository` resolves on the first tool call that asks, once per process, with PR
detection off. **A null answer is cached too**: outside a checkout the answer does not change for
the life of the process, and re-probing on every call would spawn git for nothing.

**The integration pin is indirect by design.** Detection is the only startup-time writer under the
config root's cache directory, so an absent directory after the initialize/tools-list handshake
proves it never ran; the tool call that follows then proves on-demand resolution by carrying the
repo hash. Asserting on the cache file itself would key on the child's own view of its cwd, which
macOS reports through the resolved `/private` path rather than the one the test handed it.

## Desktop shell: the work-context sidebar

**AI-2198** (spec: `docs/superpowers/specs/2026-09-03-ai2198-work-context-sidebar-design.md`) adds the
400px right column of the session workspace: the session's work item with its declared parts and
blockers, its pull requests, who is attached, and the session's facts. **It is built on the three
reads the server exposes over HTTP** — a session's assignments, a work item's topology, and the
session summary — and everything work-item detail the server serves only in-process to its own
dashboard (state, overview, per-part completion, links with URL and state, contributors) renders as
a SOON pill until a read endpoint exists. The card shows the session's primary work item; a
repo-less session has no work item at all, because the server requires a repository on one, and the
pane says so rather than showing an item without a key.

**The key is split from the server's label by convention, not contract.** The assignments route
labels a keyed item `"KEY — title"`; the pane takes the half before the separator as the key and the
topology item's title as the title. A change to that composition shows the whole label as the title
and drops the key chip — safe, but silent, which is why the dependency is named here.

**Reads are leased by session id.** The daemon puts `session_id` and `branch` on the status wire, and
each read carries a lease with its own cancellation; a switch starts the new session's read at once
and drops the old one's result, teardown cancels and awaits every outstanding lease, and all lease
bookkeeping runs on the UI thread. The reader fails closed: a 2xx with an unparseable body is a
failure, a final 401 on any route signs the pane out, a 403 is "not in plan" only with the exact plan
code. A section blip dims the pane and keeps the last good section; an authoritative empty answer
clears it; signed-out, not-in-plan and unknown-session clear every server-derived projection.

**The app's server clients are one set with one cleanup.** The work-context source holds its HTTP
client by lease so overlapping reads never see it disposed, retires it on sign-out, and is torn down
with the launch client through a holder that memoizes the cleanup, so both teardown paths reach it
and nothing is disposed twice. The window gains a minimum width equal to its default: 310 of rail plus
400 of pane must never squeeze the terminal column to nothing.

## The machine beats, so a death that cannot speak is still visible

The browser is told the machine has stopped by a relinquish the leg sends on its way out. That covers
every exit it can reach, including through `Environment.Exit` once the notice is also sent from
`AppDomain.ProcessExit` — but it is a statement, so anything that stops the process making statements
sends nothing at all: a kill, a lost network, a shut lid. The flow's own lifetime was the only backstop,
and it is twelve hours.

A beat on its own timer turns that silence into something the server can observe.

**It is not driven by the poll, and that is the whole design.** The import runs inline in the poll loop
and stops polling for its duration, so liveness derived from the poll would declare the machine gone
during the one stretch it is working hardest. A separate timer measures the process, which is the only
thing a beat can honestly claim — a wedged leg goes on beating, and nothing here should ever be read as
progress.

**Nothing inspects the result for success.** A beat is a network call on a machine whose network may be
exactly what is failing, so a failure is not handled and a throw does not end the loop: a run of them is
the signal, and only the server is positioned to read it. Two statuses are read, because neither is
discoverable by a later beat — a throttle, which is an instruction and would otherwise spend the budget
the poll needs, and an absent route, which answers the same way for the whole leg.

**A beat is never cancelled.** The beat rides the setup client, whose 401 handler rotates a single-use
refresh token and then persists it, the rotation itself being uncancellable. A cancel landing between the
two spends the credential server-side and never writes the replacement, logging the user out mid-setup.
So the stop token is kept off the request entirely: what ends a beat is the client's own timeout, and what
ends the loop is the token one level up.

**Answers are carried across ticks rather than abandoned, and beats are capped rather than serialised.**
A dropped verdict is never read, and a throttling or absent-route server is exactly the one whose answer
outruns an interval — so the two statuses worth reading would be missed precisely when they matter. But
allowing only one at a time makes a single slow request the whole liveness budget, so the bound is a
count: enough that a slow answer cannot silence the machine, few enough that a wedged network cannot
accumulate one open POST per interval on the very machine whose network is failing.

**Backing off a missing route is a pause, not an ending.** A rolling deploy or a proxy reload is minutes
long and the poll on the same client rides straight through it, so a beat that stopped for good would have
the browser infer a death from a machine still demonstrably talking to it.

**Stopping does not wait for a beat in flight**, and does not cancel it either. Nothing is owed to it:
the relinquish that follows states the ending, and the browser reads a stated ending ahead of an inferred
one.
