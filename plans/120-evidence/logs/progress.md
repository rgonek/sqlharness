# SDD ledger — plan: plans/120-repeatable-experiment-suites-study.md

Study start/base: eb28657d37dc6763938a6220b9463ba910cf50f1. Branch plan-120. Initial worktree clean. Main has unrelated README.md and plans/README.md edits, preserved.
Spec: embedded candidate/scenario requirements, captured in context.md; design.md is Task 2 deliverable.
Drift scan: empty for all mandated contract paths from 5f7342a7 to study start.
Baseline: docs-only study; clean diff and unchanged contract baseline verified; no production build required.

| Tasks | Producer / consumer or internal consistency | Finding |
|---|---|---|
| 1 | inventory plus two generated fixture trees, exact hashes | Internally consistent; fixtures must be reproducible from local synthetic data |
| 2 | design, evaluator, independent verifier, cases and results | Consistent; implement negative cases without opening any connection |
| 3 | decision and completion checks | Consistent; future production verification is a design requirement, not study gate |
| 1 / 2 | fixtures and input inventory feed schema and simulations | Task 2 may extend Task 1 fixtures while keeping inventory accurate |
| 1 / 3 | demand assumptions feed decision | Synthetic estimates cannot establish observed demand |
| 2 / 3 | exercised design feeds go/no-go | Decision must match supported shapes and exercised limitations |

Ruling: commit task work normally — explicit user instruction overrides plan no-automatic-commit line — commits remain local and reversible.
Ruling: use native isolated worktree on plan-120 based on actual current main — native tool initially used older attachment base, corrected before edits — no unrelated work incorporated.
Ruling: retain scope as research; run scoped study checks, not product just verify — no production changes permitted — executable study scripts require their own meaningful assertions.
Ruling: no merge or push — user authorized commits, plan excludes integration — branch ready for later integration.

Task 1: running; base eb28657d.
Task 2: pending.
Task 3: pending.
Whole branch review: pending (Sol low).
Task 1: implementation 86ffc20c; scoped checks passed per report; review running. DONE_WITH_CONCERNS assessed: unconfirmed demand and CLI/MCP semantic disparity are explicit study limitations, not implementation correctness doubts. Root/hash design is Task 2.
Task 1: fix round 1/5 started; duplicate-sensitive fixture must differ only in multiplicity and match SQL shape; review head 86ffc20c.
Task 1: cannot-verify resolved as scope: runtime dispatch/root enforcement are Task 2 deliverables, real operation ownership is expressly future-only; no Task 1 gap. Fix committed f5a21880; checks named with outputs in report.
Task 1: fix round 1/5 (1 addressed, 0 open; commits 86ffc20c..f5a21880).
Task 1: complete (commits eb28657d..f5a21880, review clean).
Task 2: running; base f5a21880.
Task 2: implementation 2bdaaa5e; report 24 cases/16 mock dispatches; no DB/process calls; symlink OS proof unavailable, simulated rejection labeled; review pending.
Task 2: fix round 1/5 started, head 2bdaaa5e; P2 raw source-byte cap, boolean type, stop-outcome metadata, independent date-input assertion. P3 schema example brace folded into docs edit. Cannot-verify: symlink unavailable explicitly allowed; reader parity deferred; command evidence root will verify at final stage.
Task 2: fix round 1/5 (5 addressed, 0 open; commits 2bdaaa5e..4a57d70e).
Task 2: complete (commits f5a21880..4a57d70e, review clean).
Task 3: running; base 4a57d70e.
Task 3: implementation f710c91c; C chosen, B deferred, A assessed; scoped scripts/links/whitespace/scope pass per report; task review pending.
Controller verification on f710c91c: pwsh verify.ps1 exit 0, 26 cases; 118 relative links pass; git diff --check eb28657d..HEAD pass; product src/tests/scripts/skills/.github unchanged; clean worktree.
Task 3: complete (commits 4a57d70e..f710c91c, review clean).
Whole branch review f710c91c: ready Yes, no Critical/Important, one Minor version integer coercion. Single final fix wave will address it; fix base f710c91c.
Final fix wave: 0d36ea77, version integer type enforced; 29 cases/17 dispatches; root verifier fresh PASS, whitespace/scope pass. Scoped Sol re-review pending.
Whole branch final scoped review on 0d36ea77: finding ADDRESSED, no new breakage, ready Yes. All tasks and reviews complete, no parked findings. Final evidence-only commit will archive status and reports; product/evaluator tree unchanged.
