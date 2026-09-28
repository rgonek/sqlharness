# SQLHarness MCP server (local stdio)

One `sqlharness` process can serve the SQLHarness diagnostics and benchmarks to an MCP client over local stdio. The server is part of the same single-file distribution, calls into `SqlHarness.Core` through the same module the CLI uses, and enforces the same safety rules. Only local stdio is supported, one client per process. There is no HTTP/SSE transport, no ports, no OAuth, no sampling, prompts, elicitation, or experimental tasks.

## Start

Exact syntax:

```powershell
sqlharness mcp serve <profile> --var key=value [--input-root <absolute-directory>]
```

- `<profile>` is a closed target profile from the operator's profile store, with fixed `--var key=value` pairs resolved once at startup.
- `--input-root` is repeatable and takes an absolute directory that allows file inputs. By default no input roots are configured, which means no file inputs are admitted at all.
- `--max-result-bytes` sets the process response cap in bytes: default 16384, accepted range 4096..1048576. A single tool call may only lower it.
- `--max-operation-seconds` sets the process database time budget in seconds: default 900, accepted range 1..86400. A single tool call may only lower it.
- `--unsafe-direct` is blocked for `mcp serve` and rejected with exit 2. The MCP server always runs on a closed named profile; direct server/database targets are not available on this path.

## Client configuration

SQLHarness never installs or edits a client configuration automatically. To register the server, paste an entry manually into the MCP client configuration file. Neutral shape (field names vary by client):

```json
{
  "command": "sqlharness",
  "args": ["mcp", "serve", "prod-eu", "--var", "tenant=acme", "--var", "env=uat", "--input-root", "C:\\work\\sqlharness-inputs"],
  "env": {}
}
```

Connection secrets stay in the process environment and the operator's profile store. Tool arguments never carry profile, server, database, vars, auth, engine, `unsafeDirect`, `allowMutation`, `confirmDatabase`, or password fields, and unknown argument keys are rejected before execution.

## Scope lifetime

The operator supplies the profile and vars once at process start. The server reads the profile set once, resolves the target once, and freezes an immutable scope for all calls in that process. Editing `targets.json` while the server runs cannot redirect the next call; a different profile, variables, or target requires a restart. Start and discovery open no database connection and perform no interactive login. One process serves one profile with one variable set.

## Input roots and file inputs

Inline input works without filesystem access. File input is admitted only under the absolute `--input-root` directories frozen at startup. The reader rejects path traversal, UNC and network paths, NTFS alternate streams, symlink and reparse-point escapes (including linked parent directories), and files replaced while they are read. Each file is opened once with a bounded read.

- SQL tools take exactly one of inline `sql` or `file`; the plan tool takes exactly one of inline `content` or `file`. Inline payloads above 1 MiB are rejected and must arrive as files under an input root. SQL and plan files are admitted up to 16 MiB, matching the CLI/Core SQL bound.
- Parameter-set files must use the `.sqljson` extension, are limited to 64 KiB, and are parsed with the shared Core strict parser: only `name` and `parameters`, no BOM, comments, or trailing commas. Values are never copied into reports.
- Parameters travel as `{name, type, value}` triples with culture-invariant string values or JSON null. The adapter maps them onto the existing Core binder and keeps its name, duplicate, and type validation; there is no second SQL validator and no second type list.
- A matrix argument carries a typed name and type plus a string-values array; values not representable in the Core contract are rejected instead of reinterpreted.

## Tools

Exactly 11 tools are registered explicitly by name. No assembly scanning is used, so adding a public method can never silently widen the surface.

| Tool | Behavior |
|---|---|
| `sqlharness_capabilities` | Server versions and build, scope engine, tool list, and limits; optional local diagnostics with counts and existence flags only, never secrets, paths, or profile lists. |
| `sqlharness_inspect` | One read-only catalog inspection: `ping`, `schema`, `counts`, `space`, `qstop`, or `indexes`. No SQL input. `qstop` and `indexes` are SQL Server only and are rejected before connecting on Postgres. |
| `sqlharness_validate` | Offline SQL classification for usage `query`, `setup`, or `benchmark`. One Core offline classifier serves every usage; it never connects. |
| `sqlharness_query` | Bounded read-only query with timeout and row cap. Persistent mutation is always off. |
| `sqlharness_measure` | Measure one query across repeats, with optional setup and `.sqljson` parameter-set files. Same sessions and rules as the CLI. |
| `sqlharness_compare` | Compare baseline versus candidate with the CLI sessions and equivalence rules, with an optional single matrix dimension. |
| `sqlharness_watch` | Poll a bounded read-only query until `until` or `untilUnchanged`, within interval and max-duration bounds. The watch deadline is capped by the remaining request budget. |
| `sqlharness_snapshot` | Capture a named result, which never overwrites an existing name, or diff live results against it without printing cell values. There is no force flag. |
| `sqlharness_plan` | Offline plan-document distillation. Sanitized projection only: no statement text and no literal predicates. |
| `sqlharness_artifact` | Read one safe section (`summary`, `metrics`, `operators`) of a saved benchmark artifact. |
| `sqlharness_gain` | Local output-savings aggregate with an explicit heuristic and net delta. |

