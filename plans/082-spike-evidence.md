# Plan 082 post-081 spike evidence

Date: 2026-10-19 (Europe/Warsaw)

## Scope and identity

The controller first attempted the user-authorized `local-playground` profile (`localhost,14335` / `AdventureWorks2022`); the connection timed out before SQL execution. The user then redirected this spike to the exact fixed SQL Server profile `civiclens-dev` (`localhost`, database `CivicLens`, integrated authentication). The PostgreSQL profile named `civiclens` was not used. Docker/playground diagnostics stopped after that steering; no Docker container was created, reseeded, or changed by this spike.

The first SQL request on `civiclens-dev` was the requested read-only identity query:

```sql
SELECT DB_NAME() AS database_name, @@VERSION AS engine_version;
```

It returned database `CivicLens` and `Microsoft SQL Server 2025 (RTM-GDR) (KB5128492) - 17.0.1145.4 (X64)`, Developer Edition on Windows. This matched the authorized profile before the synthetic measure ran.

The existing Task 0 baseline record covers unchanged revision `9dc73f688790b805ed5707b93f156af863f23f74`, predecessor implementation evidence for 079/080/081/087, and passing `just verify`; that gate was not rerun. The worktree CLI/MCP was rebuilt from source using `dotnet build src/SqlHarness.Cli/SqlHarness.Cli.csproj --no-restore -v minimal` (exit 0, 0 warnings, 0 errors).

## Main synthetic batch

The `sqlharness_measure` request used `repeat: 1`, subject `plan082-synthetic`, scope `post-081-spike`. It was run through the rebuilt worktree CLI/MCP with fixed profile `civiclens-dev`. Offline validation returned `status: success`, `validation: passed`, and `execution: not-run`. The successful live call returned one synthetic count and no business data.

The batch used a unique temp name because `#temp` already existed in the reused session. It drops only the temp table it creates at the end; this cleanup is necessary because `measure` performs a warmup and measured execution on a reused session. The cursor is explicitly closed and deallocated.

```sql
DECLARE @t TABLE (id int NOT NULL);
DECLARE @i int = 1;
WHILE @i <= 5 BEGIN
  INSERT INTO @t SELECT @i;
  SET @i += 1;
END;
DECLARE @cursorValue int;
DECLARE local_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT id FROM @t ORDER BY id;
OPEN local_cursor;
FETCH NEXT FROM local_cursor INTO @cursorValue;
WHILE @@FETCH_STATUS = 0 BEGIN
  FETCH NEXT FROM local_cursor INTO @cursorValue;
END;
CLOSE local_cursor;
DEALLOCATE local_cursor;
CREATE TABLE #p082_statement_io (id int NOT NULL);
INSERT INTO #p082_statement_io SELECT id FROM @t;
CREATE INDEX IX_p082_statement_io_id ON #p082_statement_io(id);
WITH generated(n) AS (
  SELECT CAST(1 AS int) UNION ALL SELECT n + 1 FROM generated WHERE n < 100
)
SELECT COUNT_BIG(*) AS synthetic_count FROM generated AS left_rows
JOIN generated AS right_rows ON right_rows.n = left_rows.n
OPTION (MAXRECURSION 100);
DROP TABLE #p082_statement_io;
```

## Plan identity and execution observations

The measure artifact is `20261019T061454516Z-CivicLens-3a93097b133b4cc4a5a117a908ebfc59`. Its statement section contains 15 plan documents, with `omittedStatements: 0`:

