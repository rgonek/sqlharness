# Task 2 scoped rereview — round 1 fixes

Reviewed the appended `task-2-report.md` and supplied `review-2bdaaa5e..4a57d70e.diff` against findings 1–5 in `task-2-review.md`. Scope was limited to those fixes and possible breakage in the fix diff. No tests or reported checks were rerun; no Git diff was regenerated.

## Finding dispositions

1. **ADDRESSED — source manifest byte cap** (`plans/120-evidence/evaluate.ps1:322-326`; `plans/120-evidence/verify.ps1:163-164`; `plans/120-evidence/design.md:5`). The evaluator appends whitespace to the actual synthetic manifest for the oversized case, measures its file length before `Read-JsonFile`, rejects above 1 MiB, and records the measured byte count. The verifier asserts the recorded source size exceeds the cap.

2. **ADDRESSED — strict `compareNullability` type** (`plans/120-evidence/evaluate.ps1:127`; `plans/120-evidence/verify.ps1:145`). The evaluator now requires a Boolean. The new string-`"false"` case is listed independently and asserts preflight rejection with zero dispatches.

3. **ADDRESSED — honest stop outcome metadata** (`plans/120-evidence/evaluate.ps1:218,231-236,250-253`; `plans/120-evidence/verify.ps1:124-132`). Stop injections set comparison to `not-run`, equivalence to `not-evaluated`, performance to `not-measured`, and coverage to 0/3. The verifier checks these on the stopped entry and confirms the earlier completed entry retains completed comparison semantics and 3/3 coverage.

4. **ADDRESSED — Ticket B date input assertions** (`plans/120-evidence/verify.ps1:82-101`). A dedicated dispatch case checks the recorded CLI fixed input and exact parameter name/type/value. The verifier also independently checks both relevant Ticket B manifest entries for `boundaryDate` / `date` / `2024-02-29`.

5. **ADDRESSED — balanced schema example** (`plans/120-evidence/design.md:22-23`). The input object now closes before the top-level object. The appended report records a successful JSON parse check for the example.

## New breakage and overall verdict

No new breakage was found in the supplied fix diff. Its changes remain within the study evidence files and preserve the mock-only dispatch boundary.

- **Spec: PASS** for findings 1–5.
- **Quality: APPROVED** for this scoped rereview.

The report's evaluator/verifier and JSON parse results remain implementer-reported; I did not rerun them, per instruction. The previously documented lack of an executed OS symlink proof remains unchanged and explicitly disclosed.
