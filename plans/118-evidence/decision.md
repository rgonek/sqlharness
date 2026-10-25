# Plan 118 decision: bounded condition explanations

## Decision

Accept the bounded, read-only explanation subset as a candidate for a separately selected product implementation. This is a research decision only. It does not authorize product edits, a payload or API change, installation, live journal inspection, or a production rollout. Any implementation must remain downstream of the existing comparer and leave its grouping, `PairBasis`, and verdict unchanged.

The study provides 27 synthetic scenarios and an independent verifier. It demonstrates that an explanation can distinguish a retained, known structural measurement-condition change from informational settings, identity failures, and unavailable evidence without deriving a second comparison policy. The candidate limits remain 32 differences, 16 gaps, and 8,192 UTF-8 bytes per explanation; these are study values that require product transport and usability review before adoption.

## Accepted behavior

- **Known measurement-condition change:** report only a field-level change supported by retained evidence. A setup hash mismatch alone remains opaque; retained setup text can be described when both values are available. Case and parameter changes require exact retained identities and an unambiguous verified pair. A fingerprint mismatch is not itself a changed field.
- **Informational change:** repeat, timeout, comparison mode, warmup, operation kind, and compare-nullability can be shown as informational according to the current source inventory. They do not alter grouping or establish measurement incompatibility. An unexplained measurement-fingerprint mismatch still produces `conditions-unavailable`, even alongside an informational difference.
- **Identity failure:** unresolved subject/scope/version identity produces an identity gap, not an invented field change. Missing, duplicate, or cross-domain pairing keys cannot be used to pair cases; array order, labels, ordinals, or renamed sets are not identity evidence. Opaque pairing keys never appear in output.
- **Unavailable evidence:** absent, null, unknown, omitted, legacy, or truncated retained fields remain distinct. Missing values and unusable collections produce gaps and never prove equality or additions/removals. A keyed fingerprint from an unverified domain cannot establish a shared identity. Never recover values from labels, SQL text hashes, keyed hashes, or whole-operation fingerprints.

Known changes may accompany evidence gaps, yielding partial availability. Explanations may display before/after values only when those exact values are retained and allowed. Overflow must report omissions truthfully while preserving the surviving item identities. Explanation generation failure or unavailability must not change the comparison result.

## Surface disposition

Defer cross-group explanations on both current dashboard paths. The group selector chooses versions within one selected conditions group and scope; the session and agent-session compare handlers resolve one group. The operation route starts from one compare operation and compares its baseline/candidate SQL variants. Neither route supplies an exact pair of operation condition records from different groups. A future product proposal must first identify an existing or separately selected interface that supplies both group identities and the exact operation pair. Do not expand an API or infer pairs from labels to make this study fit.

## Demand assumptions, costs, and open questions

The demand case assumes operators encounter generic condition-difference messages often enough that inspecting separate operations is a meaningful cost. The study did not measure recurrence, operator task time, support burden, or demand. Before implementation selection, gather that evidence through an authorized, privacy-preserving product process; this study itself adds no journal metadata or user-data inspection.

The main expected benefit is faster diagnosis when retained fields prove a difference. Costs include an optional payload contract, careful field-by-field classification maintenance, stable identity handling, duplicate UI and Markdown wording, and keeping explanation failure isolated from comparison behavior. The 8 KiB candidate limit constrains the explanation only; enclosing response size, browser rendering, production serialization cost, and usefulness of the chosen limits remain unmeasured.

Unanswered questions for a future implementation selection:

1. What observed frequency and operator task cost justify building the feature?
2. Which route can supply an exact pair of operation condition records across groups without changing comparison policy?
3. Are the proposed item and byte limits appropriate after measuring the actual response envelope and UI behavior?
4. What compatibility behavior should apply to the full set of retained journal model versions and fingerprint-key domains in supported deployments?
5. Which retained fields may be displayed under the product's current sensitivity and omission policy, and how should a future policy change update the classifier and fixtures?

## Future implementation verification map

These commands are required for a separately selected implementation; they were **not run** as part of this offline study:

```powershell
dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests'
npm --prefix src/SqlHarness.Dashboard/ui run test -- src/components/optimization/ComparisonPanel.test.tsx
just verify
pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef <selected-product-base>
```

The focused tests must pin comparer output and basis invariance, explicit legacy gaps, stable case identity and key non-disclosure, sensitive-values-off payloads, matching UI/Markdown wording, and deterministic item/UTF-8 overflow with truthful omissions. Then run the repository Windows gate and explicit branch C# formatting on the selected product branch. The study verifier is synthetic evidence only; it is not a substitute for these product checks or for live acceptance.

## Evidence boundary

The evaluator, fixtures, oracle, and measurements are offline synthetic evidence only. Results cover 27 named scenarios, 7,731 aggregate explanation UTF-8 bytes, and a 3,709-byte maximum explanation under the study serializer. They do not establish production serializer behavior, response-envelope size, browser rendering, cross-platform sort behavior, actual journal prevalence, live comparability, or product verification. Product source, tests, shared scripts, journal data, and runtime settings were not modified or inspected for this closeout.
