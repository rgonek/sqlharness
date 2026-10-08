# W4 implementation report

## Result

Outside-root MCP file inputs now receive a count-only hint: `The input path must be under a configured --input-root (N roots configured).` The hint is used for normalized paths outside every root and for links whose resolved target is outside every root. Relative, malformed, UNC, ADS, and other rejected paths retain their generic errors. No configured root or supplied path is included in the hint. The MCP input-root documentation describes this behavior.

## RED / GREEN

- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Outside_root_error_hints_at_configured_roots_without_disclosing_them|FullyQualifiedName~Relative_input_path_keeps_the_generic_invalid_path_error" --verbosity minimal` failed the outside-root assertion with expected root hint / actual `The input path is invalid.`; the relative-path assertion passed.
- RED: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Directory_link_to_outside_root_reports_root_hint_without_disclosing_target" --verbosity minimal` failed with expected root hint / actual `The input path is invalid.` for a Windows directory junction to an external target.
- GREEN: the four focused cases (outside root, relative and malformed paths, directory junction escape, and existing symlink/reparse rejection) passed: 4 passed, 0 failed.
- Final committed reader coverage: `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~McpInputReaderTests" --verbosity minimal` passed 20/20.

## Verification output

- `dotnet format SqlHarness.sln --no-restore --verify-no-changes`: exit 0.
- A full Windows gate completed with exit 0 before the junction follow-up was added: UI 25 files / 103 tests passed; build succeeded with 0 warnings and 0 errors; Core 3218 passed; MCP 253 passed / 4 skipped; `verify: OK`.
- A pre-commit full Windows run on the final behavior had an intermittent failure in `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly` (30-second `TaskCanceledException` during initialization); its isolated rerun passed. An earlier Windows run also had an intermittent stdio process failure (`Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes`, expected exit 0, got 1); its isolated rerun passed.
- Final post-commit Windows gate attempt 1: UI 103/103; build 0 warnings / 0 errors; MCP 254 passed / 4 skipped; Core 3218/3218; format then failed because the test file ended with a final newline. That newline was corrected in commit `36cb84f`.
- Before the review follow-up, post-commit Windows gate attempt 2 had UI 103/103; build 0 warnings / 0 errors; MCP 253 passed / 1 failed / 4 skipped; Core 3218/3218. The failure was `McpJournalTests.Tool_call_records_session_from_client_info(mode: "fixed")`, a 30-second `TaskCanceledException` during the MCP initialize handshake. Its isolated rerun passed both modes (2/2). A later review-requested full rerun on the same code passed, as recorded below.
- `pwsh ./scripts/verify-linux.ps1` initially exited 1 in `sync` because WSL could not resolve the Windows linked-worktree `.git` pointer. It then passed against final commit `36cb84f` from a normal-git source clone: UI 25 files / 103 tests passed; build 0 warnings / 0 errors; MCP 255 passed / 3 skipped; Core 3218 passed; final output `verify-linux: OK` and exit 0.

## Self-review and concerns

- The diagnostic reports only the number of configured roots. It does not expose root paths, the input path, or resolved link targets.
- Reparse targets that cannot be resolved safely retain the generic invalid-path message. The existing fail-closed link/reparse rejection remains in place.
- The directory-link regression test uses a Windows junction and skips its assertions if the platform cannot create links; the current Windows run created the junction and exercised the assertion.
- The earlier intermittent MCP initialize-handshake timeout was not reproduced in the review-requested final Windows gate; the Linux gate also passed on the same code commit.

## Review follow-up: final Windows gate

On current head `9e7374a`, the requested gate passed on attempt 1; no second attempt or startup investigation was needed.

```text
Command: pwsh ./scripts/verify.ps1
==> ui-install
added 193 packages, and audited 194 packages in 24s
found 0 vulnerabilities
==> ui-check
Test Files 25 passed (25)
Tests 103 passed (103)
==> restore
==> build
Build succeeded.
0 Warning(s)
0 Error(s)
==> test
Passed! - Failed: 0, Passed: 254, Skipped: 4, Total: 258 - SqlHarness.Mcp.Tests.dll
Passed! - Failed: 0, Passed: 3218, Skipped: 0, Total: 3218 - SqlHarness.Tests.dll
==> format
verify: OK
Exit code: 0
```

The earlier Windows failures were not reproduced in this run.
