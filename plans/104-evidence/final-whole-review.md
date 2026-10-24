# Plan 104 independent whole-branch review

Range: `ab46345a2bfc2c89db4646c33895edf11ceede5a..6aa801a306c4b1b4d69cd844f65dfd589c819d34` (product revision `029e1905`). I reviewed the complete product/test/current-doc supplement, relevant bounded source and tests, Plan 104, the execution ledger, and archived task reports and reviews. Historical raw XML in the full package was not reimported. This was read-only; no tests were rerun. Final `just verify` was still running when this report was written.

### Strengths

- Producer-side classification precedes temp-name normalization, and the typed anonymous identity is aggregated within each run. Legacy string maps remain Unknown. Report and journal labels reserve names across comparison or operation variants, including collisions with the aggregate label.
- The revised completeness gate prevents partial IO messages from becoming measured zero. Saved legacy metrics have a literal prior-format fixture. Core, MCP, dashboard, and artifact-reader focused logs report 213, 54, 9, and 84 passing tests respectively; branch formatting records exit 0.

### Issues

#### Critical (Must Fix)

None confirmed.

#### Important (Should Fix)

1. **Statement reconciliation still guesses a table-variable identity from a plan object name.** `src/SqlHarness.Core/Journal/StatementReconciler.cs:33,103-106` maps any eight-hex `#` plan name to `table variables`, even when `kind` is `table`. The observed side now preserves an explicit local temp's separate identity at lines 60-65. For an explicit `#ABCDEF12` temp table whose plan object uses that name and whose IO name has the recognized local suffix, equal counters become a false `Partial` because the plan key is `table variables` and the observed key is `#ABCDEF12`. If a genuine table variable is also present, the string grouping can further obscure which object mismatched. Remove the shape-only plan-side inference for explicitly typed table objects; retain aggregation for `kind == table-variable` or `@` variable evidence. Add a regression with an explicit hex-shaped temp plan object and matching producer-classified local-temp IO, plus a mixed variable/temp case. This is a status/evidence correctness issue, not a database safety issue.

#### Minor (Nice to Have)

None.

### Declined to judge

- Whether the exact bare `#8hex` producer rule holds on untested SQL Server builds: the user accepted it as an experimental operational rule, and no cross-version guarantee is available.
- Installed binary, application target, and implementation live UAT: outside this offline branch review and not performed by this reviewer.
- Linux behavior: the plan explicitly leaves Linux unverified without a separate request.
- Model-token savings and session cost: the plan excludes measured savings claims and this diff contains no such measurement.
- Historical research STOP and proposed exact-build allowlist: superseded by the user's accepted implementation rule; archived conclusions were checked as chronology, not applied as current requirements.
- Final Windows gate result: it was still running at review time; the controller must record its direct exit status before any completion claim.

### Recommendations

Fix the reconciliation identity mismatch and rerun the focused reconciliation tests plus the affected final gate on the changed tree. Record the gate's direct result and update the plan/index before declaring the implementation complete.

### Assessment

**Ready to merge? No.** One actionable status-attribution defect remains, and the final Windows gate result was unavailable at review time. The aggregation and consumer architecture otherwise aligns with the accepted Plan 104 contract. Merge, push, and installation are outside this review's authorization.
