# Plan 072 final fix wave

Base: `09d77f94f47667a07919e2abbdeba78376e13dec`

## Changes

- Reject `DropTriggerStatement` with `TriggerScope.AllServer` in the shared Mutation classifier guard. The same guard runs for nested statements. Database-scoped and unqualified trigger drops remain Schema tier.
- Restrict PostgreSQL generic `DROP` promotion to View, Schema, Sequence, and Type after the existing Table, Index, Database, and Role handling. Stage and unrecognized object types remain unsupported.
- Under PostgreSQL Mutation only, reject multipart cross-database qualifications on newly accepted DDL object names and relation targets. This covers CreateTable, CreateIndex, AlterTable, AlterIndex, AlterView, Analyze, CreateView, CreateSchema, CreateSequence, CreateFunction, CreateProcedure, CreateType, CreateTrigger, generic Drop, and Truncate. Query and CompareSetup classification paths are unchanged.
- Correct the persistent TRUNCATE comment to describe its Mutation Schema tier and legacy Query/CompareSetup rejection.
- Add compare dry-run coverage where baseline or candidate is the highest tier, including matrix routing.

## RED/GREEN evidence

RED command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafetyTierTests|FullyQualifiedName~PostgresSafetyTierTests|FullyQualifiedName~DryRunCommandTests"
```

Output: `Failed: 10, Passed: 217, Skipped: 0, Total: 227`. The failures reproduced DROP TRIGGER ALL SERVER (top-level and nested), DROP STAGE, and the six initial multipart DDL cases (DROP TABLE, DROP SCHEMA, CREATE TABLE, CREATE INDEX, ALTER TABLE, TRUNCATE). An initial table-trigger input using `DROP TRIGGER ... ON dbo.T` was not valid for this parser and was replaced with the supported unqualified drop form.

Final GREEN command:

```text
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlSafety|FullyQualifiedName~Postgres|FullyQualifiedName~DryRunCommandTests"
```

Output: `Passed: 1501, Failed: 0, Skipped: 11, Total: 1512`. The 11 skipped tests are existing PostgreSQL integration tests requiring a live database. This includes the added regression cases for all reported findings, supported DDL target families, and unchanged Query/CompareSetup rejections.

`git diff --check` completed without errors. The test project had one transient compile failure while the new public theory accepted internal `SqlUsage`; the test now accepts usage names as strings and parses them internally. The final combined run above passed.

## Exact tests added

- `SqlSafetyTierTests.Server_scope_and_cross_database_stay_rejected`: DROP TRIGGER ALL SERVER at top level, inside IF, and inside BEGIN/END.
- `SqlSafetyTierTests.Database_and_table_trigger_drops_remain_schema_tier`: `ON DATABASE` and unqualified trigger drops.
- `PostgresSafetyTierTests.Mutation_usage_assigns_postgres_tiers`: generic DROP View, Schema, Sequence, and Type remain Schema tier.
- `PostgresSafetyTierTests.Rejected_under_mutation`: DROP STAGE.
- `PostgresSafetyTierTests.Mutation_rejects_cross_database_qualification_on_ddl_targets`: multipart names across supported table, index, truncate, view, schema, sequence, function, type, trigger, and analyze forms; also sequence ownership qualification.
- `PostgresSafetyTierTests.New_mutation_rules_do_not_change_legacy_usage_rejections`: Query and CompareSetup retain their existing UnsupportedStatement decisions.
- `DryRunCommandTests.Compare_dry_run_includes_session_tier_from_each_measured_side` and `Compare_matrix_dry_run_includes_each_measured_side_tier`: baseline and candidate contributions through regular and matrix compare.

## Self-review and remaining concerns

The qualification guard is called only for `SqlUsage.Mutation`, so the new PostgreSQL rules cannot change Query or CompareSetup outcomes. The generic DROP fallback now fails closed for Stage and future enum categories. The server-trigger guard checks both the create/alter trigger body node and the distinct drop-trigger node before tier promotion, including nested traversal.

The installed parser does not parse the task-brief's PostgreSQL CREATE PROCEDURE example (it yields ParseError); no semantic regression input can exercise that AST branch with this parser version. The qualification helper covers the CreateProcedure target if a future parser version exposes it. Live database behavior and full Windows/Linux gates were not run in this fix wave; the controller owns those final gates.