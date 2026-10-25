### Finding Verdicts

- **Unexplained measurement-fingerprint mismatch hidden by informational differences** — ADDRESSED. The evaluator now compares separate measurement fingerprints and adds `conditions-unavailable` when only informational differences accompany a mismatch (`plans/118-evidence/evaluate.ps1:131-136`). The new informational-only mismatch fixture expects the gap, while the comparison-mode fixture asserts equal measurement fingerprints (`plans/118-evidence/verify.ps1:24,35,70-71,89-90`).
- **Missing properties/collections and retained typed NULL not preserved** — NOT ADDRESSED. Actual absent properties now produce gaps, and retained null before-values are preserved (`plans/118-evidence/evaluate.ps1:25-38,53-55`; `plans/118-evidence/verify.ps1:74-80`). However, a present JSON null collection is still treated as empty: `Has-Property` is true for `parameters: null`, then `Compare-Parameters` filters it into an empty map and emits no gap (`plans/118-evidence/evaluate.ps1:40-45,109-111`). The new fixture checks absent versus empty, not null versus empty (`plans/118-evidence/verify.ps1:80-83`). Distinguish null collections from empty arrays and add that case.
- **Opaque case-pair key leaked through the output path** — ADDRESSED. Paired case fields now use the key-free `cases.parameters...` path (`plans/118-evidence/evaluate.ps1:46,128)); the paired fixture checks the opaque key is absent (`plans/118-evidence/verify.ps1:85-88)).
- **Independent oracle omitted expected values and exact overflow identities** — ADDRESSED. Difference signatures include before/after values and presence (`plans/118-evidence/verify.ps1:10-17,53-54)); overflow checks the exact `@p00`–`@p31` set, 16 omissions, and dropped values (`plans/118-evidence/verify.ps1:59-65)).

### New Breakage in the Fix Diff

- None beyond the still-open null-collection distinction above.
- Route deferral checked: the design explicitly defers cross-group explanations for both current paths until an exact pair is available, without API expansion (`plans/118-evidence/design.md:28-32)).
- The appended fix report lists evaluator, verifier, diff-check, and whitespace-scan results for 22 cases; I did not rerun them.

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** Findings remain open — the missing/empty/null input-state finding remains open for present JSON null collections.

**Spec compliance:** ❌ One input-state distinction remains collapsed.

**Task quality:** Needs fixes.
