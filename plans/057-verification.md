# Plan 057 verification

Implementation and documentation landed in `d9eae367b267ceac44c56a2d4e65d02dce641cc1` (`docs(057): supported T-SQL control flow`).

## Security review

- Task 1 review verdict: **Approved**, with no critical, important, or minor findings. The review covered recursive IF/ELSE and BEGIN/END classification, propagation of nested denials, atomic-block denial, mutation accumulation across both branches, and whole-fragment inspection of predicates (including subqueries). See `D:/temp/sqlharness-plan057-evidence-20261013/task-1-review.md`.
- Task 2 review verdict: **Approved**, with no critical, important, or minor findings. The review confirmed recursive declaration collection follows nested branches and blocks while preserving source-offset ordering, so a declaration only proves later uses. See `D:/temp/sqlharness-plan057-evidence-20261013/task-2-review.md`.
- Task 3 adds an end-to-end fake-session compare and public contract text; it does not alter the classifier. The compare uses the same constant-false IF/ELSE SQL for both variants, no parameters, and verifies success plus `read-only` classification for both.

The blanket “no existing safety test changed its expectation” criterion is overridden for the two historical nested table-variable cases explicitly required by the Plan 057 specification and Task 2: declarations inside `BEGIN … END` and `IF … BEGIN … END` prove later uses in the same batch. The approved Task 2 review records these changes. Other denial expectations remain unchanged.

The public wording is identical in README, MCP docs, and the SQLHarness skill: allowed control flow is `IF`, `ELSE`, and `BEGIN … END` at any nesting depth; denied control flow remains `WHILE`, `BREAK`, `CONTINUE`, `GOTO`, labels, `RETURN`, `TRY … CATCH`, `WAITFOR`, `THROW`, and `RAISERROR`. Every branch is classified even if its predicate is constant false; predicates are never evaluated. This contract applies to SQL Server only; PostgreSQL is unchanged. The SQL Server capabilities summary also describes the rule. Its output fixture records 8,031 UTF-8 bytes under the unchanged 8,192-byte budget.

## Verification evidence

All checks ran against the Plan 057 worktree. Restore completed normally without `NU1900` audit-connectivity warnings.

| Check | Result |
|---|---|
| `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareTests"` | Passed 41, skipped 0 |
| Focused safety, compare, capabilities, and agent-workflow tests | Passed 561, skipped 0 |
| `dotnet build SqlHarness.sln -warnaserror` | Succeeded, 0 warnings, 0 errors |
| Windows `pwsh ./scripts/verify.ps1` | `verify: OK`; Core 3,865 passed, MCP 400 passed / 4 skipped, UI 148 passed |
| Linux `pwsh ./scripts/verify-linux.ps1` | `verify-linux: OK`; Core 3,865 passed, MCP 401 passed / 3 skipped, UI 148 passed |

The Linux gate synced and tested the exact implementation commit above. Linux published-x64 MCP smoke passed; three environment/platform-dependent MCP cases were skipped. Task 3 logs and report are in `D:/temp/sqlharness-plan057-evidence-20261013/`.

## Final whole-branch review and closeout

User-selected routing was Luna high for implementation, Luna xhigh for task review, and Sol low for the whole-branch review. The Sol low review at `a9b80f4` found three Important setup integration gaps and no Critical findings. One Luna high fix wave at `e05d44d` addressed all three using recursive traversal of only the already accepted SQL Server `IF`/`BEGIN … END` shapes. Atomic blocks remain opaque and denied; source-offset before-use proof remains intact. Setup splitting detects nested local-temp creation after the parameter boundary, setup/measured variable lifetime collection follows nested statements, and nested local declarations are excluded from typed-parameter detection. PostgreSQL behavior and public wording are unchanged.

The final scoped Luna xhigh re-review marked all three findings **ADDRESSED**, found no new Critical or Important issues or out-of-scope observations, and left no parked findings. The final re-review and fix report are in `D:/temp/sqlharness-plan057-evidence-20261013/final-rereview.md` and `D:/temp/sqlharness-plan057-evidence-20261013/final-fix-report.md`.

Controller rulings and costs:

| Ruling | Cost if wrong |
|---|---|
| Task 3 also updates `Capabilities.cs` and its contract fixtures when needed, as required by `AGENTS.md`. | A small additional documentation diff. |
| Use the user-selected Sol low whole-branch reviewer even though the skill recommends the most capable model. | Lower review depth, mitigated by the Luna xhigh task security review. |
| Move the two historical nested BEGIN/IF declaration cases from denied to allowed as required by the spec and Task 2, despite the blanket test-expectation freeze. | Wider acceptance of those intended shapes; offset-before-use and persistent-write denials remain mandatory and reviewed. |

The fix regressions reproduced four failures before the change and passed afterward. Focused SQL safety/setup tests passed 449/449; the warning-as-error build succeeded with zero warnings and errors. The touched-file formatter completed. No suites were rerun for the final scoped re-review.

Both final gates ran sequentially against code commit `e05d44d84f30ff620e7bed8e37c499016c77b279`; `34601ca` contains metadata only:

| Check | Result |
|---|---|
| Windows `pwsh ./scripts/verify.ps1` | `verify: OK`; Core 3,870 passed / 0 skipped, MCP 400 passed / 4 skipped, UI 148 passed; build 0 warnings / 0 errors |
| Linux `pwsh ./scripts/verify-linux.ps1` | `verify-linux: OK`; Core 3,870 passed / 0 skipped, MCP 401 passed / 3 skipped, UI 148 passed; build 0 warnings / 0 errors; format `--verify-no-changes` passed |

The Windows and Linux logs, RED/GREEN evidence, all three task packets, and final reviews are archived under `D:/temp/sqlharness-plan057-evidence-20261013/`. Before Linux sync, the dedicated gate clone was confirmed clean with no untracked files; it was then synced to the exact fix commit. Final scoped review is clean and complete.

## Local integration

Plan 057 was merged locally into `main` as `9c6b663` (`Merge plan 057 T-SQL control flow classification`). The merge had no conflicts and retained the intervening Plan 032 changes and plan-queue refresh. The existing uncommitted PostgreSQL safety-test edit on the main checkout was preserved and excluded from verification by testing the committed merge in the clean Plan 057 worktree.

Both post-merge gates passed against `9c6b663`, sequentially. Windows `pwsh ./scripts/verify.ps1` returned `verify: OK`: Core 3,861 passed / 11 skipped; MCP 400 passed / 4 skipped; UI 148 passed; build 0 warnings / 0 errors. Linux returned `verify-linux: OK`: Core 3,868 passed / 4 skipped; MCP 401 passed / 3 skipped; UI 148 passed; build 0 warnings / 0 errors. The changed platform skip counts reflect the merged Plan 032 test conditions.

Linux used an isolated ext4 clone at `/home/rgone/src/sqlharness-gate-plan057-9c6b663`. The temporary script `D:/temp/sqlharness-plan057-evidence-20261013/verify-linux-post-merge.ps1` changes only the repository-root anchor and gate-clone path from `scripts/verify-linux.ps1`; all verification stages are unchanged. Its sync log confirms `HEAD is now at 9c6b663`. Logs are `post-merge-windows.log` and `post-merge-linux.log` in the same evidence directory. This closeout changes documentation only; no push or live database operation was performed.
