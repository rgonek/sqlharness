# Plan 114 whole-branch review

Base `9718b052f40335d3647cd96750d80431248f9140`; head `b3a934be`. Reviewed the Plan 114 contract, the supplied whole-branch diff, implementation/task reports, focused production paths, and tracked verification evidence. This was read-only review; the Windows gate and branch formatter were not rerun.

## Verdict

**Approve after documentation and evidence bookkeeping.** No blocking correctness, security, or compatibility finding in the implementation. The new `statement-summary` selector is opt-in, uses the existing owner-checked saved-artifact read, preserves missing CPU/elapsed as JSON null, and returns complete discovery identities without repetition, operators, SQL, or plan content. The old `statements` projection remains unchanged and has frozen JSON snapshots. Both fixed/request schemas and the mapper accept the new section. The result is subject to the existing bounded MCP envelope and response budget.

## Findings

1. **Minor — `docs/mcp/artifacts.md:14,18`:** The safe-section inventory at line 14 and `pageSize` inventory at line 18 still omit `statement-summary`, although the new subsection describes it. Add it to both lists, with the 128-record default, to keep the user-facing contract consistent.
2. **Minor — `plans/114-evidence/verification.md:9`:** Tracked evidence still says the explicit branch formatter is pending, while the implementation report and ledger record its completed exit-0 run over 12 changed C# files. Update the tracked evidence with the exact command, result, and tested commit before integration. The ledger's review-pending text also needs final bookkeeping after this report is accepted.
3. **Informational — `plans/114-evidence/verification/ui-check.log:29`:** The final UI check passes but emits Node `DEP0190` concerning child-process arguments with `shell: true`. This change does not touch the UI runner, and the warning alone is not evidence of a Plan 114 regression. Record it as an accepted, separately investigated tooling warning; do not label the UI log warning-free.

## Evidence and residual risk

The final tracked Windows `just verify` gate reports exit 0 across five stages, and the direct branch formatter is recorded as exit 0 in the ledger/report. The focused tests cover older missing metrics, ordered pages, null serialization, Unicode/escaped identities, minimum budget, whole-record omission, continuation, and unchanged `statements` JSON. The complete fixture read is 816 UTF-8 bytes/2 calls for `statement-summary` versus 2,019 bytes/2 calls for `statements`; this is fixture evidence, not a workload or tokenizer savings claim.

The byte-budget path skips an oversized record whole, increments omission, and advances the source cursor, avoiding a stalled page or partial identity. As with other saved-artifact sections, actual behavior depends on valid bounded artifact files and the existing MCP envelope projection. Linux, live database, and paid-agent behavior were outside this review and remain unverified. No merge, push, or live write occurred.
