# Plan 105 final scoped review

Reviewed only `d78f06ea..6d3f7f1c`, the final fixes report, and the directly affected contracts. No tests or gates were rerun in this review.

## Original findings

1. **Advertised diagnostics errors — ADDRESSED.** `ArtifactReader.cs:578-588,607-631,686-690,710-724,755-759` now distinguishes manifests without a statements section from manifests advertising one. Corrupt JSON, oversized input, and non-plain/missing advertised files yield content-free `ArtifactReadException`. Valid legacy records without diagnostic fields remain unavailable. The new offline tests cover each trigger and the legacy case.
2. **Multiple QueryPlans sharing a source key — ADDRESSED.** `PlanMetricsExtractor.cs:86-103` assigns a document-local `PlanQueryOrdinal`; `StatementMetrics.cs:855-866` persists it; `ArtifactReader.cs:630-681` groups by it. Ambiguous old sources lacking the ordinal are omitted and marked partial rather than combined, while uniquely attributable old sources remain readable. The tests cover two statement plans and two batch plans across repetitions, plus both legacy cases.
3. **Workflow pager termination — ADDRESSED.** `AgentOptimizationWorkflowTests.cs:177-213` caps pages and asserts continuation exists and advances until the four expected rows are retrieved.

## New finding in the fix diff

### Important: artifact tool interface guidance was removed to fit tools/list

`McpToolCatalog.cs:383-405,712-714` and `McpRequestToolHandlers.cs:191-212` reduce the artifact tool description to “Saved artifact”, the cursor to “Cursor”, selectors to bare labels, and page size to “Page size; max 512.” The prior interface explained that cursor is zero-based and applies only to pageable sections, the concrete plan variant values, which selectors execute only for `plan`, the source-page defaults, that a byte budget may return fewer rows, and that callers must follow returned continuation. It also explicitly distinguished STATISTICS IO ScanCount from Showplan ActualScans and directed a partial metrics read to exact statement identities. Those meanings remain in `docs/mcp/artifacts.md`, but ordinary MCP callers discover the tool through its descriptions and schemas; the repository contract makes that surface authoritative for arguments. The revised descriptions are materially ambiguous, especially for executable selectors and paging. Reallocate bytes by shortening repeated general prose elsewhere or encode concise per-argument descriptions and the continuation rule here, then retain both fixed- and request-scope catalog ceiling checks. This is a documentation/interface regression caused by this fix wave, not a new product parsing defect.

## Declined to judge

- Fresh Windows full gate and branch-format result: the controller owns these checks for `6d3f7f1c`.
- Live SQL Server and installed-binary behavior: this fix wave provides offline regression evidence only.
- Any source or behavior outside this fix diff: the requested review is scoped to the final wave.

## Assessment

**Spec correctness of the three fixes: approved. Quality/readiness: with fixes; not ready to merge yet.** Restore essential artifact tool contract text and complete the controller's final Windows gate and format check. The focused Core, pager, and catalog results in the final fixes report support the changed behavior, but they do not replace the full gate.
