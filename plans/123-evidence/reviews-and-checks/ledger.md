# SDD ledger — plan: plans/123-shared-version-table-across-conditions.md

Branch: plan-123; workspace: C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/342c/sqlharness
Start/base: f54979563d7aa7575b71c7e682f1417931f7902d (latest local main, plan refreshed before implementation).
Authority: plan is its own spec. Roles: Terra medium implementation; Terra high task review; Sol low whole-branch review. No merge, push, live DB, installation or publication authorized.
Tasks: 1 pending; 2 pending; 3 pending; 4 pending; 5 pending.

## Preflight
| Tasks | Producer / consumer or internal consistency | Result |
|---|---|---|
| 1 | Row counts agreement tests vs builder and schema | Consistent; count-list length agreement is distinct from value agreement. |
| 2 | Reader evidence vs version aggregate | Consistent; missing case counts make aggregate null. |
| 3 | Pairing and endpoint tests vs implementation | Consistent; no pairs retain snapshots, suppress metric aggregates. |
| 4 | Shared rows and group selection vs UI tests | Consistent; identity includes group plus version. |
| 5 | Documentation, full gate and fixture browser evidence | Consistent; gate required on final product tree. |
| 1,2 | Nullable metric columns and varying fingerprint -> reader/models | Consistent. |
| 1,3 | Varying fingerprint -> cross-group pairing | Consistent; never pair missing keys. |
| 1,4 | Repetition disagreement -> varies UI | Gap: null alone cannot distinguish missing capture from variable counts. |
| 2,3 | Shared OptimizationModels and TS evidence | Sequential additive changes. |
| 2,4 | RowsReturnedSum -> table rows | Consistent; null renders dash. |
| 3,4 | groupB, case rows and approximate basis -> UI | Consistent; same-group exact semantics retained, no parallel legacy URL implementation. |
| 1,5 | Journal change -> documentation/new evidence | Consistent; no backfill. |
| 2,5 | Evidence fields -> docs | Consistent. |
| 3,5 | Approximate mode -> docs | Consistent. |
| 4,5 | Shared table -> docs/browser verification | Consistent. |

Ruling: Preserve a bounded row-count variation marker in existing metric JSON if needed, without adding migration columns beyond the specified two; missing capture stays unknown — required to distinguish varies from missing evidence — if wrong, the additive marker and UI propagation require rework.
Ruling: Latest plan removes backward compatibility obligations; retain only current group/groupB selection and ordinary null rendering, without old-payload adapters — explicit main update — if wrong, old links may require future repair.

Task 1 BASE: f54979563d7aa7575b71c7e682f1417931f7902d
Ruling: Refine variation-marker storage to conditions_json, keyed by case and variant; never overload waits_json, and exclude marker from condition fingerprint inputs — no suitable metric JSON exists and wait semantics must remain intact — if wrong, observation placement requires rework.
Ruling: Allow ordinary worktree-local dependency restore/build (including npm ci); installation exclusion refers to installed SQLHarness or system deployment — focused tests and required gate need build dependencies — if wrong, local ignored dependency directories require cleanup.
Task 1: implementation cba76020; Terra high review running. Baseline RED reproduced after implementation began, accurately disclosed in report. GREEN 171 pass /1 Unix-only skip plus 28 builder pass. Original implementer /root/task1_implement.
Ruling: Accept Task1 artifact propagation in SqlServerDialect.cs and PostgresBenchmark.cs instead of listed SqlHarnessModule.cs; actual constructors are in these producers and Module has none — semantic requirement is fulfilled without a meaningless Module edit — if wrong, a missed producer path would need correction. Correct plan file list in Task5.
Task 1: minor (deferred): strengthen marker test with multiple cases/variants and size-cap exclusion; current tests support implementation, no demonstrated defect.
Task 1: minor (deferred): ConditionsCase synthesized equality includes new properties; narrow report equality claim to fingerprints and CanonicalResult, no demonstrated regression.
Task 1: complete (commits f5497956..cba76020, review clean after file-location ruling; 2 deferred minors). Task2 BASE: cba7602057f375dc3087abc9214d23af9d43cb26.
Task 2: complete (commits cba76020..c503b7a6, review clean). 108/108 focused pass.
Task 2: minor (deferred): multi-measurement test would independently protect latest row-count/variation selection (OptimizationSummaryBuilderTests.cs:249).
Task 3 BASE: c503b7a6.
Task3 implementation 219eb575; Terra high review running. 106 focused tests pass; TS typecheck pending after dependency restore Task4/5. Per-case variation fields RowsAVaries/RowsBVaries propagated.
Task 3: fix round1 started at 219eb575; open missing-metric plan case refs + contract coverage + same-group semantic guard.
Task 3: minor (deferred): add adversarial subject/database/scope fixture to route tests; current filtering appears correct (DashboardServerTests.cs:636).
Task4 carry: VersionPlan currently needs selected case; no-pair/all snapshots must remain openable (task3 reviewer cannot-verify confirmed integration requirement).
Task 3: fix round 1/5 (3 addressed, 0 open — preserve plan cases without metrics; payload contract; same-group guard; commits 219eb575..6cac83b3).
Task 3: complete (commits c503b7a6..6cac83b3, review clean). Focused106 initial +57 fix pass.
Task4 BASE: 6cac83b3. Carry no-pair VersionPlan access and explicit per-side RowsAVaries/RowsBVaries.
Task4 implemented41cf268b; Terra high review running. Focused35/35 +15/15 affected after test cleanup; typecheck0. Local incomplete dependency directory self-ignored; automatic cleanup rejection only blocked by policy, no retry. Gate/browser pending Task5.
Task4 fix round1 at41cf268b: nondefault A other group without B lost through recovery. Minor missing explicit dash assertion in VersionTable test carried to fix/final review.
Task 4: fix round 1/5 (2 addressed, 0 open — preserve nondefault/valid A without or invalid B; assert dash/varies; commits41cf268b..06043305).
Task 4: complete (commits6cac83b3..06043305, review clean). Focused35 plus21 fix tests pass; typecheck before local-only expression fix passed.
Task5 BASE06043305. Full Windows gate and fixture browser mandatory; no Linux/live/install/publish/merge/push.
Resume after explicit user continuation: no live child agents; tasks1-4 remain complete. Prior Task5 docs/fixture/archive uncommitted; gate/format logs only start headers, no completion evidence. Fresh task5_resume Terra medium dispatched to finish ONLY Task5 and inspect stage evidence before any repeated gate.
Ruling: Preserve incomplete dependency directory by moving it from UI root to worktree root, with both absolute paths validated, instead of deleting it or adding product exclusions — Vitest discovered dependency tests despite Git ignore; earlier deletion was policy-blocked — if wrong, move is reversible and verification must rerun. No deletion retry authorized.

Task 5: implementation and verification complete at product d3d3c068; Windows gate PASS UI 446, Core 4792/11 skips, MCP 535/4 skips; branch format 29 files PASS; browser synthetic PASS. Task 5 review and whole-branch Sol review pending. Initial UI gate failed due to incomplete ignored dependency directory, preserved and moved outside UI root. See task-5-report.
