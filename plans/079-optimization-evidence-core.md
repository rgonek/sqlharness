# Plan 079: Optimization evidence core (stage A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plan 077 must be merged to `main`. Rebase this plan's line references on that merge commit: `git diff --stat f3655f8..HEAD -- src/SqlHarness.Core/MeasureParameterSets.cs src/SqlHarness.Core/CompareMatrixRunner.cs src/SqlHarness.Core/CompareCellRunner.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/Dialect/SqlServerDialect.cs src/SqlHarness.Core/Diagnostics.cs src/SqlHarness.Core/Journal src/SqlHarness.Mcp src/SqlHarness.Dashboard`. Adapt compatible drift; never change 077's MCP envelope, case decision fields or `ValueHash`.

**Goal:** Journal trustworthy, complete and reproducible optimization evidence for `measure` and `compare`, and expose it minimally in the dashboard.

**Architecture:** Core builds one canonical conditions model (with keyed fingerprints and case labels) from bound parameters and journals it for both operations; `measure` reuses compare's per-case session contract. Table IO is aggregated while messages stream, normalized for the journal only, and reproduction scripts are generated on demand by a SQL Server dialect component from journaled text. The dashboard gains a script endpoint, readable conditions, case labels and statement-aware plan changes; layout redesign is stage B.

**Tech Stack:** .NET 10, C#, Microsoft.Data.Sqlite, xUnit; React + TypeScript + Vitest (dashboard UI).

**Spec:** [docs/superpowers/specs/2026-10-16-optimization-evidence-core-design.md](../docs/superpowers/specs/2026-10-16-optimization-evidence-core-design.md)

## Review amendments (2026-10-16, cross-plan review) — these override task text below

1. **No `operation_runs` table exists.** Put `case_id`/`case_label` on `operation_metrics` and `operation_statements` (grouping is variant, parameter_set, matrix_cell). Add `caseId` to each case in `summary_json`. Single-case runs use case id `"0"`, label `Case 1 of 1` (not shown in UI).
2. **Where the conditions model is built.** Not in `OperationJournalDescriber` (it runs before resolve/bind). Build `ConditionsModel` in `SqlHarnessModule` after target resolution and binding, for measure and compare; pass it to the journal through new `IActivityJournal.RecordConditions(JournalHandle handle, ConditionsModel model)` (`NullActivityJournal` no-op). `ActivityJournal` owns the salt and computes all HMACs (fingerprints and any content hash); the builder produces canonical JSON inputs only. 083 will call `RecordConditions` again after per-case materialization (last write wins).
3. **Journal versioning floor.** Add `JournalSchema.FirstRetainedVersion` = the version this plan creates (14 at planning time). `0 < version < FirstRetainedVersion` → back up and start fresh; `FirstRetainedVersion ≤ version < CurrentVersion` → run step scripts (`VersionN`) as before. Later plans add steps and pins in `JournalContractTests`.
4. **Upgrade safety (Task 1).** Take an exclusive lock file next to the journal, re-check `user_version` right before the rename, then rename; test two concurrent openers (exactly one backup, no lost v-new file). Processes on an older build fail closed on a newer journal (`JournalSchemaTooNewException`), pinned by a test. Refuse the upgrade (journal unavailable warning, operations not journaled) while the old journal has pending `mutation_requests`; document in README that the fresh start drops old sessions, annotations (titles, notes, stars) and transcript cursors (they stay in the backup file).
5. **Message numbers (Task 5) — interface 081 consumes.** Define `sealed record SessionMessage(int Number, string Text)`; `ISqlSession.Messages` and `ConsumedSessionMessages.Messages` become `IReadOnlyList<SessionMessage>` (including the default implementation at `SqlExecution.cs:62`); the SqlClient handler iterates `args.Errors` (one `SessionMessage` per `SqlError`); PostgreSQL notices map to `Number = 0`. IO/time parsers consume only engine statistics numbers. Provide `FakeSqlSession` support for numbered messages in tests.
6. **Table IO availability (Tasks 5/7).** `UnavailableMetricsOrNull` must not mark reads unavailable merely because message text was dropped; journal-side availability follows `TableIoComplete`, which is journaled. Add an end-to-end `JournalBenchmarkBuilder` test: >1,000 messages → table IO journaled.
7. **Measure partial failure (Task 4).** Mirror compare: a `MeasureCaseFailedException` carrying completed runs and the partial report, handled in `SqlHarnessModule` like `CompareMatrixCellFailedException` (`BenchmarkRuns` on the error outcome) so completed sets are journaled. Reuse 077's `MatrixFailedCase`/`coverage` shape (`error.matrixStop` on MCP) — no new shape. `warmupOrder` is filled with the sequential set order. Update also `docs/mcp/tools.md:19`, `Capabilities.cs:~226`, `README.md:288-294` and the measure-set report's rotation/cache text. Anchors: `MeasureDescription` ~`McpToolCatalog.cs:609`; measure path ~`SqlHarnessModule.cs:681-800`.
8. **Task order.** Move the `JournalReader`/`CompareHistoryBuilder` switch from `compare_conditions_json` to the new conditions storage into Task 2 so the build stays green.
9. **Statement text for plan changes (Task 8).** Statement text exists only in plan XML (stored with `storeSensitive`); without it show `statement {ordinal} ({shortHash})`.
10. **Anchors/schema facts.** `Migrate` ~`ActivityJournal.cs:573`, `Quarantine` ~`:630`; `operation_table_io.lob_logical_reads` already exists (no schema change for it). Test `SqlTypes.Effective` with unsized decimal (SqlClient-inferred precision/scale).

