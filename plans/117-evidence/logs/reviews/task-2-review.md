### Spec Compliance

- ❌ Issues found: byte-budget rejection leaves known omitted result-set descriptors out of `omissionCounts.resultSetDescriptors`; unstable-side pairing can suppress baseline/candidate localization while leaving diagnostics marked complete; the verifier does not assert 4 KiB projection identity, whole-item omission, or truthful response-budget gap.
- ⚠️ Cannot verify from this offline study: compact-JSON byte estimates do not establish production serializer size or managed heap use. The report and design label this limitation correctly; treat the caps as study candidates only.

### Strengths

- `evaluate.ps1` derives verdicts and diagnostics from synthetic input and does not read expected outcomes; `verify.ps1` checks the separate oracle and allowlists output fields, so source values and hashes are not projected. [evaluate.ps1:14-16, 227-278] [verify.ps1:35-54]
- Full-input equivalence remains independent of sidecar caps, ordered locations are emitted only in ordered mode, and multiset/set row mismatches receive explicit unsupported-location gaps. [evaluate.ps1:80-92, 181-203]
- Pair items carry artifact, case, cell, side, and repetition identities; the accepted fixture repair for scenario 020 now models 64 matching baseline sets against 65 candidate sets, preserving the expected added set at ordinal 64.
- The 4 KiB projection code removes whole trailing items and adds an omission count and gap; it preserves the outer identity and has a whole-diagnostics omission marker if even the empty-item envelope does not fit. [evaluate.ps1:206-224]
- The 140-byte-name and repeated-run fixtures exercise per-run and cell metadata budgets. The report clearly limits their capture measurements to compact UTF-8 JSON estimates.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- [evaluate.ps1:172-203] When either side is unstable, `pairs` contains only same-side instability pair(s); the baseline/candidate pair is added only if there are no such pairs. Thus an unstable case that also differs across baseline and candidate produces no cross-side schema/row locations, and those unprojected events are not counted as omissions. With no other gap or omission, availability can still be `complete`. Emit the ordinary cross-side pair as well, or explicitly mark/count the skipped cross-side evidence. The current 016/017 fixtures isolate side instability and do not cover this combination.
- [evaluate.ps1:137-155] On run- or cell-metadata-byte rejection, the code records omitted columns and excess bytes but does not add the rejected set descriptors to `omitted_set_keys`. For scenario 021 one known set descriptor is omitted; for scenario 022 five unique candidate set descriptors are rejected by the cell cap. Both results currently report zero omitted set descriptors. Count these identities before projection and add exact expectations for them in `cases.json`.
- [verify.ps1:103-106] The budget-view assertions only check allowed budget values, serialized byte bounds, item upper bounds, and nonnegative omissions. They do not check that the 4 KiB projection retains outer identity, removes whole items, reports the exact known response-budget omission count, or sets the truthful gap (and they cannot inspect a whole-diagnostics omission marker). Add an independent assertion for the projected envelope, including scenario 029’s 4 KiB and 65,536-byte omission counts, or expose enough projected data for the verifier to validate those properties.

#### Minor (Nice to Have)

- [evaluate.ps1:32-61] Generated large fixtures are selected by case ID and hard-coded dimensions; the accompanying `generatedInput` recipe fields are not consumed. This does not mix expected outcomes into the evaluator, but the recipe can drift from the actual synthetic input. Generate from the recipe fields or label them as documentation-only.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The study has a sound offline oracle boundary and its core candidate logic preserves authoritative verdicts under the modeled caps. The missing descriptor counts and suppressed cross-side pairs make completeness claims unreliable in important cases, while the verifier does not independently prove the minimum-budget fallback contract.
