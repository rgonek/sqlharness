# Plan 105 gate fixture fix report

## Finding and cause

The final Windows gate on `ff0be097` passed Core (4,691 passed, 11 skipped) and UI (372 passed), while the MCP suite had one failure (513 passed, 1 failed, 4 skipped). `McpOutputTests.Mcp_compare_emits_artifact_id_resolved_target_and_truthful_metrics` writes a hand-built `report.json` and manifest but no `statements.json`. It used `ArtifactManifest.ForReport`, which advertises `statements`; the stricter metrics reader therefore correctly returned `ArtifactReadException: Artifact statements are invalid.`

## Correction

The test fixture now overrides its manifest sections to `summary` and `metrics`, matching the files it writes, and asserts diagnostic availability is `unavailable`. No production code or corruption/missing-advertised-file safety behavior changed.

## Verification

The original failing gate result is recorded in `D:/temp/sqlharness-plan105-final-verify-ff0be097/test-SqlHarness.Mcp.Tests.log`. A fresh build and focused test then passed:

```text
dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --no-restore --filter "FullyQualifiedName~Mcp_compare_emits_artifact_id_resolved_target_and_truthful_metrics"
Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1
```

No full gate was rerun in this bounded correction.
