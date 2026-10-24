# Plan 105 final fixes report

## Findings addressed

### Important: advertised diagnostics storage errors

Metrics reads now distinguish a missing optional diagnostic payload in a valid legacy artifact from failure to read a payload the manifest advertises. When `statements.json` is advertised but corrupt JSON, oversized, or not a readable plain file, metrics and STATISTICS IO reads return the existing safe artifact failure rather than successful empty evidence. Parse exceptions do not include stored content. Valid older statement records without diagnostic fields still load with unavailable status.

### Important: QueryPlan identity

`PlanMetricsExtractor` assigns `planQueryOrdinal` in document order to each QueryPlan. Persisted diagnostics and `ArtifactPlanDiagnosticDistribution` carry it; grouping and ordering include it, so multiple QueryPlans in one statement or batch remain distinct in every repetition. Legacy diagnostic records deserialize the field as null. The reader does not infer a QueryPlan ordinal from unrelated artifact order: if multiple legacy sources share the same known identity within one repetition, it omits that ambiguous group, reports partial availability, and counts omissions. A uniquely attributable old source remains readable with a null QueryPlan ordinal.

### Minor: workflow pager termination

The workflow test paging helper limits the number of pages, checks each cursor advances, and rejects missing continuation before it has retrieved the expected rows. It permits exhaustion only once all expected rows are present.

## Interface and documentation

The artifact schema and MCP descriptions expose `planQueryOrdinal` with diagnostic fields. Descriptions were compacted in both fixed-scope and request-scope catalogs to keep the existing tools/list byte limit. `docs/mcp/artifacts.md` records the exact attribution, compatibility and safe failure rules. No limits were increased and no raw XML was added to public retrieval.

## RED/GREEN evidence

The initial focused Core test run was RED: 5 of 6 tests failed for the expected missing behavior (three bad advertised file cases, missing QueryPlan identity, and ambiguous legacy attribution); the valid legacy unavailable case passed. After the changes, the expanded Core filter passed 160/160. The pager regression passed 1/1.

The MCP catalog was initially above its existing request-scope ceiling. After shortening the repeated artifact descriptions and moving schema assertions onto the `section` argument where the contract is exposed, the final narrow MCP run passed 2/2:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Operation_annotation_members_are_gated_by_the_startup_config|FullyQualifiedName~Tools_list_explains_optimization_evidence_semantics_in_both_modes"
Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2
```

The test reports both fixed-scope and request-scope catalogs below 32,768 UTF-8 bytes. An earlier broader MCP filter had 74 passes and the two catalog/assertion failures addressed above; that broader filter was not rerun in this bounded fix wave.

## Limits of this evidence

The controller's earlier Windows run compiled pre-fix `d78f06ea` and passed UI, but stopped before the full gate. It does not verify this fix tree. No fresh full Windows gate or branch-format run was performed here; the final gate and independent scoped review remain pending. No live database, installed binary, merge, or push was involved.
