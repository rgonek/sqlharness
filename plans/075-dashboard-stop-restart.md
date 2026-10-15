# Plan 075: Dashboard stop and restart Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or, when the operator selects delegation, superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the operator stop an MCP-autostarted dashboard without finding its process, and restart it using the currently invoked SQLHarness version after publishing an update.

**Architecture:** Add `--stop` and `--restart` to the existing dashboard command. A small lifecycle client discovers the instance through `DashboardLock`, sends an authenticated loopback shutdown request, and waits for shutdown. Restart then uses the existing detached launcher with the current executable/entry assembly and waits for the replacement to publish its endpoint. The server delegates shutdown to its existing ASP.NET host lifetime, which already cancels housekeeping and releases resources.

**Tech Stack:** .NET 10, C#, Spectre.Console.Cli, ASP.NET Core, HttpClient, xUnit; Windows and Linux.

**Spec:** The operator approved the short in-chat design on 2026-10-15: `dashboard --stop`, `dashboard --restart`, protected local shutdown, wait for lock release, current-version restart, and tests for no instance and timeout. This plan records the detailed decisions; no separate spec is needed for this bounded change.

## Status

- DONE on `feat/plan-075-dashboard-lifecycle`; final product revision `1524b44`, Windows gate passed; Linux explicitly waived by operator. Local integration pending. See [verification](../docs/verification/plan-075.md).
- Operator authorized implementation using Luna high, task review Luna xhigh, whole-branch review Sol low. Tests use isolated homes; publishing and operator dashboard shutdown remain outside scope.
- Before execution, reread repository instructions, inspect status/worktrees, create an isolated worktree, and check for concurrent dashboard changes (especially plans 070/071/073).

## Global Constraints and operator contract

- Scope is the effective `SQLHARNESS_HOME`, using existing `SqlHarnessPaths` resolution; never enumerate or stop arbitrary SQLHarness/MCP processes.
- `dashboard --stop`: success (0) if no instance exists or the discovered instance shuts down; never start a dashboard or open a browser.
- `dashboard --restart`: stop first, then launch `DashboardLaunchCommand.Current()` through `IDashboardLauncher` in the effective home, even when `dashboard.autoStart` is false. This is an explicit operator action. Replacement runs in background mode, including its existing idle-shutdown policy.
- Restart opens the replacement dashboard by default; `--restart --no-open` prints its URL without opening the browser, following existing announcement/token handling. Existing plain `dashboard`, `--no-open`, and internal `--background` behavior stays intact.
- Reject `--stop --restart`, lifecycle flags with `--background`, and `--stop --no-open` as invalid arguments (2), before any side effects. `--restart --no-open` is valid.
- Shutdown and startup each have a 10-second total deadline, including discovery, HTTP and waiting; caller cancellation must also end waits. Lifecycle/storage/launch/protocol failures return 6 with actionable diagnostics, never a false success. Follow existing dashboard cancellation exit 0.
- No forced process kill fallback. A pre-075 server cannot support the new shutdown route: report incompatibility and the discovered PID with manual-stop guidance. This is a one-time bootstrap limitation; first upgrading to 075 does not retrofit the endpoint into a running older server.
- Preserve token and configuration files; remove only lifecycle-owned endpoint/open-page artifacts through their existing owners. Never delete a live lock file to simulate shutdown.
- Shutdown traffic uses the existing dashboard cookie plus `X-SqlHarness-Dashboard: 1`, JSON content type and `{}` body; token never goes in shutdown URL, process arguments, diagnostics or exception text. Disable proxies and redirects for lifecycle HTTP; use only validated `127.0.0.1` endpoint ports.
- Add no MCP tool, dashboard button, journal migration, dependency, automatic version replacement, or publishing-script process management. Preserve resolved-secret protection and mutation classifications.
- Publishing already renames the installed executable to `sqlharness.previous-<timestamp>` before replacing it. Existing processes keep running old code; restart is explicit after installation. A still-running older MCP process also stays old until its client restarts it.
- MCP autostart happens at MCP startup, not continuously; stopping the dashboard does not itself cause immediate resurrection. A concurrent/new MCP startup can race lifecycle commands; detect replacement rather than claiming it is the requested version.

## Review Focus

