# 005-verification-evidence — full verification set (T4, FINAL)

HEAD: `39519d5` (`test: assert token-less progress silence over a settle window (005/T2)`),
branch `fix/plan-005-mcp-verification`, worktree `.worktrees/plan-005-mcp-verification`.
Run date: 2026-09-29. Prod/test code untouched relative to T2/T3 (T4 documentation-only).

## 1. MCP filter from the brief (a)

Command:

```powershell
dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal
```

| Item | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Mcp.Tests` | Passed 24 / Failed 0 / Skipped 2 / Total 26, Duration 26 s |
| `SqlHarness.Tests` | no filter matches (overall exit 0) |
| Wall | not measured separately (restore + build in the same invocation) |

Skips: only non-native RIDs (`..._on_linux_x64`, `..._on_osx_arm64`).
The 004 inspect↔query gates within the filter scope: green (no FAIL).

## 2. Build with `-warnaserror` (b)

Command:

```powershell
dotnet build SqlHarness.sln --no-restore -warnaserror --verbosity minimal
```

| Item | Result |
|---|---|
| Exit | 0 |
| Warnings / errors | 0 Warning(s), 0 Error(s) |
| Time | Time Elapsed 00:00:03.97, wall 4 s |

## 3. Full suite without integration (c)

Command:

```powershell
dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal
```

| Item | Result |
|---|---|
| Exit | 0 |
| `SqlHarness.Tests` | Passed 1915 / Failed 0 / Skipped 0 / Total 1915, Duration 10 s |
| `SqlHarness.Mcp.Tests` | Passed 154 / Failed 0 / Skipped 4 / Total 158, Duration 36 s |
| Total | Passed 2069 / Failed 0 / Skipped 4 |
| Wall | 49 s |

Skips (4): 2× non-native RIDs (as in §1) + 2× live stdio
(`McpStdioLiveTests.Live_postgres_stdio_drive`, `Live_sqlserver_stdio_drive` —
tests requiring a live database; no live evidence — see §5).

## 4. What was established

Progress timeout (`Progress_fires_only_for_a_client_token_without_sql_payload`,
15 s limit in `WaitForProgressCountAsync`): cause ESTABLISHED and FIXED, test-only.
Reproduced 3/3 under dual load (2× full MCP suite concurrently, 6 FAIL events,
`TaskCanceledException` in the first wait). Cause: the 2.2.0 SDK client
(`ProcessMessagesCoreAsync` — "fire and forget" + `ForceYielding`) handled
messages concurrently, and the progress receiver (`SendRequestWithProgressAsync`,
per-call, disposed via `await using` the moment the result returned) disappeared when the response
won the race against the notification — the server sent correctly (`Started` before `run()`).
Fix (commits `6c41f91`, `39519d5`, only `McpLifecycleTests.cs`):
progress observation through the session-level `RegisterNotificationHandler` (lives longer than the
call) + a silence-without-token assertion over a settle window (poll 25 ms / 300 ms,
`AssertNoProgressGrowthAsync`). Fix verification with the same dual load: 0/0,
154/0/4 in both processes. Prod code untouched.

## 5. What was NOT established

- Publish stall (`PublishAsync`, 10 min budget): NEVER reproduced.
  Baseline: Duration of the `..._on_this_rid` test 16–17 s solo (7/7 runs solo
  in T1–T3; the Duration 26 s in §1 covers restore+build+tests of the whole invocation,
  not publish itself). Rule: Duration >2× beyond 16–17 s (threshold ~32–34 s)
  in the future is a signal to re-instrument (`PublishAsync`: distinguishing
  restore/publish/launch, logs), not to guess today. Zero code changes —
  (a) stdout/stderr already drained concurrently before `WaitForExitAsync`
  (measurement M1: stdout 570 B / stderr 0 B), (b) no failure to distinguish
  (always exit 0), (c) `Kill(entireProcessTree: true)` already in the code.
- Live/platform evidence: NONE. Live tests (`Live_*_stdio_drive`) skipped
  without a live database; runs Windows x64 only (linux/osx RIDs skipped).
  Out of T4 scope (live DB, deploy — forbidden by the brief).

## 6. Statuses T1–T3

- T1 (`task-1-report.md`): DONE documentation-wise, 0 commits, timeouts not
  reproduced (causes not established at that stage).
- T2 (`task-2-report.md`): DONE — fix #1 (`6c41f91`) + review round I1
  (`39519d5`); the 3-fix limit not exhausted; publish unchanged (no measurement).
- T3 (`task-3-report.md`): DONE — 0 fixes (phase B not justified by measurement);
  3 plan hardening points confirmed as already present in the code;
  one-off T2 flakes closed (methodology artifact / overload), not reproduced
  sequentially (publish 16–17 s ×3, precancelled-token 3/3 PASS, filter 24/0/2).
- T4 (this one): only this evidence file; limits not raised;
  no environment timeouts (all commands exit 0 on the first attempt).

## 7. Checks

- `git diff --check`: exit 0.
- `git status --short`: only `plans/005-verification-evidence.md` (new file).
- `plans/README.md`: untouched (the controller updates the index).
