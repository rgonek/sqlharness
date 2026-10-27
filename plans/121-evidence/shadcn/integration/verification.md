# Local main integration

Feature: `dashboard-shadcn-fixes` at `1463f501`; last product commit `ecd3f0ae`. Main before integration: `43720796`. Merge commit: `3a0f0549`.

The operator explicitly requested local merge into main. No push or additional publication was requested for this integration. The currently installed dashboard already serves the identical product tree from the preceding verified publication.

## Preservation and review

- Main had unrelated unstaged changes in README.md and plans/README.md. Full byte copies, hashes and a binary patch were saved outside the repository at `D:/temp/sqlharness-shadcn-main-backup-20261027-145132` before touching integration.
- Only plans/README.md was temporarily restored to its committed base to permit the merge. Three-way merge retained the main-only Plan 122 documentation and the feature Plan 121 status. There were no merge conflicts.
- Local plan-index changes were reapplied before committing the merge and remained unstaged. README.md stayed byte-identical (SHA256 `275ADE0675540C9D383DF6B6E6BBF90F1EE2883D6EAF9EBC76314475069986D5`). Original added/removed lines in both local documentation diffs match exactly after integration.
- Merged src/test tree IDs equal the reviewed feature tree IDs. Main-only Plan 122 is documentation, not backend implementation.
- Final independent whole-branch source/test review: no findings. Covered Mira/Base UI migration, pending/focus/URL behavior, Monaco resizing, radio composition, evidence warnings and enum-scoped PairBasis JSON conversion.
- Branch-wide C# formatting passed for both changed C# files. [Format log](branch-format.log).

## Verification

Full Windows `just verify` passed directly on main at `3a0f0549`: **5 stages / 14m30s; UI 388 (68 files), Core 4722 (11 skipped), MCP 535 (4 skipped)**. TypeScript/lint passed, build zero warnings/errors; formatting correctly skipped for zero dirty C# files after the separate two-file branch check. [Final gate logs](gate/ui-check.log), [receipt](receipt.json). Main uses the same ignored output-only sequential xUnit settings as the feature gate; no tracked runner-policy or timeout changes and no test filtering beyond the existing gate policy.

First gate: UI 388 and MCP 535 passed; Core 4721 passed, one timing test failed. `Begin_returns_null_quickly_when_the_database_write_lock_is_held` took 5.3485702 seconds against its existing 5-second wall-clock bound. Source and test are unchanged from pre-merge main. [Failed attempt](attempt-1/test-SqlHarness.Tests.log).

The exact test passed three isolated no-build/no-restore reruns, taking 3s, 2s and 2s; [run 1](lock-test-1.log), [run 2](lock-test-2.log), [run 3](lock-test-3.log). No code, timeout or assertion changes. This is consistent with previously observed host-contention sensitivity; a deterministic source defect was not established. The complete unchanged-tree retry at `D:/temp/sqlharness-shadcn-main-integration-gate-retry` passed, including the previously failing test.

Unrelated local main edits and the feature worktree's local Plan 122 files remain preserved.
