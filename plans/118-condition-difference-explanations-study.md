# Plan 118: Specify evidence-backed explanations of measurement-condition differences

> **For agentic workers:** Use `superpowers:executing-plans`, or delegated execution when selected. This is a design/fixture study. Implement no product behavior, UI route, schema migration or new comparability policy.
>
> **Drift check:** `git diff --stat 5f7342a7..HEAD -- src/SqlHarness.Core/Journal/ConditionsModel.cs src/SqlHarness.Core/Journal/ConditionsModelBuilder.cs src/SqlHarness.Dashboard/Optimization src/SqlHarness.Dashboard/ui/src/components/optimization src/SqlHarness.Dashboard/ui/src/lib/optimizationMarkdown.ts`
> Reconcile compatible source drift. Stop for a changed fingerprint policy or authorization boundary that invalidates the study contract.

## Status

- **Status:** TODO; design study only; no implementation, live DB, merge or push.
- **Priority:** P2; selected direction proposal 3.
- **Effort / risk / confidence:** M / MED / HIGH grounding.
- **Depends on:** none of 116-120; implemented 079/080/087 supply conditions/grouping semantics.
- **Planned at:** `5f7342a7f5d48e03268491059949d1edfc24c65c`, 2026-10-25.
- **Goal:** Specify bounded explanations of known condition differences and evidence gaps without weakening metric comparison rules.
- **Architecture:** A read-only structural explanation over retained conditions, separate from existing grouping and verdict logic. Start on dashboard operation/group selection surfaces; do not add MCP journal access.
- **Tech stack:** C#/.NET 10 models, React/TypeScript dashboard conventions, PowerShell offline fixture evaluator.
- **Spec:** Candidate contract below; produce `118-evidence/design.md` as the self-contained design deliverable.

## Execution setup

Use an isolated `plan-118` checkout after inspecting `git status --short` and `git worktree list --porcelain`. Before changes run `$studyStart = (git rev-parse HEAD).Trim()` and retain the revision/initial status. Preserve unrelated edits; no automatic commit/merge/push. Apply `docs/agents/verification.md` scoped documentation/study checks. All experiment/verifier scripts must exit nonzero for missing evidence or failed assertions.

## Why this matters

The dashboard groups evidence by fingerprints and often gives only a generic incompatibility message. An operator must inspect separate operations to understand whether setup, cases, parameter metadata or unresolved identity caused it. Retained metadata can explain some differences; omitted sensitive values must remain unknown.

## Current state and conventions

- `src/SqlHarness.Core/Journal/ConditionsModel.cs:6-32` records engine/target, execution, setup, fixed parameters, cases, fingerprints and omission metadata. `ConditionsParameter` optionally stores values/source data; these may be absent by policy, not equal to NULL.
- The established measurement fallback at line 20 is:

```csharp
public string? MeasurementFingerprint { get; init; }
internal string EffectiveMeasurementFingerprint => MeasurementFingerprint ?? Fingerprint;
```

- `ConditionsModelBuilder.OperationMeasurementFingerprintPayload` at line 144 excludes repeat/timeout and comparison-only settings; reread the exact payload before classifying any field.
- `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:24-38` returns `ConditionsDiffer` for more than a known field difference, including unresolved version/scope/subject identity. Fingerprint inequality alone cannot identify which field changed.
- `src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs:113` defines `PairBasis`; the comparison record has no structured condition explanation.
- `src/SqlHarness.Dashboard/ui/src/lib/optimizationMarkdown.ts:3` says:

```typescript
export const conditionsDifferMessage = "conditions differ; SQL diff is the available comparison."
```

- `OptimizationSection.tsx:78` selects condition groups; `ComparisonPanel.tsx:108` displays the generic message. Model future tests after `tests/SqlHarness.Tests/Dashboard/OptimizationComparerTests.cs` and `ComparisonPanel.test.tsx` (typed payloads, `stubFetch`, `renderApp`, Testing Library).
- Plan 079/080 design anti-goals prohibit showing incompatible measurements as comparable. Plan 087 deliberately separates measurement fingerprints from comparison-only settings. Preserve both decisions.

## Global constraints

- Read-only current journal evidence; no added DB metadata capture, parameter retention, HMAC policy, fingerprint algorithm, authorization, default group or comparison-policy change.
- No inference of a hidden value from labels, SQL hashes, a whole-operation HMAC or another journal's keyed hash. Never introduce unkeyed parameter hashes.
- Unknown, omitted, typed NULL, absent JSON property and empty collection remain distinct inputs.
- UI wording must describe the operator's measurement, not serializer/schema implementation details. Reuse established Base UI/shadcn components if a later implementation is selected.
- Scope includes dashboard explanations and matching Markdown content only. Runtime policy remains in capabilities; tool arguments remain in schemas/descriptions; no duplication in bundled skills.

## Review focus

1. `ConditionsDiffer` can mean an identity cannot be resolved; do not invent a changed field.
2. Repeat/timeout/comparison-mode changes may be informational even when measurements share a group.
3. Missing values or truncated conditions never become evidence of equality or a claimed specific parameter-value difference.
4. Cases must be paired unambiguously; ordinal/order changes and renamed sets are not proof of matching values.
5. Legacy versions, incompatible keyed-hash domains and long names require explicit unavailable/omitted explanations.

## Scope

