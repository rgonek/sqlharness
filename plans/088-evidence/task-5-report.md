# Task 5 implementation report: Dashboard API and capabilities

## Result

Implemented the operation plan manifest, document graph/properties/raw endpoints, versioned operation source map, and active plan-storage capability state. The old `/api/plans/{hash}` route is removed; Task 6 should use the contracts below. No journal schema change was needed.

`journal.storeSensitive` is checked on every read. When it is false, operation manifest and source-map responses say `plans not stored` and contain no SQL, document identities, or occurrence rows. Raw, graph, and property document routes return HTTP 409 with `error.code = plans_not_stored`, even when the database still contains rows written while the setting was enabled. The synthetic dashboard test seeds a real sensitive plan, then turns the setting off before reading to cover this runtime behavior.

## API contract for Task 6

All endpoints below are under the authenticated `/api` group.

`GET /api/operations/{operationId}/plans` returns the full operation manifest. `state` is `stored` or `plans not stored`; each occurrence is ordered by metric, repetition, document ordinal, and statement index. An occurrence carries `variant`, `parameterSet`, `caseId`, `caseLabel`, `repetition`, `documentOrdinal`, `statementIndex`, `spanStart`, `spanLength`, `statementId`, `statementCompId`, raw-document `documentSha256`, separate plan `shapeHash`, `format`, `matchKind`, `omitted`, and `stored`.

```json
{
  "operationId": 42,
  "state": "stored",
  "occurrences": [{
    "variant": "baseline", "parameterSet": null, "caseId": "case-a", "caseLabel": "Set A",
    "repetition": 1, "documentOrdinal": 0, "statementIndex": 0,
    "spanStart": 0, "spanLength": 29, "statementId": 4, "statementCompId": 1,
    "documentSha256": "<sha256-of-raw-utf8-document>", "shapeHash": "<plan-shape-hash>",
    "format": "showplan-xml", "matchKind": "exact", "omitted": false, "stored": true
  }]
}
```

`GET /api/plan-documents/{sha}/graph` returns `PlanGraphBuildResult` (`engine`, `graph`, `error`). A graph includes statement roots, nodes, edges, estimates, actual counters, warnings and object references. Builder/parser errors are explicit HTTP 422 responses with `graph: null` and a stable error code. `GET /api/plan-documents/{sha}/nodes/{nodeId}/properties` returns `PlanGraphPropertyResult` (`engine`, `nodeId`, `tree`, `error`); the property builder keeps the complete node list and marks truncation in the tree. `GET /api/plan-documents/{sha}` downloads SQL Server XML as `<lowercase-sha>.sqlplan` with `application/xml`; JSON-format documents use `.explain.json`.

`GET /api/operations/{operationId}/source-map?variant=baseline&parameterSet=set-a&repetition=1&caseId=case-a` returns operation selection metadata, `engine`, `algorithmVersion`, the exact `executedText`, document-scoped links, omitted occurrences, and per-document build errors. Each link is `{ "documentSha256", "documentOrdinal", "link": { "nodeId", "spanStart", "spanLength", "kind", "state", "rule" } }`. `kind` is `statement` or `table_reference`; source-map states are `exact`, `inferred`, `ambiguous`, and `none`. `algorithmVersion` is 1. `caseId` may be omitted only when the variant/parameter-set selection resolves to one case; otherwise the API returns 400 with `case_required`.

```json
{
  "operationId": 42, "state": "stored", "engine": "sqlserver",
  "variant": "baseline", "parameterSet": "set-a", "caseId": "case-a", "caseLabel": "Set A",
  "repetition": 1, "executedText": "SELECT * FROM dbo.Parent AS p;", "algorithmVersion": 1,
  "links": [{
    "documentSha256": "<raw-document-sha>", "documentOrdinal": 0,
    "link": { "nodeId": "0:0", "spanStart": 14, "spanLength": 15,
      "kind": "table_reference", "state": "exact", "rule": "..." }
  }],
  "omittedDocuments": [], "errors": []
}
```

