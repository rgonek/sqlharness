# Plan 107 follow-up: source list classification

**DONE on branch `plan-107-followup`; tested implementation commit `a8aded98`.** Final Windows gate and branch formatting passed; Luna xhigh task review and Sol low whole-branch review approved with no findings. Not integrated or pushed. See the [implementation report](follow-up-implementation.md), [task review](follow-up-review.md), [whole-branch review](follow-up-whole-branch-review.md), and [execution ledger](follow-up-execution-ledger.md).

## Scope

Correct the offline compare validation path for typed SQL Server list parameters. The known-source MCP test host built from task-2 head `35abbcd6fdf08dccaf7e72bce731b448d1c383e8` returns `safety_rejected` for every one of 12 request combinations, including ordinary identical baseline/candidate SQL with one named int-list parameter. The task-2 commit changed tests and evidence only; production source remains at assigned base `4e36feebe7b7d48b72225c5a9c3a9c19b90a7a50`. No database connection is involved.

The detailed isolated-host run reports the loaded runtime assemblies directly: `SqlHarness.Mcp` and `SqlHarness.Core` both have informational version `0.0.0-dev+35abbcd6fdf08dccaf7e72bce731b448d1c383e8`, loaded from the test output directory. The test assembly is reported separately. Full paths and MVIDs are in [the exact MCP test log](task-2-mcp-tests.log).

Observed failure groups:

- `IN @Ids`: initial compare-family preflight classifies the SQL before parameter-set list inputs are available, producing `ParseError`.
- `IN (@Ids)`: per-case validation combines generated list bindings from setup, baseline, and candidate as if they were separate supplied parameters. Repeated non-empty lists therefore report `@Ids1` collisions, and repeated empty lists report `@Ids` collisions.

The caller-supplied `@Ids1` case must continue to reject a real collision. Keep caller SQL, parameter names and values intact in diagnostics.

## Required follow-up coverage

- Offline Core and real-SDK MCP compare paths using identical baseline and candidate SQL for both supported `IN` spellings.
- Singleton, multi-value and empty lists; multiple named sets; and one list reused by a valid setup statement and both variants.
- A caller-supplied `@Ids1` collision remains rejected for the parenthesized path.
- Every dry-run asserts zero connection attempts. No database, credentials, historical workload replay or executable installation is needed.
- Preserve request/response evidence from [the task-2 MCP log](task-2-mcp-tests.log) and [the request shapes](task-2-request-shapes.json).

The follow-up changes only the compare list expansion/classification path and its tests. It leaves the independent caller collision behavior intact and does not change blanket safety limits or unrelated list input behavior.

## Evidence and boundary

The original intentionally failing expected-contract runs remain preserved in [Core red log](task-2-core-red-contract.log) and [MCP red log](task-2-mcp-red-contract.log), alongside the exact [request shapes](task-2-request-shapes.json). Follow-up Core and MCP RED/GREEN commands and outcomes, source diagnosis, and final gate evidence are recorded in the linked task report.
