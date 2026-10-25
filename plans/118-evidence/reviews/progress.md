# SDD ledger — plan: plans/118-condition-difference-explanations-study.md

Study start/base: 7c1b2fbf282d8ee3aa1e970759053156a21550a9. Isolated branch plan-118 in D:/Dev/sqlharness/.worktrees/plan-118. Initial status clean; baseline git diff --check passed. Product tests unnecessary for read-only study baseline.
User routing: Luna high implementer; Luna xhigh task review; Sol low whole branch review.
Spec: reachable candidate contract inside plan; design.md is a deliverable rather than a missing prerequisite.
Ruling: Commit normal task work despite plan's no-automatic-commit sentence — explicit current user instruction overrides it — no merge/push/publication authority implied.
Ruling: use scoped documentation/study checks, not product gate — no product behavior is authorized — synthetic evaluator must still be executed and verified.
Drift scan: only dashboard UI layout changed since planned revision; Core fingerprint builders and comparer unchanged. Task 1 must reconcile current line locations and Task 2 file map with current UI.

| Check | Producer / consumer or internal consistency | Result / ruling |
|---|---|---|
| Task 1 | Inventory includes retained fields, fingerprint payloads, identities and omissions; six named topics checked against source | Consistent |
| Task 2 | Design, fixtures, evaluator, verifier and results all writable under study evidence | Consistent; independent expected oracle required |
| Task 3 | Decision and final checks consume prior outputs; plan/index closeout | Consistent; mark research complete, never product implemented |
| Tasks 1 / 2 | inventory semantics -> design and synthetic records | Shared retained-condition interface; use exact current payload participation, no second grouping engine |
| Tasks 1 / 3 | inventory evidence -> acceptance decision and test map | Decision must retain evidence and identity limitations |
| Tasks 2 / 3 | fixtures/results/design -> decision, future verification and closeout | Source untouched; immutable fixture verdicts, bounded output and measured synthetic evidence |

Task 1: pending; BASE 7c1b2fbf282d8ee3aa1e970759053156a21550a9.
Task 2: pending.
Task 3: pending.
Task 1: fix round 1/5 pending re-review (retained fields, payload excerpts, NULL display; commits 816b000f..3c927bc6).
Task 1: fix round 1/5 (3 addressed, 0 open; commits 816b000f..3c927bc6).
Task 1: complete (commits 7c1b2fbf..3c927bc6, review clean).
Task 2: in progress; BASE 3c927bc6.
Task 2: implemented commits 3c927bc6..0697b2c3; evaluator/verifier 17 scenarios passed; Luna xhigh task review pending.
Task 2 review: 3 Important (informational mismatch gap; missing-property/collection/NULL states; pair-key disclosure), 1 Minor (exact values/overflow retained set). Fix round 1 BASE 0697b2c3; address all in bounded fix.
Task 2: fix round 1/5 (3 addressed including Minor oracle, 1 open: present null collection vs empty; commits 0697b2c3..2d99dd75). Route-map cannot-verify resolved by controller exact source checks; cross-group route deferred explicitly. Fix round 2 BASE 2d99dd75.
Task 2: fix round 2/5 (1 addressed, 0 open; commits 2d99dd75..e1e6cd08).
Task 2: complete (commits 3c927bc6..e1e6cd08, review clean).
Task 3: in progress; BASE e1e6cd08.
Task 3: complete (commits e1e6cd08..b50c44f9, review clean).
Task 3 cannot-verify oracle outside diff: resolved via approved Task 2 reviews and controller final verify27 at b50c44f9, exit0. Controller git diff --check baseline..HEAD passed; src/tests/scripts diff empty.
Whole-branch review pending, user-selected Sol low; no deferred/parked findings.
