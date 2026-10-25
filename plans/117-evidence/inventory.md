# Plan 117 Task 1 — evidence inventory

Status: source-grounded inventory and independent synthetic oracle fixtures. No product code or database was used.

## Reader → capture

`BenchmarkCollector.CollectCompareAsync` (`src/SqlHarness.Core/BenchmarkCollector.cs:12-58`) drains every ordinary result set. For each set it reads column ordinal, `GetName`, provider field type, and allow-null metadata; it separately asks `GetReportedAllowNull` to decide whether nullability is known. Values are normalized and sent to canonical, comparison, and optional raw accumulators. Showplan XML-shaped result sets are diverted to plan collection and do not enter result comparison. This is the earliest point at which diagnostic source facts are all available together.

Required new capture metadata: retain a bounded per-run ordered list of result-set descriptors, each with its ordinal, column ordinal/name/type, and nullability availability/value. Also retain result-set row boundaries/counts, including zero-row/zero-column sets, plus the association of each existing row fingerprint with its set-local zero-based position. Current comparison capture only appends flattened row fingerprints, so it cannot later distinguish an empty set from no set or recover descriptor details from a hash.

## Capture → accumulator

`CanonicalComparisonAccumulator` (`src/SqlHarness.Core/ResultEquivalence.cs:484-604`) numbers result sets, hashes schema JSON containing set ordinal and each column ordinal/name/type, and separately hashes nullability ordinals/allow-null values. A missing reported-nullability fact makes the whole run's nullability fingerprint unavailable. It stores row hashes in order; each hash includes the active result-set ordinal and canonical row values. It enforces `MaximumComparedRows = 1_000_000` and fails closed instead of truncating.

Loss point: completion returns only `CanonicalComparisonResult(SchemaHash, OrderedRows, NullabilityFingerprint)` (`ResultEquivalence.cs:43-49, 558-568`). Column names/types, the explicit descriptor lists, nullability-per-column, per-set row counts, and set-local row boundaries are not available after completion. Preserve full authoritative fingerprints and their existing 1,000,000-row limit; diagnostic metadata must be an additional bounded sidecar. Repeated identical runs should share the sidecar by complete metadata identity/content and must not be charged as new unique evidence, matching the comparer registry's existing deduplication principle.

## Accumulator → comparer

`ResultComparer.Compare` (`src/SqlHarness.Core/ResultEquivalence.cs:65-160`) keeps `Off` as no verdict; otherwise it deduplicates identical schema+ordered-row representations, with a 2,000,000 unique-fingerprint per-cell limit (`:53-58, 434-480`). Ordered compares preserve sequence and count positional differences; multiset compares frequencies; set compares membership (`:272-282, 302-350`). Schema equivalence compares opaque schema hashes; nullability is evaluated separately and only gates the verdict when strict comparison is enabled (`:140-160, 188-254`). Stability is per-side and compares repetitions to that side's first run (`:173-185`).

The report has aggregate verdict/count/stability/schema fields only (`ResultEquivalenceReport`, `:15-26`). It does not identify which run pair or set/column/position caused a difference. Candidate diagnostics therefore need exact two-run provenance, including artifact, case/set/matrix cell, side, and repetition; baseline instability can use a same-side pair. For ordered mode, the first unequal set-local position and unequal tail positions can be located from retained ordered fingerprints. For multiset/set row changes, fingerprints establish row membership/count but cannot identify a cell or ordered position; emit an explicit unsupported gap. Never infer a column from a row hash.

## Comparer → report

`SqlHarnessCompareReport` carries `ResultEquivalenceReport` as its equivalence summary (`src/SqlHarness.Core/Artifacts.cs:268-289`). `BenchmarkSummary` and `AgentBenchmarkOutput` carry the same result object into compare and matrix details (`src/SqlHarness.Core/BenchmarkSummary.cs:52-70, 158-174`; `src/SqlHarness.Core/AgentBenchmarkOutput.cs:56-70, 180-191, 225-241`). Existing report decisions must remain authoritative: added diagnostics are explanatory metadata and cannot turn a false/incomplete/off verdict into success or make the verdict partial.

## Report → artifact / reader

`CompareArtifactWriter.Write` serializes the report, projects statement evidence, and writes `runs.jsonl` with variant, optional parameter set, repetition, metrics, result hash and plan filenames (`src/SqlHarness.Core/Artifacts.cs:409-420, 543-590, 660-675`). It does not persist comparison canonical rows/schema descriptors. Run index provides storage uniqueness but is not sufficient diagnostic identity on its own; artifact ID plus case/set/matrix cell, side, and repetition must survive. Current `CompareRunArtifact` also has journal-only matrix-cell metadata that is not part of `RunMetadata` (`Artifacts.cs:360-395, 660-675`).