The source map uses the exact SQL persisted in `operations.sql_text` for measure/baseline and `operations.candidate_sql_text` for candidate. Task 2's `CompareRunArtifact.ExecutedSql` is `[JsonIgnore]` only for JSON pairing; the operation writer receives the same submitted SQL string, `BenchmarkRunner` retains that value for the run, and SQL Server binds it directly to `CommandText` while binding parameters separately. The source-map builder receives this persisted batch and the occurrence spans as stored; no formatted, original, or `StatementText` fallback is used.

Invalid SHA syntax returns 400; absent operation/document/selection returns 404; gate-disabled document reads return 409; corrupt stored content and plan-builder errors return explicit 422 responses. The manifest has a 1 MiB complete serialized UTF-8 cap and returns 413 on overflow. The complete source-map response, including SQL, links, metadata, and envelope, has a 16 MiB UTF-8 cap and returns 413 on overflow without partial results. Graph responses are capped at 1 MiB including the API result envelope; the existing graph builder also enforces its 10,000-node / 1 MiB graph bound and returns explicit errors rather than clipping. Property responses keep the existing 128 KiB bound and truncation marker. Stored-document decompression rejects raw documents over 16 MiB and validates declared raw size, UTF-8, and SHA-256 before returning content.

Capabilities contract version is 6. `limits.activityJournalStorage.planStorage` reports `{ "state": "stored" | "plans not stored", "enabledBy": "journal.storeSensitive" }`; CLI capabilities pass the active configuration for both global and profile requests.

## RED/GREEN and verification

- RED: the six initial Task 5 dashboard tests failed as expected (**6 failed, 0 passed**); each route returned 404 before the API existed. The added response-boundary test also had a compile-time RED before the internal bound seam was added (`DashboardServer` had no `BoundedPlanSourceMap` method).
- GREEN: the Task 5 + capabilities focused filter passed (**10 passed, 0 failed**) after the response test context was configured with ASP.NET services.
- The adjacent dashboard/reader/graph/source-map/span/capabilities regression filter passed after updating its legacy fixture and route expectation (**262 passed, 0 failed, 0 skipped**):

  ```powershell
  dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter "FullyQualifiedName~DashboardServerTests|FullyQualifiedName~JournalReaderTests|FullyQualifiedName~PlanGraphBuilderTests|FullyQualifiedName~SourceMapBuilderTests|FullyQualifiedName~StatementSpanPairerTests|FullyQualifiedName~CapabilitiesCommandTests"
  ```

- Existing `JournalSeed` used synthetic `A…` and `B…` hashes unrelated to its raw XML. The new read-time integrity check correctly rejected that invalid fixture; the fixture now derives SHA-256 from raw UTF-8 XML and keeps the shape hash separate. The old API test was updated to the replacement raw-document endpoint; no compatibility route was restored.
- `pwsh -NoProfile ./scripts/format-changed.ps1 -Fix` completed with `format: OK`; the final `-Mode working-tree` check also completed with `format: OK`. `git diff --check` passed.
- The full `just verify` gate and full test suite were not run; Task 7 owns final verification.

## Files and self-review

- `DashboardServer.cs`, `JournalReader.cs`, and `DashboardModels.cs`: API routes, gate checks, reader integrity/bounds, and response records.
- `Capabilities.cs` and `CapabilitiesCommand.cs`: current gate state in CLI capabilities, contract version bump, and interface description.
- Dashboard/capabilities tests plus `JournalSeed`: synthetic API coverage, both response byte-cap seams, gate-on/off behavior, and content-address-correct fixture identities.
- `plans/088-plan-visualization.md` and `plans/README.md`: Task 5 implementation status and report link.

No sensitive values were redacted beyond the existing credential policy; caller SQL, plan XML, and target names remain readable when the gate permits them. There are no live database writes or schema changes. Task 6 must replace its old `/api/plans/{hash}` calls, handle 409/413/422 explicitly, and send the exact variant/parameter-set/repetition/case selector needed by the source-map route. Task 7 still owns README/MCP documentation and the full final gate.
