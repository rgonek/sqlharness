# Plan 107 local main integration

Authorized local merge: `ca0f601a9b2a4cf0d005759dae9f9d9677eedcc8`, parents `de1b8fa6` and `71d1a67c`.

The Windows gate ran before committing the merge on indexed tree `90774b847ebcf4f7bb4eec95469236b1bfd5917d`. The merge commit has exactly that tree. Later delivery-record changes affect documentation and archived evidence only.

Command: `just verify -LogDirectory plans/107-evidence/windows-gate-main-integration`.
Result: exit 0, `verify: OK stages=5 15m19s`.

- UI install: skipped, lockfile unchanged.
- .NET build summary: 0 warnings and 0 errors. Vite chunk-size advisory remains in the raw build log.
- UI check: 64 files and 372 tests passed; Node DEP0190 remains in its raw log.
- Core: 4671 passed, 11 skipped, 4682 total.
- MCP: 511 passed, 4 skipped, 515 total.
- Format: passed on both changed C# files.

The merged MCP fixture retains Plan 103 hidden-work tests and `ServedCatalog.Owner` alongside Plan 107 offline list characterization. The only merge conflict was adjacent Plan 103/107 history rows; both were preserved. `just fix` corrected only whitespace and final newlines in the two Plan 107 test files; `git diff -w` confirmed no semantic change. [Sol low integration review](reviews/integration-review.md) approved the combination; its pending-gate condition is now satisfied.

All four executed stage logs in [the integration gate directory](windows-gate-main-integration/) are nonempty. Skipped UI installation has no execution log. Raw logs are preserved verbatim, including whitespace; scoped code/document checks exclude raw logs. Original archived review EOF whitespace is unchanged.

No production behavior was repaired by Plan 107. Source repair, exact historical loaded-build attribution, Linux and live acceptance retain their existing limitations in [the diagnosis](task-3-diagnosis.md). No push, installation, restart or live database execution occurred.

Pre-existing local README and other untracked plan edits are preserved. The pre-merge local plan index and original untracked Plan 107 are additionally retained in stash `bb99d6f8f22448a41ea5f55f8610442abe8a5e25`. The local index patch is restored after delivery-record commits, excluding its obsolete Plan 107 TODO row because Plan 107 is now in history.