## Status and dependencies

- TODO. Planning base `f3655f8` (2026-10-16; 073 DONE, journal at version 13). Blocked until plan 077 is merged (executing on another machine).
- Journal: 073 is DONE and the journal is at version 13; this plan takes the next free version (14 at planning time) and removes the legacy
  migration chain; later plans migrate normally from it.
- Priority P1; effort L; risk MEDIUM (breaking journal, measure semantics change).

## Global Constraints

- Breaking journal change: no reading or migration of older journal rows; an older journal file is kept as a backup.
- Values, SQL text and setup text are journaled only with `storeSensitive` (default `false`); fingerprints are always stored and must not disclose values (HMAC with a per-journal random salt).
- New logical-reads definition (data + LOB) and table-variable grouping apply to the journal and dashboard only; MCP output, artifacts and `RegressionPolicy` keep current definitions.
- `measure` and `compare` share one session contract: one connection and one setup per case, setup bound to that case's parameters, cases sequential, first failure stops the run and completed cases are retained (plan 076).
- Reproduction scripts: SQL Server only; PostgreSQL reports "script not available for this engine".
- Case `id` = set name for a named set, else zero-based input index; never a value hash. Display label: `@Name = <literal>` (joined with `, ` for several varying values), else set name, else `Case n of N` (1-based).
- No auto-formatting; touch only needed lines; match local style. Interface meaning lives in tool descriptions/schemas, runtime policy in capabilities.
- Commit messages: `feat(079): …` / `fix(079): …` / `docs(079): …`, one short line.
- Completion requires `just verify` passing (docs/agents/verification.md).

## Review Focus

1. Literal values with quotes, Unicode, SQL `NULL`, dates with fractional seconds, binary and large decimals must round-trip in both script forms exactly as bound → Task 6 `Literals_round_trip_for_every_supported_type`.
2. `storeSensitive=false` must never produce a script that looks runnable → Task 6 `Without_stored_sql_script_is_unavailable` and Task 7 endpoint test.
3. A `measure` matrix whose second case fails must keep the first case's journal and report the failed case → Task 4 `Measure_second_case_failure_retains_first_case`.
4. A batch exceeding both the 1,000-message and the 256 KiB text caps must still journal table IO → Task 5 `Table_io_survives_message_and_character_caps`.
5. An old journal that another process holds open (dashboard running) must not be lost or half-renamed → Task 1 `Locked_old_journal_reports_and_keeps_file`.

