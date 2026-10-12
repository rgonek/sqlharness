# Plan 052 Task 2: runtime enforcement and safety mapping

Status: implemented and ready for task review. Runtime behavior and safety mapping are covered by unit tests and a confirmed PostgreSQL integration run. The task-2 changes are committed separately from proof documentation.

## Implementation

- Every new `SqlExecutionCommand` defaults to PostgreSQL `ReadOnly`. Unknown enum values also take the read-only path.
- PostgreSQL read-only commands run inside an explicit transaction with `SET TRANSACTION READ ONLY`. The Npgsql reader owns that transaction until all result sets reach EOF and are advanced through the terminal result. Complete reads commit; execution errors, read errors, cancellation, disposal before completion, or any skipped result set roll back. Cleanup errors do not replace the original database error.
- Setup commands receive `SessionSetup` only when the caller carries the successful PostgreSQL setup classification. Approved mutation commands receive `ApprovedMutation` only after the query classifier allows the request and confirms that the query contains a mutation. These modes retain the prior direct read-write execution behavior.
- SQLSTATE `25006`, including nested or deferred provider errors, maps to Safety exit code 2 and the exact message: `PostgreSQL rejected a write in a read-only transaction; persistent writes need an approved mutation.`
- Command SQL, bound parameters and timeout values remain unchanged. Benchmark timing still comes from PostgreSQL `EXPLAIN ANALYZE` output.

## TDD and verification

RED was recorded before implementation:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~PostgresReadOnlyRuntimeTests
```

Result: 2 failed, 0 passed. Both tests expected SQLSTATE `25006` to map to Safety but received `SqlExecution`, confirming the missing mapping.

After implementation, the focused PostgreSQL, setup/measure, compare and parameter-set test selection passed: 812 passed, 0 failed, and the environment-gated runtime integration test was skipped in the implementation process because it had no database credentials.

The controller ran the runtime integration test on its authorized `127.0.0.1:55452/sqlharness_plan052` target with exact disposable database confirmation. The final frozen-source run passed: 1 passed, 0 failed, 0 skipped, exit 0, 720 ms. It proved persistent writes are rejected without changing the table; setup and approved persistent mutation remain read-write; incomplete TEMP `UPDATE ... RETURNING` results roll back; fully consumed TEMP updates commit; skipped earlier result sets prevent commit; and the same session works after rejection and disposal.

Controller command (the controller process provides `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING`):

```powershell
$env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE = 'sqlharness_plan052'
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~PostgresReadOnlyRuntimeIntegrationTests
```

The first expanded integration attempt failed because it tried `CREATE TEMP TABLE` inside the explicit read-only transaction. PostgreSQL rejected temporary DDL with SQLSTATE `25006`, while the step-1 proof had established only that TEMP DML is allowed. The fixture was corrected to create the temporary table through classified setup, then verify commit and rollback using TEMP DML. The final controller run passed against that corrected fixture.

## Review notes and scope

Self-review confirmed the default and unknown execution modes fail closed, read-write metadata is gated by the existing classifier path, reader disposal cannot leave a read-only transaction active, and provider errors cannot be hidden by cleanup. No database credentials were read by this process, and no database was accessed here.

This task does not change the mutation denylist or behavior documentation. Both repository-wide verification gates remain with the controller and must run serially after task review.
