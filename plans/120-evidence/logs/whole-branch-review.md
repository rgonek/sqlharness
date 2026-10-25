# Plan 120 whole-branch review

Reviewed range: `eb28657d..f710c91c` on `plan-120`. This is a read-only review of the tracked branch; this report is the only review artifact written. No product gate, evaluator, verifier, generator, database, merge or push was run by this reviewer.

## Verdict

**Ready to merge: Yes, with one non-blocking Minor correction recommended.** This is review readiness only, not merge authorization. Critical: 0. Important: 0. Minor: 1.

## Strengths

- The branch stays within the synthetic study and plan/index scope. The evaluator has no SQLHarness, MCP-client, database or external-process dispatch path. Targets remain uninterpreted synthetic data.
- The generator computes exact byte hashes, preserves nested duplicate rows, and records the intentional stale pin separately from current bytes. The LF attribute supports portable byte identity. Ticket A preserves named-set order and empty text; Ticket B includes explicit fixed date input and a multiplicity-only negative.
- All-suite preflight, immediate per-entry rechecks and stop-on-error/timeout/cancellation/unknown completion form a coherent bounded experiment. Prior completed outcomes survive a later changed input. Failed and undispatched work does not receive invented equivalence or measurements; completed negatives retain their own identities.
- The verifier has its own explicit scenario inventory and assertions rather than accepting case-supplied expected results. Identity, dispatch count/order, stopped-entry metadata, retained coverage, no retry/reuse, and the distinction between actual and simulated symlink evidence receive meaningful assertions.
- The decision correctly chooses C and defers B. It does not turn mock feasibility or a synthetic placement estimate into demand evidence or product approval. CLI/MCP grammar differences, full reader parity, actual filesystem proof, concurrent races and live evidence remain explicit limitations.
- Historical task reports preserve earlier counts and findings, while the archive and current decision identify the final 26 cases and 17 mock dispatches. Plan and index agree that research is complete and integration/push have not happened; whole-branch review is correctly pending at this reviewed revision.

## Findings

### Minor — version validation accepts non-integer values

`plans/120-evidence/evaluate.ps1:146` uses only `$Suite.version -ne 1`. PowerShell comparison coercion accepts JSON `"1"`, `true`, and `1.0`, although `plans/120-evidence/design.md:5` explicitly specifies integer `1`. This is a small mismatch in the study's claimed exact schema. It does not affect the checked-in integer fixture or the C decision and creates no production exposure.

Recommendation: require the same integer runtime types used for repeat/timeout before comparing to 1, and add an independently asserted malformed-version rejection case. Alternatively narrow the stated study grammar; strict validation is clearer.

Evidence: a focused read-only PowerShell expression parsed each of those three JSON scalars, printed String/Boolean/Double respectively, and confirmed all pass the current comparison. No passing study check was rerun.

## Verification and limits

Reviewed the current evaluator, independent verifier, generator, fixture manifests/outcomes/hashes, case inventory, result records, design/inventory/decision, plan/index delta, SDD rulings and historical task review sequence. Bounded source reads and compact JSON rendering were used; the omitted middle of an oversized combined output was retrieved separately. The supplied controller evidence on `f710c91c` reports verifier PASS for 26 cases, 118 relative links passing, clean whitespace/scope checks and a clean worktree. Those remain controller evidence, not independently rerun checks in this review.

No Critical or Important inconsistency was found in the coherent study. The zero-call result counters alone would not prove containment, but the inspected evaluator contains only in-memory mock dispatch and ordinary local fixture/file operations. The OS-denied symlink case is explicitly simulated; full reader grammar, true concurrent file races and real execution/artifact identity are unproved, not accepted production guarantees. Research-only scoped verification is appropriate; a future supported feature still needs its own accepted design and product verification.
