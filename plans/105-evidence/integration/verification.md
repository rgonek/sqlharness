# Plan 105 + 106 combined-tree verification

Tested merge: `fcd2a3a5163ac8b9f2e2661f34fed8d5dafa93d0`, parents `1195297d` (Plan 105) and `ab46345a` (incoming local main with Plan 106). Both independent integration reviews approve the combined source: [Luna xhigh task review](task-review.md), [Sol low whole integration review](whole-review.md). The provenance documentation Minor is clarified in [resolution](resolution.md).

## Checks

`just verify -LogDirectory D:/temp/sqlharness-plan105-integration-verify-fcd2a3a5` exited **0**, five stages, 14m19s.

| Check | Result | Raw evidence |
|---|---|---|
| Build | exit 0 | [log](verification/build.log) |
| UI | 372 passed, 64 files | [log](verification/ui-check.log) |
| Core | 4700 passed, 11 skipped, 0 failed | [log](verification/test-SqlHarness.Tests.log) |
| MCP | 523 passed, 4 skipped, 0 failed | [log](verification/test-SqlHarness.Mcp.Tests.log) |
| Full Windows gate | exit 0 | [direct exit](verification/gate.exit) |
| Explicit branch format | exit 0, 24 C# files | [log](verification/branch-format.log), [direct exit](verification/branch-format.exit) |

Branch format used `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3365166212f4e6a5767e3fe2bec3f838faec5fe0`. The gate's working-tree-only format stage skipped zero changed C# files; the separate branch check covers the committed combined changes.

Focused integration checks also passed: Core/CLI 189/189 and MCP 132/132. Catalogs measured 28678 annotations-off, 32286 fixed-scope annotations-on, and 32761 request-scope annotations-on bytes within the unchanged 32768 ceiling. Integration preserves both Plan 105 artifact identity/paging/detail-ladder behavior and Plan 106 independently bounded redacted error diagnostics.

## Delivery boundary

Main was fast-forwarded from `ab46345a` to `42e7e284`. Code/tests/scripts were checked identical to the tested `fcd2a3a5` merge; subsequent closeout commits change only documentation/evidence. The final primary-checkout equivalence check covers those metadata commits without repeating the unchanged product gate. No push, installation, Linux gate or live database action occurred. Feature-only results recorded earlier in the Plan 105 report remain historical, separate from this combined-tree gate.

Local preservation backup: `D:/temp/sqlharness-plan105-main-final-20261024-142056`. It contains the original README/index, raw local patches, hashes, the colliding untracked Plan 105 draft, and the scoped-stash identity. Restoration checks are recorded there as `restoration-check.json` after the saved local queue is reapplied. Other untracked plans and root README changes are retained; Plan 105 status in the local observation queue is updated to DONE.
