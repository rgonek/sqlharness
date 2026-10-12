# SQLHarness MCP server (local stdio)

One `sqlharness` process can serve the SQLHarness diagnostics and benchmarks to an MCP client over local stdio. The server is part of the same single-file distribution, calls into `SqlHarness.Core` through the same module the CLI uses, and enforces the same safety rules. Only local stdio is supported, one client per process. There is no HTTP/SSE transport, no ports, no OAuth, no sampling, prompts, elicitation, or experimental tasks.

## Start

The server supports a fixed-profile mode and a SQL Server-only request-scoped mode. Each process serves one local stdio client.

Fixed-profile mode keeps the existing command shape:

```powershell
sqlharness mcp serve <profile> --var key=value [--input-root <absolute-directory>]
```

- `<profile>` is a closed target profile from the operator's profile store, with fixed `--var key=value` pairs resolved once at startup.
- `--input-root` is repeatable and takes an absolute directory that allows file inputs. By default no input roots are configured, which means no file inputs are admitted at all.
- `--max-result-bytes` sets the process response cap in bytes: default 16384, accepted range 4096..1048576. A single tool call may only lower it.
- `--max-operation-seconds` sets the process database time budget in seconds: default 900, accepted range 1..86400. A single tool call may only lower it.
- `--unsafe-direct` is blocked for `mcp serve` and rejected with exit 2. Direct server/database targets are not available on this path.

Request-scoped mode selects one of the explicitly allowed SQL Server profiles on each target-dependent call:

```powershell
sqlharness mcp serve --request-scope --allow-profile sample-country --allow-profile sample-shared --input-root C:\work\sqlharness-inputs
```

The nonempty `--allow-profile` list is required and repeatable. Do not provide a startup profile or `--var` in this mode. Startup rejects duplicate profile names and any allowlisted profile that resolves to Postgres. The process snapshots the allowed profile definitions, file roots, and budgets once; editing the profile store or changing the allowlist requires a restart. Startup does not connect to a database or perform interactive login. Request-scoped mode is not available for Postgres.

## Benchmark artifact storage

`measure` (including parameter sets) and `compare` (including matrices) check the benchmark artifact directory after input validation and before opening a database session. The check creates a unique probe directory, writes a nonsensitive marker, renames the directory, and removes the probe. It uses the same storage root as artifact publication and leaves existing artifacts untouched.

The process account must be able to create directories, write files, rename directories, and delete its files and directories under the artifact root (by default `~/.sqlharness/compare`). On Windows, an MCP client may launch a restricted process whose effective permissions differ from those of an interactive terminal. Check the actual MCP process identity and token before changing access rules; keep any access grant limited to the required artifact directory.

A failed probe returns `local_storage_failed`, exit code `6`, phase `artifact-preflight`, a redacted storage location, and a permissions hint. SQLHarness never changes ACLs or elevates the process. A successful check does not guarantee a later write will succeed: permissions, disk space, or other filesystem conditions may change during the benchmark. Publication failures still retain completed matrix cells and use the existing staging cleanup behavior.

`SQLHARNESS_HOME` changes the whole SQLHarness home, including configuration and profiles, not just the artifact directory. Changing it requires deliberate operator configuration. Read-only `query` and `inspect` do not run this benchmark storage check.

## Client configuration

SQLHarness never installs or edits a client configuration automatically. To register the server, paste an entry manually into the MCP client configuration file. Neutral shape (field names vary by client):

Fixed-profile client entry:

```json
{
  "command": "sqlharness",
  "args": ["mcp", "serve", "sample-country", "--var", "tenant=example", "--var", "env=test", "--input-root", "C:\\work\\sqlharness-inputs"],
  "env": {}
}
```

Request-scoped client entry:

```json
{
  "command": "C:\\tools\\sqlharness.exe",
  "args": ["mcp", "serve", "--request-scope", "--allow-profile", "sample-country", "--allow-profile", "sample-shared", "--input-root", "C:\\work\\sqlharness-inputs"],
  "env": {}
}
```

## Protect local agent configuration

