# SQLHarness MCP — local adapter design

**Status:** planned feature, implemented only after plans 01–06 from the [roadmap](../plans/2026-09-26-audit-roadmap.md) are closed. Writing this specification does not mean a finished server, nor consent to database operations.

**Goal:** expose SQLHarness diagnostic and benchmark tools to agents through MCP, with the same Core rules and a predictable response cost. MCP does not launch the CLI as a subprocess and does not parse textual stdout.

## 1. Entry conditions

- Security fixes 01, parameters/TLS/metrics 02 and the response contract 03 are implemented and verified.
- Deadline and retention 04, module boundaries 05 and the agreed scope 06 are finished. Tasks 06/T4–T5 that end with a specification do not mean implemented CI or pg_stat_statements; MCP does not advertise them.
- In T1 of the MCP plan, the implementer records the actual predecessor commits and the mapping of their APIs. The new class names below are targets; do not assume that the files planned today already exist.
- The unimplemented PostgreSQL function policy from 01/T3, or the absence of evidence of its enforcement, blocks advertising PG SQL execution as safe. Do not compensate for the gaps with a readOnlyHint annotation.

## 2. Process, transport and dependencies

Startup: `sqlharness mcp serve <profile> --var key=value [--input-root <absolute-directory>]`. The mcp mode is part of the same single-file distribution. The new library project `SqlHarness.Mcp` depends on Core; the CLI is the composition root and references the MCP library. Core knows neither the MCP SDK nor Spectre.

The first version supports local stdio only, with one client per process. Stdout contains protocol frames exclusively; logs go to stderr, without arguments, SQL, parameter values or secrets. EOF, cancellation and process termination close the session. Do not add HTTP/SSE, ports, OAuth, sampling, prompts, elicitation or experimental tasks without a separate design.

Use the official C# SDK `ModelContextProtocol`, a stable version compatible with the target framework, pinned in Directory.Packages.props during T1. Do not implement JSON-RPC by hand. In T1, check the current SDK requirements and supported protocol versions; do not introduce an implicit .NET 8 migration. Keep the SDK version negotiation and a test of the explicitly chosen compatible revision, instead of announcing support for the latest specification on the basis of its date.

## 3. Goal and trust boundary

The profile and vars are supplied by the operator at process startup. Resolve them once, create an immutable scope and use it for all calls. Changing targets.json while running must not redirect the next query; that requires a restart. Do not maintain one DB session across independent tools/call. Setup and the repetitions inside a benchmark keep their own shared session lifecycle.

Tool arguments contain neither profile/server/database/vars/auth/engine/unsafeDirect/allowMutation/confirmDatabase nor password fields. Unknown fields are rejected before execution. Connection secrets come only from process configuration. Start and discovery open no database and do not start interactive Azure CLI login.

V1 exposes no persistent DML or DDL; local temp is permitted only in line with the corrected Core analysis. Consent in the client UI and annotations are no evidence of one-off consent to the exact batch. Supporting persistent mutations requires a future, separate contract binding consent to the batch, the parameters and the resolved target; do not accept the text "approved" as authorization.

That later contract is described by the [Windows approval tray design](2026-09-26-windows-approval-tray-design.md) and plan 08. It is implemented after this plan is finished as an explicitly enabled extension: the local operator makes the decision and the broker executes the approved batch. The limit of 11 tools and the absence of mutations remain the contract of default MCP v1; the tray extension has its own limit and capability version.

