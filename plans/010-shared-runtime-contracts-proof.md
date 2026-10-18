# 010-shared-runtime-contracts-proof — final verification gates (closure)

Code tip: `f64a3da` (`docs(010/T4): record preserved interface boundary decisions`),
branch `fix/plan-010-shared-runtime-contracts`, worktree
`.worktrees/plan-010-shared-runtime-contracts` (worktree base: `20a2aaf` = main).
All gates (plan filter 125+50/0, build -warnaserror 0/0, full suite
without Integration 2074/0/0 + 193/0/4 pre-existing skips, diff --check clean)
were observed on code tip `f64a3da`. Commits after it (`98e8eb6` closure
proof + index row; this fix) are docs-only (`git show --stat`: only
`plans/*.md`) and do not require re-gating beyond `git diff --check` and a fresh
run of the plan filter on the final tree (results below). The header deliberately
does not state a moving HEAD, so it does not go stale with subsequent
docs-only commits.
Gate runs: 2026-09-30, in the worktree. No push and no merge (the plan forbids them without an instruction).

## Goal

A single watch polling engine emitting events to two sinks
(report with bounded history / NDJSON stream without retention) with
identical terminal decisions; only the identical pure bounds/duration rules
in shared Core `OperationLimits`; interface boundaries
(CLI/MCP, query/measure/compare, param-set/matrix, writers, clocks)
documented as preserved, not merged.

## Commits (code: 20a2aaf..f64a3da, 5; then docs-only only)

| Commit | Task | Subject |
|---|---|---|
| 2ef86ce | T1 | test(010/T1): add watch report/NDJSON terminal-decision parity tests |
| edb8606 | T2 | refactor(010/T2): single watch polling engine with report/NDJSON sinks |
| 8cf5b36 | T3 red | test(010/T3): pin shared operation bounds and adapter parity cases |
| 7f3687e | T3 | refactor(010/T3): share pure operation bounds and durations via Core OperationLimits |
| f64a3da | T4 | docs(010/T4): record preserved interface boundary decisions |

Docs-only after the code tip: `98e8eb6` (this evidence + the 010 index
row in `plans/README.md`); this fix (2 doc nits from the final review, only this file).
Scope is counted from the base `20a2aaf`, not from a moving HEAD.

Each task reviewed clean (spec ✅ + Approved; zero Critical/Important).
Final whole-branch review: below in "Final review".
Rulings: the name `ExecuteWatchNdjsonAsync` exists on the module layer
(Contracts.cs:21) — the plan was correct, no divergence; rules with different
units/defaults (s/m/h with a 24h cap vs m/h/d→minutes vs counters)
are deliberately unmerged (the plan's hard stop), including a different case policy
(CLI watch folds case, CLI qstop rejects capitals, MCP folds case).
Incident: the first T3 dispatch was lost to infrastructure
(model service unreachable, no work started); re-dispatch of a fresh
implementer, tree untouched.

## Gates (2026-09-30, code tip f64a3da; the plan filter rerun on the final tree)

- Plan filter
  `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests'`:
  SqlHarness.Tests 125/125, SqlHarness.Mcp.Tests 50/50 → exit 0.
- `dotnet build SqlHarness.sln --no-restore -warnaserror`: 0 warnings, 0 errors.
- Full suite without Integration: Core/CLI 2074 passed / 0 failed / 0 skipped;
  MCP 193 passed / 0 failed / 4 skipped (2 live opt-in + 2 foreign-RID smoke,
  pre-existing).
- `git diff --check`: 0. `git status --short`: clean. The branch (20a2aaf..HEAD)
  touches 12 files, all within the plan scope (§Scope: T1–T3 code + T4
  `plans/010-*.md` + status in the index): 6 code files, 3 test files,
  `plans/010-interface-decisions.md` (T4), this evidence and the 010 index row
  in `plans/README.md`.
- A fresh run of the plan filter on the final tree (docs-only delta):
  SqlHarness.Tests 125/125, SqlHarness.Mcp.Tests 50/50 → exit 0.
- Red → green: T1/T3-red are characterizing pins (green before and after —
  reported honestly as a grid, not witness-fails); the T2 loops were already
  aligned (10/10 parity from the first run); T4 a documentation step (green filter
  = no regression).

## Result documents

- `plans/010-interface-decisions.md` — the boundary-preservation decisions (T4).
- This evidence: `plans/010-shared-runtime-contracts-proof.md`.

## Gaps and leftovers (explicit)

- No live DB (the plan forbids it; tests on fakes; live tests skip).
- No platform evidence beyond Windows/x64 (as above).
- Deferred minors (non-blocking, to triage before merge): a redundant blank
  line in McpMappingTests.cs; DONE_WITH_CONCERNS/Gaps label mismatch
  in task-4-report; ORC line ranges 13–30/52–61 vs actual 19/52–58.

## Final review

Whole-branch review: 0 Critical / 0 Important, 2 minor doc nits (a stale HEAD
in the evidence header; an imprecise note about scope/file count) — both
addressed by this fix, without changes to code and tests.