`~/.sqlharness/dashboard.json` and `~/.sqlharness/dashboard-token` contain a live dashboard access token. Do not print, paste, or expose those files. Avoid broad reads such as `cat ~/.sqlharness/*.json`; name `targets.json` explicitly when you need to inspect profiles. On Windows, the corresponding dashboard path is `%USERPROFILE%\.sqlharness\dashboard.json` (and `dashboard-token` beside it).

The sample hooks in [`scripts/hooks`](../scripts/hooks) block common Claude Code and Codex shell reads of `dashboard.json` and `~/.sqlharness/*.json`, while allowing an explicit `targets.json` read. They are optional guardrails for shell commands, not a filesystem access boundary. Review and merge the relevant example into your existing client hook configuration; do not replace other settings. Claude Code hooks use the repo-local script path in `claude-settings.example.json`. For Codex, copy `protect_sqlharness_config.py` to `~/.codex/hooks/` and merge `codex-hooks.example.json` into `~/.codex/hooks.json`; review and trust the hook when Codex prompts.

Connection secrets stay in the process environment and the operator's profile store. In fixed mode, tool arguments never carry a profile or vars. In request-scoped mode, profile and vars may appear only inside the required nested `scope`. In both modes, tool arguments never carry raw server or database, auth, engine, `unsafeDirect`, `allowMutation`, `confirmDatabase`, or password fields, and unknown argument keys are rejected before execution.

## Scope lifetime

In fixed mode, the operator supplies the profile and vars once at process start. The server resolves and freezes one immutable scope for all calls; a different profile, variables, or target requires a restart.

In request-scoped mode, each target-dependent call supplies a nested scope, for example `{"scope":{"profile":"sample-country","vars":{"tenant":"example","env":"test"}}}`. The scope is required for `inspect`, `validate`, `query`, `measure`, `compare`, `watch`, `snapshot`, and `artifact`. Each call resolves the selected allowlisted profile against the immutable startup snapshot and builds a fresh scoped handler/module. The other two tools (`capabilities` and `plan`) remain target-free; in particular, offline plan distillation does not invent a scope. Fixed mode retains its existing schemas and rejects the request-only `scope` argument. Matrix and parameter-set values are execution parameters and cannot change the scope.

Capabilities identify `scopeMode` as `fixed` or `request`. Request mode reports the SQL Server engine and the permitted profile names from the explicit allowlist; neither mode reports resolved database lists, variable values, credentials, or local paths other than the configured input roots (`inputRoots`). The allowlist is an operator configuration boundary, not user authorization. Clients must confirm the intended profile and target before SQL and require a new explicit user request before changing task scope. Static validation remains offline and does not prove live identity, object existence, or database permissions.

Each resolved scope carries an artifact owner tuple built from its profile request and resolved target: `{ profile, canonical vars, engine, server, database }`. In fixed mode the scope comes from the startup request; in request-scoped mode it comes from that tool call. Vars are stored case-insensitively with deterministic (ordinal-sorted) order, so the same scope serializes stably. The owner holds no auth material and no secrets: passwords and environment values never land in owner metadata or reports. Matching is exact and fails closed: profile, engine, server, and database compare ordinal; var keys compare case-insensitively with exact values; the full var set must agree (count included). Malformed candidate metadata simply does not match and never throws. The owner survives a restart because it is persisted in the artifact, not in server memory: a new process with the same profile, canonical vars, and resolved engine/server/database matches again without any in-memory state.

## Input roots and file inputs

Inline input works without filesystem access. File input is admitted only under the absolute `--input-root` directories frozen at startup. The reader rejects path traversal, UNC and network paths, NTFS alternate streams, symlink and reparse-point escapes (including linked parent directories), and files replaced while they are read. Each file is opened once with a bounded read.

`sqlharness_capabilities` discloses the configured input roots in both modes, so a caller can find a permitted location without reading client configuration:

- `inputRoots`: a page of the normalized absolute roots, in configured order. An empty array means file inputs are disabled (no `--input-root` was configured); pass content inline instead.
- `inputRootCount`: the total number of configured roots.
- `inputRootsComplete`: `true` only when this single response holds every root (first page, nothing omitted, no next page).
- `inputRootsNextCursor`: the offset to pass as `inputRootCursor` for the next page, or `null` on the last page. The page size follows the result byte budget. To get the full inventory, repeat the call with `inputRootCursor` set to the previous `inputRootsNextCursor` until it is `null`. A cursor outside `0..inputRootCount` is rejected.
- `inputRootsOmitted`: roots skipped because a single root path is too long to fit any page; they stay enforced but cannot be listed.

