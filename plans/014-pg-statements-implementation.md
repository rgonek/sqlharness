# pgstop implementation plan (014/T5): cumulative ranking with runtime capability probing

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

Feature status: **PLANNED**. Updated: 2026-10-11, base `729d88f`. This document is not an implementation. Behavioral authority: `plans/014-pg-statements-contract.md`; the source matrix is a set of evidence/tests, not a list of allowed version pairs. Design stage 014 stays DONE.

## Instructions, scope and dependencies

Read the contract and plan 037. Before coding, check `git status --short` and `git diff --stat 729d88f..HEAD -- src/SqlHarness.Core src/SqlHarness.Cli src/SqlHarness.Mcp tests plans/014-pg-statements-contract.md`. Analyze the expected drift and adapt the plan; stop only the step whose contract conflict is unresolved. Preserve other people's changes. Work in an isolated worktree; do not push without an order.

Dependencies: scope 003, gate 004, Core boundaries 009; plan 037 supplies the sources and PG17/18 shape fixtures plus the runtime strategy. Plan 036 provides a usable live proof path. It does not require evidence for every possible version pair before allowing it at runtime.

In scope, new files:

- `src/SqlHarness.Core/Postgres/PgStatementTopQuery.cs`: catalog SQL, capability descriptor and explicit column projection.
- `src/SqlHarness.Core/Postgres/PgStatementTop.cs`: reader over `ISqlReader`, aggregation and ranking.
- `src/SqlHarness.Core/PgStatementArtifactWriter.cs`: writer through `ArtifactDirectoryPublisher`.
- `src/SqlHarness.Cli/Commands/PgStatementTopCommand.cs`.
- `tests/SqlHarness.Tests/PgStatementTopTests.cs` and `tests/SqlHarness.Tests/Cli/PgStatementTopCommandTests.cs`.

Existing files: `Contracts.cs`, `SqlHarnessPaths.cs`, `SqlHarnessModule.cs` in Core; `SqlHarnessCli.cs`, `Commands/Renderer.cs` in CLI; after the command, `Capabilities.cs`, `AGENTS.md`, `README.md`, `plans/README.md`. If task 6 is carried out, also the MCP `McpOperationMapper.cs`, `Tools/McpToolCatalog.cs`, mapping/schema/lifecycle tests and `docs/mcp.md`. Patterns: `QueryStoreTop.cs`, `QueryStoreArtifacts.cs`, `QueryStoreTopCommand.cs`, `QueryStoreTopTests`, `QueryStoreArtifactWriterTests`.

Out of scope: delta/Compare, snapshot store, installing or updating the extension, reset, changing GUC, writes to the database, user profiles/secrets, a new MCP tool. Live DB only on a separately authorized target or in plan 036's isolated environment.

## Decisions

- `pgstop` is a separate PostgreSQL PG14+ diagnostic. SQL Server `qstop`, its window and `QueryStoreAvailable: !pg` keep their own contract. Do not copy CPU, recency or `windowMinutes`.
- V1 reads cumulative counters. It does not create a Compare function, continuity statuses, unserialized restart markers, nor `save`/start-time reads solely for a future delta.
- Server/extension version numbers are context and information about the tested environment. PG14+ with an unknown number and a compatible shape is supported. A missing required column or an incompatible type is exit `5`.
- The contract's step 6 defines the minimum of columns and types plus the optional metrics. Choose explicit columns from the internal list after the probe, never `SELECT *` nor SQL with unverified names. Quote the namespace of a discovered extension object as an identifier; do not rely on `search_path`.
- An unavailable optional metric is null/unavailable with a reason, never 0. A partial sum does not masquerade as a complete one. Zero available metrics with tracking disabled are described together with the GUC.
- User SQL is not accepted. Safety stays in Core; adapters do not get a classifier. The existing `OperationFailureMapper.Map`, phases and DB gate remain the common mechanism.
- Query text exists only locally in `queries.jsonl`; it is not on stdout, stderr, in file names, in gain, nor in `sqlharness_artifact`. The artifact scope has its owner per 003. Do not add MCP artifact access to `queries.jsonl`.

