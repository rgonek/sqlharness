# Plan 103 isolation assertion follow-up

**ADDRESSED.** The P2 from gate-test-rereview.md is resolved by `874b4fc9`.

`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1271` now independently requires the full literal `Ordered sets: isolated sessions/setup`. Removing or reversing the isolation guidance will fail the assertion in the existing fixed/request catalog loop. No new breakage found in this one-line hunk; product behavior is unchanged.

Scope: supplied `e497b69e..874b4fc9` diff and this finding only. No tests rerun, broader review, or HEAD mutations. Fresh formatter and Windows gate are pending and must cover `874b4fc9`.
