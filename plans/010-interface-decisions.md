# 010/T4 — Interface boundary preservation decisions (do not merge)

**Status:** DECISIONS (a document, not an implementation — it records what T1–T3 deliberately did not merge)
**Date:** 2026-09-30
**Base:** T1–T3 of plan 010 in this worktree (`2ef86ce` watch parity tests,
`edb8606` one polling engine with `ReportWatchSink` / `NdjsonWatchSink`
sinks, `8cf5b36` + `7f3687e` shared `Core/OperationLimits.cs`).
**Live evidence:** none — a documentation-only step, no live DB.

## Goal

T1–T3 removed two real duplications: the doubled watch polling loop
(`WatchRunner.cs`) and the doubled pure bounds/duration rules
(`OperationLimits.cs`). The remaining similarities listed below are
deliberate boundaries — a similar method is not here a reason to merge.
Each section is one item whose merging the T4 brief forbids.

## 1. CLI/MCP adapters stay separate

`Cli/Commands/WatchCommand.cs` (`WatchCommand.Settings` with
`CommandOption`/`DefaultValue` attributes) and `Mcp/McpOperationMapper.cs`
(the `Parse*` methods, the `Default*` constants) are two transports with
different contracts: input formats, unit suffix sets, letter-case
and whitespace policy, default values and error texts. They share
only the pure conversion and bounds core (`OperationLimits.cs` —
without messages, never throwing transport errors), and `Core`
independently re-validates the bounds at execution time.

Merging the adapters would unify units and defaults that are
deliberately meant to stay different (the plan's stop condition), and
would move one transport's error texts into the other.

## 2. query/measure/compare operations (and the rest) stay separate

The `SqlHarnessOperation` family (`Core/Contracts.cs`) has 14 operation types,
each with its own report type (among others `SqlHarnessQueryReport`,
`SqlHarnessMeasureReport`, `SqlHarnessCompareReport`,
`SqlHarnessCompareMatrixReport`). A shared execution method (the facade
dispatch) does not make them one operation: they differ in semantics (a single
execution vs. repetitions vs. comparison of variants), report shape
and projections (`BenchmarkSummary.cs`, `AgentOutputProjection.cs`).

## 3. The measure parameter sets and the compare matrix stay separate

`MeasureParameterSets.cs` measures a single query on a single session:
setup once, rotation of the sets in user order, stability
only within a set, shared plan-cache state. `CompareMatrixRunner.cs`
compares two query variants with a new connection and a single setup
per value, sequentially in user order; the first cell failure
stops the run (`CompareMatrixCellFailedException`
with a partial report). These are different execution contracts, not two
flavors of the same loop — merging would blur the rotation vs. stop-on-first-failure rules.

## 4. Domain writers stay separate

`ArtifactDirectoryPublisher.cs` shares only the directory lifecycle
mechanics (naming under a fixed root, staging, publication
via move, a rollback that does not mask the original failure). Content policy —
what is written and which sensitive payloads may accompany the report —
stays in the domain writer (`CompareArtifactWriter`,
`QueryStoreArtifacts`, `IndexAnalysisArtifacts`, `WatchNdjsonWriter`).
`SnapshotStore`/`GainStore` do not use this contract at all.
Merging writers would move sensitive-payload decisions into
shared code.

Preserved: no history retention in NDJSON (`Capabilities.cs` entry
`watchNdjson`: each `changed` emitted immediately; the
`NdjsonWatchSink` sink does not accumulate history, unlike
`ReportWatchSink` with `HistoryLimit`).

## 5. Clocks stay injected, not shared as a static

The watch engine works on the injected `IWatchClock`: production uses
the system clock (`DateTimeOffset.UtcNow`), and the T1 parity tests
a fake clock. One monotonic budget (`IWatchBudget` /
`SystemWatchBudget` over `Stopwatch`, `CancelAfter`) covers connect,
all polls and all delays. Unifying the clocks into a shared
static system time would break test determinism; unifying
the budget with the single-poll limit would break the "one budget
for the whole operation" rule from T2.

## 6. ADR: public `object? Report` and `OperationReportContract` preserved

Per [ADR T2](../docs/superpowers/plans/2026-09-26-audit-05-architecture-adr-t2-typed-result.md):
the public `SqlHarnessOutcome.Report` remains `object?` (a compatible
adapter, without migration of `ISqlHarnessModule`, CLI renderers,
`AgentOutputProjection`, artifact writers and test fakes),
and the typed contract is enforced internally:
`Core/OperationReportContract.cs` maps the 14 operations onto allowed
reports (`Measure` allows `SqlHarnessMeasureReport` and
`SqlHarnessMeasureSetReport`; failures carry `null`), the facade dispatch
wraps returns in `Checked(...)`, and `ContractsTests` covers the
14 × 15 pairs and the closedness of the operation family. Switching to a fully typed
public result would be a public API change with no behavioral gain.

## What this document deliberately does not do

- It does not change the code or tests of T1–T3 or any other file.
- It does not merge anything from sections 1–6; a future change to any boundary
  requires a separate decision and its own plan.
- It does not unify units or defaults whose values are deliberately different
  (stop condition of plan 010).
