# Plan 118 Task 3 report: decision and verification map

## Status

Task 3 deliverables and scoped checks are complete at worktree `D:\Dev\sqlharness\.worktrees\plan-118`, HEAD `e1e6cd0884978ffe80a5e9a0027349550082ca51`. The plan and index now say RESEARCH COMPLETE. Task 3/final study review remains pending. This is not product implementation or product verification.

## Decision

`plans/118-evidence/decision.md` accepts the bounded explanation subset for a separately selected implementation, while explicitly deferring cross-group UI explanations until an interface provides both group identities and the exact operation pair. It records the demand assumption, expected benefits and maintenance/size costs, unanswered questions, no-go boundaries, four evidence categories, and future product test map. It does not authorize product changes or live inspection.

The decision preserves the important distinctions:

- A known measurement-condition change requires retained field evidence and stable exact identity.
- Repeat, timeout and other classified settings are informational and cannot explain a measurement-fingerprint mismatch by themselves.
- Unresolved identity produces a gap rather than an invented difference.
- Missing, null, unknown, omitted, legacy or unpairable evidence remains unavailable and cannot prove equality or recover hidden values.

## Commands and results

1. `pwsh -NoProfile -File plans/118-evidence/verify.ps1` — exit 0 at `e1e6cd0884978ffe80a5e9a0027349550082ca51`; `VERIFIED: 27 named scenarios; exact oracle values, basis preservation, absent-property states, fingerprint semantics, pairing non-disclosure, omissions, and UTF-8 limits passed.` The verifier invoked the evaluator. Its transient `results.json` rewrite was restored from HEAD; the committed evidence retains the tested executable revision and has no revision-only diff. Raw output: `plans/118-evidence/reviews/logs/task-3-verify.log`.
2. `git diff --check` — exit 0.
3. Markdown relative-link check for `plans/118-condition-difference-explanations-study.md`, `plans/README.md`, and `plans/118-evidence/decision.md` — passed; all targets exist.
4. `git diff --name-only 7c1b2fbf282d8ee3aa1e970759053156a21550a9 -- src tests scripts` — empty.
5. `git status --short` — only intended Plan 118 plan/index/evidence changes were present. Raw scoped-check record: `plans/118-evidence/reviews/logs/task-3-scoped-checks.log`.

The existing 27-case measurements remain 7,731 aggregate explanation UTF-8 bytes and 3,709 maximum per explanation. The study cap is 8,192 bytes per explanation; it does not measure the enclosing response.

## Archive and boundaries

Historical task review verdicts remain under `plans/118-evidence/reviews/`. The Task 1 and Task 2 implementer reports were archived under `reviews/implementer-reports/`; all five SDD diff review packages were archived under `reviews/review-packages/`. Review records are chronological evidence, not claims that the whole-study/final review has occurred.

No product source, tests or shared scripts were changed. Product-focused future commands in the decision were not run. No product gate, live database, publication, merge or push was performed. Remaining concerns are demand and production behavior: actual recurrence, UI/transport limits, serializer/browser behavior, and a safe exact-pair route are unmeasured or deferred.
