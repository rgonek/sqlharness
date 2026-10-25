# Plan 117 decision — bounded equivalence localization

## Disposition

Accept a bounded, optional, versioned diagnostic for the portion of equivalence evidence already observed during the original executions. A separately selected implementation may localize result-set and schema differences in active comparison modes `ordered`, `multiset`, and `set`, and positional row differences only in `ordered` mode; `off` remains unavailable. Existing verdicts, stability, nullability policy, regression decisions, and full comparison budgets remain authoritative and unchanged. A missing diagnostic is not a successful empty diagnostic; partial or unavailable evidence never turns an equivalence failure into success.

This is a research disposition, not product implementation approval. The candidate DTO and limits in `design.md` are implementation inputs, not measured production guarantees. In particular, the compact UTF-8 JSON byte estimator does not measure production serialization size, managed heap use, or process memory. A future implementation must measure capture and projection costs and may lower or revise these candidate caps before exposing the feature.

## Accepted and excluded diagnostic scope

| Evidence class | Decision | Required behavior |
|---|---|---|
| Result-set and schema | Accept | Identify added/removed result sets, columns, names, types, and known nullability changes from original run metadata. Work without rows, including empty result sets. Preserve exact provenance for both compared runs. |
| Ordered rows | Accept | Identify a differing position or added/removed tail position from captured ordered fingerprints, with a zero-based set-local row ordinal. Capture set boundaries during the original reader pass. |
| Unordered rows (`multiset`/`set`) | Defer positional localization | Preserve existing directional counts and verdict. Emit an explicit unsupported-location gap for row-only mismatch; do not align rows or infer positions from fingerprints. Schema locations remain eligible. |
| Raw values / changed cells | Reject | Do not retain or emit row values, cell samples, SQL text, or row hashes. A row fingerprint identifies a differing row position in ordered mode, not the changed column or cell. Reopen only with a separate privacy/resource design and explicit product selection. |

Known nullability differences may be described with strict-only or default-informational policy labels, but that item cannot alter the existing mode-specific verdict. Unavailable provider nullability remains unknown rather than being guessed.

## Deterministic examples from the independent oracle

- `eq-column-name-change-004` and `eq-column-type-change-005` localize schema differences while preserving the existing false verdict.
- `eq-added-empty-result-set-014` and `eq-removed-empty-result-set-015` show that result-set changes can be located without any row to fingerprint.
- `eq-ordered-middle-difference-009` reports the set-local ordered position; the first/last and added/removed-tail cases separately exercise boundaries.
- `eq-duplicate-multiplicity-003` preserves multiset counts and reports a positional-location gap. `eq-set-duplicate-insensitive-027` stays equivalent when multiplicity differs but distinct membership is unchanged.
- `eq-column-cap-overflow-equivalent-028` remains equivalent with partial diagnostics, showing that a descriptor omission is independent of the verdict. `eq-item-cap-overflow-known-omissions-029` retains the first 16 of 18 known items and reports two known item omissions.
- `eq-unstable-and-cross-side-difference-030` retains both the ordinary baseline/candidate location and same-side instability provenance. Pair identity includes artifact, case, matrix cell, side, and repetition; the shared SQL hash is not identity.
- `eq-legacy-absent-metadata-019` and `eq-off-018` are explicitly unavailable. Neither is represented as a complete empty result.

These examples are deterministic synthetic oracle outcomes, not runtime test results or evidence of live behavior. The 31 cases and the review reports are archived with the evidence. The generated large-fixture dimensions remain hardcoded in the evaluator while `generatedInput` is documentation-only; this review-approved Minor means the recipe can drift from its actual fixture. The fixture runner must not be described as recipe-driven or general-purpose.

## Candidate limits and fallback

The study exercised candidates of 16 emitted items, 64 result-set descriptors, 256 columns per described set, 32 KiB additional retained metadata per run, and 256 KiB per comparison cell with identical metadata shared. These figures are not approved production resource budgets until capture allocations and the production serializer are measured. If measurements or representative names show poor utility, prefer smaller explicit descriptor budgets or a clear deferred/unavailable result; never retain unbounded schema text.

Record known descriptor and item omissions before projection. Response-budget projection removes whole items, preserves their original pair identity, records exact known omissions, and supplies a truthful gap. If even the empty diagnostic envelope cannot fit the minimum 4,096-byte MCP response, omit the whole diagnostic object and leave a truthful outer marker. Keep the existing 65,536-byte default and finite process ceilings. Do not truncate identities or items to make them fit.