Discovery workflow: call `sqlharness_capabilities`, pick a root from `inputRoots`, place the file under that root, and pass its absolute path as `file`. Roots are fixed at startup. SQLHarness never reads client configuration, never creates directories, and MCP `roots/list` never widens the allowlist.

Input-path rejections name the reason and echo the caller path (credential-filtered, control characters replaced, URL or UNC `user:password@` userinfo masked, truncated at 120 characters), for example `The input path was rejected: not an absolute path. Path: ...`. A well-formed path outside every configured input root (including one that resolves through a link outside those roots) reports `The input path is outside every configured input root` and lists the configured roots when they fit a small bound (at most 8 roots and 150 characters of root text); when only a prefix fits it says `First K of N configured roots: ...` and points to the `sqlharness_capabilities` `inputRoots` pages (`inputRootCursor`) for the rest, and when not even one root fits it states the count and points to those pages. Every rejection message stays within the default 512-character cell limit. With no roots configured the message says file inputs are disabled. Resolved link targets are never echoed.

- SQL tools take exactly one of inline `sql` or `file`; the plan tool takes exactly one of inline `content` or `file`. Inline payloads above 1 MiB are rejected and must arrive as files under an input root. SQL and plan files are admitted up to 16 MiB, matching the CLI/Core SQL bound.
- Parameter-set files must use the `.sqljson` extension, are limited to 64 KiB, and are parsed with the shared Core strict parser: only `name` and `parameters`, no BOM, comments, or trailing commas. Values are never copied into reports.
- Parameters travel as `{name, type, value}` triples with culture-invariant string values or JSON null. The adapter maps them onto the existing Core binder and keeps its name, duplicate, and type validation; there is no second SQL validator and no second type list.
- A matrix argument carries a typed name and type plus a `values` array of at least two distinct elements, each a culture-invariant string or JSON null. Every element reaches the Core binder whole: a comma is an ordinary character, an empty string is a text value, and JSON null is a typed NULL of the matrix type (`null`, `""`, and the text `"null"` are three distinct values). Parameters and matrix values are never joined into or split from text on this path; the CLI `--matrix name:type=v1,v2` text grammar is unchanged. The same null carries through to the result: a compare-matrix cell's `parameterValue` is JSON null only for the cell that bound a typed NULL matrix value, and a plain string for every other cell.

## Client workspace roots

On the handshake revisions (`2025-06-18`, `2025-11-25`), when the client
declares the `roots` capability, SQLHarness requests `roots/list` after
`initialized`, and again on `roots/list_changed` only if the client declared
`listChanged`. At most 32 roots are kept. On `2026-07-28` a server-to-client
`roots/list` succeeds only while the originating `tools/call` is still waiting
for it, and a background fetch hangs until the 5 s timeout. Because a roots
fetch must never block a tool call, SQLHarness does not request roots on
`2026-07-28` sessions: their roots stay unknown (NULL). MRTR and
`subscriptions/listen` are not implemented. A successful answer is recorded in
the local activity journal for the dashboard and nothing else: client roots
never widen `--input-root`, never authorize a file input, and never change a
tool result. A failed or slow (5 s) request keeps the previous snapshot; stderr
names only the failure class.

## Tools

Exactly 10 tools are registered explicitly by name. No assembly scanning is used, so adding a public method can never silently widen the surface.

