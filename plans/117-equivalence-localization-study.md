# Plan 117: Specify bounded localization of result-equivalence failures

> **For agentic workers:** Use `superpowers:executing-plans`, or delegated execution when selected. Execute checked tasks in order. This is an offline design study; no product implementation or automatic diagnostic SQL is authorized.
>
> **Drift check:** `git diff --stat 5f7342a7..HEAD -- src/SqlHarness.Core/ResultEquivalence.cs src/SqlHarness.Core/BenchmarkCollector.cs src/SqlHarness.Core/SnapshotDiffer.cs src/SqlHarness.Core/Artifacts.cs src/SqlHarness.Core/AgentOutputProjection.cs tests/SqlHarness.Tests/ResultEquivalenceTests.cs`
> Reconcile compatible drift. Stop for changed comparison semantics, missing evidence or broader authorization requirements.

## Status

- **Status:** TODO; design/fixture work only, no implementation/live DB/merge/push.
- **Priority:** P2; selected direction proposal 2.
- **Effort / risk / confidence:** M / MED / HIGH grounding.
- **Depends on:** none of 116-120; existing compare and artifact foundations are implemented.
- **Planned at:** `5f7342a7f5d48e03268491059949d1edfc24c65c`, 2026-10-25.
- **Goal:** Specify diagnostics that identify a changed result set/schema or ordered row position from the original measured executions.
- **Architecture:** Preserve authoritative equivalence and stability decisions. Add a separately bounded, provenance-bearing diagnostic derived from capture metadata; do not rerun SQL or persist default raw-row samples.
- **Tech stack:** .NET 10/C#, System.Text.Json, PowerShell offline fixture projector.
- **Spec:** Candidate and fixture requirements in this document; deliver `117-evidence/design.md` as the future implementation specification if accepted.

## Execution setup

Use an isolated `plan-117` checkout after inspecting `git status --short` and `git worktree list --porcelain`. Run `$studyStart = (git rev-parse HEAD).Trim()` before study changes; retain the revision and initial status. Preserve unrelated edits and make no automatic commit/merge/push. Follow `docs/agents/verification.md` scoped study checks. All runner/verifier failures, including missing evidence, must return a nonzero exit code.

## Why this matters

A faster candidate can fail equivalence while giving the investigator only mismatch categories and counts. Locating the affected result set, changed column shape or first ordered position can reduce additional exploratory queries. Diagnostic completeness must remain independent of the full comparison result.

## Current state and conventions

- `src/SqlHarness.Core/ResultEquivalence.cs:15-27` has `Equivalent`, directional/positional counts, side stability, `SchemaEquivalent` and `MismatchReasons`; it has no locations.
- The internal captured representation at `ResultEquivalence.cs:46` is:

```csharp
internal sealed record CanonicalComparisonResult(
    string SchemaHash,
    IReadOnlyList<string> OrderedRows,
    string? NullabilityFingerprint = null);
```

- `CanonicalComparisonAccumulator.BeginResultSet` at line 516 sees result-set ordinal/columns; `AddRow` hashes rows with set identity; `Complete` returns combined hashes and ordered row fingerprints, losing explicit result-set ranges/schema details.
- `src/SqlHarness.Core/BenchmarkCollector.cs:38-53` sees metadata and rows before hashing. It is a capture seam, not authorization for retaining cell values.
- `src/SqlHarness.Core/SnapshotDiffer.cs:9` caps emitted differences at 100; its location/kind output is a useful bounded pattern, but snapshots retain cells and reject changed column shape. Do not call that implementation unchanged for benchmark fingerprints.
- Model test setup after `tests/SqlHarness.Tests/ResultEquivalenceTests.cs:8`, which uses an accumulator with explicit columns, duplicate rows and ordered assertions.

## Global constraints

- Existing `ordered`, `multiset`, `set`, `off`, nullability, instability and regression decisions remain unchanged. A partial diagnostic cannot make equivalence partial or imply success.
- Maximum compared rows remain 1,000,000; unique comparison fingerprint budget remains 2,000,000 per cell. Do not charge per-repetition duplicate representations as unique evidence.
- Original execution identity includes artifact, case/set/matrix cell, side and repetition. Diagnostic pair identity must identify both runs; baseline instability may be a same-side pair.
- No DB calls, EXCEPT reruns, mode relaxation, raw cell samples, row-hash publication or historical reconstruction from insufficient artifacts.
- Preserve the 4,096-byte minimum MCP response, 65,536-byte default and finite process ceilings. Missing old fields are unavailable, never empty success.

## Review focus

1. Multiple/empty result sets and schemas can differ without a retained row.
2. A stable-side mismatch and within-side instability require different run identities.
3. `multiset`/`set` have no meaningful positional alignment; reordered duplicates must retain current semantics.
4. Truncated diagnostics, unavailable legacy evidence and a genuinely empty difference list are distinct states.
5. Long/multibyte column names and minimum response budgets must not lose the identity needed to interpret a location.

## Scope

