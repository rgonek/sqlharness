# Plan 107 follow-up: source list classification

## Scope

Correct the offline compare validation path for typed SQL Server list parameters. The known-source MCP test host built from task-2 head `35abbcd6fdf08dccaf7e72bce731b448d1c383e8` returns `safety_rejected` for every one of 12 request combinations, including ordinary identical baseline/candidate SQL with one named int-list parameter. The task-2 commit changed tests and evidence only; production source remains at assigned base `4e36feebe7b7d48b72225c5a9c3a9c19b90a7a50`. No database connection is involved.

The detailed isolated-host run reports the loaded runtime assemblies directly: `SqlHarness.Mcp` and `SqlHarness.Core` both have informational version `0.0.0-dev+35abbcd6fdf08dccaf7e72bce731b448d1c383e8`, loaded from the test output directory. The test assembly is reported separately. Full paths and MVIDs are in [the exact MCP test log](task-2-mcp-tests.log).

Observed failure groups:

- `IN @Ids`: `ParseError` while classifying the expanded baseline, or setup when the list is reused by setup and variants.
- `IN (@Ids)`: generated list bindings are reported as collisions (`@Ids1` for non-empty values and `@Ids` for an empty list). A possible cause is duplicate generated bindings during compare classification, but the recorded rejection message and phase do not establish that mechanism. Setup is not required to reproduce it, so whether setup, baseline/candidate consolidation, or another validation path causes the collision remains unresolved. Confirm the cause from source before choosing an implementation point.

The caller-supplied `@Ids1` case must continue to reject a real collision. Keep caller SQL, parameter names and values intact in diagnostics.

## Required follow-up coverage

- Offline Core and real-SDK MCP compare paths using identical baseline and candidate SQL for both supported `IN` spellings.
- Singleton, multi-value and empty lists; multiple named sets; and one list reused by a valid setup statement and both variants.
- A caller-supplied `@Ids1` collision remains rejected for the parenthesized path.
- Every dry-run asserts zero connection attempts. No database, credentials, historical workload replay or executable installation is needed.
- Preserve request/response evidence from [the task-2 MCP log](task-2-mcp-tests.log) and [the request shapes](task-2-request-shapes.json).

This follow-up should change only the compare list expansion/classification path and its tests. Leave the independent caller collision behavior intact. Do not change blanket safety limits or unrelated list input behavior.

## Evidence and boundary

The intentionally failing expected-contract runs are preserved in [Core red log](task-2-core-red-contract.log) and [MCP red log](task-2-mcp-red-contract.log). Green characterization coverage records current behavior; it does not claim the requested list workflow works. Implement and review this follow-up separately before changing those characterization expectations to successful dry-runs.
