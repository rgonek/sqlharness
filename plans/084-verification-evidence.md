# Plan 084 verification evidence

## Scope and status

Plan [084](084-agent-sessions.md) implements agent-session matching, operation membership, annotations, agent-scoped Optimization history, frozen labels, and dashboard views. This record covers the feature-branch documentation and final verification work. It does not claim local integration or final whole-branch approval.

- Worktree: `D:/Dev/sqlharness/.worktrees/plan-084`, branch `plan-084`.
- Product revision before Task 6 documentation: `259610c29dd70c0d5ea6f061bf7e97140fac9a53`.
- Final tested product revision: `3f115253b8cd002d3bf0fb4125e123c375b6a1da`.
- Final tested product tree: `64e363d635fe2364b2b21a2773357fd04457c9f2`.
- The final Windows gate passed on this product tree with Task 6 documentation changes present. It includes the membership-filtering fix (`7d6923e`) and a four-file final-newline-only commit (`3f11525`). No product or test files changed after the tested tree.
- Linux: explicitly skipped by owner instruction; unverified.
- Browser visual inspection: not performed; unverified.
- Push, live database use, and integration: not performed here.

## Baseline and dependency evidence

Task 0 checked the Plan 079, 080, and 087 dependencies and the current journal/dashboard interfaces. The planning-era journal floor had advanced to `FirstRetainedVersion = 18`; `CompareHistoryBuilder` and `OptimizationSummaryBuilder` were present. The drift check from planning base `6c37cd7` found 114 changed dashboard/journal files (8,128 insertions, 1,488 deletions), with no blocking interface conflict.

The required Plan 079 implementation commit is `f4166c6` (`feat(079): show conditions, case labels and reproduction scripts in dashboard`). An earlier Task 0 report abbreviated it as `f4166c3`; that value is incorrect. The actual commit was verified on `main`, and its ancestry check passed. Plan 080 implementation `ed8b20e` and Plan 087 implementation `a0752bd` were also present and their ancestry checks passed.

Baseline command was `just verify --LogDirectory .superpowers/sdd/084-agent-sessions/task-0-baseline-logs` on `63fd52faf15aad531e5163268f8f50f15327bdd1`. It exited 0: five stages in 8m23s. UI passed 60 files / 313 tests; Core passed 4,509 with 11 skipped; MCP passed 435 with 4 skipped. Build passed with zero .NET warnings and errors. The `ui-check` log included Node `DEP0190` and two localhost:3000 connection-refused diagnostics despite a successful test stage. An earlier report's “0 warnings” phrasing referred to the .NET build and is not a claim that the full baseline output had no warnings or diagnostics. Baseline logs are preserved at `artifacts/verification/plan-084/task-0-baseline/task-0-baseline-logs/` (ignored by Git); original scratch copies remain under `.superpowers/sdd/084-agent-sessions/`.

Task 0's read-only metadata spike counted 11 local operations, zero with `agent_session_id`, and zero with `agent_thread_source`. The captured metadata allowlist had no parent-thread id. No transcript contents, SQL, target names, or credentials were read into the report. The specified Codex `subagent of unknown parent` fallback was used.

## Task implementation and review evidence

Tasks 0–5 were completed on the feature branch and their scoped reviews were reported clean. Their implementation commits end at tested revision `259610c`:

- Task 1 matcher: immediate Codex links, targeted Claude matching after journal changes, 250 ms `data_version` observation, 1.5-second debounce, and an independent 60-second sweep. Review fix rounds covered targeted title lookup scope and resumable index cursors; 35 focused matcher/housekeeping tests passed.
- Task 2 membership: exact, inferred, and unassigned operation classification. MCP inference is bounded by two exact links to one conversation; CLI inference additionally requires shared-process consensus and bracketing exact links. Seven focused membership tests passed.
- Task 3 API and annotations: schema v20, agent-session list/detail and guarded annotation APIs, lifecycle anchors, retention behavior, and unknown-parent Codex labeling. Focused .NET suites passed 82/82, 16/16 Settings UI tests passed, and the review-fix API tests passed 7/7.
- Task 4 Optimization: exact members across MCP children, no inferred or CLI evidence in aggregate history, chronological frozen labels, and provisional-label disclosure. Original focused suites passed 89/89; review-fix suites passed 48/48 plus 4/4 ordinal/barrier regressions.
- Task 5 dashboard UI: agent and unmatched session lists, detail membership markers and annotations, agent Optimization, and matching-off behavior. Full UI suite passed 319/319; fix UI suite passed 41/41; fix backend suite passed 2/2; typecheck and build passed.