`ArtifactReader` opens saved files without a DB connection and returns bounded report/section data. Legacy artifacts without the new optional field must remain `unavailable`, never be interpreted as an empty complete diagnostic (`src/SqlHarness.Core/ArtifactReader.cs:339-367, 450-460`). Keep current 4,096-byte minimum MCP response, 65,536-byte default and finite process ceilings from the Task 1 constraints. Proposed diagnostics need separate item and metadata byte budgets, explicit omission counts when known, and bounded gap reasons.

## Artifact/report → agent projection

`AgentBenchmarkProjection.Compare` copies the equivalence object into `AgentCompareOutput`; `Matrix` copies each cell's equivalence into `AgentCompareDetail` (`src/SqlHarness.Core/AgentBenchmarkOutput.cs:180-191, 225-241`). `AgentOutputProjection` applies response budgeting after creating these projections (`src/SqlHarness.Core/AgentOutputProjection.cs:150-177, 369-391`). An optional versioned diagnostics property must pass through both ordinary compare and per-cell matrix projections and remain subject to the effective output budget. Omission in projection must be visible and must not alter equivalence. Dashboard presentation is a later integration concern, not captured by this Task 1 fixture schema.

## Study constraints carried into the oracle

- Keep `ordered`, `multiset`, `set`, `off`, strict/default nullability, instability, and verdict semantics unchanged.
- Maximum compared rows remain 1,000,000; unique fingerprint budget remains 2,000,000 per cell. Do not charge repeated identical representations as unique evidence.
- Compare provenance is run-pair exact. An unstable baseline diagnostic may pair baseline repetitions from the same side.
- No database calls, EXCEPT reruns, mode relaxation, raw cell samples, row-hash publication, or historical reconstruction from insufficient artifacts.
- Candidate diagnostic caps are 16 emitted items; 64 result-set descriptors; 256 columns per described set; 32 KiB retained metadata per run; 256 KiB per comparison cell across runs with identical metadata shared. These are study candidates, not current runtime limits. Record omitted descriptors before projection and explore lower caps/deferral if useful locations cannot fit.
- Schema/result-set localization applies in active comparison modes `ordered`, `multiset`, and `set`; `off` remains unavailable. Positional row localization applies only to ordered. Multiset/set row differences use an explicit unsupported gap. A row fingerprint cannot identify a different column.

## Independent oracle boundary cases

`cases.json` keeps the modes separate: multiset duplicate removal is a mismatch, while set mode ignores duplicate multiplicity and remains equivalent when distinct row membership matches. It also includes an equivalent verdict with partial diagnostic availability: identical 257-column schemas exceed the 256-column descriptor cap by exactly one column, while the authoritative schema comparison still sees the full schema hash.

The cap fixtures isolate each proposed boundary: 65 result sets against the 64-descriptor cap; a single 256-column run whose names exceed 32 KiB; a cell-wide metadata overflow using individually bounded runs; 257 columns against the 256-column cap; and 18 known name changes against the 16-item emission cap, yielding 16 items plus 2 known omissions. These diagnostic caps do not truncate the current 1,000,000-row capture or 2,000,000 unique-fingerprint comparison budgets; separate fail-closed resource fixtures cover those existing limits. All expected verdicts are independent source-derived oracles, not runtime test outputs.

## Checks performed

- Inspected the source stages listed above in the assigned worktree; source references were reopened after writing this inventory.
- `Select-String -Path plans/117-evidence/inventory.md -Pattern 'capture|accumulator|comparer|artifact|projection'` matched all five required stages: **PASS**.
- Parsed `plans/117-evidence/cases.json` as JSON with `ConvertFrom-Json -ErrorAction Stop`: **PASS**.
- No product tests were run; this task requests a synthetic oracle, not runtime implementation.

## Self-review and concerns

- The existing API has no durable per-result-set descriptors. Useful schema locations require preserving original names/types and set boundaries at capture time; schema hashes alone cannot reconstruct them.
- The existing artifact run record omits case/matrix-cell identity in `runs.jsonl`. Any future persisted item must be linked to exact artifact/cell/run provenance, not just `Variant` or SQL hash.
- Large/multibyte names can exhaust candidate metadata budgets before descriptor-count caps. Fixture expectations mark metadata partial with omissions rather than silently dropping evidence.
- The candidate 16-item emission cap can omit additional known differences; omissions must be represented independently from evidence availability and verdict.
