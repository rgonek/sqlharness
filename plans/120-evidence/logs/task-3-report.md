# Task 3 report — Plan 120 research decision and closure

## Decision and deliverables

- Wrote `plans/120-evidence/decision.md` and chose **C: no new supported feature now**. Option B (a CLI manifest surface) is deferred until the explicit workflow-demand and design gates are met. Option A remains an operator-owned command-list workaround where current command shapes preserve input semantics.
- The decision distinguishes mock feasibility from real demand and records the synthetic 60-versus-26 placement estimate as neither measured time nor evidence of recurring use. It summarizes a coarse 2–4 engineer-week planning estimate for a narrowly supported CLI/Core implementation, plus ongoing compatibility/security maintenance; it is not measured effort.
- The supported study boundary is fixed typed scalar input and MCP named parameter-set files in a synthetic mock. The decision retains the limits: no full reader grammar or Core type-resolution parity, no CLI named-set or CLI/MCP matrix parity, no fixed-parameter-file/TVP/list/mixed-input support, no real-target execution, no executed symlink proof, and no true concurrent file-race proof.
- Updated the Plan 120 task checklists and status and the Plan 120 section in `plans/README.md`. Both say RESEARCH COMPLETE, C selected, B deferred, branch not integrated/pushed, and whole-branch review pending.
- Archived Task 1 and Task 2 reports/reviews under `plans/120-evidence/logs/`; the directory is ignored by the repository's generic logs rule, so only these exact allowed Plan 120 artifacts were force-added. No raw diff packages were copied. Trailing whitespace was removed from archived markdown, then staged whitespace checks passed.

## Checks and results

- `pwsh -NoProfile -File plans/120-evidence/evaluate.ps1` — exit 0; `Evaluated 26 synthetic cases; dispatch records: 17; database/external-process calls: 0/0`.
- `pwsh -NoProfile -File plans/120-evidence/verify.ps1` — exit 0; `PASS: 26 named cases; dispatch order, outcomes, stop behavior, input semantics, hash/path preflight, and no-connection boundary verified.` Repeated after restoring the generated JSON property-order-only diff; verifier still passed on committed `results.json`.
- Relative Markdown link scan over the changed decision, plan, and index documents — passed.
- `git diff --check` and `git diff --cached --check` — passed. Staged archive markdown was whitespace-normalized first.
- Staged path check — passed. Paths were confined to `plans/120-evidence/`, `plans/120-repeatable-experiment-suites-study.md`, and `plans/README.md`.
- `git diff 4a57d70e --name-only -- src tests scripts skills .github` — empty; product/source/test paths unchanged.
- Final `git status --short --branch` — clean at `plan-120`; `git diff --check` — clean.
- Local commit: `f710c91c0d6b9702fe4276559d235ca9018ca04f` (`docs(plan-120): record repeatability study decision`). No merge or push.

No product `just verify`, SQLHarness execution, database or real target access, runtime installation, CI dispatch, or real suite run occurred. The OS denied creation of a test symlink; the existing evidence labels the symlink rejection as simulated, not an executed filesystem proof. The whole-branch review remains pending, so this report does not claim review approval or integration.