**Only writable:** this plan/index row and `plans/117-evidence/` (`inventory.md`, `design.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, `results.json`, `decision.md`, logs). Production/test/skill files are read-only. Future integration map: ResultEquivalence/BenchmarkCollector, report DTOs/artifact writer+reader, Core agent projection, MCP adapter, dashboard compare presentation and their existing tests. Do not add a new artifact tool or rewrite the envelope.

## Candidate to evaluate

Propose an optional versioned `equivalenceDiagnostics` object with availability `complete|partial|unavailable`, `items`, omission counts when known and a bounded gap reason list. Each item has exact run-pair provenance, `resultSetOrdinal`, kind, nullable column ordinal, nullable row ordinal for ordered-only evidence. Kinds initially cover result-set added/removed, column added/removed/name/type change, known nullability change and ordered position/tail difference. Nullability diagnostics obey strict/default policy labels; their presence cannot change the verdict.

Evaluate caps of 16 emitted items, 64 result-set descriptors and 256 columns per described set, plus independent maxima of 32 KiB additional retained metadata per captured run and 256 KiB per comparison cell across all runs (share identical metadata). These are study candidates, not existing runtime limits. Record omitted descriptors before projecting. Evaluate fewer retained descriptors or deferral if these caps cannot support useful locations without unbounded names. Keep all original row fingerprints for comparison within current budgets; never truncate the authoritative comparison to satisfy diagnostic limits.

Start with schema/result-set localization in every mode and positional row localization only in `ordered`. For multiset/set row differences, return an explicit unsupported diagnostic gap while preserving existing counts. A row fingerprint can locate a different row, not a different cell; never invent a column location from a row hash.

## Tasks

### Task 1: Map evidence loss and freeze independent oracles

- [ ] Write `inventory.md` tracing reader -> accumulator -> comparer -> report -> artifact/projection, noting exactly which proposed fields require new capture metadata.
- [ ] Create `cases.json` with independent expected verdicts/locations for: identical; ordered reorder; duplicate multiplicity difference; changed name/type; strict-only nullability; unknown nullability; first/middle/last ordered difference; added/removed trailing rows; empty and added/removed result sets; unstable baseline; unstable candidate; `off`; legacy absent metadata; per-run and cell-wide caps exceeded; repeated identical schemas sharing metadata; escaped/multibyte names. Use explicit repetition and case IDs, including equal SQL hashes in different cases/artifacts.
- [ ] Verify: `Select-String -Path plans/117-evidence/inventory.md -Pattern 'capture|accumulator|comparer|artifact|projection'` -> all five stages documented; reopen source references.

### Task 2: Specify and exercise the bounded candidate

- [ ] Write `design.md` with exact DTO fields, count meanings, deterministic pair/item ordering, capture memory accounting, artifact compatibility, and minimum-budget fallback retaining identities or omitting the whole item with a truthful gap. Explain how per-set row ranges are captured; do not pretend current flattened fingerprints already retain them.
- [ ] Build `evaluate.ps1` over synthetic capture records only and produce `results.json`. Keep expected outcomes in the case data, separate from the projector. Serialize at 4,096/65,536 budgets; label estimates if not using production-compatible serialization.
- [ ] Implement `verify.ps1` asserting expected verdicts are unchanged, locations are from the named original run pair, no value/hash output, no positional items in unordered modes, explicit `off`/legacy gaps, exact known omission counts and bounded output/capture estimates.
- [ ] Verify: `pwsh -NoProfile -File plans/117-evidence/evaluate.ps1` then `pwsh -NoProfile -File plans/117-evidence/verify.ps1` -> exit 0; every named scenario has a result and independent assertion.

### Task 3: Decide scope and produce a future implementation map

- [ ] Write `decision.md` accepting a bounded subset, deferring or rejecting. Include deterministic examples and a matrix separating schema, ordered-row, unordered-row and raw-value diagnostics. Do not accept raw-value or unordered alignment as incidental scope.
- [ ] Name future characterization tests in `ResultEquivalenceTests.cs`/`CanonicalResultsTests.cs`, saved-artifact legacy/budget tests and presentation tests; preserve existing fixture conventions. Future commands: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CanonicalResultsTests|FullyQualifiedName~ArtifactReaderTests'`; MCP budget/schema checks; Windows `just verify` and branch formatting on a separately selected implementation.
- [ ] Verify study scripts, links, `git diff --check` and source immutability. Mark RESEARCH COMPLETE with explicit acceptance limits, never IMPLEMENTED.

## Done criteria

- [ ] Study verifier exits 0 and asserts every named case, including zero-row schema differences and same-side instability provenance.
- [ ] Design accounts for capture memory, repetition identity, old artifacts and output omissions independently of authoritative equivalence.
- [ ] Decision and a precise future file/test/contract map exist, or a documented no-go with reopening criteria exists.
- [ ] `git diff --name-only $studyStart -- src tests scripts` is empty; `git status --short` contains no unexpected new paths; docs links/whitespace pass.
- [ ] No claim of live correctness, deployed behavior or model-token savings; no product gate required for these notes/fixtures alone.

## STOP conditions and maintenance

Stop if locating a mismatch requires executing SQL again, keeping unbounded schema/rows, changing hash bytes/comparison semantics or guessing a missing identity. Missing legacy capture supports an unavailable result, not a backfill. A later implementation must preserve the resource-contract deduplication and cancellation behavior; source schema names remain readable but bounded. Use isolated branch `plan-117`; no merge/push/live actions.
