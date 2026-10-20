# Plan 094 Task 1 report

## Scope

Added a served-catalog schema regression for `sqlharness_annotate`: the enabled tool must expose `responseDetail` and `maxResultBytes`, and `responseDetail` must default to `receipt`. No product code changed.

## Baseline before edits

Command:

```powershell
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj -p:IsTestProject=true --filter "FullyQualifiedName~AnnotateToolTests|FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpOperationAnnotationWiringTests"
```

Result: `Passed! - Failed: 0, Passed: 65, Skipped: 0, Total: 65, Duration: 6 s`.

## RED after edit

Same command. Build succeeded and test discovery found one test assembly. The focused run failed as intended:

```text
[xUnit.net 00:00:04.85] SqlHarness.Mcp.Tests.McpToolSchemaTests.Operation_annotation_members_are_gated_by_the_startup_config [FAIL]
Error Message:
 Assert.Contains() Failure: Item not found in collection
 Collection: ["items"]
 Not found: "responseDetail"
Stack Trace:
 ...McpToolSchemaTests.cs:line 332
```

This demonstrates the currently served enabled annotation schema does not include the newly contracted receipt-mode argument. The second `maxResultBytes` and default assertions are reached after this first missing-property assertion is addressed.

## Files

- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — schema regression assertion on the enabled served annotation tool.

## Self-review

The addition changes tests only and runs through `ServedCatalog`/`ListToolsAsync`, so the failure is against the actual served catalog schema. Existing focused tests already cover mixed 20-item handler outcomes, unavailable uniformity, and full-detail rollback/adequate-budget behavior at the handler layer.

## Concerns / remaining Task 1 coverage

This is a partial RED. It does not yet exercise served annotation results with seeded eligible operations in fixed and request scope, 20 multibyte labels at 4096 in receipt mode, full mode at both budgets, or the complete call-level budget/unknown-expired-foreign/duplicate/longest-tool matrix. Existing wiring tests cover call-time annotation forwarding for query in fixed and request scope but do not create served annotation receipts. Task 2 must add the corresponding full served-tool coverage before implementation is considered complete. No production changes were made.
