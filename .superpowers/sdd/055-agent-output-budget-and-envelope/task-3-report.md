# Task 3 report: compact query projection

## RED

Command:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentOutputTests"
```

Initial output failed to compile the newly added expectations because the shared version constant did not exist yet:

```text
AgentOutputTests.cs(28,22): error CS0103: The name 'AgentEnvelopeSchema' does not exist in the current context
AgentOutputTests.cs(160,22): error CS0103: The name 'AgentEnvelopeSchema' does not exist in the current context
```

## GREEN

Focused CLI projection command:

```powershell
dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~AgentOutputTests"
```

Actual output:

```text
Passed!  - Failed:     0, Passed:    49, Skipped:     0, Total:    49
```

Required combined command:

```powershell
dotnet test SqlHarness.sln --filter "FullyQualifiedName~AgentOutput|FullyQualifiedName~McpTokenBudget|FullyQualifiedName~Canonical|FullyQualifiedName~Equivalence"
```

Actual output:

```text
Passed!  - Failed:     0, Passed:    24, Skipped:     0, Total:    24 - SqlHarness.Mcp.Tests.dll
Passed!  - Failed:     0, Passed:   188, Skipped:     0, Total:   188 - SqlHarness.Tests.dll
```

## Changes

- Added compact query/result-set/column records. Query columns use explicit CLR aliases, and the query projection no longer includes target identity, canonical result hash, or raw footprint.
- Added retained-cell clipping metadata with result-set-local row/column positions, source and returned lengths, UTF-16-safe string prefixes, and byte-counted binary prefixes.
- Omitted empty messages and default omission counters while retaining zero `recordsAffected` and empty-result schemas.
- Added the shared envelope schema version `2` used by CLI and MCP; updated capabilities and output documentation.
- Kept canonical report and equivalence data unchanged.

## Concerns

No Task 3 concerns remain. Full platform gates were intentionally not run; this task requested the scoped solution filter only.
