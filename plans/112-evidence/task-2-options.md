# Plan 112 Task 2: Capability option assessment

## Finding

The three options answer different questions and should remain separate decisions:

1. **Estimated-only DML plan:** feasible for SQL Server as compile-time evidence with an engine-level no-execution guarantee, but it supplies estimates and plan shape rather than observed writes, runtime cost, or savings. SQLHarness currently rejects `SET SHOWPLAN_XML`; the existing `plan` command only distills caller-supplied plan files/stdin.
2. **Actual write benchmark:** potentially measures one isolated workload, but every trial executes persistent DML before cleanup. A rollback is not a safe read-only boundary: transaction-external sequence allocation, triggers, logging, locks, and external effects require containment and explicit operator policy. Current `measure`/`compare` are read-only; approved `mutation` executes writes but has no rollback benchmark contract.
3. **Read-only existing-index inventory and list-family discovery:** the lowest-risk option for more descriptive evidence. Plan 063 already captures a bounded index inventory only for tables named by saved missing-index hints. It does not detect index redundancy or list-family recurrence. Any redundancy label needs complete index semantics, usage context, and an explicitly limited observation window; key-prefix resemblance alone cannot establish it.

No option is authorized or justified for implementation by this task. Task 1 found one selected DML bottleneck in the available non-report observation corpus. This sample cannot establish recurrence or expected savings.

## Option A — estimated-only DML plan

### Evidence contract and feasibility

SQL Server `SET SHOWPLAN_XML ON` asks the engine for compile-time plan information and does not execute the statements in subsequent batches. Microsoft distinguishes it from `SET STATISTICS XML`, which executes the statements and then returns runtime plan evidence. The no-execution claim therefore applies to SQL Server's execution of the submitted statement; it does not promise zero compile-time CPU, metadata reads, cache effects, or other engine work. The exact scope of such compile-time side effects has not been measured here.

The engine requires both the permissions needed to execute the described statement and `SHOWPLAN` on every referenced database. `SET SHOWPLAN_XML ON` cannot appear inside a stored procedure and must itself be the only statement in its batch; the target statements are submitted in a later batch. A batch that creates an object and then references it will not behave as an executing batch: the create is not performed, so later compilation can fail because the object does not exist. Callers therefore need the actual source DML, resolved object/parameter context, and any prerequisite objects already present. Dynamic SQL, inaccessible module text, temp-table lifecycle, version/compatibility differences, and compile errors can prevent capture; these cases were not live-tested.

An estimated DML plan could expose estimated operators and index access/maintenance shape. It cannot confirm actual affected rows, actual runtime warnings, physical/logical work, log volume, trigger behavior, or per-index write-cost share. A future output must label its estimate-only nature and preserve exact query/variant/parameter identity. Do not describe estimated subtree cost as measured write cost.

### Source and command evidence

| Inspected source | Command or fixture | Expected outcome | Observed outcome |
|---|---|---|---|
| `plans/081-session-control-flow-safety.md` denied-statement list; `src/SqlHarness.Core/SqlSafety.cs` read-only classifier; `src/SqlHarness.Cli/Commands/PlanCommand.cs`; MCP plan description in `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` | `rg -n 'SHOWPLAN_XML|STATISTICS XML|Rollback' plans/081-session-control-flow-safety.md src/SqlHarness.Core/SqlSafety.cs src/SqlHarness.Cli/Commands/PlanCommand.cs src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` | Establish whether SQLHarness admits SHOWPLAN or captures a server-side estimate | Plan 081 explicitly denies `SET SHOWPLAN_XML ON` and `SET STATISTICS IO ON`; the `plan` CLI/MCP input is a Showplan XML/EXPLAIN JSON document, not a database request. No product behavior was changed or exercised against a database. |
| Microsoft Learn, `SET SHOWPLAN_XML` and `Save an Execution Plan in XML Format` | Read the SQL Server primary docs linked below | Verify compile-only behavior, batch restrictions and permissions | Docs state statements are not executed while SHOWPLAN is on; it is batch-only/not valid inside a stored procedure, and requires execution permissions plus SHOWPLAN permissions for referenced databases. |
| Microsoft Learn, `SET STATISTICS XML` | Read the SQL Server primary doc linked below | Distinguish runtime plan capture from estimated-only capture | Docs state the statement executes before runtime plan information is returned, and both execution and SHOWPLAN permissions must succeed. This rules it out as the no-execution path. |

### Unresolved risks / decision

- No supported SQL Server version/compatibility matrix or product-level capture path has been validated. The feature is only engine-feasible based on documentation.
- The available observation does not prove estimated index-maintenance operators would answer the analyst's question or reduce the four-call source-identification workflow.
- Compile-time resource/cache/statistics side effects and failure behavior for each SQL shape remain unmeasured.
- **Provisional technical assessment: defer implementation pending a narrow offline design and further demand evidence.** If later accepted, keep it compile-only, SQL Server-specific, explicit about pre-existing dependencies/permission requirements, and separate from `query`, `measure`, `compare`, and mutation execution. The engine's documented guarantee is strong for statement execution, while the likely value is still unproven.

## Option B — actual write benchmark on an isolated target