1. Older running server returns 404/405: bounded, actionable failure, no kill and no restart launch (Task 2).
2. Locked but unpublished, corrupt or unreadable discovery data: distinguish from genuinely stopped and fail by deadline (Task 2).
3. PID reuse, stale metadata, endpoint replacement or concurrent restart: no unrelated shutdown and no false current-version claim (Tasks 2/3).
4. Active live/SSE streams: shutdown completes, housekeeping exits and lock/open page are released (Task 1).
5. Restart from a different installation or `dotnet <assembly>`: launch the caller's command and home, never the old instance's executable (Task 3).

## File map

| File | Responsibility |
|---|---|
| `src/SqlHarness.Dashboard/DashboardServer.cs` | Guarded shutdown route and host lifetime callback |
| `src/SqlHarness.Dashboard/DashboardSecurity.cs` | Exact additional guarded write |
| `src/SqlHarness.Dashboard/DashboardLifecycle.cs` (new) | Discovery, authenticated shutdown, bounded confirmation |
| `src/SqlHarness.Dashboard/DashboardLock.cs` | Safe stopped/held probe, if existing APIs cannot distinguish ambiguous discovery |
| `src/SqlHarness.Dashboard/DashboardAutostart.cs` | Return launched process identity for verified restart; autostart discards it |
| `src/SqlHarness.Cli/Commands/DashboardCommand.cs` | Flag validation, stop/restart orchestration and reporting |
| `src/SqlHarness.Cli/SqlHarnessCli.cs` | Command description/registration wiring if required |
| `tests/SqlHarness.Tests/Dashboard/DashboardLifecycleTests.cs` (new) | Lifecycle client and failure/race tests |
| `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs` | Route security and endpoint inventory |
| `tests/SqlHarness.Tests/Dashboard/DashboardHostTests.cs` | Host shutdown/cleanup with live streams |
| `tests/SqlHarness.Tests/Cli/DashboardCommandTests.cs` | CLI flags and orchestration |
| `tests/SqlHarness.Tests/Dashboard/DashboardAutostartTests.cs` | Current executable/entry assembly and launcher regression coverage |
| `README.md` | Operator commands and update workflow |
| `plans/README.md`, `plans/history.md`, this plan | Queue and final evidence |

## Task 1: Authenticated graceful shutdown

**Interfaces:** Produce `POST /api/shutdown`, returning 202 with an empty body; schedule `IHostApplicationLifetime.StopApplication()` after response completion. Keep the method/path allowlist exact and apply all existing guarded-write authentication checks. `GET /api/shutdown` must not stop the host.

- [x] Add failing integration tests to `DashboardServerTests`: correct cookie/header/JSON returns 202 and signals `RunningDashboard.Stopping`; missing/wrong cookie returns 401; missing header, foreign Origin or query token returns 403; unsupported method returns 405. Assert rejected requests never signal stopping. Update the exact endpoint inventory from three guarded writes to four.
- [x] Add a host test which opens a live stream, requests shutdown, and asserts host returns 0 within the deadline, lock can be acquired and endpoint/open-page artifacts are gone. Persistent token remains.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardServerTests|FullyQualifiedName~DashboardHostTests"`; capture relevant RED failure.
- [x] Implement the exact route and security allowlist change using the existing host lifetime. Update comments describing the write routes. Do not bypass `DashboardSecurity` or relax other routes.
- [x] Rerun the focused command; expect all selected tests passing. Commit the task.

## Task 2: Safe lifecycle client and bounded stop confirmation

**Interfaces:** Create `public enum DashboardStopStatus { AlreadyStopped, Stopped, Failed }` and `public sealed record DashboardStopResult(DashboardStopStatus Status, string? Error)`. Produce `public static Task<DashboardStopResult> DashboardLifecycle.StopAsync(string home, IProcessInfo processes, TimeSpan timeout, CancellationToken ct)`. Internal injectable HTTP/time hooks may support deterministic failure tests; keep production callers simple. Errors contain no token. Cancellation propagates for the CLI to handle consistently.

