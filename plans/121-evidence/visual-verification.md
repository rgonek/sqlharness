# Visual follow-up verification

Candidate base: `7c1b2fbf`; final candidate index snapshot:
`2e279ba84cd35be13dda8c28761c232797443210`.
Exact product tree IDs: [receipt](visual-product-trees.json).

## Browser evidence

[Design QA](../../design-qa.md) records visual acceptance within the operator-approved existing-data scope. The [gallery](visual/index.html) links rendered reference, baseline and corrected screenshots. The final browser matrix has 44 states with no page/console errors; 390px checks have no document overflow. Extra multi-select, empty, custom-file-picker and injected-plan-error checks are recorded in [interaction evidence](visual/additional-interactions.json). No target database writes occurred.

## Gate attempts

1. [First attempt](visual-logs/gate-attempt-1/ui-install.log): `npm ci` could not replace the Windows Rolldown binding while this task's Vite processes held it. Only the task-owned servers were stopped; built static servers replaced them.
2. [Second attempt](visual-logs/gate-attempt-2/): UI 383 passed, Core 4716 passed / 2 failed / 11 skipped; MCP 535 passed / 4 skipped. The two unchanged process/lifecycle timing tests failed under default parallel collections. Both passed isolated without source/timeout changes ([log](visual-logs/sqlharness-visual-isolated-dotnet.log)). This intermediate tree also preceded the last layout/context corrections.
3. [Third attempt](visual-logs/gate-attempt-3/): UI 384 passed, but TypeScript correctly rejected the sound Select's upload sentinel inferred outside its persisted-selection type. An explicit union generic and removal of the unnecessary assertion corrected this. Standalone typecheck passed. This is erased typing and changes no rendered output.
4. Final `just verify -LogDirectory D:\temp\sqlharness-visual-final-gate-4`: **exit 0**, 5 stages, 13m36s. Build: zero warnings/errors; UI lint/typecheck and **384 tests passed**; Core/Dashboard/CLI **4718 passed, 11 skipped**; MCP **535 passed, 4 skipped**; formatting appropriately skipped (no changed C#). [Final logs](visual-logs/final-gate/).

For the final gate, ignored `bin/Debug/net10.0/xunit.runner.json` files in both .NET test projects set `parallelizeTestCollections: false`, `maxParallelThreads: 1`, `diagnosticMessages: true`. The projects still run concurrently. No test is filtered out, no timeout is increased, and no tracked runner configuration or gate policy changes. Platform/explicit integration skips remain unchanged.

The caret interaction regression was observed RED, then GREEN ([RED](visual-logs/sqlharness-caret-red.log), [GREEN](visual-logs/sqlharness-caret-green.log)). Independent source review is recorded [here](visual-review.md); browser acceptance is separate from that review and from test results.

## Delivery

Pending final gate, local integration and publication. No push, Linux gate or live database acceptance is claimed.