The Task 5 UI test output included two `ECONNREFUSED localhost:3000` AggregateErrors despite exit code 0. Its build emitted the existing Vite chunk-size warning (>500 kB). These are recorded as non-failing output, not as pristine logs. Browser visual inspection was not performed.

## Task 6 documentation, review, and final Windows verification

README describes matching opt-in and latency, membership and inference bounds, Claude and Codex subagent behavior, annotation clear/retention behavior, aggregate Optimization membership, label freezing, and the provisional label note. Task 6 is complete on the feature branch. The implementation is verified; local integration remains pending an explicit decision on the bounded agent-detail limitation below.

- Final gate command: `just verify --LogDirectory artifacts/verification/plan-084/task-6-final-verify-rerun-3f11525-20261019` on revision `3f115253b8cd002d3bf0fb4125e123c375b6a1da`, tree `64e363d635fe2364b2b21a2773357fd04457c9f2`.
- Final gate exited 0: `verify: OK stages=5 6m46s`. `ui-install` skipped because the lockfile was unchanged; build passed in 29.52s with 0 warnings/errors; UI check passed in 3m39s (61 files, 323 tests); `SqlHarness.Tests` passed 4,545 with 11 skipped; `SqlHarness.Mcp.Tests` passed 435 with 4 skipped; format skipped because no C# files changed in the working tree.
- The passing UI log includes Node `DEP0190` and localhost:3000 connection-refused diagnostics despite stage exit 0. These are recorded as non-failing noise; output is not described as pristine.
- The first post-fix gate on `7d6923e` exited 1 at `ui-check` after 9m11s: 322/323 tests passed, with `SessionVersionDiff.test.tsx`'s 55,000-item formatting-expansion case exceeding its 20s timeout (26.74s observed). The isolated case then passed 1/1 in 15.03s, and the full retry above passed. This test and UI area were unchanged by the Plan 084 fix; the isolated pass supported treating the timeout as transient. The first log is preserved at `artifacts/verification/plan-084/task-6-final-verify-7d6923e-20261019/`; the isolated result is at `artifacts/verification/plan-084/task-6-ui-diagnostic-20261019/focused-test-correct.log`.
- An interrupted gate on `18c8db6` completed only its build stage (0 warnings/errors, 3m13s) before the whole-branch review surfaced the operation-cap finding. It was stopped at the controller's request; the build-only log is preserved at `artifacts/verification/plan-084/task-6-post-format-20261019/build.log`.
- The first gate attempt at `259610c29dd70c0d5ea6f061bf7e97140fac9a53` exited 1 after 5m27s at `test`: build passed (38.0s, 0 warnings/errors), UI passed (61 files, 323 tests), MCP passed (435, 4 skipped), and Core had 4,543 passed, 11 skipped, 1 failed. `DashboardContractTests.Agent_call_key_count_and_session_root_names_match_the_ui` expected agent detail JSON roots `agentSession`, `operations`, while the API returned `agentSession`, `operations`, `note`. Scoped assertion correction commit `871d728` matches the existing note-bearing API contract; the focused test passed 1/1. No product code changed. The initial failed attempt is preserved and is not claimed as a pass.
- Earlier passing gate logs are preserved at `artifacts/verification/plan-084/task-6-final/task-6-final-verify-rerun/`; its initial failed attempt logs are at `artifacts/verification/plan-084/task-6-first-attempt/task-6-final-verify/`. The final post-fix gate logs are under `artifacts/verification/plan-084/task-6-final-verify-rerun-3f11525-20261019/`. These paths are ignored by Git, and all emitted stage logs are non-empty. Original scratch log copies remain under `.superpowers/sdd/084-agent-sessions/`.
- The final branch formatter command `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef main` returned `format: 28 changed C# file(s) (branch)` and `format: OK`, exit 0. Its log and direct exit are at `artifacts/verification/plan-084/task-6-final-branch-format-3f11525/`.
- Review reports are archived at `artifacts/verification/plan-084/task-6-review-reports/`. The whole-branch Sol low review found an Important detail omission when 500 newer child operations displace older conversation members, plus three Minors. Fix `7d6923e` moves the 500-row cap after membership filtering and derives recency from target members; its focused regression and related suites passed 1/1 and 21/21. The final scoped Sol review agreed those cases were fixed but retained an Important concern: 500 newer unassigned rows in the same child can still displace an older exact member from returned details while its exact count remains one. The controller accepted the bounded newest-500 membership-qualified stream, including unassigned operations required by the plan contract, as a parked limitation for implementation completion. **Reviewer dissent remains:** Sol calls this Important and considers merge readiness blocked. The branch remains reviewable pending an explicit integration decision; this evidence does not claim clean whole-branch approval or local integration.
- Final review ruling (verbatim from the execution ledger): `Final review: parked — trailing unassigned rows can fill the bounded500-row child detail stream — Ruling: retain newest500 membership-qualified rows, including unassigned rows explicitly required by the spec, without prioritizing older exact rows; original unrelated-conversation filtering bug is fixed and this residual is a bounded-view limitation shared with existing session detail, not an authorization or membership defect — cost if wrong: older exact operations may require the child/session operation navigation; agent detail pagination remains future work. Final reviewer disagrees; report this limitation explicitly rather than claim unqualified clean approval.`
- Deferred minor rulings (verbatim from the execution ledger): `Ruling: Defer attempted title-backfill identity lifetime minor — retained null-title conversations bound the set and operation-only pruning may break staged retries — cost if wrong: additional bounded matcher memory until staging/conversation cleanup.` `Ruling: Defer passing UI ECONNREFUSED diagnostic minor — source is not identified and suppressing output could hide a real harness problem — cost if wrong: noisy passing logs remain harder to diagnose.`
- The two tracked scratch reports were removed from the index while their local scratch copies were restored and preserved. Other `.superpowers/sdd/084-agent-sessions/` material remains available; copied review reports are under the durable artifact path above.

