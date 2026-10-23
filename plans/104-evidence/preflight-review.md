# Plan 104 preflight review

Date: 2026-10-23  
Checkout: `D:\Dev\sqlharness\.worktrees\plan-104`  
Revision inspected: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`  
Scope: read-only source and plan review; no tests, implementation, commit, or database access.

## Verdict

**STOP before Tasks 1–3.** The current capture path does not positively distinguish a generated table-variable name such as `#A1B2C3D4` from an explicitly named local temp table such as `#ABCDEF12`. The same 8-hex shape is accepted by the current table-variable classifier. Existing plan and SQL provenance is retained separately but does not provide a direct mapping from STATISTICS IO rows to plan-object kinds. The plan’s STOP condition therefore applies.

No bounded implementation that both satisfies the exact median assertions and preserves the stated no-false-merging contract is grounded in the current captured fields. Obtain a reviewed resolution before implementation. Do not treat the regex as positive identification.

## Findings

1. **The STATISTICS IO capture drops provenance before any proposed cross-run classification.**  
   In [Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs), `TempTableKey.Normalize` strips only a SQL Server session suffix matching the `__<12 hex digits>` shape; otherwise it returns the input name. Both `StatisticsIoParser` and `StatisticsIoDetailParser` call it before placing the name in their dictionaries or records ([Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs), [Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs)). `TableIoCounters` contains a table string and counters only; it has no object kind or original-name/provenance field ([Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs)). `TableIoAccumulator` then merges entries by that normalized table string ([Diagnostics.cs](../../src/SqlHarness.Core/Diagnostics.cs)). Consequently, an exact `#ABCDEF12` key carries no indication whether it came from an explicit temp object or an engine-generated name.

2. **The existing aggregation is exactly the ambiguous shape match.**  
   `JournalBenchmarkBuilder.TableVariableName` matches an optional `[dbo].` prefix and optional brackets around `#` plus eight hex digits, without consulting SQL text or plan evidence ([JournalBenchmarkBuilder.cs](../../src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs)). `NormalizeName` maps every match to `table variables`, and `Normalize` groups by that result before journal medians ([JournalBenchmarkBuilder.cs](../../src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs)). This handles changing generated names but necessarily also groups an explicit `#ABCDEF12`.

3. **Plan object types are separate from STATISTICS IO keys.**  
   The plan metrics extractor can classify plan objects whose names start with `[@` as table variables and `[#` as temp tables ([StatementMetrics.cs](../../src/SqlHarness.Core/StatementMetrics.cs)). However, `StatementReconciler` does not correlate a generated STATISTICS IO key to one of those named plan objects: it normalizes plan objects by their explicit kind, while the observed side has no kind and applies another `#?8-hex` heuristic ([StatementReconciler.cs](../../src/SqlHarness.Core/Journal/StatementReconciler.cs)). Its group-sum reconciliation can detect aggregate counter mismatches; it does not establish which observed `#XXXXXXXX` row is a table variable versus an explicit temp table.

   `CompareRunArtifact` retains `PlanXmls`, `TableIo`, and `ExecutedSql`, but `TableIo` and `ExecutedSql` are journal-only/JSON-ignored fields ([Artifacts.cs](../../src/SqlHarness.Core/Artifacts.cs)). The journal uses plans for statement extraction and reconciliation, but the table IO normalization path itself receives only `TableIo` records and their names. Saved `runs.jsonl` writes `LogicalReadsByTable` plus plan-file references as separate fields ([Artifacts.cs](../../src/SqlHarness.Core/Artifacts.cs)); it does not persist the original STATISTICS IO line or an association from an IO name to a plan object. Plan XML therefore provides useful object identities, but not the missing per-counter link.

4. **The benchmark distributions also lack typed keys.**  
   `BenchmarkReports.CreateVariantReport` totals by exact dictionary key and computes each distribution by looking up that same key in each run, inserting zero when absent ([BenchmarkRunner.cs](../../src/SqlHarness.Core/BenchmarkRunner.cs)). Those dictionaries come from `StatisticsIoParser.Parse` in the SQL Server dialect ([SqlServerDialect.cs](../../src/SqlHarness.Core/Dialect/SqlServerDialect.cs)). Adding the 8-hex classifier before this distribution would repair medians for generated variables while silently changing the identity of explicit names with the same shape.

5. **The current regression style cannot prove the needed distinction without new provenance.**  
   Existing journal coverage constructs `TableIoCounters("#A1B2C3D4", ...)` directly and asserts that three such names become one `table variables` row ([JournalBenchmarkBuilderTests.cs](../../tests/SqlHarness.Tests/Journal/JournalBenchmarkBuilderTests.cs)). That fixture supplies no SQL or plans, so the same input is indistinguishable if the counter came from an explicitly named temp table. A fixture with only a conventional explicit temp name such as `#cache` would not cover the collision. Plan 104 Task 1 must include the exact collision case; current source cannot satisfy both classifications from the present fields.

## Resolution

The implementation claims that table variables are positively identified and that unrelated explicit temp tables remain separate cannot both be verified from the current capture model. Preserve the plan’s STOP condition and request a reviewed resolution before implementation.

A resolution can proceed only after the implementation boundary is made explicit: aggregate a counter only when its source has positive typed provenance; when the available evidence cannot distinguish a `#XXXXXXXX` entry, retain its original per-run identity and surface the ambiguity rather than labeling it as a table variable. The current source does not provide that typed provenance, and this preflight does not propose a new correlation algorithm from plan totals or SQL text.

The task/spec and fixtures need revision to require typed classification at collection time, an explicit `#ABCDEF12` collision case alongside generated names, and a conservative ambiguous fallback. The existing journal heuristic is itself ambiguous and does not authorize propagating the same classification into benchmark or saved-artifact distributions.

## Evidence boundary

This is a static preflight, not a behavior test or SQL Server runtime validation. No tests were run. No source, plan index, or implementation files were changed. At inspection start, the worktree contained the plan file as an untracked change; preserve it.
