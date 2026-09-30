# Plan 011: syntax contract (allowed AST matrix + negatives) — T1

Status: **CONTRACT (pre-implementation)**. Date: 2026-09-30.
Scope: matrix for T2–T5 + standing negatives. Docs only; no classifier/test change in this step.
Worktree: `D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions`.

Verification basis (not guesses):

- T-SQL AST names probed on ScriptDom 180.37.3 (`TSql160Parser`, same package as worktree
  `Directory.Packages.props`): `SET @x = …` → `SetVariableStatement`; `SET ROWCOUNT` →
  `SetRowCountStatement`; `SET TRANSACTION ISOLATION LEVEL` →
  `SetTransactionIsolationLevelStatement`; `SET DATEFORMAT/DEADLOCK_PRIORITY/LOCK_TIMEOUT/LANGUAGE`
  → `SetCommandStatement`; `SET ANSI_NULLS/QUOTED_IDENTIFIER/NOCOUNT/XACT_ABORT/ARITHABORT/…` →
  `PredicateSetStatement`; `SET IDENTITY_INSERT` → `SetIdentityInsertStatement`;
  `SET STATISTICS` → `SetStatisticsStatement`; `SET TEXTSIZE` → `SetTextSizeStatement`;
  `DECLARE @t TABLE` → `DeclareTableVariableStatement` (payload `Body: DeclareTableVariableBody`,
  no `Declarations` list); DML target `@t` → `VariableTableReference`
  (not `NamedTableReference`).
- PG AST probed on SqlParserCS 0.6.5 (`PostgreSqlDialect`, same package as worktree):
  canonical `ANALYZE t`, `ANALYZE VERBOSE t`, bare `ANALYZE` → `ParserException`
  (`Expected TABLE, found …`) → `PostgresDocument.TryParse == false` → `ParseError`.
  Only Hive-style `ANALYZE TABLE t` parses → `Statement.Analyze`. `TRUNCATE …` parses →
  `Statement.Truncate` (fields below).
- Code cites are worktree `src/SqlHarness.Core/`: `SqlSafety.cs` (`case DeclareVariableStatement`,
  `IsLocalTemp`, `ResolveDirectTarget`/`ResolveTarget`, `SafetyInspectionVisitor`),
  `Postgres/PostgresSafetyClassifier.cs` (Unsupported group incl. `Analyze`/`Truncate`,
  `IsSessionLocal`), `SqlValidation.cs` (`SqlServerReferenceCollector` covers only
  `DeclareVariableStatement` locals).

Offline baseline (verified, no re-proof): scalar `DECLARE` allowed (`SqlSafety.cs:272-280`);
`SET`-variable and table variables unsupported today (no matching `case` → `default` →
`UnsupportedStatement`); PG `CREATE TEMP` allowed but `TRUNCATE` unsupported
(`PostgresSafetyClassifier.cs:163-174` vs `:235-250`); canonical `ANALYZE` → `ParseError`;
plain CTE `SELECT` allowed on both engines.

Regression-test rule for T2–T5 (binding): each implementing task first confirms its new test
FAILS on old code, then implements to green. Planned test names below do not exist yet
(except the cited anchors); implementers must keep the exact file per row.

## T2 — T-SQL SET (implements: 011/T2; file: `tests/SqlHarness.Tests/SqlSafetyTests.cs` unless noted)

Allowed shape: exactly `SetVariableStatement` assigning to one local scalar variable;
RHS analyzed through the existing global inspection (external / stateful / cross-db /
`EXEC` sources). The inspection visitor walks the whole fragment, so a subquery/function
RHS is covered without new traversal code (T2 to confirm with a negative test).