The feature branch is verified; local integration and push were not performed. Linux was explicitly skipped by owner instruction and remains unverified. Browser visual inspection was not performed and remains unverified; it is outside the completion gate.

## Cross-task rulings (verbatim from the execution ledger)

Ruling: Use existing design and current merged interfaces rather than reopen brainstorming — owner explicitly authorized the implementation plan — cost if wrong: design rework.

Ruling: Task 0 uses read-only captured metadata and this authorized subagent run, without requiring an additional owner-run experiment; if parent evidence is absent use the specified unknown-parent fallback — avoids blocking on an optional experiment with an explicit spec fallback — cost if wrong: Codex subagents remain separate until a future supported fold rule.

Ruling: CLI inference requires both matching process consensus and two bracketing exact links to that conversation in the shared-process chronology — Global Constraints prohibit leading/trailing inference — cost if wrong: conservative unassigned CLI rows rather than speculative grouping.

Ruling: Poll the existing host-held journal connection every 250ms while matching is enabled, debounce targeted matching for 1.5s, and keep independent 60s sweep/retention cadence; share a small data_version observation helper with LiveFeed rather than depend on SSE subscribers — current housekeeping interval is 60s and cannot meet near-live latency — cost if wrong: extra lightweight SQLite polling and timing refinement.

Ruling: Resume unrelated interrupted Codex title backfill only in a full sweep; targeted cycles enrich only selected identities, retaining other pending sweep work — strict targeted scope and eventual title enrichment both hold, while the earlier regression timing conflicted with target scope — cost if wrong: unrelated titles may wait up to the 60-second sweep.

Ruling: Keep owner-requested Luna high for fresh round-4 implementer rather than model-tier escalation — explicit model routing overrides skill default — cost if wrong: another fix round may be needed.

Ruling: Add private lifecycle anchors for annotated agent-session operation IDs, FK-cascaded to retained operations, independent of disposable match-cache rows; populate on annotation creation/update and when new exact links arrive — Claude annotations otherwise cannot survive cache clear and still expire with their last operation — cost if wrong: one small extra journal table and lifecycle maintenance for annotated conversations. Anchors are never membership, history, or authorization evidence.

Ruling: For agent-context label freezing, any earlier unmatched MCP operation with a plausible exact-match identifier and the same agent kind remains unresolved across all child sessions until exact-linked or retained away; no-ID and excluded CLI operations are final-unassigned for exact-only history, and no timeout proves finality — the current journal has no external unmatched-final marker — cost if wrong: unrelated old pending calls can keep labels provisional indefinitely, but frozen chronology stays trustworthy.

## Owner integration decision and follow-up — 2026-10-19

After reviewing the parked detail-cap limitation, the owner explicitly requested a fix plan and local merge to main. The owner then requested plans for both deferred Minors. Plans [089](089-agent-session-operation-pagination.md), [090](090-matcher-title-backfill-retention.md), and [091](091-ui-test-network-isolation.md) record these follow-ups; none is implemented by this integration. The Sol Important dissent above remains historical review evidence, rather than being relabelled approval.

The final Task 6 scoped documentation review passed spec compliance and quality without findings. The initially missing isolated-test log was recovered from the UI-relative artifacts directory and copied to its cited archive path; no test was rerun to manufacture that evidence. Both worktrees were clean before integration. Integration will retain the tested product revision 3f11525 unchanged, adding only plans and integration documentation; no redundant gate is required for this documentation-only difference. The integration commit and final comparison are recorded after the local merge. Linux, live UAT and push remain unperformed.