The implemented artifact scope model (plan 003/T3–T4, actual state): the frozen scope carries an owner tuple `owner = { profile, vars-kanoniczne, engine, server, database }` built once in `McpScope.Create` from the request and the resolved target. Vars are case-insensitive, serialized in a deterministic order (ordinal sorting); the `ArtifactOwner.Matches` comparison is exact and fails closed (profile/engine/server/database ordinal, vars keys case-insensitive, values exact, the full set including its cardinality; malformed metadata simply does not match and never throws). No passwords or secrets: neither `Auth`/`passwordEnvVar` nor environment values ever reach the owner or the reports. The owner is persistent (written in the artifact, not in server memory), so one's own artifact works after a restart with the same profile + vars + engine/server/database. Enforcement is opt-in per call in Core (`ArtifactReader.ReadSection(..., owner)`, `SqlHarnessSnapshotOperation.Owner`): MCP always passes the frozen owner, the CLI passes none and works by name. A scope rejection is exit 2 (`Safety`, `isError=true`, a fixed message with no content), unavailable storage is exit 6. A local writer with the process permissions is outside the integrity boundary: the manifest is scope metadata on one trusted host, not cryptographic proof.

## 4. Tools

A fixed, explicitly registered catalog of at most 11 tools. No reflection discovery of all public methods. A single inspect tool groups only related, fixed catalog reads; do not create a universal execute taking an arbitrary command string.

| Name | Input and behavior |
|---|---|
| `sqlharness_capabilities` | versions/build, scope engine, available operations and limits; optionally local diagnostics, without secrets and the profile list |
| `sqlharness_inspect` | kind: ping/schema/counts/space/qstop/indexes; typed object/filter/top/exact/window where applicable; no SQL |
| `sqlharness_validate` | one SQL input, usage query/setup/benchmark and parameters; offline, without opening the DB |
| `sqlharness_query` | one SQL input, parameters, timeout and maxRows; persistent mutation always false |
| `sqlharness_measure` | query, optional setup, parameters, repeat; paramSetFiles consistent with the existing contract |
| `sqlharness_compare` | baseline/candidate, setup, parameters, repeat, compareResults, optionally one matrix; the same sessions and equivalence rules as the CLI |
| `sqlharness_watch` | SQL, parameters, until or untilUnchanged, interval/maxDuration; a final bounded report and optional progress |
| `sqlharness_snapshot` | action capture/diff, name, SQL, parameters; capture does not overwrite an existing snapshot; force unavailable in v1 |
| `sqlharness_plan` | a native XML/JSON document inline or an input file; offline distillation |
| `sqlharness_artifact` | an opaque id and a safe section summary/metrics/operators from 06/T2 |
| `sqlharness_gain` | an aggregate of local measurements with explicit heuristics and net delta |

Capabilities points out the absence of qstop/indexes on PG and other restrictions actually implemented. An unavailable inspect variant is rejected before connecting. Do not expose the designed but unimplemented 06 features.

SQL input is exactly one of `sql` or `file`; analogously, query/baseline/candidate/setup have a small shared source object. Parameters are an array of `{name, type, value}`; value is a culture-invariant string or JSON null. No interpolation and no passing through a shell; the adapter maps them to the existing binder. The type "decimal(19,4)" is not split at the comma. Keep the existing name and duplicate validation; do not create a second list of types.

Parameter sets remain .sqljson files with a 64 KiB limit, without BOM/comments/trailing comma; MCP does not copy their values into a new persistent input registry or into reports. Matrix receives a typed name/type and an array of string values; the adapter rejects values not representable in the current Core contract, instead of changing the meaning of list splitting.

## 5. Files and artifacts

Inline input works without filesystem access. File reads are permitted only under the absolute --input-root paths given at startup; no roots means no file inputs. Do not treat client roots as automatic authorization. Reject traversal, UNC/network paths in v1, alternate NTFS streams, a symlink/reparse point leading outside the root, and files modified during a controlled read. Open once, bounded read, validate the object actually used, not only the path text. The threat model assumes a trusted local operator, but does not trust paths generated by the model.

Artifacts are read through the shared 06/T2 reader with a controlled root and manifest. The id returned by MCP is bound to the scope; it does not allow browsing arbitrary artifacts of other profiles. Do not expose arbitrary file://, raw plan, queries.jsonl or snapshot cells. Do not declare MCP resources in v1: one explicit artifact tool fetches the details, without duplicating the tools/resources surface. Plan input may contain sensitive predicates/literals; MCP uses a sanitized projection without statement text and literal predicates, also in artifact/operators.