- [x] Add failing tests named for these outcomes: no holder -> AlreadyStopped; real published host -> Stopped and lock released; held/unpublished or corrupt metadata -> Failed by timeout; stale PID/start time -> no HTTP to stale endpoint; 404/405 -> Failed with older-version/PID guidance; 401, connection failure and timeout -> Failed unless actual shutdown is independently confirmed; replacement endpoint -> Failed rather than shutting down the replacement.
- [x] Add HTTP transport assertions: exact loopback destination, cookie/header/JSON, no redirects/proxy, no token in URL or returned errors. Test an endpoint redirect never reaches the redirect destination.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter FullyQualifiedName~DashboardLifecycleTests`; capture RED.
- [x] Implement discovery using `DashboardLock.ReadRunning` and PID/start-time validation. For a null discovery result, prove the home lock is acquirable before returning AlreadyStopped; otherwise retry within the same deadline. Keep probe scopes short and dispose acquired locks. Do not mistake `ReadRunning == null` for shutdown.
- [x] Send one guarded shutdown request to the captured instance, then poll for lock release within the remaining deadline. Confirm the captured process identity is gone before reporting Stopped, so publishing is not racing process exit; if identity cannot be verified, report failure rather than claim process exit. Detect changed published identity and stop with a race diagnostic. Never delete a replacement's files or kill a process.
- [x] Rerun focused lifecycle, lock and host tests; expect pass. Commit the task.

## Task 3: CLI stop/restart and operator documentation

**Interfaces:** Extend `DashboardCommand.Settings` with bool `Stop` (`--stop`) and bool `Restart` (`--restart`). Consume Task 2's `StopAsync`. In `DashboardAutostart.cs`, add `public sealed record DashboardLaunchIdentity(int Pid, DateTimeOffset StartedAt)` and change `IDashboardLauncher.Launch(DashboardLaunchCommand command, string workingDirectory)` to return `DashboardLaunchIdentity`, captured from the started process before disposing its handle. Existing autostart discards the result; update all launcher fakes. Reuse/refactor existing announcement behavior rather than copying browser token logic. Inject only the lifecycle/launch seams needed for meaningful CLI tests.

- [x] Add failing CLI tests for the flag matrix in Global Constraints, exit 0 for already stopped, exit 6 for stop failure, no launcher/browser on stop, no launcher after failed stop, and exactly one launch after successful stop/no instance on restart.
- [x] Add restart tests asserting the launcher receives the caller's executable/entry assembly and effective home, uses detached background mode, waits up to 10 seconds for publication, and fails for launch exception/no publication. Use existing launch-command tests for native and `dotnet <assembly>` forms.
- [x] Add a real isolated-home restart acceptance test: stop an instance, launch the replacement from the invoked test command, assert new PID/start identity and usable authenticated API; cleanup in finally. Concurrent different-instance publication must return failure and never silently announce current-version success. Do not use or stop the operator's real dashboard.
- [x] Run `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardCommandTests|FullyQualifiedName~DashboardAutostartTests"`; capture RED.
- [x] Implement early argument validation, bounded stop and restart orchestration, and clear status/error output. After launch, require the published PID/start identity to match `DashboardLaunchIdentity` using existing start-time tolerance; publication alone proves only that some dashboard is running. Extend launcher tests and MCP autostart tests for the return-contract change. Keep MCP stdio unchanged.
- [x] Update README Dashboard examples: `sqlharness dashboard --stop`; `sqlharness dashboard --restart`; `sqlharness dashboard --restart --no-open`. Explain `stop -> just publish -> dashboard`, or `just publish -> dashboard --restart` after bootstrap; background idle behavior, first-upgrade limitation, home scope and older MCP processes. CLI help owns option details. Do not expand MCP tool catalogs or user-facing skills for an operator-only command.
- [x] Rerun focused CLI/dashboard tests and `dotnet test tests/SqlHarness.Mcp.Tests --filter FullyQualifiedName~McpDashboardAutostartTests` when shared launcher wiring changes; expect pass. Commit the task.

## Completion and verification

- [x] Review final diff for credential/token leakage, exact shutdown route allowlisting, cancellation/deadline behavior and truthful version claims. Verify no live operator processes were touched.
- [x] Run `just verify` on the final product tree; inspect failing-stage logs and fix before proceeding.
- [x] Final product committed; Linux gate started sequentially after Windows, then interrupted and explicitly waived by the operator. No Linux test/format pass claimed.
- [x] Record focused RED/GREEN and both gate results here. Move this plan's queue entry to `plans/history.md` when complete. Preserve unrelated work; integration, pushing and publishing follow the operator's selected scope.

## Plan self-review

- Approved stop/restart behavior maps to Tasks 2/3; graceful server cleanup and protections map to Task 1.
- Each Review Focus item has an explicit owning test. No journal migration or new MCP tool is required.
- The old-server bootstrap limitation and concurrent-autostart race are explicit; no fallback kill or automatic publishing behavior is implied.
- Implemented using the operator-selected Luna high / Luna xhigh / Sol low routing. Focused RED/GREEN, task and whole-branch reviews, Windows gate and explicit Linux waiver are recorded in [verification](../docs/verification/plan-075.md).
