# Plan 109 verification and review evidence

This durable record covers the research branch `plan-109` through the final documentation correction wave. The executable tree verified by the Windows gate is `f6044c839890f92b4f42d942ef8a1719cbf71b94`; later executable-tree change `14a0c7a` applies whitespace formatting only. Review and evidence corrections do not change executable files.

## Reviews and correction outcomes

- Task 1 review: clean after the fixture/evidence fixes in `d2a7528..2e5f266`.
- Task 2 review: clean after the candidate-study fixes in `92f7723..f6044c8`.
- Sol low whole-branch review: approved for the research branch at `a8d7914`. It found no Critical or Important issues and three Minor documentation issues. This correction wave fixes all three: the formatter-failure archive link now uses `.txt`; the gate note scopes zero warnings to the .NET build and discloses the non-failing Vite chunk and Node `DEP0190` warnings; and the 31.0%/94.4% values are labeled synthetic projection estimates, with complete bounded-response comparisons required by Plans 114/115.
- The final scoped Sol review of these corrections is pending the controller's update. The whole-branch approval does not stand in for that pending scoped review.

The reviewer declined to judge actual model-token or paid-agent savings; live database, setup execution counts and SQL Server TVP runtime behavior; runtime clipping, Unicode and continuation behavior of APIs not present on this branch; and deployment, merge, push or integrated-tree verification. No evidence here upgrades those items to verified.

## Verification record

- Windows `just verify` exited 0 with executable tree `f6044c8` while documentation was at `56f4cd4`. Build: 0 .NET warnings and 0 errors; the Vite build separately emitted the non-failing large-chunk warning. Core: 4,652 passed, 11 skipped, 0 failed. MCP: 507 passed, 4 skipped, 0 failed. UI: 64 files and 372 tests passed; typecheck and lint completed. `ui-check` separately emitted Node `DEP0190` as a non-failing deprecation warning.
- Focused Core filter `AgentOptimizationWorkflowTests|McpTokenBudgetTests`: exit 0 with zero discovered tests because those suites are MCP-only; classified N/A, not passing evidence.
- Focused MCP filter `AgentOptimizationWorkflowTests|McpTokenBudgetTests`: 55 passed, 0 failed, 0 skipped on `92f7723`. Focused candidate projection test: 2 passed on `f6044c8`. Task 3 and this documentation-only correction wave reran no tests or gate.
- Branch formatting initially exited 2 for whitespace in three C# files. `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 677b2f93b66c06d3dc0bda8475c7570ef29738d9 -Fix` and the same command without `-Fix` subsequently reported `format: OK` for three files. The successful raw formatter output is unavailable; these results are retained as contemporaneous recorded command outcomes, not independently archived console logs. The formatter-only commit is `14a0c7a`; whitespace-stripped comparison to `f6044c8` showed identical content. The full Windows gate was not rerun after formatting.
- Linux verification, live database/agent acceptance, tokenizer counts and model-token savings were not performed. Byte/4 remains a rough estimate only.

## Durable SDD rulings

The following rulings governed the work and are retained here so the decisions survive removal of local scratch notes:

1. Use the copied selected plan only; do not import unrelated queue edits; preserve the dirty main checkout; reconcile index integration manually later if needed.
2. The Core filter may discover no matching `AgentOptimizationWorkflowTests`; inspect `McpTokenBudgetTests` discovery and report true counts. A zero-test execution is not acceptance.
3. Extend the offline test host for named-set and query synthetic cases where needed; all four studies require real-SDK traversal. The risk is mistaking fixture-only behavior for production, so label evidence boundaries and use production renderers where available.
4. The prescribed Core filter exits 0 but discovers zero tests because both named classes are MCP-only. Treat it as N/A, not passing evidence; the full Windows gate covers Core and the exact MCP filter supplies discovery. Adding irrelevant tests merely to satisfy the literal filter would be misleading.

## Review summary and residual risk

The approved research branch changes offline tests, its workflow host, plans and evidence only. The real MCP SDK capture records exact artifact identities, source statement ordinals/hashes, parameter-set names, repetition selectors, response bytes and zero database connections. Candidate revisions stay distinct even where statement hashes match. The decision preserves evidence distinctions and ranks comparisons by equivalent evidence completeness. Accepted Plans 114/115 pin opt-in contracts and leave live acceptance open.

The 31.0% and 94.4% figures compare the study's deterministic test-only projections. Future schemas and MCP envelopes add fields, and bounded-output behavior was not exercised in these projections. Treating either percentage as complete API savings could overstate expected benefit, misdirect implementation priorities, and conflate output bytes with model tokens. Plans 114 and 115 require complete bounded-response comparisons before making an implementation savings claim.

## Links

- [Plan 109](109-artifact-review-workflow-study.md)
- [Plan index](README.md)
- [Gate status and archived logs](109-evidence/task-3-gate-status.md)
- [Decision evidence](109-evidence/task-3-decision.md)
- [Plan 114](114-compact-statement-discovery.md)
- [Plan 115](115-query-result-set-selection.md)
