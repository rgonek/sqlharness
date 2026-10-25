# Task 2 review — safe synthetic dispatch study

Reviewed artifact: `.superpowers/sdd/120-repeatable-experiment-suites-study/review-f5a21880..2bdaaa5e.diff` (commit `2bdaaa5e`), against `task-2-brief.md`, `context.md`, and `task-2-report.md`. Scope was the supplied diff only, with the named existing status contract consulted for the outcome-state concern. No tracked files were changed.

## Verdict

- **Spec: ISSUES**
- **Quality: NEEDS FIXES**

The study stays within the synthetic evidence scope, covers the required scenario families, and does not add or alter a product surface. The design records the A/B/C decision matrix, explicit target rule, all-suite preflight, immediate per-entry hash recheck, and non-atomicity boundary. It correctly labels unavailable OS symlink proof as simulated and limits the parameter-reader grammar claim. The findings below keep the recorded implementation and evidence from overstating the contract.

## Findings

1. **[P2] The 1 MiB cap is not applied to the source manifest bytes — `plans/120-evidence/evaluate.ps1:303-314`.** `Read-JsonFile` loads the complete manifest before the size check, and the check measures a `ConvertTo-Json` reserialization of the parsed object. A source manifest padded with whitespace can exceed 1 MiB while normalizing below the limit; parsing it also allocates before the bound is enforced. The `invalid-manifest-size` case adds an in-memory `padding` property (`evaluate.ps1:252`), so it only proves the normalized-object check. Check the original manifest file length before parsing and make the case exceed that on-disk byte length.

2. **[P2] Nullability policy values are coerced instead of validated — `plans/120-evidence/evaluate.ps1:116-127,204`.** `Test-EntryShape` does not require `compareNullability` to be a JSON boolean. A manifest value of the string `"false"` passes for an `ordered` comparison and is later cast to `$true` in the invocation record. This silently changes a comparison policy. Reject non-boolean values before dispatch; add a malformed-type case and independent assertion.

3. **[P2] Stop cases publish completed comparison metadata for failed entries — `plans/120-evidence/evaluate.ps1:216-221,238-241`; verifier `verify.ps1:99-103`.** Each injected error, timeout, cancellation, or unknown completion changes only `execution` and `stopReason` on Ticket A's ordinary completed outcome. The supplied results therefore show entry 2 as `error`/`timeout`/`cancelled`/`unknown` while retaining `equivalence=regression`, `performance=inconclusive`, and 3/3 completed coverage. The verifier checks the execution and stop status but not the failed entry's equivalence, performance, or coverage. Make the injected partial outcome internally explicit and assert those statuses, so the study does not present a failed entry as a fully covered comparison without evidence that those measurements completed.

4. **[P2] The independent verifier does not assert the changed Ticket B date input — `plans/120-evidence/verify.ps1:59-80,134-137`.** The generator changes Ticket B's `date-boundary` and `changed-revision` inputs to fixed typed scalar data (`boundaryDate`, type `date`, value `2024-02-29`). No dispatched result includes `date-boundary`: the valid-negative case selects only `duplicate-counts`, and the stale-revision case fails preflight. The verifier checks Ticket A values and the duplicate-count negative, but never pins this changed Ticket B type/value. Add an independent assertion for the generated manifest/input so this input-semantics change is verified rather than only documented.

5. **[P3] The candidate manifest example is invalid JSON — `plans/120-evidence/design.md:22-23`.** The `input` object opened on line 22 is not closed before the top-level object closes on line 23. Add the missing brace so the example can be copied and parsed as the exact candidate schema.

## Cannot verify

- Per instruction, I did not rerun `evaluate.ps1`, `verify.ps1`, the fixture generator, or any product gate. The task report records successful script exits and `git diff --check`; those remain implementer-reported evidence in this review.
- The task report and `results.json` record that Windows denied test-symlink creation and that only simulated rejection was verified. This is allowed by the brief, but an executed filesystem symlink proof remains unavailable.
- Full current parameter-reader grammar parity and type resolution are explicitly deferred in the design and report; I did not treat those disclosed limits as defects in this synthetic study.
