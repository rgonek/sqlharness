# Plan 118 whole-branch review

Range: `7c1b2fbf..52b71b05`. Scope: research artifacts only. The archived task reviews are historical context; this verdict covers the complete study. I read the supplied full-range diff once, then inspected the current design, inventory, decision, fixtures, evaluator and verifier. I did not rerun the recorded 27-case verification.

### Strengths

- The inventory ties the field classifications to the current fingerprint builders and distinguishes measurement from regular fingerprints, sensitive omissions, and identity failures.
- The decision keeps the accepted subset downstream of the existing comparer and defers both cross-group UI paths until an exact operation pair is available.
- The independent oracle checks exact difference signatures, gaps, basis preservation, hidden-value non-disclosure and UTF-8 bounds across 27 synthetic scenarios.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

1. **Ambiguous case keys can still produce a specific change.** `plans/118-evidence/evaluate.ps1:123-135`: a duplicate `pairKey` adds `case-pairing-ambiguous` but leaves the first case in the map. If one side has two records with the same key and the other side has one, the loop pairs the first records and can report a parameter change or case addition/removal. The design says pairing must be verified and unambiguous. Exclude every duplicated key from both maps before comparing, and add an asymmetric duplicate fixture whose parameter values differ. Assert that it emits a gap and no claim for the ambiguous key.

2. **Ambiguous parameter identities can still produce a specific change.** `plans/118-evidence/evaluate.ps1:48-60`: the same first-entry retention occurs for duplicate parameter identities. With duplicates on one side and a single record on the other, the evaluator can report a field change from an arbitrary first record; with one side lacking that identity, it can report an unsupported added/removed parameter. Exclude duplicate identities from comparison and add an asymmetric duplicate fixture with an exact oracle assertion. This applies to fixed and paired-case parameters.

#### Minor (Nice to Have)

None.

### Recommendations

Keep the design's explicit pairing rule and expand the oracle to cover asymmetric duplicates. The current symmetric duplicate-case fixture only proves that a gap is emitted, not that no arbitrary pair is compared.

### Declined to judge

- Production UI behavior and payload transport: the plan authorizes an offline study only, and the decision explicitly defers product implementation.
- Live journal prevalence and cross-domain key compatibility: no live/user data access was authorized; the study records these as unanswered questions.
- Historical task review packages: they document earlier review rounds and are not new product requirements.

### Assessment

**Ready to merge?** No; needs fixes to the offline evaluator and oracle.

**Reasoning:** The study is well bounded and its main decision is supported, but the evaluator can turn ambiguous retained identities into specific field claims. That contradicts a central evidence rule and is not covered by the current 27-case oracle.
