# W4 implementation report

## Result

Outside-root MCP file inputs now receive a count-only hint: `The input path must be under a configured --input-root (N roots configured).` The hint is used for normalized paths outside every root and for links whose resolved target is outside every root. Relative, malformed, UNC, ADS, and other rejected paths retain their generic errors. No configured root or supplied path is included in the hint. The MCP input-root documentation describes this behavior.

## RED / GREEN

- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Outside_root_error_hints_at_configured_roots_without_disclosing_them|FullyQualifiedName~Relative_input_path_keeps_the_generic_invalid_path_error" --verbosity minimal` failed the outside-root assertion with expected root hint / actual `The input path is invalid.`; the relative-path assertion passed.
- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Directory_link_to_outside_root_reports_root_hint_without_disclosing_target" --verbosity minimal` failed with expected root hint / actual `The input path is invalid.` for a Windows directory junction to an external target.
- GREEN: the four focused cases (outside root, relative and malformed paths, directory junction escape, and existing symlink/reparse rejection) passed: 4 passed, 0 failed.

## Verification output

- `dotnet format SqlHarness.sln --no-restore --verify-no-changes`: exit 0.
- `pwsh ./scripts/verify.ps1` completed once with exit 0 before the junction follow-up was added: UI 25 files / 103 tests passed; build succeeded with 0 warnings and 0 errors; Core 3218 passed; MCP 253 passed / 4 skipped; `verify: OK`.
- A later full Windows run on the final code reached the MCP tests but exited 1: 253 passed / 1 failed / 4 skipped. The failure was `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`, a `TaskCanceledException` during the MCP initialize handshake. The isolated test rerun passed (1 passed / 0 failed). Core passed 3218/3218. A post-commit full-gate rerun is pending.
- An earlier full Windows run also had an intermittent stdio process failure (`Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes`, expected exit 0, got 1); its isolated rerun passed. A subsequent complete run passed before the final junction-specific behavior was added.
- `pwsh ./scripts/verify-linux.ps1` exited 1 in its `sync` stage before running Linux checks. WSL Git could not resolve the Windows linked-worktree pointer: `fatal: not a git repository: /mnt/d/Dev/sqlharness/.worktrees/plan-038/D:/Dev/sqlharness/.git/worktrees/plan-038`. A normal-git clone run is pending.

## Self-review and concerns

- The diagnostic reports only the number of configured roots. It does not expose root paths, the input path, or resolved link targets.
- Reparse targets that cannot be resolved safely retain the generic invalid-path message. The existing fail-closed link/reparse rejection remains in place.
- The directory-link regression test uses a Windows junction and skips its assertions if the platform cannot create links; the current Windows run created the junction and exercised the assertion.
- Full verification has shown intermittent unrelated MCP stdio lifecycle failures under the concurrent solution test run. The latest final-code failure passed in isolation; the final post-commit gate result will be added here.
