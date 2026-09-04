# Kurrent Capacitor CLI

**File paths:** CLI source at `src/Capacitor.Cli/`, shared core at `src/Capacitor.Cli.Core/`, transcript normalization at `src/Capacitor.Models.Transcripts/`, daemon at `src/Capacitor.Cli.Daemon/`, desktop app at `src/Capacitor.App/`, npm packages at `npm/`, Claude Code plugin at `kcap/`.

**Harness layout:** vendor-specific code lives under `Harness/`. Vendors: Antigravity, Claude, Codex, Copilot, Cursor, Gemini, Kiro, OpenCode, Pi.

- `src/Capacitor.Models.Transcripts/Harness/<Vendor>/` — transcript projections to canonical events, the one place a vendor's transcript format is read.
- `src/Capacitor.Cli.Core/Harness/<Vendor>/` — paths, hook parsers/installers, CLI runners, chat display rules.
- `src/Capacitor.Cli.Daemon/Harness/<Vendor>/` — launchers, runtimes, reviewer capabilities, posture policies; one directory per vendor covering what used to be split between `Services/` and `Acp/`.
- `src/Capacitor.Cli/Commands/Harness/` — command entry points, flat (`<Vendor>HookCommand` and friends).
- `src/Capacitor.Cli/Harness/<Vendor>/` — everything else per vendor: import sources, subagent teardowns, correlators, ledgers.

Namespaces follow the directory (`Capacitor.Cli.Core.Harness.Codex`, `Capacitor.Cli.Commands.Harness`, `Capacitor.Cli.Harness.Pi`), enforced by the compiler, so moving a file means fixing its namespace in the same change. Code shared across vendors stays outside `Harness/` — a directory named after a vendor holds only that vendor's code. Adding a harness should mean a new `Harness/<Vendor>/` directory plus one registration site per assembly, not edits to shared folders.

## What this project does

The `kcap` CLI records coding-agent sessions by forwarding hook payloads and transcript data to a
Kurrent Capacitor server. It also hosts an agent daemon for remote agent management and provides PR
review context via MCP tools.

## Invariants

Deliberate choices a change can silently undo — each looks like a bug until you know why.
`docs/CHANGES.md` carries the reasoning per feature; `docs/superpowers/specs/` holds the designs.

- **A vendor either contains borrowed review or does not offer it.** Cursor and Copilot read a
  daemon-owned snapshot, Codex its own tool clamp; Claude declares no containment, so a borrowed
  request fails closed to an owned worktree. Copilot's boundary is an OS sandbox rather than a tool
  clamp, and its support is conjunctive — macOS/ARM64 and `sandbox-exec` and a brokerable token, or
  the capability is not advertised. Containment is proven by a real process run under the profile,
  never by a model-layer refusal.
- **The daemon never looks for a credential.** No keychain read, prompt, cache, persistence or
  default command: it forwards what the operator exported, or runs the one command they configured,
  and only when a borrowed launch needs it. Availability is configuration presence, never execution
  — probing would mint a credential nobody asked for.
- **Borrowed-review capability is never gated on the installed vendor build.** A runtime factory
  declaring a containment strategy is enough to advertise it; version-gating would let a vendor
  auto-update silently withdraw the capability and drop reviewers back to a stale base.