- The loop's `INSERT INTO @t SELECT @i` has five plan documents (ordinals 0–4), each with a distinct `StatementId` (1–5), the same `StatementCompId` (4), identical `QueryHash` `0x11B3A5EB004B3079`, identical `QueryPlanHash` `0x44402F21575A02E5`, and root `ActualExecutions=1`. The five documents represent five loop iterations; aggregating by compiled statement identity (`StatementCompId`) gives five executions. `StatementId` alone would incorrectly turn iterations into distinct source statements.
- The cursor `OPEN` has one plan document. The two source `FETCH` statements are distinguishable: the initial fetch has one plan document (`StatementCompId=10`); the fetch inside the loop has five documents (`StatementCompId=12`), including the final empty fetch. All six plan documents have root `ActualExecutions=1`.
- The recursive query has a plan with `Index Spool` and `Table Spool` operators. The two `Index Spool` operators each report 201 logical reads; the spool operators report 100 actual rows, and recursive `Filter` operators report `ActualExecutions=100`.
- A separate tiny synthetic batch explicitly repeated the exact same `SELECT COUNT_BIG(*) AS generated_count FROM @v` twice. Its artifact is `20261019T061801835Z-CivicLens-c81637a5d3db455789939d30bdb5f06a`. The two plan documents have different `StatementId` and `StatementCompId` values (2/3 and 3/4), while retaining equal `QueryHash` `0x124E0414D4B99C1F` and `QueryPlanHash` `0x0160ADD81F1A82C3`. This supports using `StatementCompId` to group loop-expanded documents while keeping equal-text source occurrences separate.

## IO reconciliation

The measured batch reported 1,237 logical reads:

| Engine STATISTICS IO object | Observed reads | Plan reads | Difference | Finding |
|---|---:|---:|---:|---|
| Table-variable backing object (`#A531CF90`) | 7 | 7 (`[@t]`) | 0 | Reconciles by logical table variable identity. Plan scans total 2. |
| `#p082_statement_io` | 6 | 6 | 0 | Reconciles. Plan scans total 1, including the scan used by `CREATE INDEX`. |
| `Worktable` | 1,224 | 424 | -800 | Does not reconcile. Cursor worktable counters are 10 reads for `OPEN` plus 6 `FETCH` documents × 2 reads = 22. The recursive query's two `Index Spool` operators contribute 201 reads each = 402. Its spool objects have no plan `Object` identity, and the cursor plan exposes only `[tempdb]` / `[CWT_PrimaryKey]`, so attribution cannot establish a unique engine worktable mapping. Plan scans across these operators total 2. |
| `Workfile` | 0 | 0 | 0 | Both report zero reads. |
| **Total** | **1,237** | **437** | **-800** | The batch is partial because Worktable reads fail reconciliation. |

SQLHarness's measure artifact exposes per-object logical reads but not engine `Scan count`, so scan reconciliation could not be independently checked. Actual plan scan counters are preserved in the XML. The user-facing aggregate read totals and per-object values were taken from the measure report; per-statement `StatementCompId`, `StatementId`, hashes, operator counters, and runtime executions were read from the saved plan XML.

The first live attempt stopped at temp table creation because an object named `#temp` was already present in the reused SQL Server session; this spike did not establish how it arrived. A second attempt with a unique `#p082_statement_io` name showed that the warmup temp persists into the measured run. No persistent object or data was written. The final batch uses its own unique temp name and drops it in each execution.

## Preserved synthetic plan XML

The exact `.sqlplan` files from the two synthetic artifacts were retained in the Task 0 scratch directory for Task 1 fixture capture: 15 plans for the main spike and 3 for explicit repeats. They contain only generated SQL, table-variable/temp-table identifiers, runtime counters, and plan metadata. The originating artifact IDs are listed above.

## Task 1 implications

- Group repeated plan documents by nonzero `StatementCompId` within a batch only when normalized text and plan identity are consistent, then aggregate counters and root `ActualExecutions`. If `StatementCompId` is missing/zero or conflicts with another document, preserve separate occurrences and fail closed; retain `StatementId` and document ordinal as evidence/order, not source identity.
- Keep explicit equal-text source occurrences separate when their `StatementCompId` differs, even when `QueryHash` and `QueryPlanHash` match.
- Treat internal worktable/spool attribution as partial when a plan has no usable object identity or its logical reads do not match engine IO. This batch confirms table-variable and named temp-table read reconciliation but does not justify marking Worktable statements confirmed.
- The plan's Worktable mismatch is 800 reads for this SQL Server 2025 run; scan-count comparison remains unobserved through the measure artifact surface.
