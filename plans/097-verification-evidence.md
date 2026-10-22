# Plan 097 verification evidence

## Tested product tree

- Implementation base recorded before edits: `0b9c2e2c7304cd78e78523d87a19b3d953901546` (Plan 096 integration, also Plan 097 starting revision).
- Product revision tested by final gates: `6d658da7566d614ae22e4b61c3835715a67a2665`.
- Product tree: `997a45e416da250c0d6ab2136819f737eae093e7`.
- Product commits: `a3c869b test(097): prove split 32+1 batch workflow`; `6d658da fix(097): normalize regression test formatting`.
- Documentation/evidence commits follow the product tree and do not change source or tests; they are subject to scoped review.

## Deterministic 32+1 workflow

The served-MCP regression exercises the actual `SqlHarnessModule` over stream transport, using a recording fake SQL session factory and temporary artifact home. It submits 33 uniquely named and valued parameter sets in two operations of 32 and 1. Each report has each expected name once; each value is seen by SQL twice under the existing warmup plus `repeat: 1` contract. The operations have distinct labels and artifact IDs, and both artifacts are recovered through `sqlharness_artifact`.

Measured transport accounting across two measure calls and two artifact recovery calls:

- Calls: 4 total (2 measure, 2 artifact recovery).
- Request: 3,200 UTF-8 wire bytes.
- Response: 78,187 UTF-8 wire bytes.
- Unique cases/sessions: 33.
- SQL executions: 66 (one warmup and one measured execution per case).
- Live database connections: 0; the fixture has no live provider connection path.

These are measured fixture transport bytes and fake-session execution counts. They do not establish model-token savings, actual-agent behavior, database/provider behavior, or cross-call atomicity. See `evidence/097/raw/task-3-regression-green.log`.

## Verification commands and results

- Focused split regression: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --filter "FullyQualifiedName~Split_32_plus_1_measure_workflow_executes_each_named_case_once_and_keeps_operation_artifacts_separate" --logger "console;verbosity=detailed"` — passed 1/1.
- Focused Core matrix suite after updating two stale minimum-count assertions: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~CompareMatrixTests" --logger "console;verbosity=normal"` — passed 64/64.
- Focused dashboard shutdown request-capture rerun: passed 1/1 in 150 ms. This had failed once during an earlier full Core run and passed in isolation; it was not reproduced.
- Full MCP suite: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --no-restore --logger "console;verbosity=normal"` — 505 passed, 4 skipped, 509 total.
- Branch formatting fix mode was applied to the two changed C# test files. Direct check: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 0b9c2e2c7304cd78e78523d87a19b3d953901546` — `format: OK`, process exit 0.
- Final Windows gate: `just verify -LogDirectory D:/temp/sqlharness-plan097-verify` — exit 0; five stages completed in 8m46s. UI: 64 files/372 tests passed. Core: 4,652 passed, 11 skipped, 4,663 total. MCP: 505 passed, 4 skipped, 509 total. Build: zero warnings and errors. UI check emitted Node `DEP0190` because a child process uses `shell: true`; the stage passed, and this is recorded as a non-failing diagnostic rather than pristine-console output. `just verify` reports formatting skipped because its default change range found no C# files; the direct branch format check above covers the Plan 097 branch changes.

Gate-stage and command logs are archived in `evidence/097/raw/`, including the full Core/MCP stage output, UI/build stage logs, final gate summary and direct formatter output.

## Regression and review record

The earlier full Core run before the stale matrix assertions were corrected reported 4,655 passed, 29 skipped and 3 failed. Two failures were obsolete assertions for the former two-case minimum; the third was the dashboard request-capture case, which passed the isolated rerun. The focused matrix suite and final Windows gate pass after the assertion updates. Task 1's meaningful RED logs and report, plus Task 2's report, are preserved in `evidence/097/`.

Task 3 review and the Sol low whole-branch review report no product findings. Their archived reports are `evidence/097/task-reports/task-3-review.md` and `evidence/097/task-reports/whole-review.md`; both were conditional on gate completion and evidence documentation. The final gate is now green; controller final evidence check and scoped documentation re-review remain pending. The historical `Fixtures/OperationAnnotations/off/tools-list.json` remains unchanged; whole-branch review agreed it is archived pre-change fixture evidence, not the current served schema contract.

No integration, push, live database access, paid-agent trial or Linux gate occurred.
