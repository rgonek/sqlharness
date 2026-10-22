# SDD ledger — plan: plans/097-singleton-parameter-batches.md
Implementation base: 898f8b20f40246b3d0ff5263c5969ed4e2130b12
Spec: Fix contract in plan 097.
Models: Luna high implementation; Luna xhigh task reviews; Sol low whole-branch review (explicit user routing).
Preflight drift: Core enforcing minima unchanged; later MCP catalog/CLI timeout and output changes must be preserved. Plan096 not integrated; scheduling dependency only, no concurrent work in its checkout.
| Tasks | Producer / consumer | Finding |
| 1 | Boundary and identity tests / intended RED | Consistent: tests intentionally fail before Task2. |
| 2 | Runtime minima and interface pins / Task1 tests | Consistent: lower minimum only, preserve upper bound. |
| 3 | 32+1 regression and evidence / final gate | Consistent with separate operation identities. |
| 1,2 | Resolver/matrix tests consume changed collection validation | Intended RED then GREEN; no contradiction. |
| 1,3 | Fake execution identity/accounting fixtures consumed by 32+1 workflow | Preserve common fake semantics; do not duplicate execution. |
| 2,3 | MCP schema/runtime contracts consumed by workflow and docs | All current interfaces must agree on 1..32. |
Task 1 base: 898f8b20f40246b3d0ff5263c5969ed4e2130b12
Baseline: focused Core ParameterSet/matrix tests exit 0, 280 passed; baseline.log retained. Existing Vite chunk-size advisory from fresh UI build.
Task1 implementation ecf0c52; focused intended RED 13 failed/286 passed. Compare assertions await Task2 as expected.
Task1 review: spec/quality needs fixes — Important report/artifact context and reproduction identity assertions missing. Fix round1 dispatched to original implementer; fix base ecf0c52.
Task 1: fix round 1/5 (1 addressed, 0 open; commits ecf0c52..c3e6868)
Task 1: complete (commits 898f8b2..c3e6868, review clean; intended RED, post-guard assertions require Task2 GREEN)
Task2 base: c3e6868
Ruling: Permit updating obsolete 2..32 error expectations in tests/SqlHarness.Mcp.Tests/McpMappingTests.cs despite the listed test scope — existing tests must reflect the required 1..32 contract while explicit empty remains rejected — cost if wrong: a small additional test-file diff to revert.
Ruling: Extend source scope to SqlHarnessModule.cs for preserving explicitly supplied empty sets and updating the stale minimum-count diagnostic — this enforcing normalization path otherwise violates Fix contract item1 — cost if wrong: a localized module diff to revert; no artifact or fingerprint format change.
Ruling: Permit localized CompareCellRunner.cs and CompareMatrixRunner.cs edits to carry the selected named case into existing run-artifact parameterSet fields — activated singleton regression exposed loss of required case identity; reuse current format and unnamed null behavior — cost if wrong: additional compare-path regression risk requiring focused compare coverage.
Ruling: Update the existing Core capabilities parameterSets limits and test them there; keep MCP capabilities shape unchanged — MCP currently advertises collection cardinality through served schemas/descriptions, and this change does not require a new duplicate limit field — cost if wrong: MCP clients need an additional capabilities field in a follow-up.

Resume 2026-10-22: Task1 remains complete; Task2 implementation a5d5ccd recovered from git/report, task review dispatched. User authorizes merging main (Plan096) into this worktree; merge has three ordinary conflicts assigned to Luna high. Original implementation base remains 898f8b20f40246b3d0ff5263c5969ed4e2130b12. Untracked noop preserved.
Task2 review: Important MCP descriptions incorrectly exclude fixed parameters from named/file sets and omit cross-set shape matching; fix round1 assigned to merge implementer alongside integration. Cannot-verify omitted CLI sets null requires focused inspection. Fix base a5d5ccd; merge review will use main-relative task diff plus merge report to avoid unrelated main history.
Task 2: fix round 1/5 (1 finding addressed plus CLI cannot-verify resolved, 0 open; commits a5d5ccd..0b9c2e2; authorized main merge included).
Task 2: complete (commits c3e6868..0b9c2e2, review clean)
Task3 base: 0b9c2e2c7304cd78e78523d87a19b3d953901546
Ruling: Permit updating the obsolete minimum-count expectations in tests/SqlHarness.Tests/CompareMatrixTests.cs — full affected-suite execution exposes old 2-case assertions incompatible with the binding singleton contract — cost if wrong: a localized additional test diff to revert.
Task3 review: spec compliant, quality approved provisionally, no findings; final Windows gate pending. Product head 6d658da, whole-branch review starts while gate executes; docs completion deferred until actual results.
Task 3: complete (commits 0b9c2e2..6d658da, review clean; controller verified final just verify exit0, Core4652/11skip MCP505/4skip UI372).`nFinal review declined items: gate resolved by direct archived exit0; completion docs pending scoped check; live/provider and token efficiency remain explicitly unverified; historical tools-list snapshot retained as historical evidence, active served schemas verified by tests.
Final verification: original-base formatting passed on aea93d6, 28 files. Gate failed one Auth ProcessRunner timeout; focused rerun passed1/1 506ms. Luna worker usage limit prevented continuation; controller performs verification/evidence bookkeeping only, no product fixes. Unchanged product full retry running in unique final-retry directory.
Final corrected-tree retry: just verify exit0 on aea93d6, 7m57s, Core4652/11skip MCP505/4skip UI372; failed attempt and focused rerun retained. Final scoped evidence approval pending.
Final whole-branch review: clean; Sol low final scoped re-review Ready to merge Yes on7099003, all evidence/gate conditions closed. Branch retained; no integration/push authorized.