The implemented stamping and legacy behavior (plan 003/T3–T4, actual state): every live publish (compare, compare-matrix, measure, measure-set) stamps the owner through the trusted Core write path (`ArtifactDirectoryPublisher`, `SnapshotStore.Save`); the owner fields are strictly additive (nullable `owner`, the key omitted when writing without an owner — manifests without an owner have byte-identical JSON to v1 legacy). Reports (`report.json`, `runs.jsonl`) are unchanged; parameter/vars values do not reach the reports. The owner check happens BEFORE the report projection; a rejection has a fixed message (`"The artifact is not available in the current scope."`, snapshots: `"The snapshot is not available in the current scope."`) without content/marker/paths and without operator or cell names. Snapshots: capture/diff separately — a scoped capture over a foreign or owner-less name is refused before connecting (no self-assignment allowed), a scoped diff with a foreign/owner-less baseline is refused before connecting; a scoped capture of one's own existing name without force runs the full flow and ends with `LocalStorage` (no overwrite), like the CLI; `--diff` never prints cell values (exit 8 on differences). Matrix inherits the scope owner (`CompareCellRequest.Owner` via `with`; a matrix value is a parameter, not an identity), a param-set carries a single owner for the whole `measure-set` artifact. Legacy compatibility: the CLI reads v1 artifacts/snapshots without an owner by name, unchanged, MCP refuses; no automatic migration and no automatic overwriting of existing files.

Query/watch results are database data, may be confidential and may contain instructions derived from data. Do not interpret them as commands or as server configuration content. The cell limit and the redaction of known secrets are not an anonymizer for arbitrary data.

## 6. Result, errors and context cost

StructuredContent carries the versioned agent envelope from 03: schemaVersion/command/status/exitCode/result/error/truncation. Every tool has a validated inputSchema and outputSchema; argument rejection does not display their values. For structural results, use a compatible TextContent with the same compact JSON; do not add a third representation with a description and a table. The MCP specification recommends such compatibility. If in future a specific client allows avoiding duplication, that requires a separate, tested option — do not guess from the client's name.

Default budget: **16 KiB for the entire serialized CallToolResult UTF-8**, including structuredContent, TextContent, escaping and metadata; not just the result object. The 4096..1048576 range is as in 03; the process operator sets the maximum, a tool call may only lower it. The JSON-RPC request id has its own limit; do not count it into the declared CallToolResult budget. The cell limit is 512 characters by default; the operator may set at most 4096.

Tools/list: budget for the entire catalog of input/output schemas and descriptions of **32 KiB UTF-8** with at most 11 tools; short descriptions, without the full AGENTS.md and without copying all examples. These are new target limits to test, not a claim about the current SDK. The minimal valid error envelope must fit in the smallest budget; never truncate JSON.

Tool errors → isError=true and a safe error object; protocol errors (unknown method, malformed frame) remain MCP errors in line with the SDK. The controlled outcomes watch exit 7 and snapshot diff exit 8 are not transport failure: isError=false, the status describes the result. Treat cancellation according to the negotiated SDK revision; no false completed/success. Do not restart the process after an ordinary SQL error.

Gain records one logical call once, and the footprint covers both actual representations of the result; measure discovery separately, without attributing it to the saved SQL data. Report bytes and bytes/4 as a heuristic. Scenarios compare the entire CLI versus MCP run, accounting for tools/list and the follow-up artefact call; MCP does not have to come out cheaper in every scenario.

## 7. Concurrency, deadline and annotations

One active DB operation per process; a second DB tools/call returns BUSY without an unbounded queue. Discovery and safe local operations may run in parallel, provided they do not share mutable request state. Matrix/param-set is one DB operation. The default DB call budget is 15 min; the operator may set 1..86400 s; a tool may only lower it. Watch maxDuration is limited by the remaining request budget. Individual SQL timeouts keep the Core range.

