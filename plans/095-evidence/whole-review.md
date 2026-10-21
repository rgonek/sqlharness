# Plan 095 whole-branch review

Range: `384357c031a321ab2f32cf99f049b9c0bc4c71f2..f0f7d60`.
Review: independent, read-only source review; no suites rerun, no integration, publication or database access. Reviewed the authoritative Fix contract, verification evidence, SDD ledger, full source/test/docs diff and concrete surrounding reader, mapper, request dispatch and response-adapter code.

## Strengths

- Both fixed and request schemas expose the same optional byte budget and source window, including matching ranges and unknown-key allowlists. Request dispatch forwards both values to the fixed handler after resolving the caller's scope. The handler uses the existing shared effective-budget resolver and passes that exact budget to final response adaptation.
- Core revalidates pageSize before reading saved content. Owner validation still precedes section materialization and projection; selectors cannot bypass it. New work neither connects to databases nor changes persistence or mutation policy.
- Reader source windows are bounded independently of final wire fitting. Plan, matrix, operator and statement projection preserve the unreturned tail through continuation. Statements preserve detailLimit, source omission counts and preexisting continuation; plan nodes can consume the explicitly requested bounded window.
- Fixed and request capabilities builders both populate artifactPages from the actual reader constants (1..512; defaults 32/32/128/128). The additive payload contains no local path. Fixed-context copying and input-root pagination retain that property.
- Existing caller arguments remain in place and new Core arguments are optional. CLI configuration and artifact format are unchanged; the intentionally newly bounded statement source default is explicitly documented.
- Evidence distinguishes deterministic SDK wire measurements from provider tokens, paid-agent behavior and live database evidence. Reported final Windows gate and direct branch formatting cover the final product revision 84b4796; later commits are evidence only.

## Issues

### Critical

None.

### Important

None.

### Minor

1. **SDK completion equality does not independently establish expected traversal.**
   - File: `tests/SqlHarness.Mcp.Tests/AgentOptimizationWorkflowTests.cs:169` (comparison assertions; helper at line 195).
   - The SDK test checks 513 records and equality across all three modes, but never checks distinct paths or the fixture's exact preorder. A common duplicated or omitted path could pass every equality check. Consequently `plans/095-verification-evidence.md:48` overstates what this SDK test alone establishes when it calls the reconstruction gap-free.
   - This is nonblocking: `ArtifactReaderTests.cs` independently verifies exact expected preorder and uniqueness for 513 nodes across small/large budgets and requested source windows, and the changed runtime cursor arithmetic is consistent on inspection.
   - Add expected path sequence and uniqueness assertions to the SDK workflow (root 0, followed by 0/0 through 0/511). Also bound its traversal count/require cursor progress so regressions fail directly rather than depending on the session timeout. Alternatively narrow the SDK evidence wording until the stronger assertion is added.

## Recommendations

Resolve the Minor in the task-local review loop, or explicitly retain it as a nonblocking testing improvement and narrow the SDK evidence claim. No product redesign or broader verification is warranted by this finding.

## Declined to judge

None. No behavior was set aside as outside the plan/spec. Production latency, model token use, live database execution and Linux behavior are unverified evidence limits, not claimed results; no such execution was required or authorized for this plan.

## Assessment

**Ready to merge? Yes, with one nonblocking Minor.**

The full branch implements the specified bounded offline paging and shared response budget without a blocking correctness, compatibility or scope-safety finding. The final product has recorded green Windows verification and direct branch formatting; this review does not independently rerun those checks or claim an integrated-tree gate.

