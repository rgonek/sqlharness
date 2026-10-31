# Whole-branch review — plan 125

Range: `672e6265cd20cbbad1ba3a8d69abcf6c955591a1..a070c1ff`.
Authority: the embedded Design contract in `plans/125-optimization-evidence-clarity.md` and repository AGENTS.md. This is an integrated review, not a task-only review. Product code, interface additions, test changes, human guidance and verification evidence were inspected in passes using the prepared product diff and targeted current-source reads. No product, index, HEAD or branch mutation was performed; this report is the requested review artifact.

## Strengths

- One eligibility helper is shared by summary, direct-pair and snapshot selection; failed operations retain inspectable detail while successful executions with a failed regression policy remain eligible.
- Additive DTO fields preserve the old numeric fields and carry side-specific availability, coverage and provenance. Confirmed partial-batch statement evidence does not by itself weaken the complete-attribution verdict gate.
- Stored table counters travel through the reader and comparison model with nullable values intact; regression tests distinguish zero, missing contributors and stored representatives from repetitions.
- Results, IO and recorded policy use common case records and independent counts. Reversed and independently measured comparisons do not borrow a forward policy verdict; drill-down retains elapsed/CPU and recorded reasons.
- Script actions preserve side and raw-case mappings, source operations remain linked, and diagnostics render stored messages as text.
- Verification records failures and environmental remediation candidly. The recorded final Windows gate passed on product `b189a999` (UI 473, Core 4835/11 skipped, MCP 535/4 skipped), with a separate nine-file branch-format pass. `a070c1ff` adds documentation only.

## Issues

### Critical (must fix)

None identified.

### Important (should fix)

1. **All-cases statement and table evidence still depends on whole-batch read availability.**
   - File: `src/SqlHarness.Dashboard/Optimization/OptimizationComparer.cs:96` (the filter is at lines 79–81; consumers include lines 110–112 and 122).
   - `selectedIds` uses `commonIds`, which removes any SQL Server case lacking a logical-read median on either side. That filtered set is then passed to the new per-statement availability calculation and to every table-counter aggregate. For example, in a two-case direct comparison where case 0 has batch reads and case 1 lacks candidate batch reads but has confirmed statement reads/scans or stored physical counters, All cases silently excludes case 1. A statement can report `available`, 1/1 coverage and a case-0-only number even if case 1's statement is ambiguous; with no batch reads anywhere, individually trustworthy statement evidence disappears entirely. Selecting case 1 explicitly restores its evidence. This contradicts independent metric availability and the requirement not to silently drop missing contributors. The existing all-case partial-coverage test keeps batch read medians present, so it does not cover this boundary.
   - Fix: derive the contributor universe for statement/table metric projections from the selected pair's case identities independently of batch logical reads (including expected missing contributors), and let each metric's reducer determine availability. Keep whole-batch verdict eligibility separate. Add cases with one and all batch-read medians absent, both usable and unavailable statement/table counters, and sum/mean coverage assertions.

### Minor (nice to have)

None newly identified. Previously deferred environment diagnostics are triaged below.

## Recommendations

Resolve the contributor-universe issue with focused backend regressions before reapproval. No security, SQL-execution, mutation-policy or schema change is needed for that correction.

## Declined to judge

- Live SQL execution, installed-dashboard acceptance, publication, restart and deployment: outside this dashboard projection scope; no live workload was run.
- Linux behavior: no Linux gate was performed; repository verification explicitly does not require it for current completion.
- Real retained-plan download in a browser: the synthetic fixture had no retained plan; Plan navigation and existing component/backend coverage are evidence, not a browser download acceptance claim.
- Backend recalculation through the synthetic browser selectors: API responses were static, so those checks prove selector URL changes only; backend/component tests provide computation coverage.
- Dependency remediation for the nine npm audit findings: lockfile/dependencies are unchanged and this is unrelated to plan 125; do not treat the audit as newly introduced by this branch.
- Existing React effect-dependency warnings and Node DEP0190: recorded as baseline/toolchain diagnostics, unchanged by this work; no suppression or plan-specific defect established.
- Git LF/CRLF diagnostics: line-ending warnings without a failed scoped check; no product correctness issue established.
- Historical task-review and raw-log bodies: retained as provenance; this review does not independently rerun every archived command or adjudicate superseded intermediate failures. Current integrated code and final verification evidence control the verdict.

## Assessment

**Ready to merge? With fixes.**

The integrated implementation substantially meets the design and has strong recorded verification, but its All-cases IO projection can hide or misrepresent independently available statement/table evidence. Fix that contributor-universe boundary and verify the focused regressions before merging. Passed suites were not rerun during this read-only review.