| Tool | Behavior |
|---|---|
| `sqlharness_capabilities` | Server versions and build, scope mode, engine, tool list, and limits; request mode also reports only the explicitly allowlisted profile names, and both modes report the configured input roots (`inputRoots`, see below). Optional local diagnostics contain counts and existence flags only. Never disclosed: secrets, environment values, the dashboard token, unrelated filesystem paths, or profiles outside the allowlist. |
| `sqlharness_inspect` | One catalog inspection with fixed internal probes: `ping`, `schema`, `counts`, `space`, `qstop`, or `indexes`. No SQL input. `qstop` and `indexes` are SQL Server only and are rejected before connecting on Postgres. It runs under the process gate like the other database tools. |
| `sqlharness_validate` | Static check of SQL effects visible in the text (see `safetyAnalysis`) for usage `query`, `setup`, or `benchmark`. One Core offline classifier serves every usage; it never connects, and object and permission status stays unknown. `benchmark` adds the measured-batch shape check. The tool carries no setup-SQL input: a setup-dependent batch validates under engine query rules, as if executed without setup. In request mode it needs a scope even though it remains target-free at execution time. |
| `sqlharness_query` | Bounded query passing the static visible-effects text check, with timeout and row cap. No persistent-mutation flags are offered on this path; hidden effects beyond the text are limited only by the DB account role prepared outside SQLHarness. |
| `sqlharness_measure` | Measure one query across repeats, with optional setup and `.sqljson` parameter-set files. Same sessions and rules as the CLI. |
| `sqlharness_compare` | Compare baseline versus candidate with the CLI sessions and equivalence rules, with an optional single matrix dimension. Optional `compareNullability` (boolean, default `false`) also requires matching column nullability (`allowNull`) for equivalence; it is rejected with `compareResults: "off"` (`compareNullability requires compareResults ordered, multiset, or set.`). Results always carry `compareNullability`, `nullabilityEquivalent` (`true`, `false`, or `null` when not comparable or unavailable, including when the provider reported no nullability for a compared column; that column's `allowNull` elsewhere still reads `true`) and `mismatchReason` (`"nullability"` only when strict mode alone caused the mismatch) in the equivalence object, including matrix cells. Artifacts written before this change lack these fields: `sqlharness_artifact` reads `compareNullability` as `false` and `nullabilityEquivalent` as unavailable. |
| `sqlharness_watch` | Poll a bounded query passing the static visible-effects text check until `until` or `untilUnchanged`, within interval and max-duration bounds. The watch deadline is capped by the remaining request budget. |
| `sqlharness_snapshot` | Capture a named result, which never overwrites an existing name, or diff live results against it without printing cell values. There is no force flag. |
| `sqlharness_plan` | Offline plan-document distillation. Optional `includeText` (boolean, default `true`, fixed and request modes). The default keeps bounded statement text (`sql`) and operator predicates after credential-pattern filtering only: password/pwd, access token, client secret, account key and shared access key fields, JWTs, and string literals compared with columns named like those keys; other ordinary SQL literals are kept, so plan text can contain data and the caller chooses that surface. This is not complete sanitization: secrets in columns not named like credentials, or in free text without a key, are not detected. `includeText=false` returns metadata-only statements and operators with no text or predicates. Cell-limit clipping and truncation indicators still apply; the input document is never returned or persisted. Errors stay credential-filtered. |
| `sqlharness_artifact` | Read one safe section (`summary`, `metrics`, `operators`, `statements`, `matrix-cells`) of a saved benchmark artifact. `matrix-cells` accepts an optional zero-based `cursor` and returns a bounded page with a continuation cursor. |

Tool annotations are conservative local-effect hints only: benchmarks write artifacts, and snapshot capture writes data, so those tools never claim read-only. Hints never replace policy enforcement.

### `sqlharness_validate` usage

The `usage` argument (`query`, `setup`, `benchmark`, case-insensitive) selects the caller intent in the
shared Core offline classifier — the same classifier the CLI `validate --usage` path uses, so both surfaces
return the same decision for the same SQL, usage, and engine. `query` applies the engine query rules
(default); `setup` classifies the batch as session-local preparation; `benchmark` adds the measured-batch
shape check of the execution path (`benchmark_batch_not_supported`; on Postgres a multi-statement batch is
rejected, on SQL Server the check is a no-op). The report carries the decision (`allowed`, safe `reason`
codes, never SQL or values), statement-level AST locations (SQL Server only), and the explicit
`checkedConditions` scope for the usage; `objectAndPermissionStatus` stays `"unknown"` and `executed`
stays `false`. Unlike `measure`/`compare` (and unlike CLI `validate --setup`), the validate tool accepts no
setup-SQL input: query and benchmark pass no setup context, so a setup-dependent batch (for example one
reading a session temp table) validates under engine query rules here — the same verdict as execution
without setup. This gap is by contract, not a second classifier.

## No persistent mutations

The MCP server v1 exposes no explicit persistent DML or DDL statement path. Query operations use `AllowMutation: false` with no confirmation database, and the Core classifier enforces the static visible-effects check. Every non-approved PostgreSQL command also runs in its own `READ ONLY` transaction; PostgreSQL rejects persistent table and sequence writes, including writes reached through user functions (SQLSTATE `25006`, safety exit `2`). Allowed Postgres TEMP setup runs outside that per-command wrapper and relies on the existing static visible-effects and temp-target checks; nextval and setval remain denied in setup. Static validation does not inspect user-defined function bodies, so setup SQL can call a persistent-writing function. Review setup SQL and use a least-privilege role, but do not treat validation as proof that every effect is rejected. `READ ONLY` does not contain every administrative or external effect: the classifier denies known residual calls such as statistics resets, promotion and backup operations, and least-privilege database roles must constrain other effects hidden in user functions. The `read-only` validation label means no visible mutation and no session-local work; it is not proof that arbitrary user functions have no hidden effects. Snapshot capture stores under a fresh name only. Session-local temporary objects follow the same Core rules as the CLI (SQL Server local `#temp`, Postgres native `TEMP`). On SQL Server, plain `SET @v = <expr>` is allowed in setup when its scalar local is declared earlier in the same batch; the RHS retains external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied. Setup that references parameters is split at the first statement that references a parameter: statements before that point run in a parameter-free root-scope command in their original order, and the remainder runs parameterized on the same session. Every `CREATE TABLE #t` and `SELECT ... INTO #t` must come before the first parameter-referencing statement; any temp-creating statement at or after that point is rejected offline. A local variable declared before this split cannot be referenced after it and is rejected offline. `CREATE TABLE #t (...); INSERT #t ... VALUES (@p)` is supported, while `SELECT ... INTO #t ... WHERE ... = @p` or a parameterized `CREATE TABLE` definition is rejected. Setup variables do not cross into baseline, candidate, or measured batches; pass a supported typed parameter or declare the local in each batch that uses it. An unproven table-position variable remains denied, and named user-defined table types do not prove an inline table variable. Persistent mutations stay behind the future separate approval-tray contract (plan 08); a client-side approval text is never accepted as authorization.

## Result envelope, errors, and budgets

Every tool returns a versioned agent envelope (`schemaVersion`, `command`, `status`, `exitCode`, `result`, `error`, `truncation`) carried in both representations: `StructuredContent` holds the parsed envelope and a single text block holds the same compact JSON. There is no third descriptive or table representation.

- Statuses: `success`, `partial` (failed matrix-cell batch), `error`, `watch_max_duration` (exit 7), and `snapshot_differences` (exit 8). The two controlled outcomes are valid results with `isError=false`; only failures set `isError=true`.
- Budgets are enforced on the whole serialized `CallToolResult` in UTF-8 bytes, including both representations, JSON escaping, and SDK metadata: default 16384 B, accepted range 4096..1048576. The tools/list catalog budget is 32768 B for at most 10 tools. The per-cell character limit defaults to 512 and an operator may configure at most 4096.
- JSON is never truncated to fit: projection first tries an untruncated render, then descends through detail limits until the wire cost fits, ending in a minimum valid error envelope that always fits the smallest budget.
- Query fallback priorities (`sqlharness_query`; the CLI agent output uses the same projection). Detail limits for a query descend `unbounded, 128, 64, 32, 16, 8, 4, 2, 1, 0`, and `truncation.detailLimit` reports the level that fit. The first attempt is the full report. While the limit is above 0, every result set and every column stays, each emitted row keeps all of its cells (so rows always align with the complete `columns`), and each set emits a leading prefix of its rows. A set with more rows than the limit is still emitted whole when its complete rows fit a conservative reserve: the reserve is one third of the budget minus the rowless envelope (every set's inventory and complete schema), and it goes to sets in ascending order of their complete-row cost (ties by ordinal), whole set or nothing. Every other set is sampled to the limit, so a small summary set is not cut to a wide detail set's sample. Messages are capped at the same limit; dropped messages are counted only in `truncation.omittedItems`. Cell values are only clipped to the cell limit (binary cells whose base64 exceeds the cell limit use the `{byteLength, base64Prefix, truncated}` form, shorter ones are plain base64 strings); a clipped cell is never dropped. At limit 0 no messages are emitted, and the fallback spends a conservative share of the budget (one third; the real wire measurement stays the authority) in this order, each step taking only what is left: (1) the result-set inventory (`ordinal`, `rowCount`, `omittedRowCount`, `omittedColumnCount`) for as many leading sets as fit, counting the rest in `omittedResultSetCount`; (2) complete column schemas, cheapest schema first (ties by ordinal), whole schema or nothing; (3) complete rows for sets whose whole schema was kept, cheapest set first (ties by ordinal), whole set or nothing; (4) a leading column prefix, in ordinal order, for each set whose schema did not fit whole (such a set carries no rows); (5) a leading row prefix, in ordinal order, for each whole-schema set whose complete rows did not fit. So small summary sets keep their complete schemas and rows ahead of a wide detail set's columns or rows. If even the envelope does not fit, the minimal `output_budget_too_small` envelope applies.
  - Additive fields, always present: `result.omittedResultSetCount` (trailing source result sets not emitted; nonzero only at limit 0); per result set `ordinal` (zero-based position in the source report, equal to its index in `resultSets` because emitted sets are a prefix) and `omittedColumnCount` (trailing columns not emitted; nonzero only at limit 0, and a set with omitted columns has an empty `rows`). `omittedRowCount` is the source omitted count plus rows not emitted: `rows.length + omittedRowCount` equals the source rows plus the source omitted rows. `resultHash`, `target` and the raw footprint are unchanged.
  - `truncation.omittedItems` is an aggregate only: an omitted result set counts 1, each omitted column counts 1, each omitted row of a kept set counts 1, and clipped cells and dropped messages count too. The structured per-set and per-query counts above are the authoritative accounting; nothing implies a set, column or row exists unless the counts say so.
- `compare --matrix` results that do not fit in full provide a scope-owned `matrixArtifactId` and a continuation cursor. Fetch omitted cell ids through the `matrix-cells` artifact section under the same scope; parameter values and filesystem paths stay out of the projection.
- Error text may quote the caller's SQL and parameter values. Only credentials (the SQL password, JWTs, and the values of explicit credential fields such as `password`, `pwd`, `access_token`, `client_secret`) are redacted. Server, database, and user names stay readable. Redaction does not guarantee that other sensitive SQL or result data is absent.
- One database operation runs per process. A second concurrent database call is rejected immediately with a stable BUSY result (`busy` code, exit 2, `isError=true`): no queue. The result's `error.hint` names the running tool (a fixed tool identifier, never scope or user data) and tells the client to wait for it to return before sending the next call. Safe local tools (`capabilities`, `validate`, `plan`, `artifact`) may run in parallel because every call builds its own operation records with no shared mutable state.

## Concurrency, deadline, cancellation, and progress

`query`, `measure`, `compare`, `watch`, `snapshot`, and `inspect` run under one process-wide gate shared across every request scope; a matrix or parameter-set batch travels inside its single call and counts as one operation. A second concurrent database operation receives the stable `busy` outcome with a hint naming the running tool. Every execution is bounded by the process time budget (default 900 s, range 1..86400 s) through a linked deadline that always reaches Core.

Cancellation propagates to connection, execution, reads, delays, and artifact writes. Closing the process (host shutdown) or stdin EOF cancels an in-flight call, which reports a stable cancelled result (`isError=true`), never the natural watch-deadline exit 7. The gate is released on every path, so a cancelled or failed call never blocks the next one.

Progress notifications go out at most once per second per call, only when the client supplied a progress token, and carry only the command and stage: never SQL, parameters, or rows. Watch does not emit NDJSON on the MCP path; NDJSON stays a CLI-only surface.

The activity journal records why a call was cancelled: `client` (the client sent
`notifications/cancelled`), `shutdown` (stdin closed or the host stopped), or
`deadline` (the call time budget ran out).

## Artifacts

Benchmark and snapshot artifacts are read through the shared controlled reader with a scope-bound opaque id: a call can only address artifacts of its own scope, never arbitrary paths or other profiles' artifacts. The safe benchmark sections are `summary`, `metrics`, `operators`, `statements`, and `matrix-cells`. `statements` reports each variant's statement ordinal, SQL hash, CPU and elapsed time from SQL Server `QueryTimeStats`, degree of parallelism, and up to ten operators ranked by actual rows and executions. It omits statement text and carries an omitted-statement count when the bounded projection is truncated. PostgreSQL plans do not provide this SQL Server statement section, so it is empty for those artifacts. For an oversized compare matrix, the response gives scope-owned artifact ids and a `matrixArtifactId` with a continuation cursor; call `sqlharness_artifact` with that id, `section: "matrix-cells"`, and the cursor to fetch the next bounded page, then read each returned cell id through `summary`, `metrics`, `operators`, or `statements`. Every page repeats the same scope-owner check, and wire budgets apply to each response. There are no MCP resources in v1; the single artifact tool is the only detail surface. Raw plans, `queries.jsonl` contents, and snapshot cells are never exposed.

Every live publish stamps the frozen scope owner into versioned metadata written by the trusted Core writer (the MCP adapter and the CLI never stamp it themselves): the benchmark `ArtifactManifest` and the `SnapshotDocument` each carry a strictly additive, nullable `owner` field (`profile`, `vars`, `engine`, `server`, `database`, camelCase in JSON). Reports (`report.json`, `runs.jsonl`) are unchanged; the bounded `statements.json` artifact section contains hashes and plan metrics but no SQL text. Parameter and var values are never copied into reports, only into manifest metadata. Enforcement is opt-in per call inside Core, before any report projection: MCP always passes the frozen scope owner, the CLI passes none and keeps working by name. A scope mismatch is refused with a constant content-free message (`"The artifact is not available in the current scope."`, exit 2 `Safety`; snapshots: `"The snapshot is not available in the current scope."`, exit 2) with `isError=true` and no projection, so no operator names, index names, or cell values leak. Unavailable storage stays exit 6 `LocalStorage`. Snapshot capture additionally refuses before connecting when the existing name belongs to another scope (no self-grant), and scoped diff refuses a foreign baseline before connecting; a scoped capture over the scope's own existing name without force still runs the query and then reports `LocalStorage` (no overwrite), exactly like the CLI.

`compare --matrix` cells inherit the calling scope's owner (same profile/vars/engine/server/database; the matrix value is an execution parameter, not scope identity). `measure --param-set` carries one scope owner for the whole `measure-set` artifact; individual sets (name, value hash, types) are data inside, not separate owners. Legacy compatibility: v1 artifacts and snapshots without an owner stay byte-identical on write (the key is omitted, not nulled) and remain readable by the CLI by name, but MCP refuses them — fail closed, with no automatic migration and no way for MCP to grant itself access by name. The local process-privileged writer is outside the integrity boundary: the manifest is scope metadata for one trusted host, not a cryptographic proof against manual local edits.

## Stdout and stderr

Stdout carries only protocol frames. All logging goes to stderr without arguments, SQL, parameter values, or secrets. SDK log entries sent to stderr are limited to `Warning`, `Error`, and `Critical` levels and contain only the category, level, and numeric event ID; message, state, and exception content are never written. SQLHarness may also write generic, content-free stderr lines for a pre-handshake startup failure (exit 2), an unexpected transport failure (exit 1), a failed detached-dashboard launch, or a roots diagnostic naming only a count (`kept N of M`) or a failure class (`roots/list timed out`, `roots/list failed (<ExceptionType>)`). Clean shutdown on EOF or cancellation exits 0. Tool calls that reach the SQLHarness module (inspect, query, measure, compare, watch, snapshot, plan) are recorded in the local activity journal with the client's `clientInfo` name, version and title, the negotiated protocol revision, and a raw token estimate. When the final budgeted `CallToolResult` is built, the MCP handler also records an emitted estimate from the SDK-serialized UTF-8 bytes of that complete result representation, including text, structured content, and metadata. The `ceil(bytes / 4)` value estimates this representation; it is not actual model tokenization or the full JSON-RPC transport frame. Historical raw-only MCP rows stay unavailable and are not backfilled. Each recorded call also keeps allowlisted `tools/call._meta` labels: the client's call id (Claude Code `claudecode/toolUseId`, Codex `callId`), opencode `ai.opencode/sessionID` as the agent session id, and from Codex `x-codex-turn-metadata` the model, reasoning effort, agent session id, turn id, turn trigger and thread source; other `_meta` keys are never stored. `sqlharness_capabilities`, `sqlharness_validate`, and `sqlharness_artifact` are not recorded; journal diagnostics go to stderr only, and journal failures never change a tool result.

With `dashboard.autoStart: true` in `~/.sqlharness/config.json`, a validated startup (an invalid startup configuration never launches anything) also launches `sqlharness dashboard --background` as a detached process that inherits none of the server's standard handles, with the SQLHarness home as working directory, when no dashboard is running for that home, and starts one journal retention pass in the background (it does anything only with `journal.retention.enabled: true`). Both happen before the transport starts, neither writes to stdout, and a failed launch writes one line to stderr and never changes the server's behavior. The MCP client still sees EOF when the server exits, while the dashboard keeps running until it idle-exits after `dashboard.idleShutdownHours` (default 8). Known limitation: the dashboard is not placed in a new session or process group, so Ctrl+C or SIGHUP delivered to the client's process group (or closing its console on Windows) also stops it; the next `mcp serve` relaunches it.

## Versions

- MCP SDK: `ModelContextProtocol` 2.2.0 (`ModelContextProtocol.Core` 2.2.0), pinned in `Directory.Packages.props`.
- Offered protocol revisions: `2025-06-18`, `2025-11-25`, `2026-07-28`. The server negotiates: an `initialize` for `2025-06-18` or `2025-11-25` gets that revision; an `initialize` for an older revision gets `2025-11-25`; a `2026-07-28` client uses `server/discover` and per-request `_meta`.
- `sqlharness_capabilities` reports the revision negotiated for the current request and `supportedProtocolVersions`.
- Real-client check before release: `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness <worktree build>` (manual; covers Claude Code, Codex, Copilot CLI, opencode and Grok Build, each skippable with `-Skip<Client>`; `-OpencodeModel` picks a free opencode model when the default hits a usage limit; the Grok run trusts its scratch folder in `~/.grok/trusted_folders.toml` for the duration and restores the file byte for byte).

## Context cost

Result cost is measured as real wire bytes: the `CallToolResult` serialized with the SDK JSON options, covering both representations plus escaping and metadata. Token estimates reuse the Core `OutputFootprint.EstimateTokens` heuristic (`utf8-bytes-div-4`: bytes divided by four, rounded up). Byte counts are not token counts: do not report bytes as actual tokens, and do not assume which representation a client displays.

Scenario comparison counts the whole MCP run: tools/list discovery plus the call plus any follow-up artifact read, against the CLI agent output for the same outcome. Discovery stays a separate visible cost and is never folded into saved data. The offline comparison asserts only that the scenario total exceeds the single call and that each side fits its budget (call within 16384 B, discovery within 32768 B). MCP is not promised to be cheaper per call.

## Zakres dowodów

Executed: published single-file stdio smokes for both fixed and request-scoped modes on the native win-x64 runner. The request-mode proof uses two synthetic profiles and exercises per-call validation; it does not register or launch the server through an MCP client's private configuration. The MCP test project passed its offline tests, with live-database and non-native RID cases skipped. The full solution offline run exposed two failures in `SqlHarness.Tests`: one output-size fixture mismatch also reproduced on the request-scope implementation's base commit, and one transient artifact-directory `IOException` that passed on focused rerun. The cost comparison above comes from offline tests on synthetic data, without opening a database.

Not executed: published-binary runs on linux-x64 and osx-arm64 (binaries build; the per-RID smoke tests Skip without native runners), live SQL Server and Postgres stdio drives (Skip without explicit disposable-database authorization), or private client registration/startup. A stdio driver is protocol evidence, not actual-client evidence. Live scenarios, client registration, and additional RIDs must not be reported as passing.
