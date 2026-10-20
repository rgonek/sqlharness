# Gate summary

Command: `just verify -LogDirectory D:\Dev\sqlharness\.worktrees\plan-099\.superpowers\sdd\099-shadcn-cli-restoration\scratch\final-gate`
Product revision: `dd8b9e93` (`ui: restore shadcn stylesheet export`)
Result: exit 0; all required stages passed. Formatting was skipped because zero C# files changed. The gate ran while documentation at `79d4ead8` was present; no product changes followed `dd8b9e93`.

| Stage | Result | Duration |
| --- | --- | ---: |
| UI install | Passed | 25.8 s |
| Build | Passed | 1m25s |
| UI check | Passed (62 files, 331 tests) | 4m08s |
| .NET test projects | Passed (462 + 4,562 passed; 4 + 11 skipped) | 2m26s |
| Format | Skipped | 0 changed C# files |

The UI check log includes non-failing `DEP0190` and localhost port 3000 connection-refused diagnostics. The build log includes Vite's large JavaScript chunk warning. Linux verification and live database tests were not run.
