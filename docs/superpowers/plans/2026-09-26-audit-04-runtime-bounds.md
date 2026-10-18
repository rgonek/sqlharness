# Runtime bounds and watch Implementation Plan

**Status (2026-10-08):** DONE — T1 3865a60, T2 0bc4d62/b13ed7c, T3 f0ea5ce/e304d93, T4 015d202. The flakiness of the MCP host tests moved to `plans/005` (still observed). The checkboxes below were not ticked off during implementation.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enforce the watch deadline, bound the history and auxiliary memory, and explain the flakiness of the process test.

**Architecture:** One budget for the whole operation, controlled storage of results and messages; presentation uses the 03/T3 contract. Full result processing and limited presentation remain separate concepts.

**Tech Stack:** .NET 8, CancellationToken, Npgsql/SqlClient, xUnit, a controlled clock.

**Spec:** [audit B5, A2 and test results](../specs/2026-09-26-project-audit.md), [roadmap](2026-09-26-audit-roadmap.md).

## Global Constraints

- The watch deadline covers connect, poll and delay. Do not start a new command after the budget is exhausted.
- The operation deadline is exit 7; external cancellation must not be confused with a natural end of time.
- The timeout of a single command and the presentation limit are not a guarantee of bounded server work.
- Snapshot still rejects incomplete data; equivalence still covers the required results.

## Review Focus

- A deadline reached during connect, delay or a read: T1.
- ConditionMet exactly at the time boundary: T1.
- A changed result on every poll over a long period: T2.
- A large string and an avalanche of NOTICE/InfoMessage: T2–T3.
- A slow PowerShell start, a path with spaces and a missing second PID: T4.

## T1 — hard watch deadline

**Files:** `src/SqlHarness.Core/WatchRunner.cs`, `SqlHarnessModule.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `WatchConditionTests.cs`.

**Interfaces:** keep `IWatchClock` or extend it with a monotonic budget measurement; linked cancellation must be deterministically controllable in tests. The result remains `SqlHarnessWatchReport` and `WatchExitReason.MaxDuration`.

- [ ] Tests: a delay ending at the deadline → no further ExecuteReaderAsync; a blocking connect/poll receives cancellation; the command timeout does not exceed the remaining budget (cancellation handles sub-second precision).
- [ ] Settle the tie rule: when a read ends at the deadline, first evaluate whether the result completed within the budget; condition-met only for a result completed on time. Exceeding it → exit 7 with the last complete state.
- [ ] Implement a monotonic deadline and a linked token, correct reader/session disposal, and no confusion with the user token. Do not wait for another poll to detect the end.
- [ ] Run `dotnet test --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchConditionTests'`; local commit.

## T2 — bounded history and messages

**Files:** `WatchRunner.cs`, `Contracts.cs`, `SqlExecution.cs`, `Postgres/NpgsqlSessionFactory.cs`, `QueryResultCollector.cs` pod `src/SqlHarness.Core`; `src/SqlHarness.Cli/Commands/WatchCommand.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `QueryResultCollectorTests.cs`, `SqlExecutionTests.cs`.

**Interfaces:** a planned `--history-limit` defaulting to 100, range 1..10000; the report contains totalChangedPolls/omittedPolls and the last full result. Agent mode additionally applies a global byte budget. Do not confuse history changes with stream output from 06/T3.

- [ ] Test 10000 changing polls: at most history-limit reports remain in memory; keep the last one and the omitted counter; the unchanged criterion does not depend on retention.
- [ ] Define per-command message consumption so that the session does not store notices from all previous repetitions. Keep the SQL Server statistics required by the benchmark.
- [ ] Settle the per-command message memory limit in the spec: for diagnostics a truncation with a counter, for data necessary for metrics an explicit incomplete/error; do not report zero reads after a silent rejection of STATISTICS IO.
- [ ] Add tests for many sets/repetitions and for an avalanche of messages, redaction before publication. Run the indicated classes and BenchmarkRunTests; local commit.

## T3 — memory, input and query cost

**Files:** `src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs`, `Commands/SqlHarnessCommands.cs`; `src/SqlHarness.Core/QueryResultCollector.cs`, `CanonicalResults.cs`, `ResultEquivalence.cs`; testy QueryResultCollectorTests, CanonicalResultsTests i nowe `tests/SqlHarness.Tests/Cli/OutputCaptureWriterTests.cs`.

**Interfaces:** keep Mark/GetAnsiFreeFootprint or replace their implementation with differential counters; do not accumulate the whole stdout. The SQL input design remains file/stdin, without interpolation.

- [ ] Test a Unicode and ANSI stream split across Write calls: the footprint is correct without a full StringBuilder; Mark works for many successive operations.
- [ ] Add bounded SQL input, a proposed 16 MiB UTF-8 limit per file/stdin; rejection before parsing and connecting. Do not treat the XML plan limit as the SQL limit without an explicit implementation.
- [ ] Check the memory of a single cell/LOB: the agent projection is bounded, the canonical hash is complete. If the current GetValue forces full materialization, describe the restriction and separate a streaming stage with a test of an identical hash; do not promise constant memory beforehand.
- [ ] Clearly describe --max-rows as a presentation limit. Do not add an automatic TOP/LIMIT nor interrupt results in a way that changes hash/equivalence.
- [ ] Run the listed tests, record a retention measurement for synthetic data and a local commit.

## T4 — stability of the process cancellation test

**Files:** `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs`; `src/SqlHarness.Core/Auth/AzureCli.cs` only if the diagnosis reveals a production bug.

**Interfaces:** `IProcessRunner.RunAsync(...)` unchanged without evidence of need.

- [ ] Reproduce the PID publication timeout under controlled conditions: a delayed start, a temp path with spaces, a child failure. Preserve stderr and process state in the test diagnostics, without secrets.
- [ ] Fix the readiness synchronization and quoting if that is the cause; do not limit yourself to increasing the timeout. Start-Process of the test child uses WindowStyle Hidden.
- [ ] The test must still demonstrate termination of the whole tree and perform cleanup after timeout/failure. Do not mask the exception nor change it into a skip.
- [ ] Run ProcessRunnerTests in isolation and together with the offline set; document the result of both runs and a local commit only if there was a change.