| ID | AST node / construct | Verdict | Test (planned) |
|---|---|---|---|
| 011-T2-A1 | `SetVariableStatement`, target declared scalar local (`DECLARE @x <scalar>` same batch), constant/simple-expression RHS | ALLOW (read-only statement) | `Query_allows_SET_local_scalar_assignment` |
| 011-T2-A2 | `SetVariableStatement`, RHS scalar subquery over allowed objects, target declared scalar local | ALLOW, RHS via global inspection | `Query_allows_SET_assignment_with_subquery_RHS` |
| 011-T2-D0 | `SetVariableStatement`, target `@name` NOT proven a declared scalar local (undeclared, or table-typed) | DENY `UnsupportedStatement` (fail closed; needs DECLARE first) | `Query_denies_SET_to_undeclared_variable` |
| 011-T2-D1 | `SetVariableStatement`, RHS with external source (`OPENROWSET`/linked/`EXEC` subquery) | DENY `UnsupportedStatement` (existing inspection) | `Query_denies_SET_assignment_with_external_RHS` |
| 011-T2-D2 | `SetVariableStatement`, RHS with stateful expression (`NEXT VALUE FOR`) | DENY `UnsupportedStatement` (existing inspection) | `Query_denies_SET_assignment_with_stateful_RHS` |
| 011-T2-D3 | `SetVariableStatement`, RHS with cross-database (3-/4-part) reference | DENY `CrossDatabaseReference` (existing inspection) | `Query_denies_SET_assignment_with_cross_db_RHS` |
| 011-T2-D4 | Option-SET family, AST-level deny list: `PredicateSetStatement` (e.g. `NOCOUNT`, `ANSI_NULLS`, `XACT_ABORT`, `FMTONLY`, `FORCEPLAN`), `SetCommandStatement` (e.g. `DATEFORMAT`, `DEADLOCK_PRIORITY`, `LOCK_TIMEOUT`, `LANGUAGE`), `SetRowCountStatement`, `SetTransactionIsolationLevelStatement`, `SetIdentityInsertStatement`, `SetStatisticsStatement`, `SetTextSizeStatement` — and any other non-`SetVariableStatement` SET shape | DENY `UnsupportedStatement` (already denied today; regression anchors, no behavior change) | `Query_denies_SET_session_and_transaction_options` (Theory over the family) |
| 011-T2-V1 | Validator: `SET` target that is a declared scalar local is not a required parameter | no validator change expected; pin behavior | `Validate_SET_target_is_not_a_required_parameter` in `SqlParameterReferenceValidatorTests.cs` |

Note: `SET OFFSETS` fails to parse on this ScriptDom version (parse error → already denied);
no row needed beyond the fail-closed default.

## T3 — T-SQL table variables (implements: 011/T3)

Disambiguation rule: `@name` is a table variable **only if** proven by a
`DeclareTableVariableStatement` for that exact name in the same batch scope. A bare `@name`,
a scalar `DECLARE`, or a supplied parameter never qualifies a DML target as local.

| ID | AST node / construct | Verdict | Test (planned) |
|---|---|---|---|
| 011-T3-A1 | `DeclareTableVariableStatement` (`DECLARE @t TABLE (…)`) | ALLOW as session-local declaration | `Compare_setup_allows_declare_table_variable` in `SqlSafetyTests.cs` |
| 011-T3-A2 | DML (`INSERT`/`UPDATE`/`DELETE`/`MERGE`) whose target is `VariableTableReference` resolving to a same-batch declared table variable; `SELECT…FROM @t`; `OUTPUT…INTO @t` with primary target also proven local | ALLOW session-local (full target proof required before any allow) | `Query_allows_DML_against_declared_table_variable`, `Compare_setup_allows_table_variable_DML` in `SqlSafetyTests.cs` |
| 011-T3-D1 | DML target `@name` NOT proven a declared table variable (undeclared / scalar-`DECLARE` / parameter). Current code path: `VariableTableReference` is not `NamedTableReference` → `ResolveDirectTarget`/`ResolveTarget` return `Unsupported` → denied | DENY `UnsupportedStatement` (keep fail-closed) | `Query_denies_DML_against_undeclared_table_variable` in `SqlSafetyTests.cs` |
| 011-T3-D2 | Scalar-vs-table confusion: scalar-declared `@v` used as DML target; table variable used in scalar-expression position | DENY `UnsupportedStatement` | `Query_denies_scalar_variable_as_DML_target` in `SqlSafetyTests.cs` |
| 011-T3-D3 | Batch scope: `@t` declared in a different `GO` batch than its DML use | DENY `UnsupportedStatement` (scope = declaring batch) | `Query_denies_table_variable_across_batches` in `SqlSafetyTests.cs` |
| 011-T3-V1 | Validator: `DeclareTableVariableStatement` names join `LocalNames` (today only `DeclareVariableStatement` does — `SqlValidation.cs:256-261`), so `@t` is not a required parameter; `VariableTableReference` DML targets never surface as required parameters | implement + pin | `Validate_table_variable_is_local_not_required`, `Validate_undeclared_table_target_is_rejected` in `SqlParameterReferenceValidatorTests.cs` |

