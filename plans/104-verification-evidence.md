# Plan 104 implementation and investigation evidence

Date: 2026-10-24. Worktree: `D:/Dev/sqlharness/.worktrees/plan-104`, branch `plan-104`; product base `ab46345a2bfc2c89db4646c33895edf11ceede5a`.

## Current implementation outcome

**DONE on feature branch `plan-104`; final Windows gate passed on `f22137e8a0ad89258ac52496bbe62cf87fd09318`.** The user accepted the experimental raw-name recognition rule without a patch-version allowlist. Producer classification precedes normalization; anonymous-variable counters are summed within each run before cross-run distributions. Explicit local objects, legacy Unknown identities and real names colliding with the aggregate remain separate. Compare and journal share collision-safe labels across their respective operation contexts. Incomplete provenance stays unavailable rather than becoming zero. Dashboard/export preserve finalized stored identities.

The full SQL Server rule is reachable through `engineGuidance.tableIo` (capabilities contract 9); PostgreSQL omits it. Existing tool/catalog/result ceilings remain unchanged. Literal historical artifact fixtures preserve saved metric keys, distributions and totals. No artifact schema migration was added.

The characterization covered SQL Server `17.0.1145.4`; it does not establish a Microsoft cross-version naming guarantee. Linux, installed-binary and implementation live UAT remain unverified. Earlier bounded experiments are characterization evidence, not post-implementation target acceptance. No merge, push or installation occurred.

## Reviews and verification

| Scope | Evidence | Outcome |
|---|---|---|
| Task 1 producer/per-run grouping | [report](104-evidence/implementation-task-1-report.md), [review](104-evidence/implementation-task-1-review.md), [rereview](104-evidence/implementation-task-1-rereview.md) | Luna high implementation; Luna xhigh approved after incomplete-provenance fix |
| Task 2 shared aggregation/journal/reconciliation | [report](104-evidence/implementation-task-2-report.md), [review](104-evidence/implementation-task-2-review.md), [rereview](104-evidence/implementation-task-2-rereview.md) | Luna high implementation; Luna xhigh approved after partial-message fix |
| Task 3 consumers/contracts/legacy compatibility | [report](104-evidence/implementation-task-3-report.md), [review](104-evidence/implementation-task-3-review.md), [rereview](104-evidence/implementation-task-3-rereview.md) | Luna high implementation; Luna xhigh approved after literal historical-metrics fixture |
| Whole branch and single fix wave | [initial review](104-evidence/final-whole-review.md), [fix report](104-evidence/final-fix-report.md), [scoped rereview](104-evidence/final-whole-rereview.md) | Sol low approved after plan-side identity fix and full-gate remediations |
| Focused final fixes | [Core log](104-evidence/final-fix-logs/sqlharness-plan104-final-fix-green-core-final4.log), [MCP log](104-evidence/final-fix-logs/sqlharness-plan104-final-fix-green-mcp-final3.log) | Core 142/142, MCP 65/65; actual test discovery |
| Final branch formatting | [raw log](104-evidence/final-fix-logs/sqlharness-plan104-final-fix-branch-format.log) | `-Mode branch -BaseRef ab46345a`, 21 C# files, exit 0 |
| Full post-fix Windows gate | [console/direct exit](104-evidence/final-checks/post-fix-gate/console.log), [metadata](104-evidence/final-checks/post-fix-gate/metadata.txt), [Core](104-evidence/final-checks/post-fix-gate/test-SqlHarness.Tests.log), [MCP](104-evidence/final-checks/post-fix-gate/test-SqlHarness.Mcp.Tests.log), [UI](104-evidence/final-checks/post-fix-gate/ui-check.log), [build](104-evidence/final-checks/post-fix-gate/build.log) | Exit 0, 5 stages, 8m00s; Core 4705 passed/11 skipped, MCP 522 passed/4 skipped, UI 372 passed; build 0 warnings/errors |

The first full gate on product `029e1905` failed: one Core fixture lacked intended producer provenance; MCP exposed SQL Server wording in PostgreSQL and exceeded the annotation catalog ceiling. Its [raw logs](104-evidence/final-checks/pre-fix-gate/console.log) are preserved. The single final fix wave resolved these failures, a plan-side hex-name inference and the real-name/aggregate collision; all dispositions are in the scoped rereview. Failed intermediate focused runs remain archived, distinct from final GREEN.

