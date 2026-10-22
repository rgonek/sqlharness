# Plan 096 verification evidence

Implementation base: `898f8b20f40246b3d0ff5263c5969ed4e2130b12` (saved before implementation/Task 1; never redefined).

Product/test revision for the final gate: `2b3585e7e4fe469e3bd1f1baa71ba5b5705ad1d0`; tree: `3126e5ff0811bfcebdf7e7989dd3630a8dc66298` (`feat(096)` plus the catalog-budget fix). The gate checks the committed product tree; documentation and this evidence record are separate documentation commits. This is feature-branch evidence, pending Task 3 review and the independent whole-branch review.

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

## Evidence limits and archive

All measurements above are deterministic local fixtures. There was no live database, actual-agent run, paid provider call, or Linux gate. UTF-8 byte counts and test call counts are not measured model tokens.

Tracked raw test and gate logs, including prior task RED/GREEN evidence, are in [`plans/evidence/096`](evidence/096/README.md). The SDD workspace briefs and reports remain scratch files and are not tracked.