### Evidence contract and feasibility

An actual plan necessarily follows execution. SQL Server `SET STATISTICS XML` returns runtime details only after executing the statements. A transaction rollback can reverse many database row changes, but it does not undo all effects of executing DML. SQL Server documents that sequence values are generated outside the transaction and are consumed even when the transaction rolls back. Locks, transaction-log generation, trigger execution, and caller-visible waits also occur during the trial. Trigger code or DML that reaches external systems can have effects outside the transaction; exact effects depend on the schema and code and remain unresolved without target-specific inspection.

SQLHarness's `measure`/`compare` contract is read-only, while `mutation` sends persistent changes through the dashboard broker and configured operator policy. Plan 073 does not promise rollback, repeat isolation, benchmark statistics, or cleanup after an unknown outcome. Do not add a rollback-only usage tier or route benchmark repetitions through the read-only tools.

A future study would need a separately authorized disposable/restorable environment: a database clone or isolated instance restored from a fixed, sanitized baseline for each trial; production integrations disabled or stubbed; no shared users/workload; controlled engine/version/configuration; and an out-of-band owner for provisioning and destruction. Repeated trials should start from the same database state. A failed, cancelled, or disconnected request can leave outcome uncertain, so cleanup must destroy/recreate the disposable target rather than infer rollback succeeded. The operator must explicitly authorize the write benchmark policy and accepted effects before each target run. A dashboard click that approves ordinary mutation is not itself a benchmark/cleanup protocol.

### Source and command evidence