Keep all original row fingerprints within the current 1,000,000 compared-row and 2,000,000 unique-fingerprint-per-cell limits. The diagnostic sidecar does not consume the unique-fingerprint budget again for repeated identical representations and never reduces authoritative comparison evidence to meet its own caps. Capture set-local row ranges at the original reader boundary; historical flattened hashes or legacy artifacts cannot reconstruct missing metadata.

## Future implementation map

This map is a proposal for a separately selected implementation. No source or test file is changed by this study.

| Area | Future files / seam | Characterization and contract coverage |
|---|---|---|
| Capture and comparison | `src/SqlHarness.Core/BenchmarkCollector.cs`, `CanonicalResults.cs`, `ResultEquivalence.cs`, and the call path through `CompareCellRunner.cs` | Extend `tests/SqlHarness.Tests/ResultEquivalenceTests.cs` with `Schema_diagnostics_cover_empty_and_multiple_result_sets`, `Ordered_diagnostics_report_first_middle_last_and_tail_positions`, `Unordered_row_differences_report_gap_without_alignment`, `Diagnostic_pairs_preserve_cross_side_and_same_side_repetition_identity`, and `Partial_diagnostics_do_not_change_verdict_or_fingerprint_budgets`. Extend `CanonicalResultsTests.cs` with `Capture_preserves_bounded_set_ranges_and_schema_metadata`, `Shared_metadata_is_charged_once_per_cell`, and `Capture_limits_preserve_existing_row_and_fingerprint_rejections`. Use the existing accumulator and explicit-column/duplicate-row fixture conventions. |
| Report, saved artifacts, and old artifacts | Compare report DTOs and `src/SqlHarness.Core/BenchmarkSummary.cs`, `Artifacts.cs`, `ArtifactReader.cs` | Add report/writer-reader coverage for additive versioned serialization, `Legacy_artifact_without_diagnostics_is_unavailable`, `Saved_diagnostics_budget_omits_whole_items_and_preserves_identity`, and `Saved_diagnostics_minimum_budget_omits_whole_object_with_gap`. Keep saved-artifact fixtures in the existing artifact test conventions; do not backfill from hashes. |
| Agent projection and MCP interface | `src/SqlHarness.Core/AgentBenchmarkOutput.cs`, `AgentOutputProjection.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` and the applicable MCP schema/capability contract | Add schema/property tests for compare and matrix-cell passthrough, budget omission counts, 4,096-byte minimum response, 65,536-byte default, and omission of the whole diagnostic object when required. Update interface descriptions/schema/capabilities with availability, gaps, provenance, and no-value guarantees. |
| Dashboard presentation | `src/SqlHarness.Dashboard/ui/src/api/types.ts` (`OperationDetail.summary` and per-cell `compareCases`); `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.tsx` (`compareCards` selects summary versus matrix-cell equivalence and renders the per-case view); `src/SqlHarness.Dashboard/ui/src/lib/compare.ts` (`equivalenceLabel` keeps the verdict label separate). Existing presentation tests: `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.test.tsx` and `src/SqlHarness.Dashboard/ui/src/lib/compare.test.ts`. | Add `CompareDetail.test.tsx` cases `renders complete schema and ordered-position diagnostics with pair provenance`, `renders partial and unavailable diagnostic gaps without changing the equivalence verdict`, `shows unordered-location gaps without positional claims`, and `keeps matrix-cell diagnostics scoped to the selected case`. Add `compare.test.ts` case `labels diagnostic availability independently from equivalence`. Follow the existing `operationDetail` fixtures and Testing Library render helpers in `CompareDetail.test.tsx`, and the direct label assertions in `compare.test.ts`. Preserve the verdict and original run-pair/location; do not present row positions as changed cells or infer success from absent items. |

The suggested focused characterization command is:

```powershell
dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CanonicalResultsTests|FullyQualifiedName~ArtifactReaderTests'
```

Then run the MCP budget/schema checks and the Windows `just verify` gate plus branch formatting against the separately selected implementation base. Those product checks were not run for this offline study. Test names above are proposed future coverage, not tests that exist or passed today.

## Reopening criteria and limits

Reopen the design if production measurements cannot preserve useful source names/identities within finite per-run, cell, and response budgets; if capture cannot retain result-set boundaries without changing cancellation/resource contracts; if any diagnostic requires an additional SQL execution; or if unordered alignment or raw-value/cell localization is requested. Such scope requires a new decision and explicit resource/privacy analysis.

No database calls, EXCEPT reruns, source changes, implementation tests, deployment, live correctness, model-token savings, merge, or push were part of this study. Plan 117 is **RESEARCH COMPLETE** only. All task reviews and the Sol whole-study review are approved; final scoped Sol closeout re-review is approved.