---

### Task 0: Drift check and baseline

**Files:** none changed.

- [ ] **Step 1:** Confirm 077 is merged (`git log --oneline main | grep -i 077`) and run the drift check from the header. Record the base commit in this plan's status line.
- [ ] **Step 2:** Run `just verify` on the base; record the result as the pre-change baseline.

### Task 1: Journal fresh start at the next version

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (replace `Version1`..`Version13` with one `Current` create script containing today's final schema plus Task 2/3 additions)
- Modify: `src/SqlHarness.Core/Journal/ActivityJournal.cs:508-561` (`Migrate`), `:563` (`Quarantine` → shared rename helper)
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `JournalContractTests.cs`

**Interfaces:**
- Produces: `JournalSchema.CurrentVersion = 14` (next free at execution); `JournalSchema.Current` (create script); tables/columns listed in Tasks 2–3 exist in it; `journal_meta(key TEXT PRIMARY KEY, value BLOB)` holding `fingerprint_salt` (32 random bytes created with the journal).

- [ ] **Step 1: Write failing tests** in `ActivityJournalTests`:
  - `Opening_older_journal_keeps_backup_and_creates_current`: seed a file with `PRAGMA user_version = 13` and one row; open; assert a sibling `<name>.v13-<utc>.bak` (plus `-wal`/`-shm` when present) exists with the row, and the new file has `user_version = 14`, empty `operations`, and a 32-byte `fingerprint_salt`.
  - `Current_journal_opens_without_rename`.
  - `Newer_journal_still_throws_too_new` (existing behavior kept).
  - `Locked_old_journal_reports_and_keeps_file`: hold an open connection to a v13 file; opening must fail with the existing journal-unavailable warning path (no exception to the caller, operation not journaled) and the v13 file must remain intact.
- [ ] **Step 2:** Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ActivityJournalTests"` → new tests FAIL.
- [ ] **Step 3:** Implement: in `Migrate`, `0 < version < CurrentVersion` → rename via the quarantine helper with suffix `.v{version}-{utc}.bak`, then create; `version == 0` → execute `JournalSchema.Current`, insert salt, set `user_version`. Remove the per-version chain. Update `JournalContractTests` pins.
- [ ] **Step 4:** Run the journal test folder → PASS.
- [ ] **Step 5:** Commit `feat(079): start a fresh journal version and back up older journals`.

### Task 2: Canonical conditions model and fingerprints

**Files:**
- Create: `src/SqlHarness.Core/Journal/ConditionsModel.cs`, `src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs`, `src/SqlHarness.Core/CaseLabel.cs`
- Modify: `src/SqlHarness.Core/Journal/JournalModels.cs:51-61` (remove `CompareJournalConditions`; `OperationStart.Conditions`), `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs:28-46`, `src/SqlHarness.Core/Journal/ActivityJournal.cs:24-37,730-775` (sensitive-field handling, size bound), `JournalSchema.cs` (`operations.conditions_json`, `operations.conditions_fingerprint`)
- Test: create `tests/SqlHarness.Tests/Journal/ConditionsModelBuilderTests.cs`, `tests/SqlHarness.Tests/CaseLabelTests.cs`; update `OperationJournalDescriberTests.cs`, `ActivityJournalTests.cs`

**Interfaces:**
- Consumes: `ParameterSetResolver.Resolve(...)`, `ISqlDialect.BindParameters(...)`, `SqlHarnessParameter(Name, Type, Value, Size, Precision, Scale, UdtTypeName)`.
- Produces:
  - `sealed record ConditionsModel(int Version, string Engine, string? Server, string? Database, ConditionsExecution Execution, ConditionsSetup? Setup, IReadOnlyList<ConditionsParameter> Fixed, IReadOnlyList<ConditionsCase> Cases, string Fingerprint, IReadOnlyList<string> Omitted)`
  - `sealed record ConditionsExecution(string Operation /* "measure"|"compare" */, string SessionPolicy /* "per-case" */, string SetupBinding /* "per-case" */, int Warmup, int Repeat, int TimeoutSeconds, string? ComparisonMode, bool? CompareNullability)`
  - `sealed record ConditionsSetup(string Hash, string? Text)`
  - `sealed record ConditionsParameter(string Name, string EffectiveType, JsonElement? Value)` — `Value` null when not stored; JSON `null` literal for SQL NULL.
  - `sealed record ConditionsCase(string Id, int Index, string Label, string? SetName, IReadOnlyList<ConditionsParameter> Varying, string Fingerprint)`
  - `static ConditionsModel ConditionsModelBuilder.Build(SqlHarnessOperation operation, ISqlDialect dialect, ReadOnlySpan<byte> salt, bool storeSensitive)`
  - `static string SqlTypes.Effective(SqlHarnessParameter p)` — e.g. `nvarchar(4000)`, `nvarchar(max)`, `varchar(15)`, `decimal(18,4)`, `datetime`, `datetime2(7)`.
  - `static string CaseLabel.Format(ConditionsCase c, int total, bool valuesStored)`; long literals middle-truncated only by the UI.

- [ ] **Step 1: Write failing tests:**
  - `Matrix_and_equivalent_sets_build_identical_models` — MCP matrix `@ClassType nvarchar(4000) [A,B]` and inline sets `{a:[@ClassType=A]}, {b:[@ClassType=B]}` produce equal `Fixed`/`Varying`/fingerprints (case ids differ: `0`,`1` vs `a`,`b`).
  - `Effective_types_are_bound_types` — unsized `nvarchar` value of 10 chars → `nvarchar(4000)`; 5000 chars → `nvarchar(max)`; `decimal(18,4)`; `datetime`.
  - `Fingerprint_is_keyed_and_value_sensitive` — same model, different salt → different fingerprint; `@N int = 0` vs `1` → different; `repeat` 5 vs 10 → same.
  - `Values_and_setup_text_absent_without_storeSensitive` — `Value` null, `Setup.Text` null, `Setup.Hash` and fingerprints present.
  - `Measure_journals_conditions` (describer test) — a `measure` start now carries `Conditions` with `Operation = "measure"`.
  - `CaseLabel` tests: `@ClassType = N'A'`; two varying → `@A = 1, @B = N'x'`; values absent with set name → `a`; neither → `Case 2 of 3`; NULL → `@X = NULL`.
  - `Oversized_conditions_record_omissions` — a 2 MiB setup text with storeSensitive is journaled without `Setup.Text`, `Omitted` contains `setup.text`, the rest intact.
- [ ] **Step 2:** Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ConditionsModel|FullyQualifiedName~CaseLabel|FullyQualifiedName~OperationJournalDescriber"` → FAIL.
- [ ] **Step 3:** Implement. Fingerprint = lowercase hex HMAC-SHA256(salt, canonical JSON of {engine, server, database, setup.hash, fixed, varying for the case, execution.sessionPolicy, execution.setupBinding, execution.comparisonMode}); operation fingerprint covers all cases in order. Size bound for `conditions_json`: 1 MiB; omit in order `setup.text`, then per-value `value` (largest first), recording each in `Omitted`. Delete the raw input fields (`parameters`, `typedParameters`, `matrix`, `matrixText`, `parameterSets`) from journaling.
- [ ] **Step 4:** Run the same filter → PASS; run the full journal folder → PASS.
- [ ] **Step 5:** Commit `feat(079): journal canonical conditions with keyed fingerprints`.

### Task 3: Case labels on journaled runs

**Files:**
- Modify: `src/SqlHarness.Core/CompareMatrixRunner.cs:49,89-93`, `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs`, `BenchmarkJournalRecord.cs`, `JournalSchema.cs` (`operation_runs.case_id TEXT`, `operation_runs.case_label TEXT`)
- Test: `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs`, `tests/SqlHarness.Tests/CompareMatrixTests.cs`

**Interfaces:**
- Consumes: `ConditionsCase.Id`, `CaseLabel.Format`.
- Produces: every journaled run row carries `case_id`/`case_label`; `JournalReader` exposes them as `caseId`/`caseLabel` on compare cases (Task 7).

- [ ] **Step 1:** Failing test `Matrix_runs_journal_case_label` — matrix `@ClassType [A,B]` with storeSensitive journals labels `@ClassType = N'A'`, `@ClassType = N'B'`; without storeSensitive `Case 1 of 2`, `Case 2 of 2`.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(079): journal case identity and labels`.

### Task 4: `measure` uses compare's per-case session contract

**Files:**
- Modify: `src/SqlHarness.Core/MeasureParameterSets.cs:246-326` (`MeasureParameterSetRunner`), `src/SqlHarness.Core/SqlHarnessModule.cs:~620-660` (measure path), `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:505` (`MeasureDescription`), `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:270` (help text), `src/SqlHarness.Core/Capabilities.cs` (if it states rotation), `README.md:~290`, `docs/mcp.md`
- Test: `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs`, `MeasureTests.cs`, existing docs-contract pins

**Interfaces:**
- Consumes: `ISqlSessionFactory.ConnectAsync(ResolvedTarget, CancellationToken)`, `SetupSqlExecution.ExecuteSetupAsync(...)`.
- Produces: `MeasureParameterSetRunner(ISqlDialect dialect, ISqlSessionFactory sessions, int comparisonMaximumRows)`; `ExecuteAsync(ResolvedTarget target, SqlHarnessMeasureOperation operation, IReadOnlyList<PreparedMeasureParameterSet> sets, CanonicalResultAccumulator raw, CancellationToken ct, SqlSafetyDecision? setupSafety = null)`; `MeasureParameterSetExecution` gains `int CompletedCases` and `MeasureCaseFailure? FailedCase` (`Index`, `SetName`, `Phase`).
- New `MeasureDescription`: `"Measure one query across repeats. Each parameter set runs on its own session with its own setup bound to that set; sets run in order and the first failure stops the run, keeping completed sets. Results report per-set stability, not cross-set equivalence."`

- [ ] **Step 1: Write failing tests:**
  - `Measure_opens_one_session_per_set` — fake factory `ConnectCount == 2` for two sets (replace the plan-061 test asserting `1`).
  - `Measure_setup_binds_each_sets_parameters` — setup `SELECT @N AS n INTO #s` observed bound with 1 for set A and 2 for set B.
  - `Measure_second_case_failure_retains_first_case` — second set throws during execute; outcome error with `FailedCase.Index == 1`, journal has the first set's runs.
  - Docs contract pin updated to the new description.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~MeasureParameterSet|FullyQualifiedName~MeasureTests"` → FAIL.
- [ ] **Step 3:** Implement sequentially per set: connect, setup (bound to that set), warm-up, `Repeat` measured runs, dispose; remove round rotation and `warmupOrder` rotation semantics (keep the report field if 077 relies on it, filled with the sequential order).
- [ ] **Step 4:** Run → PASS. Update README/docs/mcp.md text that says "rotates on one connection".
- [ ] **Step 5:** Commit `feat(079): measure runs each parameter set on its own session`.

### Task 5: Table IO survives message caps

**Files:**
- Modify: `src/SqlHarness.Core/SqlExecution.cs:71-150` (`SessionMessageBuffer`, `ConsumedSessionMessages`), `src/SqlHarness.Core/Diagnostics.cs:80-135` (`StatisticsIoDetailParser` gains incremental `Accumulate`), `src/SqlHarness.Core/Dialect/SqlServerDialect.cs:~85-92`
- Test: `tests/SqlHarness.Tests/StatisticsIoDetailParserTests.cs`, `DialectMessageBudgetTests.cs`

**Interfaces:**
- Produces: `ConsumedSessionMessages(IReadOnlyList<string> Messages, int OmittedMessageCount, IReadOnlyList<TableIoCounters> TableIo, bool TableIoComplete)`; `sealed class TableIoAccumulator { void Add(int messageNumber, string message); IReadOnlyList<TableIoCounters> Snapshot(); bool Complete { get; } }`.
- Message numbers: the session keeps each info message's number (`SqlError.Number` from `InfoMessage`); IO and time parsers consume only engine statistics messages (IO 3615; time 3612/3613 — verify exact numbers against a live run and pin them in a test), so `PRINT`/`RAISERROR` text that imitates statistics is ignored (needed by plan 081, which admits `PRINT`/`RAISERROR`).

- [ ] **Step 1: Write failing tests:**
  - `Table_io_survives_message_and_character_caps` — feed 1,500 messages totalling > 256 KiB, each with `Table 'T'. Scan count 1, logical reads 2, … lob logical reads 1 …`; consumed `TableIo` has `T` with scans 1,500, logical 3,000, LOB 1,500; `OmittedMessageCount > 0`; `TableIoComplete == true`.
  - `Accumulate_matches_parse_for_same_text` — same counters as today's `Parse` for the fixtures in `StatisticsIoDetailParserTests`.
  - `User_messages_imitating_statistics_are_ignored` — a message number 0 (PRINT) with text `Table 'X'. Scan count 1, logical reads 999…` and one imitating `SQL Server Execution Times` leave IO and time counters unchanged.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement: `SessionMessageBuffer.Add` feeds the accumulator before eviction; `Consume` returns and resets it; `SqlServerDialect` uses `consumed.TableIo` instead of dropping IO when messages were omitted. `TableIoComplete` is false only if a single message could not be parsed (oversized/garbled).
- [ ] **Step 4:** Run → PASS. **Step 5:** Commit `fix(079): aggregate table IO while messages stream`.

### Task 6: Reproduction script generator (SQL Server)

**Files:**
- Create: `src/SqlHarness.Core/Repro/ReproScript.cs`, `src/SqlHarness.Core/Repro/SqlServerReproScriptWriter.cs`, `src/SqlHarness.Core/Repro/SqlServerLiteral.cs`
- Test: create `tests/SqlHarness.Tests/Repro/SqlServerReproScriptWriterTests.cs`

**Interfaces:**
- Consumes: `ConditionsModel`, `SetupSqlExecution.PrepareCommands(string setupSql, IReadOnlyList<SqlHarnessParameter> parameters, int timeoutSeconds)`.
- Produces:
  - `enum ReproScriptForm { AsExecuted, LocalVariables }`
  - `enum ReproCompleteness { Complete, ValuesMissing, Unavailable }`
  - `sealed record ReproScript(string? Text, ReproCompleteness Completeness, string Label)`
  - `static ReproScript ReproScripts.Create(ConditionsModel conditions, string? sideSql, string side /* "baseline"|"candidate"|"query" */, string caseId, ReproScriptForm form)` — dispatches by `conditions.Engine`; non-SQL Server → `Unavailable`, label `"Script not available for this engine"`.
  - `static string SqlServerLiteral.Format(ConditionsParameter p)`.
- Labels (exact copy): AsExecuted `"As executed (sp_executesql)"`; LocalVariables `"Local variables — estimates may differ"`, or `"Local variables — equivalent (every statement uses OPTION (RECOMPILE))"`; missing values `"Incomplete — values not stored"`; no SQL `"Script not available — enable storeSensitive"`.

- [ ] **Step 1: Write failing tests:**
  - `As_executed_matches_bound_definitions` — for op-203-like conditions (`@NClients int 0`, `@NGroups int 0`, `@NCountries int 1`, case `@ClassType nvarchar(4000) N'ClientConfigurationModel2'`) the text contains `SET STATISTICS IO, TIME ON;`, `EXEC sys.sp_executesql N'`, the definition string `N'@NClients int, @NGroups int, @NCountries int, @ClassType nvarchar(4000)'` and `@ClassType = N'ClientConfigurationModel2'`; header comments name side, case label and "new connection; setup runs per case".
  - `Local_variables_form_declares_each_parameter` — `DECLARE @NClients int = 0;` … and SQL inline; label "estimates may differ".
  - `Recompile_everywhere_is_labelled_equivalent`.
  - `Setup_split_matches_execution` — setup `CREATE TABLE #t(i int); INSERT #t SELECT @N;` renders the parameter-free prefix as its own batch, then `GO`, then the parameterized remainder through `sp_executesql`, matching `PrepareCommands` boundaries.
  - `Literals_round_trip_for_every_supported_type` — `O'Brien` → `N'O''Brien'`; Unicode `Zażółć`; SQL NULL → `CAST(NULL AS int)` in AsExecuted argument position and `= NULL` in DECLARE; `datetime` `2026-10-16T12:34:56.997`; `datetime2(7)` with 7 fractional digits; `varbinary` `0x0A0B`; `decimal(38,10)` invariant; `bit` `1`.
  - `Missing_values_render_typed_placeholders` — `@N int = /* value not stored */` and completeness `ValuesMissing`.
  - `Without_stored_sql_script_is_unavailable` — `sideSql == null` → `Unavailable`, `Text == null`.
  - `Postgres_is_unavailable`.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~Repro"` → FAIL.
- [ ] **Step 3:** Implement. Header also states that SQLHarness discards one warm-up and reports medians while the script runs once, and sets `SET ARITHABORT ON;` (verify SqlClient vs SSMS defaults; include only options that differ).
- [ ] **Step 4:** Run → PASS. **Step 5:** Commit `feat(079): generate SQL Server reproduction scripts`.

### Task 7: Journal normalization and dashboard API

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs:117,137-168` (table-variable grouping before medians; logical = data + LOB; store LOB per table), `JournalSchema.cs` (`operation_table_io.lob_logical_reads`), `src/SqlHarness.Dashboard/JournalReader.cs:~297,387-458`, `src/SqlHarness.Dashboard/DashboardModels.cs:84`, `src/SqlHarness.Dashboard/DashboardServer.cs:~216` (new route)
- Test: `tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `DashboardServerTests.cs`, `DashboardContractTests.cs`

**Interfaces:**
- Consumes: `ConditionsModel`, `ReproScripts.Create`.
- Produces:
  - Journal table key `"table variables"` for names matching `^(\[dbo\]\.)?\[?#[0-9A-Fa-f]{8}\]?$`; counters summed per run before medians.
  - Operation detail JSON: `conditions` (the model), compare cases with `caseId`, `caseLabel`, per-table rows `{ table, scans, logicalReads, lobLogicalReads, totalLogicalReads }`, headline `totalLogicalReadsMedian` labelled "median of run totals (data + LOB)".
  - `GET /api/operations/{id}/repro?side=baseline|candidate|query&case=<caseId>&form=as-executed|local-variables` → `{ text, completeness, label }`; 404 for unknown operation/case; 400 for bad side/form.

- [ ] **Step 1: Write failing tests:**
  - `Table_variables_group_before_medians` — three runs each with a different `#A1B2C3D4`-style name reading 10 pages → journal row `table variables` median 10 (today 0).
  - `Headline_equals_per_table_definition` — per run sum of (logical + LOB) across tables; median of those sums equals the stored headline.
  - `Repro_endpoint_returns_script` and `Repro_endpoint_without_sensitive_storage_is_unavailable` (seeded via `JournalSeed`).
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~JournalBenchmarkBuilder|FullyQualifiedName~JournalReader|FullyQualifiedName~DashboardServer"` → FAIL.
- [ ] **Step 3:** Implement. **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(079): normalize journaled table IO and serve reproduction scripts`.

### Task 8: Dashboard minimal surfaces

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `api/queries.ts`, `components/CompareDetail.tsx:55-61,105-124,189`, `components/TableIoComparison.tsx`, `components/PlanChanges.tsx:25-37`, `lib/planChanges.ts:17-21`, `lib/prMarkdown.ts:63-84`, `lib/tableIoDelta.ts:38-39`; measure operation view in `pages/OperationPage.tsx`
- Create: `components/ConditionsList.tsx`, `components/ReproScriptButtons.tsx`
- Test: `CompareDetail.test.tsx`, `PlanChanges.test.tsx`, `lib/planChanges.test.ts`, `lib/prMarkdown.test.ts`, `lib/tableIoDelta.test.ts`, create `components/ConditionsList.test.tsx`, `components/ReproScriptButtons.test.tsx`

**Interfaces:**
- Consumes: Task 7 JSON and endpoint.
- Produces: `planChangeStatus(baseline, candidate): { same: boolean; changed: { ordinal: number; statement: string }[] }` over each variant's plan list (the same entries `firstPlanHash` reads today; it replaces `firstPlanHash`); `<ConditionsList conditions={…} />`; `<ReproScriptButtons operationId side caseId />` with two buttons labelled `Copy script (as executed)` and `Copy script (local variables)`, disabled with the API label as tooltip when not `Complete`/`ValuesMissing`.

- [ ] **Step 1: Write failing tests:**
  - Case headings render `caseLabel` (`@ClassType = N'ClientConfigurationModel2'`), not `Cell 1`; long labels middle-truncated with full value in `title`.
  - `ConditionsList` renders setup, fixed parameters and per-case varying values as a definition list; empty fields are not rendered; no raw field names (`typedParameters`, `matrixText`).
  - `planChangeStatus`: second statement hash differs → `same=false`, `changed=[{ordinal:1,…}]`; different statement counts → `same=false`; all equal → `same=true`.
  - `prMarkdown` includes `Scans` columns, groups `table variables`, and uses the same equivalence wording as the screen.
  - Table IO shows `Logical`, `LOB logical`, `Total` and the headline label "median of run totals (data + LOB)".
  - `ReproScriptButtons` copies the endpoint text; shows "Incomplete — values not stored" warning; disabled for `Unavailable`.
- [ ] **Step 2:** Run `npm --prefix src/SqlHarness.Dashboard/ui test` → FAIL.
- [ ] **Step 3:** Implement with existing components and styles; no layout redesign (stage B).
- [ ] **Step 4:** Run → PASS; `npm --prefix src/SqlHarness.Dashboard/ui run build`.
- [ ] **Step 5:** Commit `feat(079): show conditions, case labels and reproduction scripts in dashboard`.

### Task 9: Documentation, verification and queue

**Files:** `README.md`, `docs/mcp.md` (journal/dashboard and measure semantics), `plans/README.md`, `plans/history.md` (at completion), `skills/sqlharness/SKILL.md` only if it states measure rotation.

- [ ] **Step 1:** Update docs; check links and contract pins.
- [ ] **Step 2:** Run `just verify` → exit 0. Record the revision and result in this plan.
- [ ] **Step 3 (operator-authorized, optional):** with `storeSensitive` on, run one UAT compare with a matrix and setup, copy both scripts into SSMS, confirm per-table logical reads match the journal. Record the outcome without business data.
- [ ] **Step 4:** Move the queue entry to history with evidence; commit `docs(079): record plan 079 evidence`.