## T4 — PG TRUNCATE (implements: 011/T4; file: `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`)

Real AST (`Statement.Truncate`): `Names: Sequence<TruncateTableTarget{Name: ObjectName}>`,
`Table`, `Only`, `Partitions`, `Identity: Nullable<TruncateIdentityOption{Restart, Continue}>`,
`Cascade: Nullable<TruncateCascadeOption{Cascade, Restrict}>`, `OnCluster`.
Allowed shape: **ALL** names proven current-session temps. Proof = membership in the
session `knownTemps` set (single-part name recorded from `CREATE TEMP` / `SELECT INTO TEMP`
in the same session flow). The current `pg_temp` / `pg_temp_*` prefix shortcut
(`PostgresSafetyClassifier.cs:340-344`) is NOT proof of current-session ownership and must
stop qualifying targets (untrusted schema can carry that prefix).

| ID | AST node / construct | Verdict | Test (planned) |
|---|---|---|---|
| 011-T4-A1 | `Statement.Truncate`, every `Names[].Name` in `knownTemps`, `Identity` null or `Continue`, `Cascade` null or `Restrict`, `OnCluster` null | ALLOW session-local (same standing as temp DML: no mutation approval needed; persistent-mutation path never engaged) | `Truncate_all_session_temps_is_session_local` |
| 011-T4-D1 | `Statement.Truncate` with any persistent (non-`knownTemps`) target | DENY `NonTemporaryWrite` | `Truncate_persistent_target_is_denied` |
| 011-T4-D2 | `Statement.Truncate` with mixed temp + persistent list (one bad target poisons the batch) | DENY `NonTemporaryWrite` | `Truncate_mixed_targets_are_denied` |
| 011-T4-D3 | `Statement.Truncate` with `Cascade == Cascade` (blast radius exceeds named targets) | DENY `UnsupportedStatement` | `Truncate_cascade_is_denied` |
| 011-T4-D4 | `Statement.Truncate` on `pg_temp_*`-prefixed name with NO `knownTemps` provenance | DENY (prefix is not proof). Scope: the prefix-tightening applies to the TRUNCATE path only and MUST NOT change the shared `IsSessionLocal` helper (used by the write / create-index / drop paths) — the T4 brief enforces this. | `Truncate_pg_temp_prefix_without_provenance_is_denied` |
| 011-T4-D5 | `Statement.Truncate` with `OnCluster` set (non-PG cluster routing; session-locality unprovable) | DENY `UnsupportedStatement` | `Truncate_on_cluster_is_denied` |

Ruling R4 (decided HERE, final — T4 must not re-decide): **`RESTART IDENTITY` is DENIED.**
`TRUNCATE … RESTART IDENTITY` on session temps stays `UnsupportedStatement` even when all
targets are proven temps. Reason: `RESTART` mutates sequence state beyond row removal, and
the session-locality of the backing sequence is not proven by temp-table tracking today;
per the plan stop-condition (no allowlist widening on keyword alone) the closed posture is
deny until a later plan proves sequence provenance. `CONTINUE IDENTITY` (or omitted identity
option) is the allowed form per 011-T4-A1. Planned test: `Truncate_restart_identity_is_denied_R4`
plus `Truncate_continue_identity_over_temps_is_session_local`.

## T5 — PG ANALYZE (implements: 011/T5 → `plans/011-analyze-parser-spike.md`, DESIGN COMPLETE)

Real offline parser AST (SqlParserCS 0.6.5, `PostgreSqlDialect`, probe-verified):

- Canonical PG `ANALYZE [VERBOSE] tbl` does **not** produce an AST at all: the parser only
  accepts Hive-style `ANALYZE TABLE …` and throws `ParserException` (`Expected TABLE, found t`;
  `Expected TABLE, found VERBOSE`; bare `ANALYZE` → `found EOF`). `PostgresDocument.TryParse`
  catches → `null` → classifier returns `ParseError`. Existing anchor test
  `Canonical_analyze_is_a_parse_error_until_the_parser_supports_it` pins this.