The gate explicitly skipped UI installation (unchanged lockfile) and working-tree formatting (no C# edits); the separate final branch formatter checked 21 changed C# files. Unix permissions, live stdio and other-platform smoke skips remain skips, not passed acceptance. The unchanged UI check script emits Node DEP0190; the UI check exits 0 and all 372 tests pass.

The [execution ledger](104-evidence/execution-ledger.md) preserves the full chronology and rulings. The isolated branch and local recovery workspace are retained; this evidence establishes feature-branch readiness only.

## Historical investigation (before implementation authorization)

The following STOP and no-product-change conclusions record earlier revisions only. The accepted experimental rule in the plan supersedes their unresolved-policy outcome and the proposed exact-build allowlist. Preserve their source facts, evidence and limitations as historical records.

Luna high investigated suffix provenance, additive typed fields and conservative ambiguity handling. Luna xhigh independently reviewed those proposals and confirmed the STOP. An unpopulated typed field would not repair real capture. An availability-only fallback needs a separate report/journal/dashboard/export contract and does not satisfy the original aggregate-median assertions. See the [proposal](104-evidence/resolution-proposal.md) and binding [resolution review](104-evidence/resolution-review.md).

## Historical pre-experiment investigation

This section records the investigation before the user-authorized raw-name experiment. Its STOP conclusion was accurate for that earlier evidence set; the later experiment revised the producer-evidence description, while leaving implementation and general recognition policy unresolved.

## Revision and executable evidence

- Original branch base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`.
- Current main `ab46345a2bfc2c89db4646c33895edf11ceede5a` was merged into this isolated feature branch at `160067bab0337f98e7db0d054511e4fba0ff963c`. Main was not modified.
- Baseline at `160067b`: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "BenchmarkRunTests|BenchmarkSummaryTests" -v minimal`, exit 0, 30 passed, no failures/skips. Existing Vite chunk-size warning occurred during the build. The unmodified [baseline log](104-evidence/baseline.log) is retained.
- Temporary Task 1 reproduction: 36 discovered, 31 passed and 5 expected failures; implementer reported exit 1. All intended failures concern the absent `table variables` aggregate. The collision case fails first on that absent aggregate and alone does not prove classification impossibility. See [report](104-evidence/task-1-report.md), [exact patch](104-evidence/reproduction.patch) and [RED output](104-evidence/task1-red.log). Evidence commit: `c0aa2f3a`.
- Temporary test changes were removed. `git diff --exit-code ab46345a HEAD -- src tests` and `git diff --exit-code -- src tests` both exit 0; `git apply --check plans/104-evidence/reproduction.patch` exits 0. The captured RED patch is not an enabled regression suite or a passing implementation.

## Verification boundary

The current delivery changes documentation/evidence only. Documentation-scoped diff/link checks apply; no Windows implementation gate, branch-wide product formatting gate, MCP regression suite, Linux, live database, installed-binary or paid-agent acceptance is claimed. Original implementation gates remain pending. No merge into main, push, installation or persistent database mutation occurred.

## Required next input

The experiment shows that the raw IO name retains candidate provenance before normalization for the tested `17.0.1145.4` cases, including an explicit hex-shaped temp name. What remains unresolved is a general, supported recognition policy across engine versions and uncharacterized name shapes; the empirical spelling rule is not a Microsoft guarantee. A suffix pattern beyond the characterized build, SQL-text guess, group-sum reconciliation or manually typed fixture cannot establish that broader policy. A separately scoped ambiguity-reporting change remains feasible, but must keep total reads and per-run evidence while making omitted object distributions explicit in every consumer.

## Independent review disposition

Luna xhigh accepted the [Task 1 diagnostic evidence](104-evidence/task-1-review.md), while explicitly retaining original Task 1 BLOCKED/NOT DONE. Sol low accepted the [whole-branch diagnostic delivery](104-evidence/whole-review.md) for `ab46345a..95b95ad2` with no blocking findings. Neither review approves original implementation or a future fallback. All new relative Markdown links resolve, the branch diff check exits 0, and the reproduction patch applies. The gate explicitly skipped UI installation (unchanged lockfile) and working-tree formatting (no C# edits); the separate final branch formatter checked 21 changed C# files. Unix permissions, live stdio and other-platform smoke skips remain skips, not passed acceptance. The unchanged UI check script emits Node DEP0190; the UI check exits 0 and all 372 tests pass.

The [execution ledger](104-evidence/execution-ledger.md) records resumption and rulings. The SDD workspace is retained for the incomplete plan.

## Subsequent live experiments and primary-source research

The user subsequently authorized experiments and asked for Microsoft documentation. This supersedes the earlier no-live-evidence boundary only for the bounded local investigation; all original historical test/review outcomes above remain as recorded.

On localhost/tempdb SQL Server17.0.1145.4, the [matrix](104-evidence/experiments-report.md) captured distinct raw IO names for generated variables and explicit temp tables, including a literal #ABCDEF12 and repeated temporary procedure calls. Existing normalization discards the suffix distinguishing the observed explicit temp names. Two captures were made because an intended analysis invocation accidentally executed a second3-repeat matrix; the report preserves this deviation and the runner now requires an explicit mode. No product code or application database changed.

[Microsoft-source research](104-evidence/sqlserver-internal-naming-research.md) documents the suffix/caching mechanisms and cached explicit-temp catalog counterexamples. [Transport research](104-evidence/statistics-io-transport-research.md) establishes that diagnostic3615/TDS INFO has no independent typed object field. The [updated Luna xhigh review](104-evidence/experiment-resolution-review.md) proposes a characterized-build classifier; this is not an implemented or Microsoft-guaranteed runtime policy. A hard-coded development-build allowlist has not been silently substituted for the original general repair. Positive recognition scope remains unresolved. Original Tasks1–3 and implementation gates remain incomplete.
