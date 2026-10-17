# Plan 087: Measure observations in the Optimization view Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Drift check (run first):** plan 080 must be merged. `git diff --stat 13b468a..HEAD -- src/SqlHarness.Dashboard src/SqlHarness.Core/Journal src/SqlHarness.Mcp/Tools src/SqlHarness.Cli/Commands`. Use 080's actual names (`OptimizationSummaryBuilder`, `OptimizationComparer`, `CompareEvidence`, `PairBasis`, session-list badge) — the names below follow plan 080.

**Goal:** Let `measure` carry `subject`/`scope` and treat measure runs as version observations in the Optimization view alongside compare.

**Architecture:** `measure` accepts optional context (same validation as compare) journaled in existing columns; the journal adds a measurement fingerprint (079 payload without comparison settings) so measure and compare observations group together; 080's summary builder and comparer consume measure evidence with the pair/default/equivalence rules from the spec.

**Tech Stack:** .NET 10, MCP SDK, Spectre CLI, Microsoft.Data.Sqlite, xUnit; React + Vitest.

**Spec:** [docs/superpowers/specs/2026-10-17-measure-in-optimization-design.md](../docs/superpowers/specs/2026-10-17-measure-in-optimization-design.md)

## Status and dependencies

- TODO. Planning base `13b468a` (079 DONE). Depends on 080. Plans 082 and 084 follow this plan.
- Priority P2; effort M; risk LOW–MEDIUM.

## Global Constraints

- `measure` `subject`/`scope`: optional regardless of `compare.requireContext`; validated by `CompareContext.Parse` (`^[a-z0-9][a-z0-9._-]{0,63}$`, scope `whole` or a fragment label); MCP members always present; CLI `--subject`/`--scope` on `measure`.
- Measurement fingerprint = HMAC (journal salt) over the 079 fingerprint payload minus `comparisonMode` and `compareNullability`; operation-level and per case; null for rows written before this plan (fallback: 079 fingerprint).
- Pair basis: `measured together` only when one compare contains both versions; otherwise `different runs, same conditions`; `conditions differ` when measurement fingerprints differ.
- Measure-only versions: equivalence `results not compared`; version-table outcome `measured only`.
- Default pair: A = earliest version; B = 080 equivalence-chain rule, else newest version labelled `results not compared`.
- Section visible when the session has a compare or a measure with a subject; list filter label `Contains optimization`.
- Commits `feat(087): …`; `just verify`.

## Review Focus

1. measure v0 then compare v1→v2, same parameters/setup/db → one group; v0 row present; default pair v0→v2 labelled `results not compared` → Task 3 `Measure_then_compare_shares_group`.
2. A version observed by both a compare and a newer measure → pair with its compare partner uses `measured together` → Task 3 `Common_compare_beats_newer_measure`.
3. Measure matrix (`0`,`1`) vs compare sets (`a`,`b`) with equal values → cases pair by fingerprint → Task 3 `Cases_pair_by_fingerprint_not_id`.
4. Measure without subject → absent from Optimization, still listed as an operation → Task 3 `Measure_without_subject_is_not_versioned`.
5. Pre-087 rows without measurement fingerprint → grouping falls back without crashing → Task 2 `Legacy_rows_fall_back_to_operation_fingerprint`.

---

### Task 0: Drift check
- [ ] Confirm 080 merged; drift check; `just verify` baseline; record base.

### Task 1: measure accepts subject/scope
**Files:** `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (~`:163-193` `MeasureAsync` + request-scoped twin; `ThrowIfUnknown` lists), `src/SqlHarness.Mcp/McpOperationMapper.cs` (`MapMeasureAsync`), `src/SqlHarness.Core/Contracts.cs` (`SqlHarnessMeasureOperation.Context`), `src/SqlHarness.Core/Journal/OperationJournalDescriber.cs` (journal `compare_subject`/`compare_scope` for measure), `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs` (measure `--subject`/`--scope`), `MeasureDescription`; tests `tests/SqlHarness.Mcp.Tests` (schema, mapping, catalog budget), `tests/SqlHarness.Tests/MeasureTests.cs`, CLI tests, `OperationJournalDescriberTests.cs`.
- [ ] Failing tests: MCP measure accepts and journals context; invalid slug → validation error; absent → allowed even with `requireContext=true`; CLI parity; tools/list ≤ 32 KiB.
- [ ] FAIL → implement → PASS. Commit `feat(087): measure accepts subject and scope`.

### Task 2: Measurement fingerprint
**Files:** `src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs` (~`:69-82` payloads), `ConditionsModel.cs` (`MeasurementFingerprint` on operation and cases), `ActivityJournal.cs` (HMAC), `JournalSchema.cs` (step: `operations.measurement_fingerprint`, case-level storage alongside 079's case fingerprints; `CurrentVersion` +1, `JournalContractTests` pin).
- [ ] Failing tests: measure and compare with equal params/setup/db/engine → equal measurement fingerprints, different operation fingerprints; ordered vs multiset compare → equal measurement fingerprints; values differ → different; keyed by salt; `Legacy_rows_fall_back_to_operation_fingerprint`.
- [ ] FAIL → implement → PASS. Commit `feat(087): journal measurement fingerprints`.

### Task 3: Optimization consumes measure evidence
**Files:** `src/SqlHarness.Dashboard/JournalReader.cs` (load measure evidence with context), `CompareHistoryBuilder.cs` (measure hashes in version numbering), 080's `Optimization/OptimizationSummaryBuilder.cs`, `OptimizationComparer.cs`, `OptimizationModels.cs`, session-list badge/filter; UI `ComparisonPanel.tsx`, `VersionTable.tsx`, `SessionTable.tsx`/`SessionsPage.tsx` labels; tests `OptimizationSummaryBuilderTests.cs`, `OptimizationComparerTests.cs`, `CompareHistoryBuilderTests.cs`, UI tests.
- [x] Failing tests: `Measure_then_compare_shares_group`; `Common_compare_beats_newer_measure`; `Cases_pair_by_fingerprint_not_id`; `Measure_without_subject_is_not_versioned`; measure-only group default pair earliest→newest with label; outcome `measured only`; Mixed/better/worse computed for measure pairs; filter `Contains optimization` includes measure-only sessions; Markdown copy states basis and `results not compared`.
- [x] Scoped FAIL → implement → PASS. Backend focused dashboard tests: 49 passed; UI focused Vitest tests: 28 passed. Full `just verify` is reserved for Task 4/controller. Commit `feat(087): measure observations in optimization`.

### Task 4: Documentation and verification
- [ ] README/`docs/mcp.md` (measure context, measurement fingerprint, panel semantics); `plans/README.md`; note in plans 082/084 headers that they follow 087; `just verify`; queue → history; commit `docs(087): record plan 087 evidence`.
