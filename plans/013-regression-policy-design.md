# Plan 013: Clarify and prepare the implementation of the CI regression decision

> **2026-10-11:** the decision thresholds here are reused by [051](051-compare-regression-verdict.md). Everything about reading saved artifacts offline (run-count inference, legacy producer proof, `regress` command) is superseded.

Status: **DONE — design/diagnostic stage** (the function is still **PLANNED**; documents: `plans/013-regression-contract.md`, `plans/013-regression-implementation.md`; commits `983f7b6..f6c8d7a`)
Contract and implementation plan revision: 2026-10-11, HEAD729d88f. The historical design stage remains DONE; the function is still PLANNED.
Date: 2026-09-29. Base: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priority: P2; effort: L; change risk: MED.
Coverage: **regression feature proposal**. Dependencies: **003, 006, 009**.

## Context and execution instruction

Read the whole plan. Work in an isolated worktree, preserve others' changes. Writing the plan is not an implementation. On completion update plans/README.md together with the evidence; do not push or publish without an order.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/013-regression-contract.md" "plans/013-regression-implementation.md"
```

Predecessor changes are expected: read them and align the plan before coding. An unexplained divergence requires updating the assumptions, not mechanically pasting old code.

The spec docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md is a design. R1/R2 return pass for zeros before R4–R7 check missing data/instability/equivalence, which collides with the declared caution. ArtifactMetricsSection does not contain all the inputs postulated by the spec table.

Reference point from the code/command:
```text
public sealed record ArtifactMetricsSection(
```

.NET8/C#/xUnit. Align the patterns with the tests listed in the scope. Core remains the common place for safety; the adapters do not create their own classifier. Use the existing fake reader/session/module and the synthetic SQLHARNESS_HOME. Do not read real secrets or user artifacts.

## Scope

- `plans/013-regression-contract.md`
- `plans/013-regression-implementation.md`

Additionally: the new files named in the steps, the resulting documents plans/013-*.md and the status in the index. Other files require an explicit scope correction and justification before any change.

Out of scope: live DB, deploy, user profiles and passwords, installation, push, global disabling of validation and unrelated refactorings. This stage creates documents; it adds no commands, stubs or capabilities.

## Tasks

### 013/T1

- [x] Map the needed data onto ArtifactReader, CompareReport and MetricReport: run count, spread, time/CPU/reads availability, equivalence, stability. In the contract mark the missing fields as unknown; do not default them.

**Verification:** check the indicated sources and local references; `git diff --check` → exit 0; the step result recorded in the resulting document.

### 013/T2

- [x] Write an unambiguous order: completeness/availability/equivalence/stability before the thresholds and the zero cases. Document the change relative to the old spec. Add an expectations table for the zero combinations + mismatch/missing/unstable, and not only for isolated cases.

**Verification:** check the indicated sources and local references; `git diff --check` → exit 0; the step result recorded in the resulting document.

### 013/T3

- [x] Preserve the offline read without re-benchmark, pass/fail/inconclusive as a verdict with exit0, no takeover of exit8, PG CPU unavailable and the AND of the relative+absolute thresholds. Preserve minimum5 and spread25%. The 2026-10-11 revision explicitly removes the mandatory corroboration: a stable time regression gives fail regardless of reads/CPU; costs are a separate diagnostic. Time0 to >5ms gives fail after the gates; a lack of availability evidence is still inconclusive.

**Verification:** check the indicated sources and local references; `git diff --check` → exit 0; the step result recorded in the resulting document.

### 013/T4

- [x] Write a standalone implementation plan for RegressionPolicy.cs, RegressionDecider.cs, RegressCommand.cs and the tests; indicate the necessary additional artifact metadata, legacy compatibility, offline errors and a sample CI gate on the verdict. Add MCP only in a separately agreed scope extension.

**Verification:** check the indicated sources and local references; `git diff --check` → exit 0; the step result recorded in the resulting document.

## Tests and completion

Two documents result: a contract without a conflicting order and an executive plan with the full test table. The feature status is still PLANNED, without capabilities or stubs.

- [x] The resulting documents contain the decisions, boundaries, scope of future files and the test table.
- [x] Uncertainties are marked UNPROVEN with the condition for obtaining evidence.
- [x] Do not run the application tests when writing the documents alone.
- [x] `git diff --check` → exit 0.
- [x] `git status --short` shows only the author's changes within the scope.
- [x] The index contains status, commit and evidence; local paths exist or are marked as new.
- [x] The absence of live/platform evidence is explicit.

Audit baseline: 1895 Core/CLI passed; MCP 122 passed, 2 timeouts, 4 skipped. Plan005 diagnoses the timeouts. An earlier safety fix may be ready for review when there is a documented independent gate failure; do not announce a full PASS then.

## Stop conditions and maintenance

This stage is design. Do not implement the old matrix directly and do not pretend a metric is available on the basis of the value 0.

If a step requires a real DB target, secrets or going beyond authorization, stop that step and record the absence. Complete the independent tasks. Do not mark the whole thing DONE without all the required results.

The reviewer checks the safety of negative cases, compatibility and the truthfulness of the evidence. After a change to the parser/SDK/operation, return to these tests. Small commits of the fix/test/docs type, without push. The required characteristic tests must precede the refactoring.
