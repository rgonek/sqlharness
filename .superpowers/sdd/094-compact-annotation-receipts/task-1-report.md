# Plan 094 Task 1 report

## Scope and outcome

Frozen the compact annotation contract with served-tool regressions in both fixed and request scope. No production code changed. The regressions intentionally remain RED until Task 2 implements the amended receipt contract.

## Baseline before edits

Command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~AnnotateToolTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpOperationAnnotationWiringTests"
```

Result: `Passed! - Failed: 0, Passed: 65, Skipped: 0, Total: 65, Duration: 6 s`.

## RED command and result

The final focused command adds the served receipt fixture to the requested test filter:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~AnnotateToolTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpOperationAnnotationWiringTests|FullyQualifiedName~ServedAnnotationReceiptTests"
```

The final result was `Failed! - Failed: 9, Passed: 66, Skipped: 0, Total: 75, Duration: 7 s`. The full captured output is in `task-1-red-output.txt`.

The served calls use the MCP client/server transport and a temporary `SQLHARNESS_HOME` activity database seeded with the process caller key, eligible refs, a foreign ref, an expired/deleted ref, and prior annotations. Both fixed and request scope are exercised. The current served tool rejects the new `responseDetail` and call-level `maxResultBytes` inputs, so receipt/full calls return the generic invocation error before applying writes. The tests fail on the missing success/result contract; the minimum-budget full-detail cases also fail because that old error omits the effective budget and supported next action. The transaction-preservation assertions run before those diagnostic assertions and confirm prior labels/keep values remain present.

The served schema test fails at `responseDetail` missing from the enabled annotation tool's schema. Its later assertions require `maxResultBytes` and default `responseDetail: "receipt"`. The off-mode schema test also submits `responseDetail` to a normal query and requires rejection.

## Contract coverage added

- At 4096 bytes, fixed and request served calls submit 20 complete 120-code-point multibyte labels, request 65536 at call level to prove the process ceiling cannot be raised, require 20 success outcomes, check the actual response byte count, and verify persisted labels.
- At 65536 bytes, fixed and request served calls request full detail and verify all resulting labels plus the longest eligible tool name, `snapshot`.
- At 4096 bytes, fixed and request full-detail calls must reject and preserve all 20 pre-existing labels and keep flags, with a clear effective-budget/receipt action message.
- Mixed outcomes cover repeated refs in order, last-write persistence, foreign, retention-expired/deleted, and unknown refs. Unavailable outcomes must be byte-equivalent in shape and contain only `operationRef` and `status`; the served response must fit 4096 bytes.
- Separate unsupported `responseDetail` and out-of-range `maxResultBytes` calls must fail before writes.
- Existing off-mode catalog tests continue checking the feature is absent and call-time labels are rejected; the added off-mode assertion rejects `responseDetail` on a normal served query. Existing handler tests retain full-detail rollback and adequate-budget behavior at the handler layer.

## Files

- `tests/SqlHarness.Mcp.Tests/ServedAnnotationReceiptTests.cs` — fixture host and fixed/request served contract cases.
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — enabled annotation schema and annotations-off argument rejection.
- `task-1-red-output.txt` — captured final focused RED output.

## Self-review and concerns

`git diff --check` is clean. The fixture uses the actual `McpToolCatalog.Wire` process path for both scope modes and seeds the same journal database used by served calls. The red tests are contract assertions, not compile failures. The minimal-budget full-detail cases currently stop at the new-argument rejection in the pre-Task-2 product; after implementation they will exercise response-size rollback and its error text. No additional gate was run because this task adds intentionally failing regression tests only.
