# Plan 068: Operation phase timings and opt-in connection diagnostics

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** `git diff --stat ed44649..HEAD -- src/SqlHarness.Core/SqlExecution.cs src/SqlHarness.Core/EngineSessionFactory.cs src/SqlHarness.Core/SqlHarnessModule.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/ArtifactDirectoryPublisher.cs src/SqlHarness.Core/Journal src/SqlHarness.Core/SqlHarnessConfig.cs src/SqlHarness.Core/SqlHarnessConfigWriter.cs src/SqlHarness.Dashboard src/SqlHarness.Cli/Commands/DoctorCommand.cs README.md`. Plan 062 (compare dashboard) changes the operation page and journal reader; rebase on it if it lands first. A journal schema version taken by another plan moves this plan to the next free version; that is adaptation, not a STOP.

**Goal:** Every journaled operation records how its wall time splits into connect, setup, execute, artifact-write and other phases. An operator can enable per-connection diagnostic events in `config.json`/Settings to investigate slow connections.

**Architecture:** An `OperationPhaseRecorder` lives in an `AsyncLocal` scope opened by `JournalingModule` for each operation. Concurrent MCP calls get separate recorders. A decorating `ISqlSessionFactory` times `ConnectAsync`, and the artifact publisher times its writes. Benchmark runners time setup and measured execution through the same recorder. Aggregates (count, total, max per phase) go to a new journal table whenever the journal is enabled: they are numbers, not sensitive. Per-event diagnostics (one row per connection or artifact write, with offset, duration, outcome, error number and client connection id) are stored only when `diagnostics.enabled` is true. The default is off; an invalid config means off.

**Tech Stack:** .NET 10, C#, SQLite (Microsoft.Data.Sqlite), xUnit, React/TypeScript dashboard (Vitest).

