# Plan 106 verification evidence

Status: DONE on isolated branch `plan-106`; no merge, push or installation.

## Tested identity

- Branch base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`.
- Windows-tested revision: `a5ec831fae055594035d2ef218f59c4020f9fdb2`.
- Reviewed product revision: `43d0a189903cf6c73aa6bc1d0608693281794bdc`; subsequent change is a test-only help assertion and evidence. Final closeout changes only documentation/evidence.
- Worktree: `C:/Users/rgone/.codex/worktrees/39fb/sqlharness`.
- Unrelated main-checkout README/index edits and untracked plans were preserved. Historical drift scan of the four specified source paths was empty before implementation.

## Delivered behavior

Core owns the shared diagnostic floor and surrogate-safe clipping. CLI and MCP redact error Message/Hint before clipping with `max(requested, 2048)`, within the existing 4096-character and total-byte ceilings. Cells and normal Location.Path projection retain caller limits. Under tiny byte budgets, optional diagnostics including paths may shrink or disappear while original code/phase and exact distinct-field truncation remain. CLI reserves the actual UTF-8 writer newline. No public knob, response ceiling increase, authentication retry or persistent mutation was added.

CLI help, MCP schemas/capabilities and runtime documentation describe the chosen semantics. Repeated schema descriptions stay compact to fit the unchanged tools/list ceiling; catalog tests cover fixed/request modes and annotations off/on.

## Task evidence and reviews

| Task | Commits | Focused evidence | Review |
|---|---|---|---|
| 1 | `db343382`, `354b4667`, `3b42ebfc` | Real low-cell RED; minimum-budget identity and exact omission regressions; GREEN Core/CLI 92, MCP 50 | Luna xhigh approved after two scoped fix rounds |
| 2 | `2c0d9b36` | New edges green before/after pure extraction; Core/CLI 93, MCP 51 | Luna xhigh approved |
| 3 | `b3a404f9`, `d78eac9d` | Schema/help RED; Core/CLI 93, MCP 51, schema 64; catalog ceiling unchanged | Luna xhigh approved after tiny-budget policy wording fix |
| Final fix | `43d0a189` | Long ASCII/multibyte/escaped paths; Core/CLI 95, MCP plus schema 117 after formatting | Sol low scoped review approved Important and Minor fixes |
| Gate remediation | `a5ec831f` | Rendered help semantic clauses 1/1; exact existing ProcessRunner rerun 1/1; no product change | Sol low scoped review approved |

Details: [Task 1](106-evidence/task-1.md), [Task 2](106-evidence/task-2.md), [Task 3](106-evidence/task-3.md), [final fix](106-evidence/final-fix.md), [gate diagnosis](106-evidence/gate-debug.md). Full [review reports](106-evidence/reviews/whole-rereview.md) and [gate remediation review](106-evidence/reviews/gate-remediation-review.md) are archived alongside the [SDD ledger](106-evidence/sdd-progress.md). No findings remain open.

The first completed gate on `43d0a189` failed the new help test's punctuation-dependent assertion and an existing ProcessRunner PID-publication test. The latter passed isolated and in the final gate; its cause remains unproven. The help test now verifies all five semantic clauses independently. [Initial failed gate logs](106-evidence/windows-gate-initial-failure/summary-output.txt) remain recorded. An earlier gate on `d78eac9d` was canceled after UI installation when final review required a product fix; it is not completion evidence.

## Final checks

- Direct branch formatting: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 6d543e509734a374caf882bc8c05ffe5ccaa98b2` exited 0, 13 changed C# files, after required `just fix` corrections. See [gate diagnosis](106-evidence/gate-debug.md).
- `just verify -LogDirectory D:/temp/sqlharness-plan106-final-verify` exited **0** on `a5ec831f`; five stages, 11m18s. [Raw summary](106-evidence/windows-gate/summary-output.txt).
- UI install skipped: unchanged lockfile. Build passed with 0 warnings and 0 errors. UI lint/typecheck/tests passed, **372 tests / 64 files**. Core **4661 passed / 11 skipped**, MCP **516 passed / 4 skipped**. Gate format skipped because committed worktree had no changed C# files; the independent branch-format check above covers the branch.
- [Build](106-evidence/windows-gate/build-output.txt), [UI](106-evidence/windows-gate/ui-check-output.txt), [Core](106-evidence/windows-gate/test-SqlHarness.Tests-output.txt), [MCP](106-evidence/windows-gate/test-SqlHarness.Mcp.Tests-output.txt) logs are nonempty; archived text normalizes line endings and trailing whitespace only. The UI log retains Node DEP0190 deprecation output; the stage still exits 0.
- Final relative Markdown file links and `git diff --check` checked during documentation closeout.
- Linux, live DB, paid/live-agent trials, loaded-binary behavior and deployment remain **unverified**. Synthetic evidence establishes neither live acceptance nor model-token savings.

## Controller decisions

1. Task 1 makes the minimum local floor change needed for GREEN; Task 2 extracts it to Core. Both tasks require GREEN, and Task 2 owns shared policy. Cost if wrong: small refactoring rework.
2. Task 3 uses a focused contract-description RED where feasible without inventing runtime behavior. Cost if wrong: an extra narrow test.
3. Task 2 uses baseline GREEN, extraction, then GREEN because Task 1 established the behavior with real RED. Cost if wrong: weaker incremental regression evidence, mitigated by existing RED and before/after edge runs.
4. Byte-budget fallback may shorten/omit optional Location.Path while preserving its normal cell cap because review demonstrated path-only exhaustion of the minimum envelope. Cost if wrong: less path detail in tiny envelopes, explicitly reported as truncation.
