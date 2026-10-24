# Plan 113 Task 2: focused offline confirmation

Tested revision: `49f4ea9679fd20dedf95c3f23b7eae87c5718e45` (`plan-113`), Windows.

## Inspected behavior and expectation

- `src/SqlHarness.Core/ArtifactDirectoryPublisher.cs:79,92-109` writes staging once, then retries only the atomic staging-to-final directory move for the bounded Windows access-denied case. The retry path must not re-run artifact generation. Existing `tests/SqlHarness.Tests/ArtifactDirectoryPublisherTests.cs` fixtures inject move operations and cover a transient denial (two move attempts, one staging write), persistent denial (four attempts, original exception preserved, cleanup), and non-retry move failures. No missing meaningful boundary was identified.
- `src/SqlHarness.Core/CompareMatrixRunner.cs:32,54` retains completed runs on matrix failure. `tests/SqlHarness.Tests/Journal/JournalingBenchmarkTests.cs:151` covers journaling those completed runs for a failed outcome.
- MCP has no filesystem publication-retry test. The adjacent `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs:415,441` cases cover projection of partial matrix coverage and partial measure stop coverage; they are downstream envelope checks, not publisher or journal-retention evidence.

## Commands and results

| Command/filter | Expected | Observed | Raw log |
|---|---|---|---|
| `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "ArtifactDirectoryPublisherTests|CompareMatrixTests|JournalingBenchmarkTests"` | Matching Core fixtures run and pass | Exit 0; 84 passed, 0 failed, 0 skipped, 84 total | [Core focused log](task-2-core-focused.log) |
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "ArtifactDirectoryPublisherTests|CompareMatrixTests|JournalingBenchmarkTests"` | Discover relevant MCP tests; zero discovery is not pass evidence | Exit 0, but explicitly **zero tests matched**. This is recorded as zero discovery, not success evidence. | [Required MCP filter log](task-2-mcp-required-focused.log) |
| `dotnet test tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj --filter "FullyQualifiedName~McpOutputTests.Partial_matrix_puts_coverage_on_result_never_error|FullyQualifiedName~McpOutputTests.Partial_measure_keeps_coverage_on_error_matrix_stop"` | Adjacent partial-result projection tests pass | Exit 0; 2 passed, 0 failed, 0 skipped, 2 total | [MCP partial-output log](task-2-mcp-partial-output.log) |

All runs were on Windows, so the Core `[WindowsFact]` retry tests ran rather than being skipped. The initial Core command built the dependent dashboard UI; npm reported existing package audit findings and Vite emitted a large-chunk warning, but the build and filtered test command completed with exit 0.

## Outcome and limits

Existing Core coverage is sufficient for this offline confirmation. No tests or production code were changed. The focused tests establish behavior of the checked-out source and injected local filesystem fixtures. They do not identify the historical external process, prove the loaded binary matched this revision, reproduce the old event, or provide live database/install acceptance. The historical access-denied cause remains unknown.
