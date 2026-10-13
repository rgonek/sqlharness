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

## Review fix round 1

The reader now loads every in-window operation row for workflow eligibility, including operations without an emitted-byte receipt. Output byte/token totals and call counts still aggregate only measured emitted-byte rows. Any failed row in the referenced workflow now suppresses its comparison, even when that row has no emitted footprint.

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Workflow|FullyQualifiedName~Failed"` exposed the regression: `Failed_linked_operation_without_emitted_bytes_disables_workflow_comparison` failed because a non-null comparison was returned. This broad filter also ran `AgentWorkflowTests.Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access`, which independently failed its byte budget (8724 actual, 8192 allowed); it is outside this reader change.
- GREEN: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~SqlHarness.Tests.Dashboard.JournalReaderTests.Workflow|FullyQualifiedName~SqlHarness.Tests.Dashboard.JournalReaderTests.Failed"` passed 5, skipped 0, failed 0 (880 ms).
