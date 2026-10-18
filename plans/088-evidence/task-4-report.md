# Task 4 implementation report: source map

## Result

Added `SourceMapBuilder` to compute versioned SQL Server plan links from the exact executed SQL, a bounded Showplan document, the stable `PlanGraph`, and statement spans persisted by Task 2. The implementation parses SQL with ScriptDom, expands CTE bodies per reference, and links plan objects to their own SQL source-reference spans without reading module definitions or relying on descendant operator objects.

Task 5 call contract:

```csharp
SourceMapBuilder.Build(
    string? executedText,
    string? showplanXml,
    PlanGraph graph,
    IReadOnlyList<SourceMapStatementSpan> statementSpans)
```

`SourceMapResult` carries `Engine`, `AlgorithmVersion` (currently `1`), and `Links`. Each `SourceLink` carries stable graph `NodeId` (or `null` for a surviving SQL reference with no operator), nullable executed-text span offsets, `Kind` (`statement` or `table_reference`), `State` (`exact`, `inferred`, `ambiguous`, or `none`), and an explanatory `Rule`. The raw Showplan argument is used only to confirm owned `UserDefinedFunction` markers for Compute Scalar nodes, after the existing validated/limited loader accepts the document. XML elements below nested RelOps are excluded from the owning node.

## Matching and ambiguity rules

- Statement links use the persisted statement pairing state. `exact` remains exact; prefix, PSP-suffix, and normalized pairings become `inferred`; ambiguous or unusable spans do not produce an exact link.
- A plan object first matches ScriptDom source references by table, and by schema/database when both sides provide them. A unique source reference is exact. For repeated instances of the same object, a positive `TableReferenceId` chooses the corresponding instance in expanded source order; a unique alias can settle the remaining same-object candidates. Otherwise each candidate is returned as ambiguous.
- CTE definitions are expanded each time a CTE reference is visited, preserving each underlying table-reference source span and expanded order. Derived tables, APPLY, nested queries, subqueries, and DML targets are visited through ScriptDom scopes.
- Key Lookup `TableReferenceId = -1` first uses a unique same-object reference. If that is repeated, it follows the nearest graph-related resolved seek for the same object. Equal-nearest seeks to different references stay ambiguous; no seek evidence means the candidates stay ambiguous.
- When a plan object is absent from executed text, only a single unmatched possible module reference in the statement's single ScriptDom query scope receives an inferred link. If multiple query scopes exist, candidates remain ambiguous; graph topology does not establish lexical scope. A single known scope with no candidate produces a node-level `none` link. ScriptDom alone cannot distinguish a view from a base table without catalog/module-definition access; therefore unmatched named sources are treated only as possible modules and fallback remains inferred, never exact. No module candidate means no invented link.
- SQL references with no surviving mapped operator emit `none` links. A compute-scalar function call is linked only when an owned Showplan `UserDefinedFunction` marker matches one ScriptDom function call; built-ins without that marker receive no function link.

## RED/GREEN and formatting evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SourceMapBuilderTests` failed while compiling the new tests because `SourceMapResult` and `SourceLink` had not yet been implemented.
- GREEN: the same focused command passed after implementation: **13 passed, 0 failed, 0 skipped**.
- Focused regression command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceMapBuilderTests|FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~StatementSpanPairerTests"` passed: **36 passed, 0 failed, 0 skipped**.
- Changed-file format check initially reported only final-newline diagnostics in the two new C# files. `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix` exited 0 (`format: OK`), then `pwsh -NoProfile ./scripts/format-changed.ps1 -Mode working-tree` exited 0 (`format: OK`) for both C# files.

The full `just verify` gate was not run for this task; Task 7 owns final full-gate verification.

## Files and self-review

- Added `src/SqlHarness.Core/Plans/SourceMapBuilder.cs`.
- Added `tests/SqlHarness.Tests/SourceMapBuilderTests.cs` with handwritten synthetic Showplan/SQL cases for CTE instances, repeated table instances, repeated aliases in derived tables, view/module alias collisions, MSTVF and APPLY invocations, owned scalar-UDF markers, eliminated branches, seek plus Key Lookup, ambiguous modules, and DML targets.
- Updated the Task 4 checklist and `plans/README.md` status.

Self-review found no schema, `journal.storeSensitive`, UI Showplan-parsing, module-fetching, or live-database changes. Remaining limitation: module inference is necessarily conservative without catalog metadata; unresolved cases remain ambiguous or unlinked as described above.

## Review-fix round 1

