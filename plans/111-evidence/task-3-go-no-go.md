# Plan 111 Task 3: Go/no-go and handoff

Decision snapshot: source tree `028e864292ef26e40801113006eea28eefe3524f` (`plan-111`), before Task 3 documentation edits. This is an offline source and fixture study. No database, installed MCP process, model round-trip, deployment, or live acceptance was used.

## Decision

**Defer implementation for insufficient recurrence.** The evidence shows an internal capture-and-distill path is technically plausible for SQL Server Showplan: the existing offline `PlanDistiller` accepted the 22,608-byte static fixture, and Task 2 identified the shape of a bounded catalog read. However, the study has one originating observation and synthetic cases only. It did not measure repeated demand, real response-envelope fit, model-token savings, actual catalog availability/permissions, or equivalent source/identity semantics across engines. No executor-ready implementation plan is justified by that evidence.

Use the existing workflow when a plan must be inspected: have an operator supply the document as inline content or from a configured input-root file, then call the existing offline plan distiller. For metadata-only catalog questions, an operator-authored, narrowly selected read-only query may suffice, subject to the existing query contract. Neither path is an automatic catalog retrieval feature. Do not increase the output cell limit to carry XML.

Reopen implementation planning when real cases recur and record, for each case, engine/version, reason measurement cannot replace the plan, exact plan/query identity availability, plan document size, available parameter/runtime context, existing-tool steps and failure point, output budget outcome, and whether the resulting evidence changed an operator decision. Keep production feasibility separate from deployment/live authorization; this research grants neither.

## Evidence and expected/observed outcomes

| Inspected source or fixture | Expected from current contract | Observed | Result / unresolved |
|---|---|---|---|
| `tests/SqlHarness.Tests/Fixtures/Plans/082-recursive-spool.sqlplan`; `dotnet run --project src/SqlHarness.Cli/SqlHarness.Cli.csproj -- plan tests/SqlHarness.Tests/Fixtures/Plans/082-recursive-spool.sqlplan --json` (Task 2) | Offline supplied-plan distillation accepts a valid document larger than 4,096 characters without a database connection. | Exit 0 on the 22,608-byte fixture; output contained a statement and nested operator tree. Tool display truncated captured stdout, which is not an MCP response-budget measurement. | Accepts offline feasibility for this fixture; live retrieval and real MCP envelope fit remain unknown. |
| `tests/SqlHarness.Tests/PlanDistillerTests.cs` and `tests/SqlHarness.Tests/Cli/PlanCommandTests.cs` (Task 2 focused tests) | Malformed and over-16-MiB inputs are rejected under existing bounds. | `PlanDistillerTests`: 31/31; `PlanCommandTests`: 14/14. | Existing offline behavior verified; no catalog-reader error mapping is tested. |
| `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs` (Task 2 focused test) | Existing request/process cancellation is propagated to database calls and session disposal. | 11/11 passed. | Existing call cancellation behavior verified; synchronous parse cancellation remains unresolved. |
| `src/SqlHarness.Mcp/McpInputReader.cs`, `src/SqlHarness.Mcp/McpLimits.cs`, `src/SqlHarness.Core/PlanDistiller.cs` | Supplied input is admitted and parsed within documented bounds. | Source shows 1 MiB inline UTF-8 limit, 16 MiB rooted-file input, and distiller limits of 16 MiB characters, 100,000 elements, depth 128, with DTD/entity protections. | These are source contracts for supplied input, not proof of an eventual catalog capture's materialization bound. |
| `src/SqlHarness.Mcp/McpOperationMapper.cs`, `src/SqlHarness.Core/SqlHarnessModule.cs`, `src/SqlHarness.Core/ArtifactReader.cs`, `docs/mcp/artifacts.md` | Existing plan/artifact tools do not retrieve a catalog plan; saved artifacts expose distilled JSON. | Plan mapping passes caller content/rooted file to offline distillation; artifact reader accesses saved plan JSON. | No existing no-round-trip catalog retrieval path found in inspected sources. |
| `src/SqlHarness.Core/AgentOutputLimits.cs`, `src/SqlHarness.Mcp/McpResultAdapter.cs` | Output stays within existing response/cell budgets; XML is not exposed as a result cell. | The cell maximum remains 4,096 characters; internal capture then distillation is a candidate, not an implemented path. | Exact response size for retrieved plans and need for pagination remain unmeasured. |

The source snapshot is the Task 2 HEAD above. The named CLI/test results are carried from the archived [Task 2 execution report](reviews-and-checks/task-2-report.md); they were not rerun because Task 3 changes only notes and index metadata. No claim is made that a binary at that revision was installed or loaded.

## Review focus assessed

1. **Older artifacts and missing engine fields — assessed, future implementation constraint.** No artifact schema or reader changed. Existing `StoredPlanReader` compatibility is relevant if retrieval is later implemented; older artifacts and engine-specific missing fields must remain explicitly readable/unavailable. No runtime legacy-artifact compatibility trial was performed for a new field set.
2. **Identity collisions — assessed, unresolved for any future catalog.** Task 2 found that a query selector alone can select multiple plans in SQL Server Query Store. A future engine-specific design must use target-scoped exact selectors, validate relationships, bound zero/ambiguous matches, and retain identities in the response. No live catalog enumeration was done.
3. **Minimum byte budgets, escaping, and continuations — assessed, no behavior change.** Do not ship raw XML or raise cell ceilings. The fixture proves parser acceptance only; the CLI capture truncation is not an output-budget result. Real response fit, escaped/multibyte budget behavior, and whether node continuation is needed remain unknown.
4. **Errors and negative results — assessed, no future API contract selected.** Existing malformed/oversized supplied documents reject; a future retrieval must preserve not-found, ambiguous, unsupported engine, permission/unavailable catalog, malformed/oversized document, and cancellation as distinct outcomes, not an empty success. No catalog error mapping was executed.
5. **Offline result versus live acceptance or token savings — assessed and bounded.** This is static source plus an offline fixture/test study only. There is no live acceptance, deployment, real usage recurrence, or measured model-token savings. None is implied by the CLI fixture or focused tests.

## Rejected alternatives and limits

- **Guidance-only as the final resolution:** existing supplied-plan guidance remains a practical interim path, but changing interface docs alone would not answer whether catalog retrieval is warranted and would prematurely legitimize a workflow that has not been observed recurring. Retain it as the current fallback, not as the study's go decision.
- **Separate executor-ready implementation plan now:** rejected because supported catalog sources are not selected across engines, recurrence and output economics are unmeasured, and synchronous parse cancellation and catalog error semantics need design. Creating a detailed executor plan now would encode guesses as settled requirements.
- **Increase the 4,096-character cell limit or export automatically:** rejected because it conflicts with the plan contract and would carry raw XML through a lossy/model-visible result path or expand file authority. No export or limit change was attempted.

## Scope

Task 3 changes only plan/evidence/index documentation. No production code, tests, tool schema, capability, database state, installation, merge, or push changed. Linux verification is unrun; live database, loaded-binary, deployment, recurrence, response-budget fit, and token-savings acceptance remain unverified.
