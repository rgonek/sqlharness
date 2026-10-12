# Plan 052 verification evidence

This record covers the frozen implementation at `c07f0014dad7cf999be1299799820c1ca5003fe1` (`Qualify PostgreSQL mutation approval exception`). Documentation status is complete; the implementation branch has not been merged or pushed.

## Verification

### Windows gate

Executed `pwsh ./scripts/verify.ps1` at `c07f0014dad7cf999be1299799820c1ca5003fe1`.

Result: exit 0 and literal `verify: OK`; build completed with 0 warnings and 0 errors; `SqlHarness.Tests` passed 3745/3745 with no skips; `SqlHarness.Mcp.Tests` passed 390 with 4 expected skips. See `.superpowers/sdd/052-postgres-read-only-transactions/verify-windows-final.log` for the local run record.

### Linux gate history and final result

The first standard shared-clone invocation, `pwsh ./scripts/verify-linux.ps1`, failed at `ui-check`. During that run the shared clone was externally switched to `ci/plan-032-matrix7f6f1d0`; the UI install then lacked `is-potential-custom-element-name`, and Vitest could not start a jsdom worker. The recorded UI output included 134 passing tests before the stage failed. This run did not reach a successful full gate and is retained in `.superpowers/sdd/052-postgres-read-only-transactions/verify-linux.log`.

The first isolated ext4 run used an otherwise identical scratch copy of `scripts/verify-linux.ps1`, changing only the repository-root and clone-path constants for the dedicated `sqlharness-gate-plan052` clone. It passed all 32 UI files / 148 tests, built with 0 warnings and 0 errors, and passed all 3745 Core tests. One unchanged MCP test, `Request_scope_EOF_cancels_A_and_never_runs_queued_B`, timed out at 30 seconds; the isolated full run exited 1 with MCP 390 passed, 1 failed, 3 skipped. The exact test passed in a focused run in 705 ms (`linux-mcp-timeout-focused.log`). The initial isolated full run remains recorded in `verify-linux-isolated.log`.

The controller then ran one unchanged full-gate recovery against the same frozen source and isolated ext4 clone, using the same scratch-script path-constant adjustments and no test-stage changes. Command: `pwsh ./scripts/verify-linux-isolated.ps1`. Result: session 32268 exited 0 with literal `verify-linux: OK`; UI passed 148 tests across 32 files, build had 0 warnings and 0 errors, Core passed 3745/3745, and MCP passed 391 with 3 expected skips. The recovery log is `.superpowers/sdd/052-postgres-read-only-transactions/verify-linux-recovery.log`.

### PostgreSQL proof and integration

Live proof used the authorized disposable native PostgreSQL 18.6 target `127.0.0.1:55452/sqlharness_plan052`; the runner command was `pwsh ./scripts/prove-postgres-readonly.ps1 -ConfirmDisposableDatabase sqlharness_plan052`. It passed 1/1 with 0 skips. Persistent function writes, persistent-sequence `nextval`, and the next per-command write after changing the session default were rejected with SQLSTATE `25006`. TEMP DML and TEMP `EXPLAIN ANALYZE` succeeded. `pg_stat_statements_reset()` succeeded inside READ ONLY, so it remains explicitly denied. `pg_promote()` returned SQLSTATE `55000` because the target was a primary; that result does not prove READ ONLY containment. The unique proof schema was cleaned, the dedicated cluster was stopped, and process credentials were cleared. Evidence is in [052-readonly-proof.md](052-readonly-proof.md); the approved live-proof review found no issues.

The frozen runtime integration command was run with `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` supplied by the controller process and `$env:SQLHARNESS_PG_READONLY_PROOF_CONFIRM_DATABASE = 'sqlharness_plan052'`:

```powershell
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~PostgresReadOnlyRuntimeIntegrationTests
```

It passed 1/1, 0 failed, 0 skipped, exit 0, in 720 ms. It covered persistent-write rejection and unchanged table state; read-write setup and approved mutation; TEMP update commit and rollback for incomplete results; skipped earlier result sets; and session reuse after rejection and disposal. Full details are in the ignored task report and the recorded runtime integration output.