The scoped review found a mismatch between ScriptDom traversal order and Showplan `TableReferenceId` ordering, plus module fallback that could use statement-wide unmatched references without scope evidence. Round 1 made FROM visitation precede select-list expressions and limited module inference to a scope the implementation could identify. Its initial scope resolver also used the nearest directly resolved graph node; round 2 removes that topology-based assumption because operator adjacency does not prove lexical SQL scope. The final rule permits module inference only when the statement has exactly one ScriptDom query scope. Multiple scopes leave candidates ambiguous; a single known scope with no candidate emits a node-level `none` link. Existing possible-module treatment remains intentionally broad because module-definition/catalog lookup is out of scope, but it cannot create an exact match.

The review regressions now assert that each CTE operator links to both the shared CTE body span and its own `a` or `b` invocation span; view-internal `Partners` TRI 1 infers to `PartnerView` while outer `Partners` TRI 2 exactly links to the base table; and inline-TVF internal `Customer` TRI 1 infers to the function invocation while outer `Customer` TRI 2 exactly links to the APPLY query's table. Another regression establishes that a nested select-list subquery does not consume the outer FROM's TRI 1. Two scope-fallback regressions prove that unresolved modules spanning outer/inner scopes remain ambiguous and that an unresolved inner-scope plan object does not infer against an outer-scope view.

### Review-fix RED/GREEN and verification evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SourceMapBuilderTests` failed as expected: **5 failed, 13 passed, 0 skipped, total 18**. The failures were `Cte_instances_resolve_by_table_reference_id`, `View_internal_partner_and_outer_partner_map_to_distinct_spans_by_table_reference_id`, `Table_reference_id_counts_outer_from_before_select_list_subquery`, `Inline_tvf_internal_alias_collision_does_not_capture_outer_apply_table`, and `Unresolved_inner_scope_object_does_not_infer_from_outer_scope_module`.
- GREEN: the same focused command passed: **18 passed, 0 failed, 0 skipped**.
- Post-format focused regression command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceMapBuilderTests|FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~StatementSpanPairerTests"` passed: **41 passed, 0 failed, 0 skipped**.
- `git diff --check` exited 0; it emitted only Git's LF-to-CRLF normalization notices. `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix` exited 0 (`format: OK`), followed by `pwsh -NoProfile ./scripts/format-changed.ps1 -Mode working-tree` with the same result.

The full `just verify` gate remains assigned to Task 7 and was not run in this review-fix round. No schema, UI, sensitive-storage, live-database, or module-definition fetch behavior changed. The Task 5 source-map input signature and versioned result contract remain unchanged.

## Review-fix round 2

The second scoped review showed that a graph-nearest resolved operator cannot reliably establish an unresolved node's lexical SQL scope: Showplan topology may connect or reorder operators across nested query blocks. `ResolveSingleQueryScope` now returns a scope only when ScriptDom found exactly one query scope in the statement. This preserves the required standalone view and inline-TVF inference cases while making multi-scope module candidates ambiguous even if a graph neighbor maps directly to one block. No plan-graph edge, distance, or operator ordering is used as scope evidence.

The new nested-scope regression has an unresolved `InternalEntity` plan object adjacent to an exactly matched outer `Customer` node, with possible `InnerView` and `OuterView` references in different query scopes. It asserts both candidates are `ambiguous` and neither receives an inferred link. The prior inner-match case now also asserts ambiguity instead of treating graph adjacency as a reason to return `none` for the outer candidate.

### Review-fix round 2 RED/GREEN and verification evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~SourceMapBuilderTests` failed as expected: **2 failed, 17 passed, 0 skipped, total 19**. `Outer_graph_neighbor_does_not_infer_nested_scope_module` received one outer `OuterView` link instead of two ambiguous links; `Unresolved_object_with_multiple_query_scopes_stays_ambiguous_even_with_inner_match` received `none` rather than the expected ambiguous `OuterView` candidate.
- GREEN: the same focused command passed after removing topology-based scope inference: **19 passed, 0 failed, 0 skipped**.
- Post-fix focused regression command: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceMapBuilderTests|FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~StatementSpanPairerTests"` passed after formatting: **42 passed, 0 failed, 0 skipped**.
- `pwsh -NoProfile ./scripts/format-changed.ps1 -Mode working-tree` first reported only final-newline diagnostics and exited 1; `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix` exited 0 (`format: OK`), and the repeated working-tree check exited 0 (`format: OK`). `git diff --check` exited 0 with only Git's LF-to-CRLF normalization notices.

The full `just verify` gate remains assigned to Task 7. No single-scope view or inline-TVF/APPLY inference case was lost; no schema, UI, sensitive-storage, live-database, or module-definition fetch behavior changed. The Task 5 source-map input signature and algorithm version remain unchanged.
