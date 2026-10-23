# Plan 108 Task 2 report

## Result

Added artifact-kind-specific invalid plan variant messages in the shared `ArtifactReader`, preserving the existing Safety exit code and validation order after manifest and scope-owner checks. The two served MCP tool descriptions and `docs/mcp/artifacts.md` now explain the measure default and compare requirement.

## Evidence

- RED: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --no-restore --filter FullyQualifiedName~ArtifactReaderTests` — exit 1; 6 failed, 71 passed. The failures were the new accepted-value diagnostic assertions: existing generic errors did not name `query` or `baseline, candidate`. Wrong-owner assertions passed, confirming owner rejection precedes variant diagnostics.
- GREEN: same command after implementation — exit 0; 77 passed, 0 failed, 0 skipped.
- Diff check: `git diff --check` — exit 0.
- No full gate run; final gate is assigned to Task 3/controller.

## Changed files

- `src/SqlHarness.Core/ArtifactReader.cs`
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Mcp/Tools/McpRequestToolHandlers.cs`
- `docs/mcp/artifacts.md`

## Concerns

No task-scope or correctness concern identified. Offline request-level MCP omission coverage and the full gate remain for Task 3/controller.
