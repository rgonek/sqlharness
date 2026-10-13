# Plan 059 Task 3 report: workflow output cost

## Result

Dashboard statistics now report workflow output bytes, estimated output tokens, response call counts, dry-run/database-execution counts, measurement coverage, and an optional named output-volume comparison. The old aggregate token and USD savings fields were removed from the Statistics API and UI. Per-operation raw/emitted token columns remain readable; operation detail now also exposes bytes and the token-estimation method.

## RED / GREEN evidence

- RED: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~Journal|FullyQualifiedName~Dashboard"` failed to compile the new reader tests because `DashboardStats.WorkflowCosts` did not exist yet (`CS1061`). This was the expected missing-interface failure.
- GREEN: `dotnet test SqlHarness.sln --filter "FullyQualifiedName~Journal|FullyQualifiedName~Dashboard"` passed 401, skipped 4, failed 0 (1m 42s). The skips are Unix-only permission tests on Windows.
- GREEN: `npm run typecheck` passed.
- GREEN: `npm test -- src/pages/StatsPage.test.tsx src/pages/OperationPage.test.tsx` passed 2 test files, 20 tests.
- Focused reader RED/GREEN cases cover a root plus two exact-linked artifact reads (400 + 300 + 300 estimated tokens), zero delta at matching baseline, positive output overhead, producer lookup outside the selected window, unavailable baseline, failed/dry-run/artifact-read baseline rejection, separation of legacy volume, and dry-run call accounting.

## Interfaces and behavior

- `DashboardStats.WorkflowCosts` contains one row per referenced root workflow and a separate unreferenced legacy volume bucket. Cost is grouped by `parent_operation_ref ?? operation_ref`; the reader joins the producer by exact `operation_ref`, including producers outside the time window. Each in-window emitted response is counted once.
- `WorkflowCost` carries output bytes, estimated output tokens, call count, `utf8-bytes-div-4`, coverage (`input`, `discovery`, and `chat` are `unmeasured`), accounting kind, dry-run count, database-execution count, and optional comparison.
- `WorkflowCostComparison` names `single-measured-execution-output`, reports baseline bytes/tokens and output overhead bytes, and exposes only the optional estimated output-token delta. A comparison is absent for failed workflows, dry-run roots, artifact roots, missing baselines, and legacy rows.
- `OperationSummary` exposes operation/producer references and dry-run state. `OperationDetail` exposes existing token columns plus raw/emitted bytes and the estimation method.
- `ActivityJournal.RecordEmission` already persisted raw/emitted byte columns from v3; this task adds assertions proving both byte counts persist alongside estimates. Schema/ref changes from earlier tasks were reused, with no migration added.

## Files changed

- `src/SqlHarness.Dashboard/JournalReader.cs`, `DashboardModels.cs`, `DashboardServer.cs`
- `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `lib/stats.ts`, `lib/stats.test.ts`, `pages/StatsPage.tsx`, `pages/StatsPage.test.tsx`, `pages/OperationPage.tsx`, `pages/OperationPage.test.tsx`, `pages/SettingsPage.tsx`, `test/fixtures.ts`
- `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `DashboardContractTests.cs`, `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs`, `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`
- `README.md`, `skills/sqlharness/SKILL.md`, `src/SqlHarness.Core/Capabilities.cs`

## Limits

Input tokens, discovery/tool listing, capabilities discovery, and final chat remain unmeasured. Token counts remain estimates from output bytes, not provider tokenizer measurements. No live database calls or full Windows/Linux verification gates were run.
