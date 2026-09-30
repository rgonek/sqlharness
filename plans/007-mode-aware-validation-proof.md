# 007-mode-aware-validation-proof — final verification gates (closure)

HEAD: `c4a7a5c` (`docs(007/T4): document validate usage modes and the MCP no-setup-input caveat`),
branch `fix/plan-007-mode-aware-validation`, worktree `.worktrees/plan-007-mode-aware-validation`.
Gate runs: 2026-09-30, in the worktree. All commands use absolute worktree paths.

## Goal

Shared mode-aware preflight for query/setup/benchmark: `MapValidateAsync` no longer
always delegates to `SqlValidation.Validate` with `SqlUsage.Query`. Usage
(query/setup/benchmark) now flows from CLI (`--usage`, `--setup`) and MCP into the
Core model, benchmark shape rules (single PG statement) are enforced per usage, and
verdicts report safe reason/location plus the checked-conditions scope. Offline:
validate never opens a session (zero connect/execute in tests).

## Commits (dc56144..HEAD, 10)

| Commit | Task | Subject |
|---|---|---|
| fcc5f3c | T1 | test(007/T1): red regression witness for benchmark-mode PG multi-statement |
| 614c0b9 | T1 | feat(007/T1): shared mode-aware ValidationUsage model in Core |
| 4cbae87 | T1 | test(007/T1): mode-aware validation proof for benchmark and setup context |
| aad57b1 | T2 | test(007/T2): per-usage validation behavior tests |
| f03e1db | T2 | test(007/T2): assert setup-reason propagation and offline Executed pins |
| 6749e77 | T3 | feat(007/T3): wire --usage/--setup CLI flags and MCP usage into Core model |
| cb43476 | T3 | test(007/T3): per-usage CLI runner and MCP mapping coverage |
| 0fa25d0 | T4 | feat(007/T4): report checked-conditions scope on validation verdicts |
| 3e87613 | T4 | test(007/T4): pin checked-conditions scope and CLI-MCP decision agreement |
| c4a7a5c | T4 | docs(007/T4): document validate usage modes and the MCP no-setup-input caveat |

Each task reviewed clean (zero Important at completion; T2 fix round 1/5 recorded in
`.superpowers/sdd/007-mode-aware-validation/progress.md`).

## 1. Focused filter (plan gate)

Command:

```powershell
dotnet test D:\Dev\sqlharness\.worktrees\plan-007-mode-aware-validation\SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal
```

| Position | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Tests` | Passed 44 / Failed 0 / Skipped 0 / Total 44 |
| `SqlHarness.Mcp.Tests` | Passed 38 / Failed 0 / Skipped 0 / Total 38 |
| Total | Passed 82 / Failed 0 / Skipped 0 |

## 2. Build with `-warnaserror` (plan gate)

Command:

```powershell
dotnet build D:\Dev\sqlharness\.worktrees\plan-007-mode-aware-validation\SqlHarness.sln --no-restore -warnaserror --verbosity minimal
```

| Position | Result |
|---|---|
| Exit | 0 |
| Warnings / errors | 0 Warning(s), 0 Error(s) |
| Time | Elapsed 00:00:02.72 |

## 3. Full suite without Integration (plan gate)

Command:

```powershell
dotnet test D:\Dev\sqlharness\.worktrees\plan-007-mode-aware-validation\SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal
```

| Position | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Tests` | Passed 1962 / Failed 0 / Skipped 0 / Total 1962, Duration 15 s |
| `SqlHarness.Mcp.Tests` | Passed 164 / Failed 0 / Skipped 4 / Total 168, Duration 37 s |
| Total | Passed 2126 / Failed 0 / Skipped 4 |

Skips (4): 2x non-native RIDs
(`Published_single_file_server_passes_the_stdio_smoke_on_linux_x64`,
`..._on_osx_arm64`) + 2x live stdio opt-in
(`McpStdioLiveTests.Live_postgres_stdio_drive`, `Live_sqlserver_stdio_drive`).

Known-flaky `McpStdioProcessTests` stdio smoke (plan005 property): PASSED in the
parallel run — no solo re-run needed, both outcomes recorded as one (pass in-run).

## 4. Behavior checklist (from the plan)

- CLI and MCP return agreeing decisions for the same operation: pinned by T4
  agreement test (commit `3e87613`) — PASS.
- PG benchmark refuses the inadmissible shape (multi-statement): red witness
  `fcc5f3c` failed on old code, passes after T1 fix — PASS.
- Validate never opens a session: offline pins (`Executed` never set, zero
  connect/execute in scope tests, T2/T3 coverage) — PASS.
- Tests cover adapter/runner behavior, not name lists or constants: per-usage
  behavior tests (T2), per-usage CLI runner + MCP mapping tests (T3),
  checked-conditions scope pins (T4) — PASS.

## 5. No live/platform proof (explicit)

- No live DB: live stdio tests skipped (see §3); out of scope by plan.
- Platform proof: runs on Windows x64 only; non-native RIDs skipped (see §3).

## 6. Deferred / parked minors (from `.superpowers/sdd/007-mode-aware-validation/progress.md`)

- T1/M1 (deferred): Required/MissingParameters counted from the main batch only;
  a reference from SetupSql alone does not enter required/missing (fail-closed on
  execution). Consider in a follow-up.
- T1/M2 (deferred): out-of-range ValidationUsage behaves as Query; T3 was to
  validate input strings (MCP already validates; CLI `--usage` added).
- T2/M4-rereview (documented, not a defect): reviewer's premise for the
  SQL-Server without-context leg was wrong for that engine (offline has no
  object-existence check); documented in test and fix report.
- T3/M1 (deferred, owner decision pending): `--setup` is also read under
  `--usage setup` and its value is discarded in Core; either skip the read or
  adjust wording.
- T3/M2 (deferred): no CLI test for `--usage setup` + `--setup` together
  (ignore path covered at Core only).
- T3/M3 (deferred): unknown-usage test does not assert message text.
- T3/M4 (deferred): MCP tests go through MapValidateAsync, not the full catalog
  entry (catalog surface zero).
- T4 minor (deferred): CheckedConditions is program-scope, not executed-scope
  (short-circuit lists controls that did not run); consider tightening the XML
  doc + README/mcp.md wording.
- Known flake (not blocking, plan005 property): McpStdioProcessTests stdio smoke
  may fail under parallel load; green solo. This run: green in-run (see §3).

## 7. T4 main-checkout incident and residue check

During T4, edits briefly landed in the main checkout via relative `[IO.File]`
paths; they were reverted. Residue check at closure (`git status --short` in the
main checkout scope per progress.md baseline): main holds only the pre-existing
`M plans/README.md` + `?? plans/004-inspect-gate-proof.md` from session start —
no 007 residue. All 007 edits, tests, and commits are in the worktree only.

## 8. Controls

- `git diff --check`: exit 0.
- `git status --short` (worktree, pre-docs): clean (no output) — all work committed.
- `plans/README.md`: 007 row set to DONE by the closure commit (index entry points
  to this file).