- **Every server-driven launch passes the consent gate** (a local spawn on the 0600 socket is the
  owner's by construction). Its store degrades a missing or corrupt policy to `allow` rather than
  failing closed, so an update cannot brick a pre-existing daemon. Decisions append to an
  owner-only log; no gate path blocks a launch on a terminal prompt, and cancellation aborts the
  launch rather than fabricating a decision.
- **The daemons directory is a fixed location and ignores `KCAP_CONFIG_DIR`.** It shares the
  `~/.config/kcap` prefix with the config directory by coincidence, not derivation: when it was
  derived, two daemons under different config dirs took different `flock`s, never saw each other,
  both authenticated as the same GitHub ID and oscillated the server's registry slot. Same literal,
  different anchor — `DaemonStoreTests` guards it.
- **Local control IPC is append-only and capability-gated.** `FrameType` values are never reused or
  renumbered, and `LocalControlCapabilities.Current` is assembled beside the routing switch so
  nothing can be advertised without a live handler.
- **Server-origin launch and stop share one serial lane**, sequenced and un-sequenced alike, so
  arrival order survives. One lock guards handler admission and the processor's single null→live
  transition: there is no second command domain, ever.
- **Codex MCP setup owns only what it created.** Entries it added are tracked in
  `mcp-ownership-v1.json`, and uninstall preserves anything it cannot prove it wrote.
- **Desktop-app daemon mutations go through one app-lifetime lane** with one shared evidence
  predicate: classifying a failure as success is the cardinal sin.
- **Auth commits in one ordered boundary** — claims, then config and provider stamp, then tokens —
  behind a totalized result. `kcap login` never repoints `server_url`; `kcap setup` and the wizard
  adopt it.
- **A 401 spools; only a rejected payload drops.** Every live hook post and every drain poster
  classifies a status through `HookSpool.IsRetryable`, where a 401 is retryable like a 5xx: the
  credential is what `kcap login` repairs, so the event must outlive the lapse. Restating the rule
  at a call site lets a vendor spool a status the drain then drops — a visible loss turned into a
  delayed silent one. Retention while the lapse lasts is the spool's own byte cap and 30-day reap.
- **Secret redaction rewrites decoded JSON string values, never the serialized line.** A pattern run
  over the whole line matches past the value it found into the surrounding structure, and the server
  drops an unparseable line silently. A line the writer refuses is replaced by a placeholder —
  never by the raw line, which would re-expose what the redactor just matched.
- **Transcript event ids are a persistence contract.** `TranscriptIds` in `Capacitor.Models.Transcripts`
  fixes the bytes each id hashes; the server dedups by them, and a session it has already ingested
  is never re-projected. A different derivation would append duplicates on the next re-import, so
  the leaf's fixed vectors pin every row and a change needs a migration, not a bump.

## Tech stack

- .NET 10, NativeAOT compiled
- SignalR client for real-time server communication
- TUnit for testing, WireMock.Net for HTTP mocking

## Building

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```

## Test conventions

**Layout:** one test project per prod project, each mirroring that project's directories — `test/Capacitor.Cli.Core.Tests.Unit/`, `test/Capacitor.Cli.Tests.Unit/`, `test/Capacitor.Cli.Daemon.Tests.Unit/`, `test/Capacitor.Models.Transcripts.Tests.Unit/`, plus `test/Capacitor.Cli.Tests.Integration/`. A test project references its own prod project and `test/Capacitor.Tests.Helpers/`, never another test project: anything shared across suites goes in Helpers, with a `public` surface (no `InternalsVisibleTo`). Helpers' `Guards/` holds the process-global pins every assembly needs, and Helpers is a global `using` everywhere, so its types need no import.

- Throwaway directories come from Helpers' `TempDir` — `using var tmp = new TempDir();` — never a per-class copy. Build paths under it with its own members — `tmp.PathTo(…)` for a path that must not exist yet, `tmp.CreateDir(…)`, `tmp.CreateFile(…)` — not `Path.Combine(tmp.Path, …)` + `Directory.CreateDirectory`/`File.WriteAllText`. When the code under test refuses a symlinked path component, hand it `tmp.GetResolvedPath(…)` instead: a Mac's temp root is under `/var`, a symlink into `/private`, so the plain path is rejected there and nowhere else — CI has no macOS leg to catch it. It is not the default because resolving costs 8 characters of the `sockaddr_un` budget.
- When every test in a class needs one, inject it instead of holding a field: `[TempDir] public required TempDir Tmp { get; init; }` (or `[TempDaemonPaths]` for a `TempDaemonPaths`; both take an optional hint, `[TempDir("short")]`). TUnit builds one per test and disposes it, so the class needs no `IDisposable` — which is also how CA1001 stops applying. `Shared` widens the lifetime (`SharedType.PerClass` and friends) but hands one directory to tests that run concurrently, so it fits a read-only fixture only. The property is set *after* construction, so a ctor or another field initializer cannot read it (make those members lazy) and a `static` helper cannot see it at all; a `[Before(Test)]` hook can, because injection runs first.
- Outside a test class — a nested harness type, a fixture object — keep the field and implement `IDisposable`: TUnit only injects into test classes. CA1001 is an error there, so an `[After(Test)]` hook will not build.
- A class whose time goes into real child processes (git, a vendor CLI, a PTY) can draw from `[ParallelLimiter<SubprocessLimit>]` — one pool of half the cores, shared by every class in the assembly that names it. TUnit's own cap is 4x the cores, sized for IO-bound tests; at that width these classes starve each other's timing assertions. Pool the CPU hogs, not the test that failed. Today the pool is the daemon suite's; elsewhere a whole-class `[NotInParallel(nameof(TheClass))]` is the cheaper tool when the point is to keep one class's own tests apart.
- Spawning the real `kcap` binary goes through Helpers' `KcapProcess`, which requires a `DaemonStore` and pins it for the child. The assembly-wide `KCAP_DAEMONS_DIR` pin is a path that cannot be created, so a hand-rolled spawn dies with a bare ENOTDIR `IOException` instead of quietly resolving the developer's own daemons directory.
- Capture console output with `ConsoleOutput.StartCapture()` / `StartErrorCapture()`, never a hand-rolled `Console.SetOut`/`SetError` save-restore — TUnit0055 is an error. Console is process-global, so every caller needs bare `[NotInParallel]`; a group key is not enough.
- Never assert that an environment variable is *absent* from a built `ProcessStartInfo`: its environment is seeded from the current process, and the repo's own `.envrc` exports several. Assert what the code under test contributed, by comparing against the inherited value.

**Parallelism:** bare `[NotInParallel]` is exclusive against the whole assembly and its tests run last, one at a time; keyed `[NotInParallel("k")]` excludes only tests carrying `k`.

- Bare is what process-global state needs: an environment variable, `Console`, a mutable static in production code, the working directory. Keyed is sound only when every *reader* is in the cohort too, not just every writer — an env var fails that as soon as a concurrent peer spawns a child (it inherits) or calls a path helper (it reads).
- A method-level `[NotInParallel(…)]` shadows a class-level one: the first constraint wins and the method's comes first. Never carry both.
- Mutate an environment variable through Helpers' `EnvScope` — `EnvScope.Exclusive(key, value)` for one a child inherits or a path helper reads, the constructor for one whose readers all carry the same key. It checks the constraint and throws naming the fix, so the rule is enforced rather than commented.
- Reaping a child you forked is safe: the zombie holds its pid until you wait on it. Asserting on a pid you *no longer own* is not — one the shim already reaped, or a grandchild reparented to init. `kill(pid, 0)` reads a squatter as your process still alive, and stealing a wait from a child `System.Diagnostics.Process` owns FailFasts the test host. Use Helpers' `PidIdentity`: `Capture` while alive, then `IsGone`/`WaitUntilGoneAsync`.

## Running tests

Tests use TUnit on Microsoft Testing Platform.

```bash
dotnet test --solution Capacitor.slnx
```

A single suite still runs directly as an executable, which is the faster loop when iterating:

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
```

Every suite, the daemon one included, runs green at full parallelism — no flag needed. CI's
`--maximum-parallel-tests 1` caps only the unconstrained bucket, so it narrows race windows rather
than closing them; a test that needs exclusion carries the constraint itself.

## Publishing

AOT publish for the current platform:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release
```

Always verify no IL3050/IL2026 AOT warnings after changes:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

## Comments

**Scarce by default — never modelled on the ones already in the tree.** Existing comments are long and heavily historical, so they are the one thing here you *MUST NOT* imitate: do not pattern-match on them, and do not match their density. Follow these rules whatever the neighboring code looks like.

**The test, before writing any comment:** would it still be true and useful to a reader who has only the current source — no ticket, no diff, no review thread, no calendar? And does it name something that breaks or gets undone if unknown (a trap, a non-obvious constraint, a deliberate decision)? Both yes, or write nothing. Restating the code fails the second question; narrating how the code got here fails the first. One or two lines is the norm; a longer block needs something the reader can get nowhere else.

**Never write** — and the ban is on the content, not these exact words; paraphrases count:

- **design/spec/plan coordinates** — "§4", "decision 3", "Task 12", "Phase 2", "scenario (a)". State the constraint itself.
- **ticket ids as narration** — "ABC-123 adds…", "pre-ABC-456 semantics", "the ABC-789 invariant".
- **review artifacts** — reviewer or bot names, "round 2's fix", "PR #1289 finding 3", severity labels ("P1", "Medium 2"). If the text only parses once the reader reconstructs earlier versions, rewrite it to describe what is there now.
- **facts true only at the time of writing** — "as of this writing", "measured today", dates, run ids, site counts, open-PR numbers.
- **change narration** — "used to", "previously", "no longer", "originally", "this replaces", "moved from", "do not re-add", or any other account of how the code evolved. The diff, the commit message and the issue own that.

**Exactly three exceptions**, each with its condition:

- A ticket id pointing at *still-open* work the reader must act on (TODO-style). Closed tickets never qualify, and the id is the GitHub issue number — see Dos and donts.
- A measurement justifying a value that would otherwise look arbitrary (a timeout, a retry gap) — keep the number, drop the date and the run it came from.
- An old shape — a renamed field, a dropped enum value, an older event version — that persisted data, stored config or an older client **can still present**. That is a live compatibility constraint, not history: name the old form and what must keep accepting it, not the story of the change.

**A test's doc comment says what the test pins.** A precondition that keeps the test honest ("the stale spec genuinely cannot — otherwise this test proves nothing") belongs; its review history or its place in a CI budget does not.

**Rewrite as you go.** When you touch code, shorten and de-historicise the comments on and around it: delete anything the Never list bans, keep only what still passes the test. Expected work, not scope creep.

Applies to every file type: source, tests, config and build files, YAML, shell scripts.

## Commit messages

Subject: `one clause (#123)`, imperative, **at most 80 characters including the trailing reference**. No second clause, and no parenthetical other than that reference.

The reference is the GitHub issue number — Linear ids stay out of commit messages, as they do out of comments. It goes in only when context already gives it. **Never invent one, and never settle a near-match on your own judgement** — offer the candidates and let the user pick, or ask whether one should be created. Leave the reference off until then.

Body: a line or two naming the constraint that forced this shape, or a consequence easy to miss — something neither the diff nor the issue shows. Nothing to name means no body. **Five lines max.** Comments bans apply, and no inventory of the diff (file lists, per-site counts), however labelled.

Squash-merge concatenates the branch's messages verbatim, and the merge is usually not yours, so write them to survive that untouched: every subject standing on its own, context stated once instead of repeated in each message, and no cross-references between commits ("as above", "fixes the previous commit"). If you do perform the merge, discard the concatenated default and write the squash body fresh, under these same rules.

## Issues and pull requests

This is a public repository — we develop in the open.

- **Open issues in GitHub Issues**, not Linear. Linear auto-imports GitHub issues, so there is no need to create the issue in Linear by hand.
- **PRs must reference both the Linear issue and the GitHub issue.** Both go on the reference line in the PR *description*: the GitHub issue with a closing keyword (e.g. `Closes #123`) and the Linear issue (e.g. `AI-774`), so Linear links the PR back to the imported issue. The title carries no reference of its own: squash-merge appends the PR number to it, so an issue reference there lands beside that one and reads as a second PR.

Title: commit-subject rules minus the reference — `Show "Copied" tooltip on clipboard copy`.

Description: **before writing it, open [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md) and follow its comment block** — it owns length, headings and the Never list. `gh pr create --body` not rendering the template is not an exemption from it.

## Code conventions

**Return `FrozenDictionary<K,V>.Empty` / `FrozenSet<T>.Empty` for an empty `IReadOnlyDictionary`/`IReadOnlySet`** — a fresh empty `Dictionary`/`HashSet` per miss allocates for nothing, and these are shared singletons.

**No `InternalsVisibleTo` to a production assembly.** If a shipping project needs a member, that member is not internal — make it public. Test-assembly grants are fine, and most of the grants here are those. `Capacitor.Cli.Core` keeps its grants to `kcap` and `kcap-daemon`, the two shipping executables; that is grandfathered, not a precedent, and **new projects start with none**.

**One type per file, named after the type.** Several types in one file is discouraged, whatever the neighbouring files do — and plenty here do. Three exceptions: an enum plus its extension methods; a closely-related hierarchy (an interface plus many small implementations); a registry of descriptors. The last two are rare — reach for them when splitting would leave files that only make sense read together, not to save a file.

## Dos and donts

- DO use `JsonElementExtensions` instead of checking JSON value kind.
- DO take a `UserHome` rather than resolving a home yourself: `Environment.GetFolderPath` is banned
  (`RS0030`, `BannedSymbols.txt`), so a new call is a build error. An exemption is a per-site
  `#pragma warning disable RS0030` naming why that site cannot take one.
- DO NOT use Linear issue numbers in comments. If you absolutely need an issue number, use the GitHub issue number.

## Common mistakes to avoid

- **AOT warnings only show on publish** — `dotnet build` does NOT surface IL3050/IL2026 trimming warnings. Run `dotnet publish -c Release` after changes.
- **JsonArray collection expressions** — `[item1, item2]` compiles to `Add<T>()` which requires dynamic code. Use `new JsonArray(item1, item2)` constructor instead.
- **TUnit test filtering** — Use `--treenode-filter` with glob syntax, NOT `--filter`.
- **macOS AOT binary code signing** — After copying an AOT binary, run `codesign --force --sign -` to re-sign.
- **Never read an agent-owned file with a write-denying open** — `File.ReadAllText`/`ReadAllTextAsync` open `FileShare.Read`, which *denies Write to every other handle* for the duration. On Windows that sharing is mandatory, so it stops the agent writing to its own transcript/sidecar — worst on the shutdown final drain, when it is flushing its last records. Read via `WatchCommand.ReadAllTextShared`/`ReadAllTextSharedAsync` (or your own `FileStream(..., FileShare.ReadWrite)`) for anything the agent writes: transcripts and their `{id}.json` sidecars. Config/settings files we own are fine. **This is invisible on macOS/Linux** — Unix has no mandatory sharing, so a violation passes locally and only reddens the Windows CI leg (AI-1629 was exactly this, on the one read that missed the rule while seven siblings had it).
- **README sync on CLI changes** — Any change to user-facing CLI surface (new command, new/renamed/removed flag, changed default behavior, new prerequisite) must update `README.md` in the *same* PR. Check both the quick-start (`## Getting started`) and the per-command section under `## CLI commands`. Updating only `src/Capacitor.Cli.Core/Resources/help-*.txt` is not enough — the README is the public-facing docs. This has been missed repeatedly and has required follow-up doc-only PRs (#60, #61).
