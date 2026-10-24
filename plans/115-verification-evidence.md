# Plan 115 verification evidence

Implementation on branch `plan-115`, based on `9718b052f40335d3647cd96750d80431248f9140`. Final product/test revision: `3ffc574b`. **IMPLEMENTATION COMPLETE; all reviews approved; final Windows gate and explicit branch formatting passed.** Subsequent changes only archive evidence and update plan/index status; they do not alter the verified product/test tree.

## Behavior

Fixed/request `sqlharness_query` accept nullable `resultSetOrdinals` with at most 64 unique non-negative source ordinals. Omitted/null keeps legacy output; explicit empty selects none. Successful selected responses retain original ordinals, exact resolved SQL SHA-256, source count and normalized selection, with set-only omission counts and existing bounded row/cell metadata. Execution happens once with original SQL/parameters. Dry-run validation stays offline and successful; executed-query errors and missing result evidence remain distinct.

## Focused evidence

- [Implementation and task fixes](115-evidence/task-1-report.md): initial RED on unknown selector, focused GREEN, first/middle/last/non-prefix/empty/invalid selection, schema parity and minimum-budget escaped Unicode. Review regressions reproduced omitted-set count 180 versus expected 0 and missing item minimum.
- [Dry-run fix](115-evidence/final-fix-report.md): served fixed/request empty/nonempty selectors, RED safety rejection then GREEN 8/8 with unreachable target and no connection.
- [Full-gate repair](115-evidence/gate-repair-report.md): two existing description tests failed after budget trimming. Required guidance restored without changing tests or raising the 32 KiB ceiling; scoped 125/125 and final wording checks 3/3 plus annotation bound 1/1 passed.
- Complete bounded MCP response comparison against frozen Plan 109 fixture: 11,469 to 732 UTF-8 bytes (10,737 fewer). Source ordinals and assertion evidence retained; zero database connections. These are synthetic response-byte measurements, not model-token savings or reduced SQL execution.
- Final request annotated `tools/list`: 32,743 / 32,768 bytes; fixed annotated: 32,268 bytes. This small remaining margin is protected by existing contract tests.
- Explicit final branch format against `9718b052`: eight changed C# files, exit 0 (`format: OK`), as recorded in the [repair report](115-evidence/gate-repair-report.md). [Earlier raw format history](115-evidence/branch-format.log) also retains failed and corrected runs.

## Review record

Luna high implemented; Luna xhigh [initial task review](115-evidence/task-1-review.md) found set-count and item-schema issues, both [fixed and approved](115-evidence/task-1-rereview-1.md). Sol low [whole-branch review](115-evidence/whole-branch-review.md) found dry-run compatibility, fixed in `7b66ee17` and [approved in scoped review](115-evidence/whole-branch-rereview.md). That report's addendum withdraws the hypothetical injected-module report mismatch after verifying the supported production path and explicit `execution: not-run` output.

The mandatory description-contract repair in `3ffc574b` passed [Luna xhigh spec/quality review](115-evidence/gate-repair-review.md) and [Sol low scoped final review](115-evidence/gate-repair-final-review.md), both with no findings. No open or parked findings remain.

## Windows gate

Final `just verify -LogDirectory .superpowers/sdd/115-query-result-set-selection/windows-final-guidance-fix` on `3ffc574bf45e65b3cd892dcc9d4f91800f199372`: exit **0**, `verify: OK`, five stages, **8m32s**. [Direct summary](115-evidence/windows-gate.log), [build](115-evidence/windows-final/build.log), [UI](115-evidence/windows-final/ui-check.log), [Core](115-evidence/windows-final/test-SqlHarness.Tests.log), [MCP](115-evidence/windows-final/test-SqlHarness.Mcp.Tests.log). Build: zero warnings/errors; UI: 64 files, **372 passed**; Core: **4700 passed, 11 skipped**; MCP: **529 passed, 4 skipped**. The gate skips formatting after committed changes; the separate branch check above covers all eight changed C# files. UI logged the existing Node DEP0190 child-process warning. Earlier [failed gate](115-evidence/windows-failed-gate.log) is retained with [raw logs](115-evidence/windows-failed/test-SqlHarness.Mcp.Tests.log): Core 4700 passed/11 skipped, UI 372 passed, MCP 527 passed/4 skipped/2 failed description assertions. The [earlier interrupted gate](115-evidence/windows-interrupted-gate.log) stopped for the dry-run fix after build passed; it is not accepted as verification.

Initial baseline installation encountered npm ENOTEMPTY; generated modules were retained outside UI, then a fresh install succeeded. Baseline build overlapping a controller test run caused file-copy locks; the temporary backup also reached an initial UI test scan before being moved. These controller/environment failures are not product acceptance evidence. The clean focused baseline passed 119/119.

## Decisions and boundaries

The handoff has no numbered task headings or separate named spec. Its complete Contract was used as one integrated task and copied verbatim into the brief; the cost if that division is wrong is coarser review/rework granularity.

No local integration, push, installation, publication, live database, paid-agent trial or tokenizer measurement was performed. Linux was not run. The main checkout's unrelated edits were preserved.
