# Plan 115 whole-branch scoped rereview

Fix range: `2d75e6658c3a23a5b733187c2337761f770150c0..7b66ee17`. Reviewed the two-file fix diff and `final-fix-report.md` against the task brief and whole-branch review. I did not rerun the reported tests or formatter. The controller's full Windows gate was still pending.

## Finding verdicts

- **Selected query dry runs fail after successful validation — ADDRESSED for valid dry runs.** `McpToolCatalog.cs:122-123` now returns a successful `SqlHarnessDryRunReport` before the executed-result evidence check. The added served regression exercises `[]` and `[0]` in fixed and request modes, and the final focused report records 8/8 passing.

## New breakage in the fix diff

- **Important — non-dry-run success can bypass the missing-evidence guard** — `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:122-123`. The early return checks only the report type, not the request's `dryRun` flag. Thus a selected ordinary query whose module returns `Success` with `SqlHarnessDryRunReport` is treated as successful despite lacking executed result-set evidence. The brief explicitly requires missing/incomplete result evidence never to become a successful check. Guard the exception with `dryRun && outcome.Report is SqlHarnessDryRunReport`, and add a canned-module regression for `dryRun: false` plus that success report. This is a narrow condition change; valid dry-run behavior remains intact.

## Out-of-scope observations

- Live provider behavior remains outside the offline-only plan.
- Full Windows `just verify` is controller-owned and had no reported result at rereview time.
- Archival plan/index/status edits are outside this fix diff.

## Assessment

**Ready to merge? No.** The dry-run finding is fixed, but the new return condition weakens the required missing-evidence check for a non-dry-run selected query. Merge readiness also depends on the controller's full gate result.

## Adjudication addendum

Further focused source inspection changes the assessment above. In the supported production path, `SqlHarnessModule.QueryAsync` returns `SqlHarnessDryRunReport` only inside `if (query.DryRun)` (`SqlHarnessModule.cs:228-236`). The ordinary path collects execution results and returns `SqlHarnessQueryReport` (`SqlHarnessModule.cs:255-285`). MCP constructs that module and decorates it with journaling; the hypothesized non-dry-run `Success` plus dry-run report requires an injected canned module violating this semantic contract. The dry-run report itself explicitly serializes `validation: passed` and `execution: not-run` (`Contracts.cs:308-323`; `AgentOutputProjection.cs:744-745`), so it cannot be mistaken for successful executed empty result-set evidence. I withdraw the Important finding as a supported production breakage. A `dryRun &&` check would be defensive hardening only, outside this final fix requirement.

**Updated verdict: Ready to merge on reviewed behavior, conditional on the controller's full Windows gate passing.** The original dry-run finding is addressed, and no actionable new breakage remains in the fix diff. This addendum supersedes the preceding assessment; it does not assert a gate result.
