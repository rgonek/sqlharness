# Plan 105 gate fixture correction

The full-gate MCP run on `ff0be097` exposed one test-fixture mismatch. `Mcp_compare_emits_artifact_id_resolved_target_and_truthful_metrics` creates only `report.json`, but serialized `ArtifactManifest.ForReport` advertises every supported section, including `statements`. The stricter reader correctly rejected the absent advertised `statements.json` while reading metrics.

The fixture now advertises only the sections it actually serves (`summary` and `metrics`) and asserts that plan diagnostics remain unavailable. Production error handling was not weakened or changed.

Fresh-build focused verification:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Mcp_compare_emits_artifact_id_resolved_target_and_truthful_metrics"
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

No full gate was rerun in this fix wave.