**Spec:** No separate spec. Origin: 2026-10-14 operator decisions P4/P6 after a retro ([report observation](../docs/superpowers/specs/2026-10-07-agent-session-report-draft.md#2026-10-14--small-point-lookup-query-marginal-accepted-candidate-rejected-reshape)). The Design decisions below are the spec.

## Status

- TODO; planning accepted 2026-10-14 (operator). No implementation, installation, integration or publication authorization.
- Planning base: `ed44649`, 2026-10-14.
- Priority: P2. Effort: M–L. Risk: MEDIUM (journal schema migration; decorator on every connection; dashboard).
- Owner: this plan. Coordinate with 062 (operation page, journal reader).

## Evidence

- A five-cell compare took several times longer than an equivalent compare minutes later. Per-cell artifact timestamps showed cells taking tens of seconds versus a few seconds; statements ran in about a millisecond. Database resource statistics for that window showed the database idle. The journal stores only one `duration_ms` per operation (`JournalSchema.cs` `operations`), so connection, execution and artifact time could not be separated. The cause stayed unproven. Workload values are omitted.
- Each compare cell opens one session per variant (`CompareCellRunner.cs:182`); `measure`, `query`, `watch` and `snapshot` connect in `SqlHarnessModule.cs:177,601,646`, `WatchRunner.cs:95,194`, `SnapshotRunner.cs:76`. All go through `ISqlSessionFactory` (`SqlExecution.cs:150-160`), built in `SqlHarnessModule.cs:65`.
- `ActivityJournal` already owns a `TimeProvider` (`ActivityJournal.cs:26`) and migrates by `user_version` (`:340-360`, current version 7).

## Design decisions

1. **Phases (closed set):** `connect` (includes auth/token acquisition and login), `setup`, `execute` (measured or query execution including result and plan reading), `artifact` (publishing files), `other` (wall time minus the sum of the others, never negative). No `auth` phase separate from `connect`: SqlClient's `ActiveDirectoryDefault` acquires tokens inside `Open`; splitting it would need provider hooks. The `az` CLI path (`AzureCli`) is timed as its own event detail when diagnostics are on.
2. **Aggregates always (when the journal is enabled):** table `operation_phases(operation_id, phase, count, total_ms, max_ms)`. They are cheap, non-sensitive and answer "where did the time go".
3. **Events opt-in:** `config.json` `"diagnostics": { "enabled": false }`. When true, table `operation_events(operation_id, seq, offset_ms, phase, duration_ms, outcome, error_number, detail_json)`. `detail_json` is bounded to 512 characters and holds only: `clientConnectionId`, `authStrategy` (`ad-default`/`azure-cli`/`sql`/`postgres`), `attempt`, and for `artifact` the file count. Never SQL, parameter values, tokens, or user names. Events per operation are capped at 256 (later ones counted in a final `truncated` event).
4. **Output unchanged.** No new fields in CLI/MCP responses (agents see phases in the dashboard or the dashboard API). Adding a timing object to every response was rejected: bytes on every call for a rare diagnosis.
5. **Retention.** Both tables cascade on `operations` delete; existing retention covers them.
6. **Fail-closed config.** Invalid `config.json` → defaults (diagnostics off), as for other settings.

## Global Constraints

- Journal failures never change output or exit codes (README journal section).
- `IActivityJournal` implementations do not throw; new methods get default no-op bodies like `RecordBenchmark`.
- No parameter values, secrets or result cells in any journal field.
- Concurrent calls must not mix phase data (AsyncLocal per operation).

## Review Focus

1. Two concurrent MCP calls: each operation's phases contain only its own connects.
2. A connection that fails (timeout, auth error) still records a `connect` phase and, with diagnostics on, an event with `outcome: "failed"` and the SQL error number.
3. `watch` reconnects (`WatchRunner.cs:194`): count increments, max reflects the slowest.
4. Journal disabled (`journal.enabled: false`): recorder is a no-op, no per-call allocation beyond a null check.
5. Schema migration from v7 with existing rows; dashboard on a v7 journal reading a v8 file fails with the existing "schema newer" path, not a crash.

---

### Task 1: Phase recorder and timed session factory (Core)

**Files:**
- Create: `src/SqlHarness.Core/Journal/OperationPhaseRecorder.cs`, `src/SqlHarness.Core/TimedSessionFactory.cs`
- Modify: `src/SqlHarness.Core/SqlHarnessModule.cs:65` (wrap factory), `src/SqlHarness.Core/Artifacts.cs` (time publisher calls near `_publisher`, `:348`), setup/execute call sites in `CompareCellRunner.cs`, `BenchmarkRunner.cs`, `SetupSqlExecution.cs`, `SqlHarnessModule.cs` query path
- Test: `tests/SqlHarness.Tests/Journal/OperationPhaseRecorderTests.cs`, `tests/SqlHarness.Tests/TimedSessionFactoryTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace SqlHarness.Core.Journal;

public enum OperationPhase { Connect, Setup, Execute, Artifact }

public sealed record PhaseAggregate(OperationPhase Phase, int Count, long TotalMilliseconds, long MaxMilliseconds);

public sealed record PhaseEvent(int Sequence, long OffsetMilliseconds, OperationPhase Phase, long DurationMilliseconds,
    string Outcome, int? ErrorNumber, string? DetailJson);

public sealed class OperationPhaseRecorder
{
    public const int MaximumEvents = 256;
    public static OperationPhaseRecorder? Current { get; }               // AsyncLocal-backed
    public static IDisposable Begin(TimeProvider time, bool captureEvents); // sets Current for this async flow
    public PhaseScope Start(OperationPhase phase);                     // disposable; records on Dispose
    public IReadOnlyList<PhaseAggregate> Aggregates();
    public IReadOnlyList<PhaseEvent> Events();                          // empty unless captureEvents
    public int DroppedEvents { get; }
}

public struct PhaseScope : IDisposable
{
    public void Fail(int? errorNumber);                                 // outcome "failed"
    public void Detail(string detailJson);                              // clipped to 512 chars
    public void Dispose();
}
```

- [ ] **Step 1: Failing tests** (use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if the test project references it; otherwise a minimal `TimeProvider` subclass with a settable timestamp):

```csharp
    [Fact]
    public void Aggregates_count_total_and_max_per_phase()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: false))
        {
            var r = OperationPhaseRecorder.Current!;
            using (r.Start(OperationPhase.Connect)) time.Advance(30);
            using (r.Start(OperationPhase.Connect)) time.Advance(10);
            using (r.Start(OperationPhase.Execute)) time.Advance(1);
            Assert.Equal(new PhaseAggregate(OperationPhase.Connect, 2, 40, 30), r.Aggregates().Single(a => a.Phase == OperationPhase.Connect));
            Assert.Empty(r.Events());
        }
        Assert.Null(OperationPhaseRecorder.Current);
    }

    [Fact]
    public async Task Concurrent_flows_do_not_share_a_recorder()
    {
        var time = new ManualTime();
        async Task<int> Flow()
        {
            using (OperationPhaseRecorder.Begin(time, false))
            {
                using (OperationPhaseRecorder.Current!.Start(OperationPhase.Connect)) await Task.Yield();
                return OperationPhaseRecorder.Current!.Aggregates().Single().Count;
            }
        }
        var counts = await Task.WhenAll(Flow(), Flow());
        Assert.Equal([1, 1], counts);
    }

    [Fact]
    public void Events_are_capped_and_counted()
    {
        var time = new ManualTime();
        using (OperationPhaseRecorder.Begin(time, captureEvents: true))
        {
            var r = OperationPhaseRecorder.Current!;
            for (var i = 0; i < OperationPhaseRecorder.MaximumEvents + 5; i++)
                using (r.Start(OperationPhase.Connect)) { }
            Assert.Equal(OperationPhaseRecorder.MaximumEvents, r.Events().Count);
            Assert.Equal(5, r.DroppedEvents);
        }
    }
```

`TimedSessionFactoryTests`: a fake inner factory that advances time and either returns a session or throws `SqlException`-like `SqlHarnessError` with a number. Assert a `Connect` aggregate is recorded in both cases, `Fail` sets outcome `failed`, and no recorder (null `Current`) means pass-through without exceptions.

- [ ] **Step 2: Run** `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~OperationPhaseRecorderTests|FullyQualifiedName~TimedSessionFactoryTests"` — FAIL (types missing).
- [ ] **Step 3: Implement** the recorder with `AsyncLocal<OperationPhaseRecorder?>`, thread-safe aggregation (`lock` on a small dictionary; phases are few). `TimedSessionFactory : ISqlSessionFactory` wraps both `ConnectAsync` overloads in `Current?.Start(OperationPhase.Connect)`. On `SqlException` it calls `Fail(ex.Number)`. With diagnostics on, it calls `Detail` with `{"clientConnectionId":..., "authStrategy":..., "attempt":n}`, reading `ClientConnectionId` from the opened `SqlConnection` through a new optional `ISqlSession.ClientConnectionId` (default `null`; set in the SqlClient session). Wrap the factory at `SqlHarnessModule.cs:65`. Add `Start(Setup)`, `Start(Execute)` and `Start(Artifact)` scopes at the listed call sites, around the existing awaits only.
- [ ] **Step 4: Run** the full `tests/SqlHarness.Tests` project — PASS.
- [ ] **Step 5: Commit** `git commit -m "Core: per-operation phase recorder and timed session factory"`

### Task 2: Journal schema v8 and recording

**Files:**
- Modify: `src/SqlHarness.Core/Journal/JournalSchema.cs` (add `Version8`, `CurrentVersion = 8`), `ActivityJournal.cs` (migration line after `Version7`; new write method), `IActivityJournal.cs` (default no-op), `JournalingModule.cs` (open recorder in the operation wrapper; record before `Complete`)
- Test: `tests/SqlHarness.Tests/Journal/ActivityJournalTests.cs` (or the existing migration test file)

**Interfaces:**
- Consumes: Task 1 types.
- Produces: `void IActivityJournal.RecordPhases(JournalHandle? handle, IReadOnlyList<PhaseAggregate> phases, IReadOnlyList<PhaseEvent> events, int droppedEvents) { }`

```sql
-- Version8
CREATE TABLE operation_phases (
    operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
    phase TEXT NOT NULL,
    count INTEGER NOT NULL,
    total_ms INTEGER NOT NULL,
    max_ms INTEGER NOT NULL,
    PRIMARY KEY (operation_id, phase)
);
CREATE TABLE operation_events (
    operation_id INTEGER NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
    seq INTEGER NOT NULL,
    offset_ms INTEGER NOT NULL,
    phase TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    outcome TEXT NOT NULL,
    error_number INTEGER,
    detail_json TEXT,
    PRIMARY KEY (operation_id, seq)
);
```

- [ ] **Step 1: Failing tests:** (a) a v7 database migrates to v8 with existing rows intact. (b) After a fake operation with two connects and one execute, `operation_phases` has `connect|2` and `execute|1`, plus an `other` row equal to `duration_ms − sum`, clamped at 0. (c) With `captureEvents: false`, `operation_events` is empty. (d) With `true`, events are in `seq` order, and when events were dropped a final row has `outcome = 'truncated'` and `detail_json = '{"dropped":N}'`.
- [ ] **Step 2: Run** the journal tests — FAIL.
- [ ] **Step 3: Implement.** In `JournalingModule`, open `OperationPhaseRecorder.Begin(time, config.Diagnostics.Enabled)` only when the journal handle is non-null. Call `RecordPhases` inside the same guard pattern as `RecordBenchmark`, before `Complete`. The `other` phase is computed at write time from `OperationEnd.DurationMilliseconds`.
- [ ] **Step 4: Run** the full Core tests — PASS.
- [ ] **Step 5: Commit** `git commit -m "Journal v8: operation phases and opt-in events"`

### Task 3: `diagnostics.enabled` setting

**Files:**
- Modify: `src/SqlHarness.Core/SqlHarnessConfig.cs` (add `DiagnosticsConfig { bool Enabled }` and `SqlHarnessConfig.Diagnostics`), `SqlHarnessConfigWriter.cs`, `src/SqlHarness.Cli/Commands/DoctorCommand.cs` (report effective value), `src/SqlHarness.Dashboard/DashboardSettings.cs`, `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`, `ui/src/api/types.ts`
- Test: config loader tests, `SettingsPage.test.tsx`

- [ ] **Step 1: Failing tests:** `{"diagnostics":{"enabled":true}}` loads as enabled; an unknown field under `diagnostics` makes the file invalid → defaults (off); the writer round-trips the field; the Settings page renders a "Connection diagnostics" switch and saves without confirmation (non-sensitive).
- [ ] **Step 2–4:** Implement, run Core and UI tests (`npm test` in `src/SqlHarness.Dashboard/ui`), PASS. Rebuild `ui/dist` with the repository's normal UI build command if dist is committed (check AGENTS.md).
- [ ] **Step 5: Commit** `git commit -m "Settings: diagnostics.enabled for connection events"`

### Task 4: Dashboard operation detail

**Files:**
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs`, `DashboardModels.cs`, `ui/src/api/types.ts`, `ui/src/pages/OperationPage.tsx`
- Test: `OperationPage.test.tsx`, JournalReader tests

- [ ] **Step 1: Failing tests:** operation detail JSON contains `phases: [{phase,count,totalMs,maxMs}]` (empty for v7 rows) and `events` (present only when rows exist). The page shows a "Time breakdown" table (phase, count, total, max, share of wall time). When events exist, a collapsible "Connection events" list shows offset, duration, outcome, error number and client connection id.
- [ ] **Step 2–4:** Implement and run, PASS.
- [ ] **Step 5: Commit** `git commit -m "Dashboard: operation time breakdown and diagnostic events"`

### Task 5: Documentation and gates

- [ ] README journal section: describe `operation_phases` (always with the journal) and `operation_events` (only with `diagnostics.enabled`), the exact fields, and that neither stores SQL, values or tokens. Add `diagnostics` to the optional-behavior `config.json` example and the Settings description. Schema v8 sentence next to the v7 one.
- [ ] Run Windows and Linux gates; record counts. Update Status and the `plans/README.md` row.

## Acceptance

- After any journaled operation, phase aggregates sum (with `other`) to its `duration_ms`.
- With diagnostics off, no `operation_events` rows; with them on, one event per connection and artifact write, bounded detail, no secrets.
- Concurrent operations have disjoint phase data.
- CLI/MCP output bytes are unchanged for identical inputs.

## STOP conditions

- `SqlClient` does not expose `ClientConnectionId` on failed opens in the used version: record `null`; do not add reflection.
- A schema version collision with another plan that cannot be resolved by renumbering: stop and ask.
