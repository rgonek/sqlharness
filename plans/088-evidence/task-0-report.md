# Plan 088 Task 0 report

## Base and drift

- Worktree: `D:\Dev\sqlharness\.worktrees\plan-088`, branch `plan-088`.
- Base revision: `272d5dbb5f721536f85355b925273507e22de67b`.
- Confirmed predecessor merges in history and as ancestors of base: Plan 080 `42a1e365b14843e301e8db3922ca9204fb41fc85`; Plan 087 `c4de0da03878be7d7ed230ba90a4e8d2e1b92c4c`.
- Ran the plan's targeted drift command: `git diff --stat 5261846..HEAD -- src/SqlHarness.Core/Journal src/SqlHarness.Core/PlanIdentity.cs src/SqlHarness.Core/PlanDistiller.cs src/SqlHarness.Dashboard`. It showed the expected 080 optimization-dashboard and 087 measurement-fingerprint edits across `ActivityJournal`, `ConditionsModel*`, `JournalSchema`, `JournalReader`, dashboard models/server, optimization components, and `SessionPage`; `PlanIdentity.cs` and `PlanDistiller.cs` did not change in this range. `JournalSchema.CurrentVersion` is 16. The dashboard names confirmed in merged code include `SessionPage.tsx`, `OptimizationSection.tsx`, `ComparisonPanel.tsx`, and `JournalReader.cs`; `SessionPage` renders `OptimizationSection` and the reader exposes optimization history with measurement fingerprints.

## Baseline gate

Command: `just verify` at base `272d5dbb5f721536f85355b925273507e22de67b`. Duration: 12m26s. Results from `D:\temp\sqlharness-gate-logs\verify`:

- `ui-install`: skipped because dependencies were already installed.
- `build`: passed in 3m22s.
- `ui-check`: passed in 7m01s.
- `tests/SqlHarness.Tests`: passed, 4,317 passed / 11 skipped, duration 4m17s.
- `tests/SqlHarness.Mcp.Tests`: failed, 429 passed / 4 skipped / 1 failed, duration 4m27s.
- `format`: not reached because the gate stops on test failure.

The failure is `McpStderrLeakRegressionTests.Process_stderr_has_no_trace_debug_or_information_lines(revision: "2026-07-28")`. `McpClientImpl.ConnectAsync` fails before the test's stderr assertion because the launched server says it does not support protocol version `2026-07-28` and reports that it requires the initialize handshake. The original shared temp logs were later overwritten by another worktree's gate. The original tool capture was preserved under [Plan 088 evidence](evidence/baseline-verify-captured-output.txt); it contains the complete captured MCP failure and the captured gate summary, plus the captured core-test summary. The original full build, UI, and core raw log files are no longer available, so those individual stage results cannot now be independently rechecked against immutable full logs. This is an unresolved baseline failure; Task 0 made no product or test changes to it.

## Prototype and measurement method

Both prototypes were isolated under `D:\temp\sqlharness-plan088-renderer-bench` and deleted after measurement. They were Vite production builds served by Vite preview on loopback and measured in headless Chrome. No prototype code was added to the shipped UI or repository. Versions: Windows 10 Pro N build 19045; Intel Core i7-4700MQ at 2.40 GHz; Chrome 155.0.8059.40; Node 24.18.0; npm 11.16.0; React/ReactDOM 19.3.0; Vite 8.3.3; `d3-zoom` 3.0.0 (ISC); `@xyflow/react` 12.12.0 (MIT).

Each candidate renders the same deterministic balanced binary tree. The 95-node fixture is passed as a synthetic `PlanGraph`; 250- and 1000-node cases use generated trees. The React SVG prototype used its own right-to-left coordinates: `x = graph.width - 90 - depth * 220`, placing each deeper level 220 px left of its parent; the captured source excerpt is in [the layout evidence](evidence/d3-layout-source-capture.txt). The viewport is 1600×900. Three fresh browser pages were measured for each candidate and graph size. First render starts immediately before `createRoot(...).render(...)` and ends at the renderer's first React layout effect. Highlighting selects 20 evenly distributed external node ids and times state update through layout effect. Click latency is dispatch-to-handler, with 25 synthetic clicks per sample. Pan uses synthetic mouse/pointer drag events, wheel zoom uses synthetic wheel events; requestAnimationFrame intervals are collected over 600 ms per sample. The table reports medians across the three samples (frame p95 is the median of each sample's p95); frame drops count intervals over 25 ms. There were no page errors in the final repeated run.

Vite production builds report the lazy renderer chunks (shared app entry excluded):

| Candidate | Lazy JS gzip | Lazy CSS gzip | Increment |
|---|---:|---:|---:|
| React SVG + `d3-zoom` | 16.44 KB | 0 KB | 16.44 KB |
| `@xyflow/react` | 57.70 KB | 2.56 KB | 60.26 KB |

Three-sample measurements and the decision are recorded under `## Renderer decision` in `plans/088-plan-visualization.md`.

## License check

The prototypes use only text labels, so no icon set was considered or copied. The official vscode-mssql repository identifies its project license as MIT, with separate third-party notices: [LICENSE.txt](https://github.com/microsoft/vscode-mssql/blob/main/LICENSE.txt), [ThirdPartyNotices.txt](https://github.com/microsoft/vscode-mssql/blob/main/ThirdPartyNotices.txt). No specific upstream icon asset was evaluated for reuse.

## Changed files and concerns

- `plans/088-plan-visualization.md`: completed Task 0 checklist and recorded the renderer decision with measurements and tradeoffs.
- `task-0-report.md`: this permanent evidence report.
- `evidence/baseline-verify-captured-output.txt`: recovered original Task 0 tool-output excerpts, no longer dependent on the shared temp log directory.
- `evidence/d3-layout-source-capture.txt`: captured prototype source excerpt confirming the custom right-to-left layout.
- The review-fix reconciliation is summarized in [verification evidence](../088-verification-evidence.md).
- Concern: the baseline MCP test failure remains unresolved and the full gate did not reach format. The plan's final gate must pass before overall completion.
- Evidence limitation: the shared raw gate logs were overwritten by Plan 083. The captured MCP failure and summary plus core-test summary remain available; complete raw build, UI, and core logs do not.
- No live database, persistent writes, merge, or push occurred.