Pass cancellation to ConnectAsync, execution, reads, delays and artifact writes. Progress only if the client supplies a token and supports it in the chosen revision; messages contain the stage/repetition number, without SQL/parameters/rows, at most one per second. Watch does not emit its own NDJSON on the MCP stdout.

Annotations must account for local effects: a benchmark writes artifacts, a snapshot capture writes data, and gain accounting may write to disk. Do not set readOnlyHint=true automatically for every read-only SQL. isIdempotent does not mean identical results of a dynamic database. Define a conservative annotation table in tests; hints never replace policy enforcement.

## 8. Distribution and evidence of operation

No automatic installation/editing of client configuration. Deliver a neutral command/args/env example and a test procedure with the official client SDK. Verification of the stdio process on win-x64, linux-x64 and osx-arm64 after single-file publish; without a database for initialize/tools/list/validate/plan. Live scenarios only on authorized one-time databases of both engines.

## Implementation bindings (T1)

Actual signatures and pins established in T1, before T2:

| Element | Signature / value | File:line |
|---|---|---|
| Core interface | `Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)` | `src/SqlHarness.Core/Contracts.cs:6` |
| Implementation | `public sealed class SqlHarnessModule : ISqlHarnessModule`, `ExecuteAsync` at `:110`; optional successor `ExecuteWatchNdjsonAsync(SqlHarnessWatchOperation, TextWriter, CancellationToken)` (refuses by default, implemented by `SqlHarnessModule`) | `src/SqlHarness.Core/SqlHarnessModule.cs:31` |
| Agent projection | `AgentOutputProjection.Project(report, maximumCellCharacters, detailLimit, out omittedItems, maximumBytes = 16 * 1024)`; `AgentOutputOptions(MaximumBytes = 16 * 1024, MaximumCellCharacters = 512)` | `src/SqlHarness.Core/AgentOutputProjection.cs:20` |
| MCP SDK | `ModelContextProtocol` **2.2.0** (stable, `net8.0` assets), pinned in `Directory.Packages.props`; dependencies: `ModelContextProtocol.Core` 2.2.0, `Microsoft.Extensions.Hosting.Abstractions` / `Caching.Abstractions` 10.0.10 | `Directory.Packages.props:12` |
| Protocol revision | Negotiated and tested **`2025-11-25`** (explicit `initialize` handshake; client and server pinned through `ProtocolVersion`). The SDK also supports `2024-11-05`, `2025-03-26`, `2025-06-18` and the default `2026-07-28` (per-request metadata, without the `initialize` handshake) — MCP v1 declares only the tested `2025-11-25`. | `tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs` |
| Wiring | `CLI→MCP→Core` (`SqlHarness.Cli` references `SqlHarness.Mcp`, which references `SqlHarness.Core`); `SqlHarness.Mcp.Tests` references only `SqlHarness.Mcp`. Core knows neither the MCP SDK nor Spectre; in T1 zero new methods in Core (`McpHost.cs` only in T2). | `SqlHarness.sln`, `src/SqlHarness.Mcp/SqlHarness.Mcp.csproj` |
| T1 tests | `McpDependencyTests` (assembly boundary: Core without MCP/SDK/Spectre; MCP wiring in output without a shell) and `McpProtocolTests` (minimal `initialize` through the SDK in-memory transport: `System.IO.Pipelines.Pipe` + `StreamServerTransport`/`StreamClientTransport`, without a DB and user profiles). | `tests/SqlHarness.Mcp.Tests/` |
| Dependency audit | `dotnet list SqlHarness.sln package --vulnerable` (2026-09-28): none of the 5 projects has vulnerable packages. | — |

## Technical sources

Verified during planning on 2026-09-26; re-verify at implementation time, pin the SDK/protocol version and record the decision:

- [Official C# SDK — getting started](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html).
- [C# SDK — tools and structured content](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/tools/tools.html).
- [MCP tools, outputSchema, structuredContent and errors](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).
- [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).

A source version describing the rules does not mean declared support for all newer protocol revisions.