- The existing `Statement.Analyze` node is Hive-shaped —
  `{ Name: ObjectName, Partitions, ForColumns, Columns, CacheMetadata, NoScan, ComputeStatistics }`
  (no `VERBOSE`, no PG column list) — and the classifier explicitly denies it
  (`case Statement.Analyze` in the Unsupported group). It must NOT be reinterpreted as PG
  `ANALYZE`.

| ID | AST node / construct | Verdict | Test (planned) |
|---|---|---|---|
| 011-T5-R1 | Canonical `ANALYZE …` → no AST (`ParserException`) → `ParseError` | stays DENIED; T5 writes `plans/011-analyze-parser-spike.md`, marks DESIGN COMPLETE, not IMPLEMENTED | (a) green anchor `Canonical_analyze_is_a_parse_error_until_the_parser_supports_it` in `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs` (stays green); (b) spike-doc acceptance tests, intended file `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`: `Analyze_canonical_form_supported_after_parser_spike`, `Analyze_verbose_form_supported_after_parser_spike` (full definitions land in the spike doc) |
| 011-T5-R2 | Hive `ANALYZE TABLE …` → `Statement.Analyze` | stays DENIED `UnsupportedStatement` (must not be mistaken for PG `ANALYZE`) | `Analyze_hive_form_stays_unsupported` in `PostgresSafetyTests.cs` |

Constraints carried into T5: no regex bypass around the parser; no dependency bump without
a regression review (per plan 011/T5 + repo regression policy).

## Standing negatives (must remain denied — regression anchors, no new tests)

| # | Construct | Current anchor (stays green) |
|---|---|---|
| S1 | Dynamic SQL / `EXEC` (`ExecuteInsertSource`, `EXEC (…)`) | `Query_denies_EXEC_and_dynamic_SQL`, `Compare_setup_denies_INSERT_EXEC_even_when_destination_is_local_temp` (`SqlSafetyTests.cs`) |
| S2 | Persistent DDL (incl. non-temp `CREATE/ALTER/DROP`, PG persistent `CreateTable`, `CreateView`, `Alter*`) | `Query_denies_DDL_even_with_mutation_confirmations`; PG `Persistent_create_table_is_unsupported` |
| S3 | Cross-database references (T-SQL 3-/4-part names) | `Classifier_denies_three_and_four_part_names` |
| S4 | External / stateful sources (`OPENROWSET`/`OPENDATASOURCE`/`OPENQUERY`/bulk, `OPENXML`, `NEXT VALUE FOR`) | `Classifier_denies_OPENDATASOURCE`, `Query_denies_stateful_OPENXML_table_source`, `Query_denies_stateful_NEXT_VALUE_FOR_expression` |
| S5 | Function side effects (PG `DeniedExactFunctions`: `nextval`, `setval`, `pg_sleep`, `set_config`, …) | `Visible_admin_and_lo_calls_are_denied_without_echoing_sql`, `Denied_calls_nested_in_other_calls_are_rejected`, `Temp_*_cannot_hide_matrix_calls` family (`PostgresSafetyTests.cs`) |
| S6 | Transaction control (T-SQL `BEGIN/COMMIT/…`; PG `StartTransaction/Commit/Rollback/Savepoint/…`, `SetTransaction`, `SetVariable/SetNames/SetRole/SetTimeZone`) | `Query_denies_transaction_control`; PG denied in the Unsupported group (`PostgresSafetyClassifier.cs:214-234`); `Session_scope_does_not_wrap_sql_or_change_role_or_transaction_mode` |
| S7 | `##temp` (global temps are not session-local: `IsLocalTemp` requires exactly-one-`#` prefix, `SqlSafety.cs:537-540`) | covered by `IsLocalTemp` unit-adjacent temp tests; T3 must not widen it |

Note (drift, T1-relevant only): `SqlValidation.cs` / `Capabilities.cs` moved since plan base
011 (`8aa01f8`). Cites above are against current worktree code. `Capabilities`/`AGENTS.md`
updates stay in 011/T6 and cover only constructs actually shipped by T2–T5.
