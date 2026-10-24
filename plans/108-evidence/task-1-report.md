# Plan 108 Task 1 report

## Result

Implemented the shared-reader empty/default variant behavior for saved plans. An omitted selector now reaches `ReadPlan` only after the owner and section checks. Empty measure and measure-set variants resolve to the public `query` identity; compare still rejects an omitted or empty variant. Nonempty invalid variants are preserved for validation.

## Changes

- `src/SqlHarness.Core/ArtifactReader.cs`: pass the optional selector to `ReadPlan`; normalize null to an empty selector there; default only empty measure/measure-set variants to `query` after owner/section validation. The existing safety exit classification and selector fields are retained.
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`: compare empty-default and explicit-query results for both measure kinds, including artifact identity, parameter set, statement, selected and available repetitions, and plan nodes. Cover invalid casing/nonempty values, omitted and empty compare variants, and selectors on non-plan sections.
- The plan copy is included with the task files. Plan status, index, and final plan evidence remain with Task 3.

## RED/GREEN evidence

RED was run against the original production implementation after adding the regression tests:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter ArtifactReaderTests --nologo
exit 1 — Failed: 2, Passed: 72, Skipped: 0, Total: 74, Duration: 27s
```

The two failures were the measure and measure-set cases for an empty mapped variant. Both failed at the existing invalid-variant rejection in `ArtifactReader.ReadPlan`; the other 72 tests passed.

After the minimal reader change, GREEN was run against the final Task 1 source and test files:

```text
dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter ArtifactReaderTests --nologo
exit 0 — Failed: 0, Passed: 74, Skipped: 0, Total: 74, Duration: 23s
```

The command built the Core, MCP, Dashboard, CLI, and test projects before executing the reader tests. The controller's initial unrelated environment run also reported npm audit vulnerabilities and a Vite chunk-size warning; neither was produced by this C# test command or changed here.

Additional scoped check: `git diff --check` exited 0.

## Self-review

- Owner validation and section availability checks still run before `ReadPlan`; non-plan selector validation remains before artifact loading.
- The default is kind-specific and limited to empty/null variants on measure and measure-set. Compare omission and empty string both fail with `SqlHarnessExitCode.Safety`.
- Nonempty values such as `Query`, `baseline`, and leading-space ` query` are not rewritten. Statement, repetition, parameter-set, cursor, page size, and output identity handling remain unchanged.
- No live database, merge, or push was used.

## Review state and concerns

The focused reader suite is green. Independent task review is still pending before proceeding to Task 2. Task 2 owns artifact-specific error wording and interface documentation. The broader `just verify` gate remains with the controller.