Tool annotations are conservative local-effect hints only: benchmarks write artifacts, snapshot capture writes data, and gain accounting may write to disk, so those tools never claim read-only. Hints never replace policy enforcement.

## No persistent mutations

The MCP server v1 exposes no persistent DML or DDL. Query operations are built with always `AllowMutation: false` with no confirmation database, and the Core classifier enforces read-only on top. Snapshot capture stores under a fresh name only. Session-local temporary objects follow the same Core rules as the CLI (SQL Server local `#temp`, Postgres native `TEMP`). Persistent mutations stay behind the future separate approval-tray contract (plan 08); a client-side approval text is never accepted as authorization.

## Result envelope, errors, and budgets

Every tool returns a versioned agent envelope (`schemaVersion`, `command`, `status`, `exitCode`, `result`, `error`, `truncation`) carried in both representations: `StructuredContent` holds the parsed envelope and a single text block holds the same compact JSON. There is no third descriptive or table representation.

- Statuses: `success`, `partial` (failed matrix-cell batch), `error`, `watch_max_duration` (exit 7), and `snapshot_differences` (exit 8). The two controlled outcomes are valid results with `isError=false`; only failures set `isError=true`.
- Budgets are enforced on the whole serialized `CallToolResult` in UTF-8 bytes, including both representations, JSON escaping, and SDK metadata: default 16384 B, accepted range 4096..1048576. The tools/list catalog budget is 32768 B for at most 11 tools. The per-cell character limit defaults to 512 and an operator may configure at most 4096.
- JSON is never truncated to fit: detail levels descend until the wire cost fits, ending in a minimum valid error envelope that always fits the smallest budget.
- One database operation runs per process. A second concurrent database call is rejected immediately with a stable BUSY result (`busy` code, exit 2, `isError=true`): no queue, no retry hint. Discovery and safe local tools may run in parallel because every call builds its own operation records with no shared mutable state.

## Concurrency, deadline, cancellation, and progress

`query`, `measure`, `compare`, `watch`, and `snapshot` run under the process gate; a matrix or parameter-set batch travels inside its single call and counts as one operation. Every execution is bounded by the process time budget (default 900 s, range 1..86400 s) through a linked deadline that always reaches Core.

Cancellation propagates to connection, execution, reads, delays, and artifact writes. Closing the process (host shutdown) or stdin EOF cancels an in-flight call, which reports a stable cancelled result (`isError=true`), never the natural watch-deadline exit 7. The gate is released on every path, so a cancelled or failed call never blocks the next one.

Progress notifications go out at most once per second per call, only when the client supplied a progress token, and carry only the command and stage: never SQL, parameters, or rows. Watch does not emit NDJSON on the MCP path; NDJSON stays a CLI-only surface.

## Artifacts

Benchmark and snapshot artifacts are read through the shared controlled reader with a scope-bound opaque id: a call can only address artifacts of its own scope, never arbitrary paths or other profiles' artifacts. Only the safe sections `summary`, `metrics`, and `operators` are exposed. There are no MCP resources in v1; the single artifact tool is the only detail surface. Raw plans, `queries.jsonl` contents, and snapshot cells are never exposed.

## Stdout and stderr

Stdout carries only protocol frames. All logging goes to stderr without arguments, SQL, parameter values, or secrets. A pre-handshake startup failure writes one generic line to stderr and exits 2 without echoing values or secrets; an unexpected transport failure exits 1. Clean shutdown on EOF or cancellation exits 0.

## Versions

- MCP SDK: `ModelContextProtocol` 2.2.0 (`ModelContextProtocol.Core` 2.2.0), pinned in `Directory.Packages.props`.
- Tested protocol revision: 2025-11-25, pinned on both client and server in tests. No other revision is declared.

## Context cost

Result cost is measured as real wire bytes: the `CallToolResult` serialized with the SDK JSON options, covering both representations plus escaping and metadata. Token estimates reuse the Core `OutputFootprint.EstimateTokens` heuristic (`utf8-bytes-div-4`: bytes divided by four, rounded up). Byte counts are not token counts: do not report bytes as actual tokens, and do not assume which representation a client displays.

Scenario comparison counts the whole MCP run: tools/list discovery plus the call plus any follow-up artifact read, against the CLI agent output for the same outcome. Discovery stays a separate visible cost and is never folded into saved data. The offline comparison asserts only that the scenario total exceeds the single call and that each side fits its budget (call within 16384 B, discovery within 32768 B). MCP is not promised to be cheaper per call.

## Zakres dowodów

Executed: published single-file stdio smoke on the native win-x64 runner (PASS in `McpStdioProcessTests`), the full offline test suite, and the NuGet dependency audit. The cost comparison above comes from the offline `McpGainTests` on synthetic data, without opening a database.

Not executed: published-binary runs on linux-x64 and osx-arm64 (binaries build; the per-RID smoke tests Skip without native runners), and live SQL Server and Postgres stdio drives (Skip without explicit disposable-database authorization). Live scenarios and additional RIDs must not be reported as passing.
