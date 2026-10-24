# Plan 115 whole-branch review

Range: `9718b052f40335d3647cd96750d80431248f9140..2d75e6658c3a23a5b733187c2337761f770150c0`. Reviewed the complete branch diff, task brief, implementation report, task review and rereview, plus focused surrounding query dry-run handling. I did not rerun tests. The full Windows `just verify` result was pending at review time.

## Strengths

- Selection happens after execution; original source ordinals and source-order normalization are preserved. The SQL hash uses the mapped SQL string's exact UTF-8 bytes (`McpToolCatalog.cs:115-134`).
- Null requests keep legacy response fields, while opted-in results carry the source count, selected ordinals, hash and set-only omission count (`AgentOutputProjection.cs:1190-1205`). The earlier omission-count and schema item-minimum findings are fixed.
- Fixed and request schemas, allowlists and documentation are updated. Offline tests exercise selection order, empty and invalid values, missing result evidence, minimum-budget clipping and full MCP-response bytes.

## Issues

### Critical

None.

### Important

1. **Selected query dry runs fail after successful validation** — `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:120-125`. When `dryRun: true` and `resultSetOrdinals` is non-null, the query module returns a successful `SqlHarnessDryRunReport` (`src/SqlHarness.Core/SqlHarnessModule.cs:228-236`), but this branch requires a `SqlHarnessQueryReport` and replaces it with a safety error. This changes the existing offline validation behavior even for `resultSetOrdinals: []`, contrary to the brief's requirement that execution and safety checks remain unchanged. Return the successful dry-run outcome before checking result-set evidence; the selector has no executed result sets to project. Add a served fixed/request regression proving `dryRun: true` with a valid selector remains a successful offline validation and makes no database connection. Keep the missing-evidence error for successful, non-dry-run executions.

### Minor

None.

## Declined to judge

- Live provider behavior and live database acceptance: the plan explicitly requires offline evidence and authorizes no live database.
- Complete Windows `just verify`: it was running under the controller; no result was available to this reviewer.
- Repository archival plan/index/status changes: controller work outside this product diff, scheduled after verification and review.

## Recommendations

Fix the dry-run compatibility path and add the focused regression, then confirm the controller's full gate result. Recheck this finding in the one available fix wave.

## Assessment

**Ready to merge? No.** The selected dry-run request currently converts a valid offline success to an error. The rest of the reviewed selection path and prior fixes appear consistent with the brief, subject to the pending Windows verification result.