## Sequence and SQL

1. Validation: `top` 1..500 and `timeout` 1..300, both required explicitly. Invalid or omitted: exit `2`, zero connections. `--json`/`--json-summary` are mutually exclusive.
2. The existing resolve/auth/identity. `ConnectAsync` confirms the target. Exit `3` for auth, `4` for mismatch; missing authentication data before Open keeps the current exit `2`. Do not add extension reading before identity.
3. After the session, the Sql phase. Another engine: exit `5`, without this diagnostic's SQL. `SELECT current_setting('server_version_num')`; an invalid read is exit `5`; PG <14 is exit `2` after closing the session.
4. The extension catalog, its namespace and identification of the views belonging to it. No extension: exit `5`, without a success artifact. Confirm the catalog and object-dependency SQL in primary sources before coding; use PostgreSQL's name-quoting helper. Do not send CREATE/ALTER EXTENSION/reset/set_config.
5. A catalog of the names/types of required and optional columns. The descriptor determines the available fields; it does not select SQL by comparing server+extversion. Missing minimum or an incompatible required type: exit `5`; additional columns are ignored. Do not send a projection referring to an unconfirmed column.
6. Optional GUC/info context per the contract. A recognized absence/permission for context means unavailable with a reason; other transport/execution errors remain exit `5`.
7. Explicit projection, filter `dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())`. No database-name parameter. Read all filtered rows before aggregating; do not apply LIMIT to raw rows. Timeout/cancellation remain restrictions of the existing session.
8. Aggregation of known queryids, ranking and top. A readable empty set of positions: exit `0`, report and empty queries.jsonl. The absence of known ids does not prove the absence of all statistics.
9. Artifact through the existing publisher: a write error is exit `6`, report null, without a final success directory. The journal uses the current operation model; do not restore GainRecord/gain.jsonl.

## Reader, report and artifact

Aggregate a known `queryid` within the current database; `userid`/`toplevel` do not split positions. Skip a NULL queryid, without a shared synthetic id; bigint 0 and negative values are known ids. Order: total_exec_time sum descending, calls sum descending, shared_blks_hit+shared_blks_read sum descending, queryid ascending signed. Apply top after aggregation.

The formulas and JSON position names come from the contract's Aggregation section. Execution times are in ms, without dividing by 1000. The execution average disappears when calls=0. The optional planning average also disappears when the sum is unavailable. Optional sums/maxima are null/unavailable with a reason when incomplete. Do not add unforeseen columns as report fields.

The report contains the metrics, queryId, version and availability context, artifactDirectory, without SQL text. `queries.jsonl` has one `{queryId, query}` line per position; a null text stays null. Keep uniform texts verbatim. Divergent aggregation texts give query=null per the contract, not a choice dependent on read order. The writer uses `ArtifactDirectoryPublisher.SanitizeTarget` and scope 003.

`SqlHarnessPgStatementTopOperation(SqlTargetRequest Target, int Top, int TimeoutSeconds)` has no WindowMinutes. `SqlHarnessPaths.PgStatementsDir` is the `pg-statements` directory under Home. The command uses `TargetSettings` and the existing target path. Registration `c.AddCommand<PgStatementTopCommand>("pgstop")` next to qstop. A separate Renderer, without CPU/window. The JSON summary is the report already stripped of query; there is no separate delta model.

## Tasks and verification

### 1. Descriptor, SQL and reader

Add the new PgStatementTopQuery/PgStatementTop files plus a fixture in PgStatementTopTests, modelling them on FakeReader/QueryStoreTopTests. Before implementing, check the catalog and type sources; plan 037 describes the shapes. Cover F14/F15/F17/F18/F-new/F-missing/F-type/F-optional/F-schema and the ranking. Do not implement Compare.

**Verification:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests" --verbosity minimal` → exit 0.

### 2. Artifacts

Add PgStatementArtifactWriter on the publisher and tests in PgStatementTopTests. Check that text appears only in queries.jsonl, the empty file, the scope and the absence of a success directory after an error.

