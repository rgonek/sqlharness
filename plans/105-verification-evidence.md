# Plan 105 verification evidence

Status: DONE locally on `plan-105`; implementation, independent reviews, Windows gate and branch formatting complete. Not merged, pushed, installed or exercised against a live database.

## Scope and revision

Worktree: `D:/Dev/sqlharness/.worktrees/plan-105`. Base: `3365166212f4e6a5767e3fe2bec3f838faec5fe0`. Tested product/test revision: `f6a7f492`; subsequent completion changes are documentation/evidence only.

Saved artifacts now expose source-scoped Showplan grants/compile fields, statement logical/LOB IO and ActualScans, independent STATISTICS IO ScanCount records, exact QueryPlan identities, legacy unavailable/partial semantics and bounded retrieval. Corrupt advertised saved diagnostics produce an artifact failure rather than successful absent evidence. A projection fit failure tries smaller detail candidates; truly unfit exact identity returns a render-budget error.

## Final Windows gate

`just verify -LogDirectory D:/temp/sqlharness-plan105-final-verify-f6a7f492-retry` exited **0**, five stages, 8m54s.

| Check | Result | Raw evidence |
|---|---|---|
| UI | 372 passed, 64 files | [UI log](105-evidence/verification/ui-check.log) |
| Build (`-warnaserror`) | exit 0 | [Build log](105-evidence/verification/build.log) |
| Core | 4691 passed, 11 skipped, 0 failed | [Core log](105-evidence/verification/test-SqlHarness.Tests.log) |
| MCP | 514 passed, 4 skipped, 0 failed | [MCP log](105-evidence/verification/test-SqlHarness.Mcp.Tests.log) |
| Full gate | exit 0 | [Direct exit](105-evidence/verification/gate.exit) |
| Explicit branch formatting | exit 0, 16 changed C# files | [Format log](105-evidence/verification/format-branch.log), [direct exit](105-evidence/verification/format-branch.exit) |

Branch formatting used `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3365166212f4e6a5767e3fe2bec3f838faec5fe0` on the final product/test tree. The gate's working-tree-only format stage skipped zero changed C# files; the separate branch check covers all committed C# changes. The unique gate directory preserves earlier incomplete/failed runs.

The final unchanged-tree retry passed after a transient five-second `ProcessRunnerTests.PidPublicationError_PreservesOriginalWhenMarkerWriteFails` timeout. The isolated test on the same binaries passed 1/1 in 835ms before the retry. The [failed Core log](105-evidence/verification/pre-retry-core-timeout.log) is retained; the green retry does not erase the initial failure. No production code, test timeout or limit was changed for it.

An earlier gate on `ff0be097` passed Core and UI but found a hand-written MCP fixture advertising nonexistent statements. The [fixture-only correction](105-evidence/gate-fixture-fix.md) makes its manifest match the files it creates and preserves the strict production validation. The [earlier MCP failure log](105-evidence/verification/pre-fixture-fix-mcp.log) is retained. Pre-fix `d78f06ea` was stopped after build/UI when review required product fixes; other superseded format/gate sequences are not claimed as passing full gates.

The UI log includes the non-failing Node DEP0190 deprecation warning also present in earlier runs; no UI source or launcher changes are claimed here.

## Independent review and focused evidence

Luna high implemented; Luna xhigh reviewed each task; Sol low reviewed the whole branch and scoped corrections. All Important findings and the test pager Minor are addressed. See the [review archive](105-evidence/reviews/README.md) and [execution decisions](105-evidence/decisions.md).

- [Persisted source shape](105-evidence/task-1-shape.md): units, extraction, write/read, zero versus absent, variants/repetitions and engine availability.
- [4096-byte diagnosis](105-evidence/task-2-debugging.md): exception source, measured overage, detail-ladder correction and true no-fit regression.
- [Workflow evidence](105-evidence/task-3-workflow.md): exact baseline/candidate memory/compile tradeoff and multi-statement IO. Final-shape metrics response bytes are 3105/7659 (minimum/default); eight statement responses total 23003/35467 bytes. Every call fits its budget.
- [Final correctness fixes](105-evidence/final-fixes.md): advertised file errors, per-document QueryPlan identity, ambiguous legacy partial evidence and bounded pager.
- [Interface repair](105-evidence/interface-repair.md): selector/continuation contracts retained within 32768-byte tools/list ceilings; enabled catalogs measured 32270 fixed / 32713 request-scope bytes.

## Boundaries

Linux, live database acceptance, installed-binary behavior and paid-agent/model-token savings remain unverified. Byte measurements are offline synthetic UTF-8 counts, not token savings. Missing fields are not measured zero; diagnostic distributions may be partial under capture/response limits. Plan 104 was recommended but not implemented here. No mutation, merge, push or installation occurred.
