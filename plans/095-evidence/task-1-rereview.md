# Plan 095 Task 1 Scoped Re-review

## Prior findings

- **Important content-free refusal assertions — ADDRESSED.** The test now parses the refusal envelope, requires the result payload to be absent or null, rejects saved section field names, and checks distinctive fixture values ([McpToolSchemaTests.cs:692](/D:/Dev/sqlharness/.worktrees/plan-095/tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:692), [McpToolSchemaTests.cs:694](/D:/Dev/sqlharness/.worktrees/plan-095/tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:694)).
- **Minor unguarded reconstruction loops — ADDRESSED.** The served loop asserts cursor progress and caps pages; both Core loops assert progress and cap iterations by the source inventory ([McpToolSchemaTests.cs:605](/D:/Dev/sqlharness/.worktrees/plan-095/tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:605), [ArtifactReaderTests.cs:671](/D:/Dev/sqlharness/.worktrees/plan-095/tests/SqlHarness.Tests/ArtifactReaderTests.cs:671), [ArtifactReaderTests.cs:710](/D:/Dev/sqlharness/.worktrees/plan-095/tests/SqlHarness.Tests/ArtifactReaderTests.cs:710)).

## New Critical or Important issues

None found in this scoped fix diff. It changes test assertions, loop guards, reports, and saved test logs; no production code changed.

## Package note

The supplied diff is 43,577 bytes across five files. Its size comes from refreshing the two complete test logs and appending the fix evidence. The log differences are timing and shifted test source-line references from the new assertions, not broad line-ending churn. The meaningful code hunks remain visible and reviewable.

The appended report records final focused runs with the expected RED counts and notes that the strengthened refusal regression passes. I did not rerun tests or broaden the review.