| Inspected source | Command or fixture | Expected outcome | Observed outcome |
|---|---|---|---|
| `plans/072-mutation-risk-tiers.md`, `plans/073-mutation-approval-broker.md`, `docs/mcp/tools.md`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` | `rg -n 'Persistent DML|SET STATISTICS XML|rollback|approval|Mutation' plans/072-mutation-risk-tiers.md plans/073-mutation-approval-broker.md docs/mcp/tools.md src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` | Check current safety tier, execution route, and benchmark/rollback guarantee | Persistent changes are classified and sent via mutation; the broker policy controls approval. The inspected contract has no transactional rollback benchmark, isolated-target reset, or outcome-unknown cleanup guarantee. No mutation or database command was run. |
| Microsoft Learn, `SET STATISTICS XML` and actual execution plan docs | Read the SQL Server primary docs linked below | Verify actual-plan execution boundary | Docs state the statement/batch executes to produce runtime plan details; it requires permission to execute plus SHOWPLAN permission. |
| Microsoft Learn, sequence-number docs | Read `Sequence Numbers (Transact-SQL)` linked below | Test whether rollback guarantees all side effects are undone | Docs explicitly state sequence allocations are outside the transaction and remain consumed on rollback. |

### Unresolved risks / decision

- Target-specific trigger bodies, sequences, identity values, log growth, lock duration, external calls and side effects cannot be assessed from the observation corpus.
- A restore/clone boundary is operationally expensive and requires platform/owner support; no target strategy or cleanup SLA is present in the current product contract.
- No deterministic synthetic workload or authorized UAT target has been run as part of this task.
- **Provisional assessment: reject a rollback-only benchmark path; defer actual benchmarking to a separately designed and explicitly authorized disposable-target study.** Current evidence does not justify building this high-risk capability for a single observed case.

## Option C — read-only index inventory and expanded-list family discovery

### Evidence contract and feasibility

SQL Server catalog views can describe index structure, while `sys.dm_db_index_usage_stats` provides seek, scan, lookup, and update operation counters. A useful inventory must retain, at minimum, index type/state, uniqueness and constraint role, ordered key columns with direction, included columns, filter presence/definition, partition mapping, and per-partition compression. It must account for implicit clustered-key row locators and distinguish rowstore from columnstore and other index types. Comparing only key prefixes can produce false positives: uniqueness may enforce a contract, filters change row coverage, included columns affect reads, ordering affects supported access, and partition/compression differences affect storage and maintenance semantics.

Usage statistics are workload observations, not permanent facts. SQL Server documents that `user_updates` counts operations rather than affected rows and that usage counters reset on engine start; database detach or shutdown can also clear rows. Missing rows/counters therefore mean unavailable or not observed in this DMV window, never measured zero. Required server-state permissions differ by engine version and platform. Metadata visibility can also return only a subset of catalog rows to a principal, so an apparently complete inventory requires an explicit completeness/permission result.

Plan 063 is reusable bounded infrastructure but answers only whether a saved missing-index hint overlaps indexes on up to its hinted tables. It does not perform index-vs-index redundancy classification or measure maintenance cost. Any extension should first report structural similarities as candidates, with evidence/limitations, and leave retain/drop decisions to a DBA. It must not generate DROP DDL.

For list-family discovery, SQL Server Query Store tracks query texts, plans, and runtime metrics aggregated by query plan and time interval. Different text variants can be separate query identities; plan/context/module settings can create further query entries. A future read-only grouping heuristic could use application/source identity where available plus parameterized shape and list cardinality as a varying condition. Plan 083's list expansion preserves unexpanded operation identity on supported read-only paths, but neither it nor SQL Server Query Store documentation supplies a canonical source-level normalization rule. Group only as a candidate, preserve members and unmatched evidence, and require source/query-shape confirmation. Query Store aggregates must not be turned into per-index write cost.

### Source and command evidence

| Inspected source | Command or fixture | Expected outcome | Observed outcome |
|---|---|---|---|
| `plans/063-missing-index-overlap.md`; `plans/083-table-and-list-parameters.md`; `src/SqlHarness.Core/ListParameterExpander.cs`; `docs/observations/write-statement-and-index-maintenance-evidence.md` | `rg -n 'index-inventory|covered|user_updates|ListParameter|query text|Query Store' plans/063-missing-index-overlap.md plans/083-table-and-list-parameters.md src/SqlHarness.Core/ListParameterExpander.cs docs/observations/write-statement-and-index-maintenance-evidence.md` | Delimit existing inventory, input expansion and observation support | Plan 063 captures at most 8 hinted tables and 64 indexes per table; absent inventory is an evidence gap. Plan 083 supports TVP/list parameter representation on read-only paths. The observation reports an apparent redundant-index hypothesis and list expansion as unmeasured, not proven cost or normalization. |
| Microsoft Learn, `sys.indexes`, `sys.index_columns`, `sys.partitions`, and `sys.dm_db_index_usage_stats` | Read the SQL Server primary docs linked below | Verify required structural and usage fields and their temporal limits | Catalog docs expose uniqueness/filter, ordered key direction, included columns, partitioning and per-partition compression. Usage docs define operation counters, clarify that updates are operation counts rather than row counts, and document resets. |
| Microsoft Learn, Query Store collection and metadata visibility docs | Read the SQL Server primary docs linked below | Bound Query Store grouping and inventory completeness claims | Query Store stores query text/plan identities with aggregated interval runtime metrics; plan/context/module settings can split entries. Catalog visibility can omit rows based on permissions. Neither source establishes canonical list-family grouping or per-index DML maintenance cost. |

### Unresolved risks / decision

- Definition capture may be incomplete due metadata visibility, unsupported index types, version-specific columns, partition/object scope, or concurrent DDL. A report must surface these gaps rather than call missing metadata “no overlap.”
- Usage-window length and relevance are unknown for the observed system; the DMV alone cannot establish a representative workload or write savings.
- Indexes can support constraints, ordering, filters, included-column coverage, partitioning or other workload paths not represented in one observation. Similarity is not a drop recommendation.
- No workload corpus beyond Task 1's one selected DML case shows repeated list-family or index-inventory demand.
- **Provisional assessment: defer a general redundancy classifier; consider bounded read-only descriptive inventory/list-family candidates only if later tasks establish recurring demand and define explicit completeness, permissions, and evidence-window contracts.** Current Plan 063 coverage should be reused, not duplicated.

## Primary technical sources

- Microsoft Learn, [SET SHOWPLAN_XML (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-showplan-xml-transact-sql): no statement execution, batch restrictions, output and permissions.
- Microsoft Learn, [Save an Execution Plan in XML Format](https://learn.microsoft.com/en-us/sql/relational-databases/performance/save-an-execution-plan-in-xml-format): estimated versus actual plans and permission requirements.
- Microsoft Learn, [SET STATISTICS XML (Transact-SQL)](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-statistics-xml-transact-sql): executes statements before returning runtime XML.
- Microsoft Learn, [Sequence Numbers (Transact-SQL)](https://learn.microsoft.com/en-us/sql/relational-databases/sequence-numbers/sequence-numbers): sequence values remain consumed after transaction rollback.
- Microsoft Learn, [sys.indexes](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-indexes-transact-sql), [sys.index_columns](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-index-columns-transact-sql), and [sys.partitions](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-partitions-transact-sql): index properties, ordered/included/partition columns, partition compression.
- Microsoft Learn, [sys.dm_db_index_usage_stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-db-index-usage-stats-transact-sql): read/use counters, update-operation meaning, reset conditions and permission requirements.
- Microsoft Learn, [How Query Store collects data](https://learn.microsoft.com/en-us/sql/relational-databases/performance/how-query-store-collects-data) and [sys.query_store_runtime_stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-runtime-stats-transact-sql): query/plan identities and aggregated runtime statistics.
- Microsoft Learn, [Metadata Visibility Configuration](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration): catalog rows may be limited by permissions.

## Task 2 review focus

1. **Old/missing evidence:** no artifact or runtime evidence was changed. Engine fields and usage counters absent or hidden must stay unavailable, not zero.
2. **Identity:** a DML plan or list-family candidate must retain exact SQL/variant/parameter context; grouping expanded lists remains a source-confirmed heuristic only.
3. **Budgets:** no new capture or response surface was implemented. Any future plan/index inventory must stay bounded and report omission/completeness explicitly.
4. **Errors/negative results:** permission failure, missing objects, compile failure, unsupported plan fields and DMV gaps are evidence gaps, not “no index” or “zero writes.”
5. **Offline versus live:** this is documentation research based on repository contracts and Microsoft Learn. No database, DML, DDL, SSMS acceptance, mutation, or live verification occurred.
