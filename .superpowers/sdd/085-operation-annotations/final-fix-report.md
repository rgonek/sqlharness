# Plan 085 final fix wave

Base: `ccbc896` (`docs(085): record plan 085 evidence`). Scope was limited to the whole-review findings and controller rulings. No Linux, live database, agent trial, merge, or push was performed.

## Findings and resolutions

1. Keep/star retention expectation: changed the agent-facing keep description and MCP guide to describe a marker subject to normal retention. Added a journal-retention test covering kept and ordinary annotations expiring with old operations while a recent kept operation remains.
2. Oversized annotation response: added a pre-commit check that uses `McpResultAdapter`'s real envelope serializer and byte-budget logic against the complete result, including Unicode JSON escaping and result echoes. On overflow the handler returns a clear raise-budget/split-batch validation error and disposing the SQLite transaction rolls back the batch. Added a 20-item, maximum-length CJK-label rollback case at 4096 bytes and an adequate-budget mixed-status case at 65536 bytes.
3. Owner feedback: operation detail displays API mutation failures and clipboard failures. Added UI tests for both.
4. Scope docs: expanded `docs/mcp/tools.md` with resolved and unresolved caller fallback, exact provider/conversation identity, and the rule that inferred membership never authorizes.
5. Historical capabilities golden: left the existing evidence limitation intact; did not repeat the expensive historical build.

## Validation and TDD evidence

- `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AnnotateToolTests" --logger "console;verbosity=minimal"` — 10 passed. The first oversized-response test run was red because the general adapter degraded the complete result into a smaller projection. The correction was to expose the adapter's full-result fit check and call it before commit; the corrected test passed.
- `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Normal_retention_expires_kept_and_unkept_operation_annotations" --logger "console;verbosity=minimal"` — 1 passed after correcting the test SQL to qualify `operations.id`.
- `npm test -- src/pages/OperationPage.test.tsx` from the dashboard UI directory — 28 passed. Initial assertions queried an ambiguous alert role because the page also had a plan-load alert; narrowed the assertions to the actual error text and reran successfully.
- `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3702ce1 -Fix` — reported 39 changed C# files.
- Direct `pwsh ./scripts/format-changed.ps1 -Mode branch -BaseRef 3702ce1` on committed tree `9adc27d` — passed, 40 C# files.
- `just verify -LogDirectory D:/temp/sqlharness-plan085-finalwave3` — all five stages passed on tree `9adc27d8cc1c935968ece5a4023acff8313f50a1`: build 0 warnings/errors; UI 62 files / 331 tests passed; Core 4,562 passed / 11 skipped; MCP 462 passed / 4 skipped. The gate skipped its working-tree format stage because no C# files were modified after the direct branch-format check.
- First full-gate attempt failed only on `DashboardHousekeepingTests.Matcher_commits_do_not_keep_an_idle_dashboard_up` (null title); focused rerun passed 1/1. No code changed before the fresh full-gate rerun, which passed. Logs are in `D:/temp/sqlharness-plan085-finalwave2` and `D:/temp/sqlharness-plan085-finalwave3`.

TDD note: the first version of the handler edit preceded the exact regression test. That test then failed as expected, exposing adapter projection behavior. I have retained that chronology here rather than claiming a strict test-first cycle.

## Files changed

- `src/SqlHarness.Mcp/McpResultAdapter.cs`
- `src/SqlHarness.Mcp/Tools/AnnotateHandler.cs`
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Mcp.Tests/AnnotateToolTests.cs`
- `tests/SqlHarness.Tests/Journal/JournalRetentionTests.cs`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx`
- `src/SqlHarness.Dashboard/ui/src/pages/OperationPage.test.tsx`
- `docs/mcp/tools.md`
- `plans/README.md`
- `plans/085-verification-evidence.md`

## Self-review

The fit preflight checks the same envelope builder and byte limit as final adaptation before the annotation transaction commits. The batch-level transaction remains intact. The ordinary mixed-status path remains `isError=false` and is covered by the existing adapter assertion. Remaining limitation: exact historical capabilities bytes remain unproven, as adjudicated by the whole-review finding; final Sol review should adjudicate that evidence limit. No historical builds were repeated.
