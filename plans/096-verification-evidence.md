# Plan 096 verification evidence

Implementation base: `898f8b20f40246b3d0ff5263c5969ed4e2130b12` (saved before implementation/Task 1; never redefined).

Product/test revision for the initial gate: `2b3585e7e4fe469e3bd1f1baa71ba5b5705ad1d0`; tree: `3126e5ff0811bfcebdf7e7989dd3630a8dc66298` (`feat(096)` plus the catalog-budget fix). After the final review fixes, product/test revision `478d2d227bf77c000eb9aab14cb5eaccce934c69`, tree `d30b1915a7c896a90777e040f8870712b8471f7e`, passed the final Windows gate. All task, whole-branch and final scoped reviews are approved. The branch awaits separate integration authorization; it is not merged or pushed. Later changes after the tested product commit are documentation and archived review evidence only.

## Offline large-file workflow

`Measure_reads_large_typed_fixed_and_set_files_matching_inline_typed_values` creates three external `.sqljson` occurrences: one fixed-parameter file and two named parameter-set files. Each is larger than 64 KiB and smaller than 16 MiB. The fixture admits the decoded typed values as equivalent to inline parsing, then dispatches one operation to an in-memory fake module.

| Occurrence | Raw UTF-8 bytes |
|---|---:|
| Fixed parameter file | 125,069 |
| Parameter-set file 1 | 125,094 |
| Parameter-set file 2 | 125,099 |
| Total external raw ingress | 375,262 |

Measured result: 15,000 list values decoded; one fake-module dispatch; zero database calls; zero output bytes. The test verifies that output contains none of the input JSON, values, or set labels. This demonstrates bounded raw admission for the fixture; it does not claim that a larger raw file authorizes more SQL parameter materialization.

The reported materialization ceiling remains 33,554,432 bytes (32 MiB), the existing Core resolver limit. Raw ingress limits are independently 16,777,216 bytes per external file and 33,554,432 bytes total per logical call. The CLI capabilities document reports these under `limits.parameterFileIngress` and `limits.materializedParameterBytes`; MCP capabilities reports `parameterFileIngress` and `materializedParameterBytes` as separate fields. Tool descriptions and the human docs identify the same distinction.

## Focused checks

- Core/CLI parameter reader, CLI measure, and capabilities tests: 127 passed, 0 failed, exit 0. The detailed fixture run above is retained separately.
- MCP tool schema and token-budget tests: 108 passed, 0 failed, exit 0. Served catalogs measured 28,702 bytes with annotations off, 32,310 bytes with annotations on, and 27,842 bytes in the request catalog; each stayed within 32,768 bytes. The public schema test used 9 tools and no output schemas, then made one `tools/list` request and one capabilities request.
- Old-limit scan: `rg -n "64 KiB|64KiB|65536|65_536" src/SqlHarness.Core/ParameterInputFileReader.cs src/SqlHarness.Core/ParameterSetFileReader.cs src/SqlHarness.Mcp/McpLimits.cs docs/mcp/inputs.md`; exit 1, no matches. This scan was reviewed; unrelated result/frame budget constants are outside its paths.
- `git diff --check`: exit 0.
- Branch formatter fix: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 898f8b20f40246b3d0ff5263c5969ed4e2130b12 -Fix`; exit 0.
- Direct branch formatter check after code commits: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 898f8b20f40246b3d0ff5263c5969ed4e2130b12`; exit 0, 14 changed C# files checked.
- Final Windows gate: `just verify -LogDirectory D:/temp/sqlharness-plan096-verify`; exit 0, summary `verify: OK stages=5` in 10m37s. UI install, build (0 warnings/0 errors), UI check (64 files/372 tests), and test stages passed. Core tests: 4,631 passed/11 skipped; MCP tests: 501 passed/4 skipped. The gate's format stage reported skipped because all changed C# files were committed; the separate direct branch format check above passed across 14 changed files.

## Final review fixes and verification

