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
Allowed shape: **ALL** names proven current-session temps. No schema qualifier qualifies a
TRUNCATE target, not even the `pg_temp` alias that the shared `IsSessionLocal` helper accepts
for other statements, and a `pg_temp_` prefix (in a schema name or in a relation name) is
proof of nothing for any statement: `pg_temp_<N>` can be another session's temp schema and a
persistent table can be named `pg_temp_stuff`.

Proof as implemented (011/T4 fix round 1 + 011/T4b). "Proven temp" below means all of:

1. the target is a single-part name (no schema qualifier);
2. the target identifier has a stored name that is known offline: it is at most 63 bytes
   long in UTF-8, and it is quoted (taken as written) or unquoted and all-ASCII (lower-cased
   A-Z only). An unquoted identifier with any non-ASCII character proves nothing, because the
   server's fold of it depends on the server encoding. An identifier longer than 63 bytes
   proves nothing, because the server truncates it and two different spellings can then
   address one relation;
3. the same name was recorded by this classifier from `CREATE TEMP TABLE` / `SELECT INTO TEMP`
   in the same session flow, from a declaring identifier that also satisfies (2). Names are
   compared ordinally after the ASCII-only fold; no Unicode case mapping takes part;
4. the declaration was not `ON COMMIT DROP` (carried from setup or in the same batch), and it
   was not a `CREATE TEMP TABLE IF NOT EXISTS` of a name this flow already declared
   `ON COMMIT DROP` (a server no-op while that temp exists), and no later `DROP TABLE` in the
   flow may have addressed it (011-T4b-D5);
5. the record reached the statement inside a set this classifier produced
   (`SqlSafetyDecision.SessionTempTables` passed back unchanged). A name that only a
   caller-built plain set supplies proves no TRUNCATE target.

