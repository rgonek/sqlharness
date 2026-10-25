# Plan 118 final fix report

## Review findings resolved

The whole-branch review identified two Important findings in the offline evaluator:

1. Duplicate case pairing keys retained a first entry, allowing a specific paired parameter change or an unsupported case addition/removal. The evaluator now counts keys on both sides before building comparison maps and excludes every duplicated key from both maps. It emits `case-pairing-ambiguous` and continues to compare unrelated unique cases.
2. Duplicate parameter identities retained a first entry, allowing an arbitrary field change or an unsupported addition/removal. The evaluator now counts identities on both sides before building comparison maps and excludes every duplicated identity from both maps. It emits `identity-unresolved` and continues to compare unrelated unique parameters. This applies to top-level parameters and parameters in uniquely paired cases.

The seven new asymmetric fixtures cover duplicates on either side, single or absent counterparts, and valid unique differences alongside ambiguity gaps. The independent verifier asserts exact difference signatures, availability, and gaps for every case, plus explicit absence of claims for ambiguous parameter/case identities. The previous ambiguous-case fixture remains in place.

## Verification

- The new top-level duplicate oracle failed against the pre-fix evaluator with an unsupported `parameters.@dup.value` change.
- `pwsh -NoProfile -File plans/118-evidence/evaluate.ps1` — passed, 34 scenarios.
- `pwsh -NoProfile -File plans/118-evidence/verify.ps1` — passed, 34 exact-oracle scenarios; basis preservation, identity non-claims, output bounds, and value protection passed.
- Tested executable commit: `890053e25f7929b7d588b8cba9c586ada6cd67dc`.
- `results.json` records that commit, 34 scenarios, 9,742 aggregate explanation UTF-8 bytes, and a 3,709-byte maximum explanation.
- Raw command output: `plans/118-evidence/reviews/logs/final-fix-verification.log`.
- Scoped checks passed: `git diff --check` from FIX_BASE, 100 relative Markdown links across seven affected documents, and source immutability (`src`, `tests`, and `scripts` unchanged from FIX_BASE). Raw check output: `plans/118-evidence/reviews/logs/final-fix-scoped-checks.log`.

The change is limited to Plan 118 study artifacts and the Plan 118 index entry. No product source, tests, shared scripts, journal data, or runtime settings changed. No merge, push, live database action, or installation occurred. Final scoped Sol re-review remains pending.