- Generated CLI help regression: `Measure_help_publishes_current_parameter_file_limits_and_types` failed before the fix because `measure --help` lacked `16 MiB per file` and `32 MiB total raw bytes per call`; it passed after the description was corrected. The served `--param-set` contract now states both raw limits and table/list support. The existing large typed CLI fixture verifies list values are admitted.
- Direct final formatter: `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 898f8b20f40246b3d0ff5263c5969ed4e2130b12`; exit 0, 14 C# files checked.
- Final gate: `just verify -LogDirectory D:/temp/sqlharness-plan096-final-verify`; exit 0, `verify: OK stages=5` in 9m15s. Build: 0 warnings/0 errors. UI: 64 files and 372 tests passed. Core: 4,632 passed/11 skipped. MCP: 501 passed/4 skipped. The UI log includes the existing non-failing `DEP0190` Node warning.
- The gate's exact committed product revision/tree and raw stage logs are archived under `plans/evidence/096/final-fix-*`. Its format stage skipped committed files; the separate direct branch check above is the formatting evidence.

## Accepted follow-up

The materialization-bound test at `tests/SqlHarness.Tests/ParameterInputFileReaderTests.cs` admits file bytes and calls `ParameterSetResolver.ValidateInputs` directly. The current `SqlHarnessModule` validates typed inputs before session opening, and existing CLI/MCP aggregate admission tests assert no dispatch/session on raw-budget rejection. Keep a concrete follow-up to exercise this materialization failure through the full `SqlHarnessModule` path with a fail-on-open session factory, asserting the materialization error occurs with zero factory opens. This accepted Minor was not expanded into the final help-test wave.

## Review record

Review model assignments were gpt-6-luna xhigh for task reviews and gpt-6-sol low for the whole-branch and final scoped reviews. The durable review texts are archived as [whole-branch review at b9ea0fd](evidence/096/whole-review-b9ea0fd.md) and [final scoped re-review at 752a653](evidence/096/whole-rereview-752a653.md).

| Review | Reviewed range/head | Verdict |
|---|---|---|
| Task 1 initial review | `898f8b2..9351ec5` | Needs fixes: add full typed file-to-inline parity assertions. |
| Task 1 fix-round re-review | `9351ec5..3a9745f` | Approved: parity covers fixed, CLI set and MCP set file paths. |
| Task 2 task review | `3a9745f..4dceb9c` | Approved; accepted Minor remains for the full-module materialization failure with an unopened session factory. |
| Task 3 initial review | `4dceb9c..9458fc9` | Needs fixes: README still published 64 KiB and CLI materialization capability path was ambiguous. |
| Task 3 fix-round re-review | `9458fc9..b9ea0fd` | Approved: both documentation findings addressed, no new breakage. |
| Whole-branch review | `898f8b20f40246b3d0ff5263c5969ed4e2130b12..b9ea0fd` | With fixes: correct served CLI help and completion bookkeeping; retain materialization Minor as follow-up. |
| Final scoped re-review | `b9ea0fd..752a653` (product/test commit `478d2d2`, tree `d30b1915a7c896a90777e040f8870712b8471f7e`) | **Approved; Ready to merge: Yes, subject to separate integration authorization.** No new breakage. |

The final scoped review examined the original findings and fix diff only; it did not rerun suites. The product gate and direct formatter evidence above apply to the reviewed product tree.

## Preserved scope rulings

These implementation-time rulings remain part of the decision record:

> Stream-specific coverage requiring the new admission API is completed in Task 2; Task 1 freezes existing public-path RED regressions — avoids uncompilable tests before the API exists — cost if wrong: task-boundary rework only.

> Leave historical 64 KiB documents and separate ArtifactReader manifest policy outside this change; Linux/live/paid-agent behavior remains unverified — final reviewer declined these scopes and plan excludes them — cost if wrong: separate documentation/policy follow-up, no claimed coverage.

> Defer the full module-path materialization/session-open regression as accepted Minor — current pre-session validation was inspected and aggregate full-path regressions pass — cost if wrong: weaker future regression detection; follow-up must exercise fail-on-open session factory after admitted typed file exceeds materialization limits.

## Evidence limits and archive

All measurements above are deterministic local fixtures. There was no live database, actual-agent run, paid provider call, or Linux gate. UTF-8 byte counts and test call counts are not measured model tokens.

Tracked raw test and gate logs, including prior task RED/GREEN evidence, are in [`plans/evidence/096`](evidence/096/README.md). The SDD workspace briefs and reports remain scratch files and are not tracked.
