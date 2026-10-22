# Plan 096 whole-branch review

Base `898f8b20f40246b3d0ff5263c5969ed4e2130b12`; head `b9ea0fd`. Reviewed the plan Fix contract, progress ledger, verification evidence, and product/doc/test diff. Read-only review; no suites rerun.

## Strengths

- The Core budget has one 16 MiB per-file and 32 MiB per-call authority. CLI measure shares it across the fixed file and ordered set-file occurrences; MCP shares it across its set files. The bounded read uses a sentinel byte, detects growth, and allocates a small initial buffer.
- CLI and MCP regressions exercise exact limits, repeated occurrences, combined overage before dispatch/session, large typed value parity, rooted MCP paths, parser behavior, and no value echo. The Windows gate and direct branch formatter passed on the final product/test tree (`2b3585e`; docs-only commits followed).
- Capability documents and MCP tool descriptions expose raw ingress separately from the unchanged materialized input bound. No fixed-file MCP argument or mutation approval bypass was introduced.

## Issues

### Important (should fix before merge)

1. **CLI `measure --help` still publishes the old file cap.** `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:314` says `--param-set` files are `64 KiB`; the reader now admits 16 MiB each and 32 MiB across the call. This is a served interface contract and directly conflicts with Plan 096 Fix contract item 8. Change that description to the two raw byte limits, then add a focused assertion for the generated CLI help so the contract cannot drift again. The same description also contains the pre-existing claim that table/list execution is unavailable; confirm whether to correct it in the same edit, since large typed TVP/list input is this plan's use case.

### Minor (nice to have)

1. **Completion state is stale.** `plans/096-bounded-large-parameter-files.md:9,49,61-62,85-89` still marks completed regressions, publication, and verification as pending. Update the plan and index/history after the help fix and final review; leave integration authorization distinct. This is required plan bookkeeping but does not change runtime safety.

2. **Materialization integration regression is narrower than its name.** `tests/SqlHarness.Tests/ParameterInputFileReaderTests.cs:93` admits file bytes then calls `ParameterSetResolver.ValidateInputs` directly. It does not itself assert zero opened sessions. I accept this as a deferred minor for this branch: `SqlHarnessModule.cs` validates typed inputs before session opening, the full Windows suite passed, and the CLI/MCP aggregate admission tests do assert no dispatch/session. A future regression covering the module boundary would make this guarantee more robust.

## Declined to judge

- Historical design and plan documents retaining 64 KiB refer to their original decisions and are outside this plan's affected served/current docs.
- The `ArtifactReader` 64 KiB manifest bound is a separate artifact resource limit; its comment's reference to the old parameter-set cap is stale but the artifact policy itself is outside Plan 096.
- Linux, live database, and actual-agent behavior have no evidence here and were expressly outside authorized verification; this verdict does not assert them.

## Recommendations

Fix the served CLI help and refresh Plan 096 completion state. A focused CLI help assertion and documentation-only checks are enough to validate those changes; the already green product gate need not be rerun unless product behavior changes.

## Assessment

**Ready to merge? With fixes.** The runtime implementation and available verification satisfy the safety and accounting design, but the served CLI help violates the authoritative public limit contract. The stale plan status should be corrected before declaring the plan complete.
