### Finding Verdicts

- **Present JSON null collections collapsed into empty arrays** — ADDRESSED. `Has-UsableCollection` now requires the property to exist, be non-null, and be an array; otherwise it adds `field-omitted` and skips comparison (`plans/118-evidence/evaluate.ps1:11-16`). The guard is used for top-level parameter/case collections and matched-case parameter collections (`evaluate.ps1:115-137`). Five new cases cover null parameter, case, and paired-case parameter collections against empty/nonempty arrays; the independent oracle expects a gap and zero differences for each (`plans/118-evidence/verify.ps1:40-44,87-92`).

### New Breakage in the Fix Diff

None found in the scoped null-collection changes.

- The appended report lists evaluator and verifier success for 27 cases, plus `git diff --check` and whitespace-scan results. I did not rerun them.

### Out-of-Scope Observations

None.

### Verdict

**Fix round:** All scoped findings addressed; no new Critical or Important breakage found.

**Spec compliance:** ✅ The null, absent, and empty collection states now remain distinct in the evaluator.

**Task quality:** Approved.
