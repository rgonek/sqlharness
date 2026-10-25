# Plan 117 Task 2 — bounded localization candidate

## Scope and oracle boundary

This is an offline candidate model. `evaluate.ps1` reads only the synthetic source facts in `cases.json` and deliberately ignores its `expected` member. It constructs result-set descriptors, row-position facts, and row comparisons from the synthetic captures and fixed large-fixture generators; it never invokes a database, runtime comparer, EXCEPT rerun, or historical artifact reconstruction. The `generatedInput` objects document generator dimensions but do not currently parameterize those generators; this recipe-to-generator drift risk is deferred for final study triage. `verify.ps1` is the separate oracle consumer: it compares the generated result to the reviewed expectations and checks the study invariants. Expected results are never imported into the evaluator.

## Candidate DTO

The optional top-level report property is `equivalenceDiagnostics`:

```json
{
  "version": 1,
  "availability": "complete | partial | unavailable",
  "items": [
    {
      "pair": {
        "left": { "artifactId": "…", "caseId": "…", "matrixCell": null, "side": "baseline", "repetition": 1 },
        "right": { "artifactId": "…", "caseId": "…", "matrixCell": null, "side": "candidate", "repetition": 1 }
      },
      "resultSetOrdinal": 0,
      "kind": "result-set-added | result-set-removed | column-added | column-removed | column-name-change | column-type-change | nullability-change | ordered-position-difference",
      "columnOrdinal": null,
      "rowOrdinal": null
    }
  ],
  "omissionCounts": { "items": 0, "resultSetDescriptors": 0, "columns": 0, "metadataBytes": 0 },
  "gapReasons": []
}
```

`policy` is present only on `nullability-change`, with `strict-only` or `default-informational`; nullability evidence cannot change a verdict. `rowOrdinal` is set only for `ordered-position-difference`, and is zero-based within its result set. Pair identities are exact original-run identities. Stability diagnostics pair repetitions on the same side; ordinary diagnostics pair the original baseline and candidate runs. No SQL hash, row hash, row value, or cell sample is in the DTO.

`omissionCounts.items` is the number of known diagnostic events not emitted by the item/response budget. `resultSetDescriptors` and `columns` count unique omitted descriptor identities (identical metadata shared by runs is counted once). `metadataBytes` records the exact positive UTF-8 compact-JSON byte excess for a rejected unique descriptor representation under this estimator. A run/cell metadata-byte miss defers schema item projection for that comparison cell and names the reason; the authoritative full-input verdict and ordered row comparison remain independent. `captureEstimate` in `results.json` is study bookkeeping, outside the proposed report DTO.

## Counts, order, and verdict independence

The oracle reproduces the source comparison rules from the inspected `ResultEquivalence.cs`: schema (set/column/name/type) and ordered rows contribute to the existing equivalence result; strict nullability only fails equivalence when all required nullability fingerprints are known and unequal/matched according to that policy; unknown strict nullability remains non-success. Ordered compares retain sequence, multiset compares multiplicity, and set compares distinct membership. `off` has no verdict. Partial/unavailable diagnostics never change these results.

Schema/result-set localization applies in active comparison modes `ordered`, `multiset`, and `set`; `off` is unavailable. Result sets are visited by ascending ordinal, columns by ascending ordinal, then rows by ascending set-local position. Within an ordinal, a name change, type change, and known nullability change are emitted in that order. Pair order is deterministic: the primary baseline/candidate pair first, then the baseline's first differing repetition pair, then the candidate's first differing repetition pair. This retains cross-side localization even when either side is unstable. Ordered row differences are emitted after schema differences, by set and row ordinal. Multiset/set row differences emit `row-difference-location-unsupported-for-<mode>` and no positional item. An opaque row fingerprint can identify a differing ordered position, not a differing column.

The item cap is 16. All known candidate events are counted before projection; excess events increment `omissionCounts.items`. The per-run capture limits are 64 set descriptors and 256 columns per described set. A descriptor omitted by those caps is counted before projection. Full synthetic input is still used for the authoritative verdict, so a diagnostic cap cannot turn a mismatch into a match or vice versa. The fixture models the existing 1,000,000 compared-row and 2,000,000 unique-fingerprint ceilings as pre-comparison failures, without allocating those rows/fingerprints.

## Capture memory accounting and fallback

The capture sidecar retains ordered result-set ordinals, per-set row counts/positions, and column ordinal/name/type/nullable facts. The base comparison still owns every authorized row fingerprint subject to its existing limits; diagnostics are a bounded sidecar and do not truncate or charge repeated duplicate fingerprints as new unique evidence. Identical complete descriptor representations are charged once per comparison cell, but each item's provenance still names its actual run pair.

During the original reader loop, capture must record each set's ordinal and its start/end offsets in the flattened ordered-fingerprint sequence, along with the set-local row count. A per-set row ordinal then maps to the corresponding original fingerprint offset. The current flattened fingerprints contain set ordinal in their hash input but do not retain a durable set boundary table, so the present artifacts cannot recover these ranges after the run; diagnostics cannot be reconstructed from historical hashes alone.

Candidate limits are 32 KiB per run and 256 KiB per cell. This offline evaluator counts UTF-8 bytes of compact JSON descriptor representations as a deterministic proxy. It is **not** a production serializer measurement or a managed-heap allocation estimate; object headers, string storage, collection capacity, and allocator behavior are excluded. A run exceeding its cap is omitted as a whole; a cell-cap overflow omits the new unique representation as a whole. Before dropping each rejected representation, the evaluator records unique set descriptor and column identities; identities already retained/shared are subtracted from omission counts. The exact descriptor omission count and compact-JSON byte excess are recorded. A 65th result set can still yield a count-only added/removed-set location because the original set count/ordinal is known, even when its descriptor payload is capped.

The serializer is exercised at 4,096 and 65,536 bytes using compact UTF-8 JSON. Each result retains the actual projected envelope so the independent verifier can inspect its outer identity and diagnostic fields. If an envelope exceeds budget, it removes whole trailing diagnostic items, adding each to the known output-budget omission count and setting availability to `partial`; if the empty-item envelope still exceeds budget, it omits the whole diagnostics object and returns an explicit `response-budget-diagnostics-omitted` marker plus the exact number of omitted diagnostic items. It never truncates an item or identity string. A long-identity fixture exercises the whole-object path at 4,096 bytes. These measurements describe only the synthetic DTO serializer, not the production MCP serializer or process memory.

## Legacy artifacts and gaps

`off` is `unavailable` with `comparison-off`. A legacy artifact without the optional field is `unavailable` with `diagnostic-metadata-absent`, never an empty complete success. Missing or unknown nullability yields `nullability-unknown`. Multiset/set row mismatch yields an explicit unsupported-location gap. Metadata/cap omissions are `partial` and carry exact counts where the input makes them knowable. Existing verdicts, stability, nullability policy, and authoritative count ceilings remain independent.

Artifact compatibility is additive and optional: the new report object is written/read when present and can pass through compare/matrix projections under their normal response budgets. Readers of older artifacts do not synthesize an empty object. Artifact ID plus case, matrix cell, side, and repetition remain the run identity; SQL hash is deliberately excluded from identity.
