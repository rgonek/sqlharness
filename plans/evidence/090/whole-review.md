# Plan 090 whole-branch review

Reviewed range: `e6e74e8217fccf4e8d814a05ffa3d34dc06013e2..902f125c7d481de0c4b905f429e49c973bfed55d`.

### Strengths

- Full-sweep pruning uses kind-qualified identities, retained operations, unresolved current-batch membership and persisted title resolution. A null-title cache row alone cannot keep historical bookkeeping alive. Batch membership preserves the operation-retention race through the current sweep.
- The helper reads only candidate identities, chunks queries into 400 identity pairs, and uses bound parameters. Existing pending limits, transcript byte budgets, opt-in behavior, public interfaces and schema are unchanged.
- Targeted calls bypass the new live-set queries and pruning and retain selected-only lookup behavior. Existing cursor ownership remains intact; the interrupted-scan regression checks that a targeted cycle does not consume the full sweep's work.
- Regressions cover eviction/reintroduction, same-ID kind separation, current-batch survival, interrupted cursor progress and repeated retention cycles. Count evidence is appropriately distinguished from process-memory measurements.
- Source inspection confirms `Collect`/`Commit` stage Claude only, and retention preserves conversations referenced by operation links or staged calls. The explicit ruling to omit a currently inapplicable Codex/OpenCode staging query matches actual lifetimes.

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

None.

#### Minor (Nice to Have)

1. **Distinguish defect RED failures from corrected test assertions.** `plans/090-verification-evidence.md:21` calls all five first-run failures “expected failures,” although only three demonstrated the production defect and two required correcting the tests. The parenthetical discloses this, but the headline still overstates RED evidence. Say “five initial failures: three defect regressions and two over-specific assertions corrected before implementation.” This is the already recorded Task 2 Minor; it does not undermine the three meaningful RED observations.
2. **Align the remaining closure checklist with the staging ruling.** `plans/090-matcher-title-backfill-retention.md:54` still asks for counts “plus staged survivors,” although the binding contract and source-backed ruling establish that no Codex/OpenCode persistent staged writer exists. Replace that phrase with “plus in-flight batch survivors; persistent staged survivors only if an applicable writer exists.” Otherwise a future executor could read the unchecked closure item as requiring a synthetic scenario expressly ruled out elsewhere.

### Recommendations

Resolve both wording items during the already required final documentation closure, and update pending review/status markers after recording this verdict. No further product changes or repeated gate are justified by this review.

### Declined to judge

- Future Codex/OpenCode persistent staging: no production writer exists; the explicit contract records that any future writer must extend the live union and its tests. Current Claude staging was inspected for that ruling.
- Process memory and throughput claims: no such claim is made; deterministic live-set counts establish the requested lifecycle behavior without a benchmark.
- Linux and live transcript/database behavior: outside the authorized validation scope; this review uses the recorded Windows gate and synthetic fixtures.

### Assessment

**Ready to merge? Yes**, with two nonblocking documentation Minors to resolve during closure. This is a technical readiness verdict, not merge/push authorization.

**Reasoning:** The implementation satisfies the Fix contract and Global Constraints, including kind scoping, retained-operation/current-batch lifetimes, targeted isolation and resumable retries. No Critical or Important finding remains. The focused 26/26 run, direct branch formatter exit 0 and full Windows gate on product revision `1e6b0eb03330099d69495d4e59d478498ca27097` are recorded in the evidence; `902f125` is documentation-only. Tests were not rerun, as instructed. Review was read-only on source, index and branch state; only this requested report was written.