Rule for every other statement that relies on session-temp proof (011/T4b; `INSERT` /
`UPDATE` / `DELETE` / `MERGE` targets, `DROP TABLE`, `CREATE INDEX ... ON`): points 2, 3 and 4
apply unchanged through the shared `IsSessionLocal` helper. Two differences from TRUNCATE
remain: a two-part name whose schema is exactly `pg_temp` (the server's alias for the current
session's temp schema; unquoted in any case, or quoted `"pg_temp"`) qualifies the target
without a recorded declaration, and a name supplied in a caller-built plain set still proves
it. Nothing else about the spelling counts (011/T4b fix round 1): an unqualified name such as
`pg_temp_stuff` or `pg_temp` must be proven like any other temp, a `pg_temp_<N>` schema
qualifier is never proof, and a name with three or more parts is never session-local. An
unproven target is not session-local: DML takes the persistent-mutation path
(`MutationNotAllowed` without approval, `NonTemporaryWrite` in compare setup); `DROP TABLE` /
`CREATE INDEX` are `UnsupportedStatement`.

| ID | AST node / construct | Verdict | Test (planned) |
|---|---|---|---|
| 011-T4-A1 | `Statement.Truncate`, every `Names[].Name` a proven temp (points 1-5 above), `Identity` null or `Continue`, `Cascade` null or `Restrict`, `OnCluster` null, `Only` true or false | ALLOW session-local (same standing as temp DML: no mutation approval needed; persistent-mutation path never engaged). `ONLY` decision: `TRUNCATE [TABLE] ONLY <temp>[, ...]` is ALLOWED on the same terms as the form without `ONLY`. `Only` is one flag for the whole statement, it only narrows the statement to the named tables (no descendants), and it does not relax the target proof: `TRUNCATE ONLY <persistent>` stays DENY `NonTemporaryWrite` (011-T4-D1). | `T4_Truncate_all_session_temps_is_session_local` (case `TRUNCATE ONLY t`), `T4_Truncate_persistent_target_is_denied` (case `TRUNCATE ONLY items`) |
| 011-T4-D1 | `Statement.Truncate` with any target that is not a proven temp (points 1-5 above): persistent, schema-qualified, non-ASCII unquoted or longer than 63 bytes on either side, `ON COMMIT DROP`, or supplied only by a caller-built set | DENY `NonTemporaryWrite` | `T4_Truncate_persistent_target_is_denied`, `T4_Truncate_schema_qualified_name_is_denied_even_when_relation_name_is_a_tracked_temp`, `T4_Truncate_proof_does_not_depend_on_unicode_case_folding`, `T4_Truncate_of_setup_temp_created_on_commit_drop_is_denied`, `T4_Truncate_of_on_commit_drop_temp_in_the_same_batch_is_denied`, `T4_Truncate_proof_travels_only_in_a_classifier_produced_temp_set` |
| 011-T4-D2 | `Statement.Truncate` with mixed temp + persistent list (one bad target poisons the batch) | DENY `NonTemporaryWrite` | `T4_Truncate_mixed_targets_are_denied` |
| 011-T4-D3 | `Statement.Truncate` with `Cascade == Cascade` (blast radius exceeds named targets) | DENY `UnsupportedStatement` | `T4_Truncate_cascade_is_denied` |
| 011-T4-D4 | `Statement.Truncate` on `pg_temp_*`-prefixed name that is not a proven temp | DENY `NonTemporaryWrite` (prefix is not proof). TRUNCATE also rejects the plain `pg_temp.<relation>` alias, which the shared `IsSessionLocal` helper still accepts on the write / create-index / drop paths. The helper itself was tightened later under 011/T4b, because every statement must rest on the same proof that the target is a temp of the current session: its identifier fold and `ON COMMIT DROP` handling (011-T4b-D1..D4), and its `pg_temp_` prefix shortcut, which no longer exists for any statement (011-T4b-D7). | `T4_Truncate_pg_temp_prefix_without_provenance_is_denied` |
| 011-T4-D5 | `Statement.Truncate` with `OnCluster` set (non-PG cluster routing; session-locality unprovable) | DENY `UnsupportedStatement` | `T4_Truncate_on_cluster_is_denied` |
| 011-T4b-D1 | DML / `DROP TABLE` / `CREATE INDEX` whose target or whose declaring `CREATE TEMP` / `SELECT INTO TEMP` identifier is unquoted with a non-ASCII character (for example `CREATE TEMP TABLE "é" ...; INSERT INTO É`, and the reverse quoting) | DENY as not session-local: DML `MutationNotAllowed` / `NonTemporaryWrite` in setup; DDL `UnsupportedStatement`. ASCII is unchanged: unquoted `Items` matches temp `items`, quoted `"Items"` does not. Stricter than the server needs when both sides are the same unquoted non-ASCII spelling; quote the name to use it. | `T4b_Non_ascii_name_never_proves_a_target_across_quoting`, `T4b_Carried_temp_declared_with_non_ascii_unquoted_name_proves_no_target`, `T4_Non_truncate_non_ascii_fold_is_denied`, `T4b_Ascii_fold_and_exact_quoted_match_stay_session_local`, `T4b_Ascii_quoted_case_mismatch_stays_denied` |
| 011-T4b-D2 | DML / `DROP TABLE` / `CREATE INDEX` / `TRUNCATE` on a name declared `CREATE TEMP TABLE ... ON COMMIT DROP`, carried from setup or in the same batch | DENY as not session-local: DML `MutationNotAllowed` / `NonTemporaryWrite` in setup; `DROP TABLE` / `CREATE INDEX` `UnsupportedStatement`; `TRUNCATE` `NonTemporaryWrite`. The name is not reported in `SessionTempTables`. `ON COMMIT DELETE ROWS` / `PRESERVE ROWS` keep the table and stay allowed. | `T4b_Carried_on_commit_drop_temp_proves_no_target`, `T4b_On_commit_drop_temp_in_the_same_batch_proves_no_target`, `T4b_Temp_that_survives_commit_stays_session_local` |
| 011-T4b-D3 | `CREATE TEMP TABLE IF NOT EXISTS x` after this flow declared `x` `ON COMMIT DROP` (also after an unquoted non-ASCII `ON COMMIT DROP` declaration, for any non-ASCII `x`) | The create itself stays allowed; it records no proof, so later writes / DDL / TRUNCATE on `x` are DENIED. An all-ASCII `x` is still proven after an unquoted non-ASCII `ON COMMIT DROP` declaration, whose stored name is non-ASCII under every server fold. A plain `CREATE TEMP TABLE x` (no `IF NOT EXISTS`) records proof again, same batch or carried: it fails on the server while the name is taken and a failed statement ends the batch, so reaching the next statement means a new temp without `ON COMMIT DROP` exists. | `T4b_If_not_exists_does_not_restore_proof_for_an_on_commit_drop_temp`, `T4b_If_not_exists_after_non_ascii_on_commit_drop_proves_no_non_ascii_name`, `T4b_Carried_non_ascii_on_commit_drop_record_blocks_if_not_exists_proof`, `T4b_Ascii_name_is_still_proven_after_a_non_ascii_on_commit_drop_declaration`, `T4b_If_not_exists_without_an_on_commit_drop_record_still_proves_the_temp`, `T4b_Plain_create_after_on_commit_drop_of_the_same_name_records_proof_again`, `T4b_Plain_create_is_proven_after_a_long_on_commit_drop_declaration` |
| 011-T4b-D4 | Unicode-escape identifier `U&"..."` (with or without `UESCAPE`) as a relation name | No AST: the offline parser rejects it → `ParseError`. A quoted `"\0074"` is an ordinary name and is never decoded to `t`. | `T4b_Unicode_escape_identifier_is_a_parse_error`, `T4b_Quoted_backslash_name_is_not_decoded` |
| 011-T4b-D5 | `DROP TABLE` (session-local through proof or through the `pg_temp.<relation>` alias) and the proof it revokes | The DROP revokes proof for every tracked temp it may address. Relation identifier with a known stored name (point 2): exactly that name loses proof. Relation identifier without one (unquoted non-ASCII, or longer than 63 bytes; reachable only as `pg_temp.<relation>`): the DROP itself stays session-local, and EVERY proof in the flow is revoked, including unrelated names and names from a caller-built set, because the match cannot be narrowed offline. Later writes / DDL / TRUNCATE on those names are DENIED until a new declaration proves them. | `T4b_Drop_with_unknown_stored_name_revokes_every_tracked_temp`, `T4b_Drop_with_unknown_stored_name_revokes_a_carried_temp`, `T4b_Drop_with_known_stored_name_keeps_the_other_temps`, `T4_Truncate_after_drop_loses_provenance` |
| 011-T4b-D6 | Any identifier longer than 63 bytes (UTF-8) as a declaring `CREATE TEMP` / `SELECT INTO TEMP` name or as a DML / `DROP TABLE` / `CREATE INDEX` / `TRUNCATE` target | DENY as not session-local (reasons as in 011-T4b-D2); the declaration itself stays allowed and records nothing. Stricter than the server needs when both sides are the same long spelling; shorten the name to use it. A long `ON COMMIT DROP` declaration revokes every proof and afterwards `IF NOT EXISTS` proves no name at all (the truncated name may be any name); a plain `CREATE TEMP TABLE` still proves. Exactly 63 bytes is unchanged. The limit assumes a stock server (`NAMEDATALEN` 64) and counts UTF-8 bytes. | `T4b_Name_longer_than_63_bytes_never_proves_and_is_never_proven`, `T4b_Carried_long_on_commit_drop_name_blocks_if_not_exists_proof`, `T4b_Name_of_exactly_63_bytes_stays_session_local`, `T4b_Plain_create_is_proven_after_a_long_on_commit_drop_declaration` |
| 011-T4b-D7 | DML / `DROP TABLE` / `CREATE INDEX` on an unqualified name spelled `pg_temp_*` or `pg_temp` that is not a proven temp; on a `pg_temp_<N>.<relation>` name; on a name of three or more parts starting with `pg_temp` | DENY as not session-local (reasons as in 011-T4b-D1); with mutation approval the DML is an ordinary persistent mutation. A proven temp that happens to be named `pg_temp_stuff` is session-local like any other. The two-part `pg_temp.<relation>` alias stays session-local for these statements (not for TRUNCATE, 011-T4-D4). | `T4b_Pg_temp_prefix_is_not_proof_for_any_statement`, `T4b_Write_to_a_pg_temp_prefixed_name_is_an_ordinary_approvable_mutation`, `T4b_Proven_pg_temp_prefixed_temp_and_pg_temp_alias_stay_session_local`, `T4b_Dropped_pg_temp_prefixed_temp_loses_proof`, `T4_Non_truncate_session_local_verdicts_are_unchanged` |

Ruling R4 (decided HERE, final — T4 must not re-decide): **`RESTART IDENTITY` is DENIED.**
`TRUNCATE … RESTART IDENTITY` on session temps stays `UnsupportedStatement` even when all
targets are proven temps. Reason: `RESTART` mutates sequence state beyond row removal, and
the session-locality of the backing sequence is not proven by temp-table tracking today;
per the plan stop-condition (no allowlist widening on keyword alone) the closed posture is
deny until a later plan proves sequence provenance. `CONTINUE IDENTITY` (or omitted identity
option) is the allowed form per 011-T4-A1. Tests: `T4_Truncate_restart_identity_is_denied_R4`
plus `T4_Truncate_continue_identity_over_temps_is_session_local`.

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
