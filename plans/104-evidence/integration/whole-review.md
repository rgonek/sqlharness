# Plan 104 + 105 whole-integration review

Scope: `9718b052..b4865845` (product merge `d2f2b060`; later commits are evidence-only for `src`, `tests`, and `docs`). I reviewed the integration brief and report, exact conflict resolutions, product changes, prior Plan 104 whole rereview, and retained focused verification. This review made no source, index, branch, or test changes.

## Strengths

- SQL Server STATISTICS IO classification occurs before temporary-name normalization. Typed identities keep the anonymous aggregate separate from an ordinary object named `table variables`; reports allocate collision-safe labels across both compare sides (`src/SqlHarness.Core/Diagnostics.cs:8`, `src/SqlHarness.Core/BenchmarkRunner.cs:189`, `src/SqlHarness.Core/CompareCellRunner.cs:283`). Legacy artifact names remain historical data rather than being inferred again.
- The merge keeps Plan 105 artifact selectors, page defaults and limits, continuation, clipping, and diagnostics language alongside Plan 104 per-case counter semantics (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711`). SQL Server capabilities contract 9 contains the experimental IO rule; PostgreSQL omits it (`src/SqlHarness.Core/Capabilities.cs:68`, `src/SqlHarness.Mcp/McpOperationMapper.cs:156`).
- Tests exercise collisions, repeated-run distributions, legacy reads, schema contracts, and annotation budget. Retained focused logs report Core 288/288, MCP schema 65/65, MCP token/output/workflow 117/117, and annotation catalog 1/1. Branch formatting reports exit 0 for 21 C# files.

## Issues

### Critical (Must Fix)

None found.

### Important (Should Fix)

None found.

### Minor (Nice to Have)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:711-713`: measure/compare no longer name `sqlharness_artifact` or point directly to its `metrics` section for optional memory, compile, and IO diagnostics. The artifact tool's section description still exposes those sections, so discovery remains possible. A compact route hint in measure/compare would make the optional evidence easier to find; keep the annotation-enabled tools/list within its 32,768-byte budget.

## Declined to judge

- Combined Windows `just verify` on the integrated tree: controller-owned and still in progress when this review was requested; focused logs are not a replacement.
- Linux, installed-binary, and live database acceptance: no such run is part of this offline review.
- Cross-version naming guarantee for the bare `#` plus eight-hex SQL Server IO signature: the user explicitly accepted an experimental, observed rule with this evidence limit.
- Protected dirty main checkout and final fast-forward: controller owns preservation and integration; this review assesses the prepared branch.

## Assessment

**Ready to merge?** Yes, subject to the controller's combined Windows gate and safe local main integration.

**Reasoning:** The reviewed product merge preserves both plans' observable contracts and the resolved conflicts have focused passing coverage. The only issue found is a small discovery hint; it does not block the local merge.