**Only writable:** this plan/index section and `plans/118-evidence/` with `inventory.md`, `design.md`, `cases.json`, `evaluate.ps1`, `verify.ps1`, `results.json`, `decision.md`, logs. All Core, journal, dashboard source/tests, user data and runtime settings are read-only.

## Candidate contract to evaluate

Specify an optional `conditionExplanation` on future dashboard comparison/detail payloads with `availability: complete|partial|unavailable`, `differences`, `gaps` and `omittedDifferences` when known. A difference has `field`, optional exact retained case/parameter identity, `kind: changed|added|removed`, `relation: measurement-condition|informational`, and bounded before/after display values only when actually retained and allowed. A gap has a stable code, not an invented value. Candidate limits: 32 difference items, 16 gap items, 8 KiB UTF-8 per explanation; overflow produces truthful omissions. These are study values, not runtime policy changes.

Evaluate explicit reasons `identity-unresolved`, `conditions-unavailable`, `field-omitted`, `case-pairing-ambiguous`, `fingerprint-domain-unverified` and `legacy-model-unsupported`. A fingerprint mismatch with no provable field change gets a gap. Known structural differences can still be shown alongside gaps, but the availability is partial.

Show repeat, timeout and comparison-only differences as informational unless current source proves they affect the selected measurement key. Do not claim a parameter value changed just because a combined fingerprint differs. Equal current measurement fingerprints can establish the existing grouping outcome; they do not disclose hidden values or unchanged database data. Preserve existing outcomes even if the explanation fails.

## Tasks

### Task 1: Reconstruct the authoritative field semantics

- [ ] Write `inventory.md`: table of retained fields, fingerprint participation, sensitivity/omission rules, matching identities and current generic-message branches. Copy short excerpts from the exact current payload builders.
- [ ] Verify: `Select-String -Path plans/118-evidence/inventory.md -Pattern 'MeasurementFingerprint|Repeat|Timeout|CompareNullability|identity|omitted'` -> all six topics have an evidence-backed disposition. Reopen each cited source location.

### Task 2: Freeze examples and evaluate the explanation contract

- [ ] Write `design.md` with exact field/gap contracts, deterministic ordering, grouping-versus-explanation separation, max-size policy, UI wording and future route/component/file map. Give both a group-selector explanation path and an operation-pair path; if current routes cannot expose the former safely, explicitly defer that surface instead of expanding API scope.
- [ ] Create `cases.json` for: equal conditions; changed setup hash; changed effective type; added/removed case; informational repeat/timeout; changed comparison mode only; hidden values with mismatching combined fingerprints; unavailable old model; omitted long fields; ambiguous case rename/order; cross-journal fingerprints without a verified common key domain; unresolved subject/scope; identical SQL with different cases; typed NULL versus absent value; enough multibyte differences to exceed caps.
- [ ] Implement the standalone evaluator over synthetic retained-condition records. Its inputs include existing authoritative basis/grouping outcome as an immutable fixture field. Return explanatory output only; never recompute or override the verdict.
- [ ] Implement `verify.ps1` with independent expected categories/availability. Assert the input basis/verdict is unchanged, informational fields do not become incompatibilities, hidden values are absent, ambiguous pairing produces a gap and budget overflow preserves item identities/omissions.
- [ ] Verify: `pwsh -NoProfile -File plans/118-evidence/evaluate.ps1` and `pwsh -NoProfile -File plans/118-evidence/verify.ps1` -> exit 0; every named case checked. Write `results.json` with serializer, revision, byte counts and known limits.

### Task 3: Decide and map future verification

- [ ] Write `decision.md`: accept the bounded explanation subset, defer or reject, with demand assumptions, costs and unanswered questions. A design that changes comparability or requires recovering unstored values is a no-go.
- [ ] Future implementation tests must pin comparer output invariance, legacy gaps, stable case identity, sensitive-off payloads, UI/Markdown parity and overflow. Commands: `dotnet test tests/SqlHarness.Tests --filter 'FullyQualifiedName~OptimizationComparerTests|FullyQualifiedName~OptimizationSummaryBuilderTests'`; `npm --prefix src/SqlHarness.Dashboard/ui run test -- src/components/optimization/ComparisonPanel.test.tsx`; then `just verify` and explicit branch C# formatting on separately selected product work.
- [ ] Run study verification, Markdown link/whitespace checks and source immutability check; update the index to RESEARCH COMPLETE with the decision.

## Done criteria

- [ ] `pwsh -NoProfile -File plans/118-evidence/verify.ps1` exits 0 with one independently asserted result per named scenario.
- [ ] Design and decision explicitly separate a known change, informative difference, identity failure and unavailable evidence.
- [ ] Output has a bounded item/byte policy and tests for escaped/multibyte names, with no unstored-value recovery.
- [ ] `git diff --name-only $studyStart -- src tests scripts` is empty; `git status --short` shows no unexpected new files; links and `git diff --check` pass.
- [ ] Study outputs do not claim production UI, live comparability or product verification. Notes-only work uses scoped documentation checks.

## STOP conditions and maintenance

Stop for requests to change keys/grouping, inspect real journals/transcripts, gather new server metadata, add sensitive storage or permit comparison overrides. A missing retained field is an expected gap, not a reason to query the DB. Future condition-model changes must update the classification table and fixture matrix; never let explanation code become a second policy engine. Use isolated `plan-118`; no merge, push or publication.
