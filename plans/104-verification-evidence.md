# Plan 104 renewed investigation evidence

Date: 2026-10-24
Worktree: `D:/Dev/sqlharness/.worktrees/plan-104`, branch `plan-104`.

## Outcome

**BLOCKED, implementation incomplete.** Original Tasks 1–3 are not complete. No product behavior changed. The original contract needs positively identified table-variable IO, while the current producer emits only names and counters. The same `#ABCDEF12` spelling can represent an explicit temporary name or an anonymous variable observation; current inputs contain no object-kind discriminator.

Luna high investigated suffix provenance, additive typed fields and conservative ambiguity handling. Luna xhigh independently reviewed those proposals and confirmed the STOP. An unpopulated typed field would not repair real capture. An availability-only fallback needs a separate report/journal/dashboard/export contract and does not satisfy the original aggregate-median assertions. See the [proposal](104-evidence/resolution-proposal.md) and binding [resolution review](104-evidence/resolution-review.md).

## Revision and executable evidence

- Original branch base: `6d543e509734a374caf882bc8c05ffe5ccaa98b2`.
- Current main `ab46345a2bfc2c89db4646c33895edf11ceede5a` was merged into this isolated feature branch at `160067bab0337f98e7db0d054511e4fba0ff963c`. Main was not modified.
- Baseline at `160067b`: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -p:IsTestProject=true --filter "BenchmarkRunTests|BenchmarkSummaryTests" -v minimal`, exit 0, 30 passed, no failures/skips. Existing Vite chunk-size warning occurred during the build. The unmodified [baseline log](104-evidence/baseline.log) is retained.
- Temporary Task 1 reproduction: 36 discovered, 31 passed and 5 expected failures; implementer reported exit 1. All intended failures concern the absent `table variables` aggregate. The collision case fails first on that absent aggregate and alone does not prove classification impossibility. See [report](104-evidence/task-1-report.md), [exact patch](104-evidence/reproduction.patch) and [RED output](104-evidence/task1-red.log). Evidence commit: `c0aa2f3a`.
- Temporary test changes were removed. `git diff --exit-code ab46345a HEAD -- src tests` and `git diff --exit-code -- src tests` both exit 0; `git apply --check plans/104-evidence/reproduction.patch` exits 0. The captured RED patch is not an enabled regression suite or a passing implementation.

## Verification boundary

The current delivery changes documentation/evidence only. Documentation-scoped diff/link checks apply; no Windows implementation gate, branch-wide product formatting gate, MCP regression suite, Linux, live database, installed-binary or paid-agent acceptance is claimed. Original implementation gates remain pending. No merge into main, push, installation or persistent database mutation occurred.

## Required next input

The original repair needs a supported capture producer that associates each IO counter with an object kind, validated against explicit hex-shaped temp names and SQL Server versions. A suffix pattern, SQL text guess, group-sum reconciliation or manually typed fixture is not that producer. A separately scoped ambiguity-reporting change is feasible, but must keep total reads and per-run evidence while making omitted object distributions explicit in every consumer.

## Independent review disposition

Luna xhigh accepted the [Task 1 diagnostic evidence](104-evidence/task-1-review.md), while explicitly retaining original Task 1 BLOCKED/NOT DONE. Sol low accepted the [whole-branch diagnostic delivery](104-evidence/whole-review.md) for `ab46345a..95b95ad2` with no blocking findings. Neither review approves original implementation or a future fallback. All new relative Markdown links resolve, the branch diff check exits 0, and the reproduction patch applies. The [execution ledger](104-evidence/execution-ledger.md) records resumption and rulings. The SDD workspace is retained for the incomplete plan.

## Subsequent live experiments and primary-source research

The user subsequently authorized experiments and asked for Microsoft documentation. This supersedes the earlier no-live-evidence boundary only for the bounded local investigation; all original historical test/review outcomes above remain as recorded.

On localhost/tempdb SQL Server17.0.1145.4, the [matrix](104-evidence/experiments-report.md) captured distinct raw IO names for generated variables and explicit temp tables, including a literal #ABCDEF12 and repeated temporary procedure calls. Existing normalization discards the suffix distinguishing the observed explicit temp names. Two captures were made because an intended analysis invocation accidentally executed a second3-repeat matrix; the report preserves this deviation and the runner now requires an explicit mode. No product code or application database changed.

[Microsoft-source research](104-evidence/sqlserver-internal-naming-research.md) documents the suffix/caching mechanisms and cached explicit-temp catalog counterexamples. [Transport research](104-evidence/statistics-io-transport-research.md) establishes that diagnostic3615/TDS INFO has no independent typed object field. The [updated Luna xhigh review](104-evidence/experiment-resolution-review.md) proposes a characterized-build classifier; this is not an implemented or Microsoft-guaranteed runtime policy. A hard-coded development-build allowlist has not been silently substituted for the original general repair. Positive recognition scope remains unresolved. Original Tasks1–3 and implementation gates remain incomplete.