The integration category command was `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter Category=PostgresIntegration`. Result: 3 passed and 4 skipped. All 4 skips were PostgreSQL TLS fixtures; those TLS cases were not executed. The final PostgreSQL-focused selection, `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Postgres"`, passed 832 with 4 TLS skips, as recorded in `postgres-final.log`. These two counts refer to different selections and are recorded separately.

### Reviews

The final whole-branch read-only review covered `24bf611..c07f001` (Sol low). It found no Critical, Important, or Minor issues and assessed the branch ready to merge with gates. The step-1 live-proof review approved the evidence and its interpretation. Reviews explicitly left outside their judgment the risk of opaque user-defined functions in read-write TEMP setup, whether measured client overhead meets a product latency budget, and behavior beyond the live-tested PostgreSQL 18.6 version.

## Implemented boundary and known limits

Every non-approved PostgreSQL command runs in a per-command READ ONLY transaction; PostgreSQL rejects persistent relation writes. Approved persistent mutation uses the exact database confirmation path. Explicit denials remain for effects not contained by READ ONLY, including statistics reset, promotion and backup, WAL/replication, external access, backend control, advisory locks, sleep, server-file access, and `set_config`. `nextval` and `setval` also remain denied in read-write PostgreSQL CompareSetup.

TEMP setup retains the existing name-proof and static visible-effects rules and executes read-write. A setup `SELECT` can call an opaque user-defined function that writes persistent data because function bodies and transitive effects are not inspected. This limitation is documented; use a least-privilege setup role. No new opaque-function whitelist was added. PostgreSQL live behavior was proved on 18.6 only.

The timing proof used 100 balanced pairs after warm-up for trivial local `SELECT 1`: bare execution totaled 24.491 ms; the wrapped transaction (`BeginTransaction` + `SET TRANSACTION READ ONLY` + query + `COMMIT`) totaled 56.540 ms. The aggregate delta was 32.050 ms (130.9%); the median paired delta was 0.323 ms. This is measurable client overhead. SQLHarness measure/compare latency remains based on server `EXPLAIN` Planning Time + Execution Time and excludes client transaction round trips; the evidence does not claim zero client overhead.

## Progress ledger rulings (verbatim, in order)

> Ruling: Use installed native PostgreSQL 18.6 instead of Docker PostgreSQL 16 — Docker engine fails HTTP 500 and disposable target authorization is engine-version independent — if wrong, repeat proof on PostgreSQL 16 before release.

> Ruling: Retain and extend denials for statistics reset, promotion, backup, WAL/replication and other effects not contained by READ ONLY — live pg_stat_statements_reset succeeded and primary pg_promote failed 55000, so plan step4 cannot safely remove these — if wrong, harmless administrative reads may remain overrestricted.

> Ruling: Accept measured client wrapper overhead and preserve server EXPLAIN Planning+Execution Time as benchmark metric — proof shows +0.323ms median full-command delta but production measurement excludes transaction round trips — if wrong, benchmark distortion requires revisiting timing validation.

> Ruling: Keep nextval/setval statically denied in PostgreSQL CompareSetup while allowing wrapped query/measurement — setup executes read-write so removing global denials would create a new persistent-sequence mutation path — if wrong, legitimate temp-sequence setup stays overrestricted.

> Ruling: Preserve existing name-proof/read-write TEMP setup exemption rather than introduce a new opaque-function whitelist in plan052; explicitly document that persistent-writing UDFs inside setup remain outside server containment — design mandates current setup rules and plan047 owns setup safety simplification; an AST function whitelist cannot prove transitive effects — if wrong, a least-privilege setup role can still mutate persistent data through permitted UDFs and follow-up setup hardening is required.

> Ruling: Run identical verify-linux script via scratch copy pointing at dedicated ext4 clone sqlharness-gate-plan052 — shared clone was externally switched to ci/plan-032-matrix7f6f1d0 mid-run and npm modules disappeared — if wrong, rerun standard shared gate after other jobs finish; no test/verification stages altered.