**Verification:** the same PgStatementTopTests filter → exit 0.

### 3. Module

Add Contracts/Paths and the module branch next to ExecuteQueryStoreTopAsync. A fake session records order and queries, a fake writer records publication. Cover the errors in the table below; keep qstop.

**Verification:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests|FullyQualifiedName~QueryStoreTopTests" --verbosity minimal` → exit 0.

### 4. CLI

Add the command/tests, dispatch and rendering. Bounds and a missing flag are exit 2 before connecting; mutually exclusive JSON; help lists pgstop, with no window/mutation.

**Verification:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### 5. Capabilities and documentation

Only after the command, update Capabilities/AGENTS/README and the index. Do not set SupportsQstop for postgres, nor QueryStoreAvailable. Describe cumulative, optionally unavailable and the deferred delta.

**Verification:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### 6. Inspect kind (a separate optional stage)

Add the pgstop kind to the existing sqlharness_inspect, its catalog and MapInspect; do not create a twelfth tool. The request scope/input policies and the single RunDbAsync gate remain. Require top/timeout as in the CLI; do not accept window. On SQL Server reject before the module. Update docs/mcp and the schema/mapping/lifecycle tests. The artifact does not expose queries.jsonl.

**Verification:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

## Test table

| Id | Input | Outcome |
|---|---|---|
| F14/F15/F17/F18 | shapes described in the sources, differing version numbers | the same ranking of required metrics; optional ones per the descriptor |
| F-new | unknown server/extension number, conforming minimum | exit 0; there is no pair allowlist |
| F-missing/F-type | missing calls or a queryid of another type | exit 5; zero ranking queries and no success artifacts |
| F-optional | missing/ill-typed optional metric, additional columns | ranking available, metric null/unavailable with a reason, extra ones ignored |
| F-schema | unusual namespace, a quote in the name, an object of the same name in search_path | quoting and extension ownership; no interpolation of the raw name |
| F-context | no info/GUC or a recognized rejection of context reading | unavailable with a reason; the required ranking remains |
| F-text/F-nullid/F-zero | null/divergent text, null id, calls=0 | the reader contract; no false aggregation/average |
| F-order | many userid/toplevel and ties, small top | top after aggregation, deterministic order |
| E-auth/E-identity/E-mismatch | errors of the existing phases | exit 3/4 per the mapper, without extension SQL |
| E-floor/E-bounds | PG<14 / bad top-timeout | exit 2; floor after the session, bounds before the session |
| E-version/E-ext/E-perm/E-shape | bad version read, no extension, no ranking permissions, wrong shape | exit 5, with no empty success |
| E-engine | SQL Server | exit 5, zero SQL from this diagnostic |
| E-artifact | writer error | exit 6; report null |
| P-empty | a readable view without known ids | exit 0 and empty queries.jsonl |
| F-no-delta | a plain v1 command | zero Compare/save/start/reset, no delta statuses |

## Final verification and completion conditions

- [ ] The above tests and the full `pwsh ./scripts/verify.ps1`, `pwsh ./scripts/verify-linux.ps1` → exit 0; Windows ends with the literal `verify: OK`, the Linux wrapper with `verify-linux: OK`, without FAILED. Keep the evidence of both gates; a fixture is not live evidence. The scripts do not print VERIFICATION PASS.
- [ ] `git diff --check` → exit 0; the scope is reviewed, the index is current.
- [ ] No allowlist of numbers, no Compare/delta, no mutating SQL; a required but incompatible shape gives exit 5.
- [ ] Live evidence on 036's isolated environment or on an authorized target saved separately; it does not extend the declared list of verified environments beyond the evidence.

Stop a dependent step on an unresolvable contract/source conflict, on the need to change transport/identity, or on going beyond the authorized target. Adapt a drift of the name/file itself after checking the intent. Do not install the extension or reset the user's database to obtain evidence. A further delta plan requires a new contract for the observed difference and its restrictions; the questions of the historical reset matrix do not block v1 ranking.
