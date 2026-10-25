# Plan 120 final fix report

## Scope and change

Worktree: `C:/Users/rgone/MultiCliProfiles/codex/account-b/worktrees/502b/sqlharness`, branch `plan-120`.
Base reviewed revision: `f710c91c0d6b9702fe4276559d235ca9018ca04f`.
Local fix commit: `0d36ea77a609643ffe146c013c1ea89e34b5dc00` (`fix(plan-120): reject coerced manifest versions`).

The evaluator now accepts manifest `version` only when its runtime type is `[int]` or `[long]` and its value is `1`. Three cases and independent verifier expectations reject JSON string `"1"`, boolean `true`, and floating-point `1.0`; the existing integer `1` fixture remains valid. Current synthetic evidence is 29 cases and 17 in-memory mock dispatches. No SQLHarness product paths changed.

Archived the Task 3 report/review and initial whole-branch review from the SDD workspace into `plans/120-evidence/logs/`. Current decision, plan, both Plan 120 index entries, and archive README consistently record 29 cases/17 dispatches. Historical Task 1/2 reports retain their original counts. Initial whole-branch review is approved after its single Minor correction; final scoped rereview is pending.

## Red/green evidence

Before tightening the evaluator gate, after adding all three malformed scenarios and independent assertions:

```text
pwsh -NoProfile -File plans/120-evidence/evaluate.ps1
Evaluated 29 synthetic cases; dispatch records: 23; database/external-process calls: 0/0

pwsh -NoProfile -File plans/120-evidence/verify.ps1
exit 1
23 assertion(s) failed.
```

Each malformed version was accepted by the old comparison, dispatched both entries, and failed the independent expectations for preflight rejection and zero dispatch. The verifier also flagged the expected inventory count/order until the three cases were added to that inventory.

After the integer runtime-type guard:

```text
pwsh -NoProfile -File plans/120-evidence/evaluate.ps1
exit 0
Evaluated 29 synthetic cases; dispatch records: 17; database/external-process calls: 0/0

pwsh -NoProfile -File plans/120-evidence/verify.ps1
exit 0
PASS: 29 named cases; dispatch order, outcomes, stop behavior, input semantics, hash/path preflight, and no-connection boundary verified.
```

## Focused checks

- Relative Markdown link scan over the changed Plan 120 documents and archived reports: `PASS: relative Markdown links resolve in all changed Plan 120 documents.`
- `git diff --check`: exit 0.
- `git diff --cached --check`: exit 0 after stripping trailing whitespace and extra final blank lines from the archived Markdown.
- Staged path review: all 12 paths are confined to `plans/120-evidence/`, `plans/120-repeatable-experiment-suites-study.md`, and `plans/README.md`; the three ignored review/report files were force-added by exact path only.
- Final `git status --short --branch`: clean at `plan-120` after commit.

No full product gate was run because this is a research evaluator correction and product code/tests are unchanged. No SQLHarness invocation, real target, database, merge, or push occurred. The archived OS-denied symlink limitation remains unchanged.
