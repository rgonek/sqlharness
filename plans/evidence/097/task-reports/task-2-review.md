### Spec Compliance

- ❌ Issues found: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:705`, `:706`, `:707`, and `:708` describe fixed parameters as mutually exclusive with parameter-set inputs. The contract permits non-overlapping fixed parameters to be shared across every set; only name collisions are rejected. The `parameterSets` description also drops the requirement that declared parameter shapes match across sets.
- ⚠️ Cannot verify from diff: the unchanged CLI operation-construction path must pass `null` when `--param-set` is omitted, while preserving explicitly supplied empty collections where representable. `SqlHarnessModule.ResolveMeasureParameterSets` now relies on this distinction.

### Strengths

- Core matrix and set resolution accept one case while retaining the 32-case maximum; explicit empty inputs remain rejected (`src/SqlHarness.Core/ParameterSetResolver.cs:167`, `src/SqlHarness.Core/SqlParameterMatrix.cs:90`).
- MCP fixed and request tool schemas pin `minItems: 1` and `maxItems: 32` for sets, files, and matrix values in both benchmark tools (`tests/SqlHarness.Mcp.Tests/McpParameterBatchTests.cs:44`).
- Named compare-set identity is carried into run artifacts, and the regression checks the report, artifact index, runs, and journal reproduction identity (`src/SqlHarness.Core/CompareMatrixRunner.cs:119`, `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs:353`).
- The implementer report includes the dedicated `McpParameterBatchTests` discovery output for both fixed and request scope. Tests were not rerun for this review.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:705`, `:706`, `:707`, `:708`: the parameter-set field descriptions say the batch form is exclusive with fixed parameters, which incorrectly rules out valid non-overlapping fixed parameters. The `parameterSets` description at `:707` also omits the required cross-set type/shape matching. State that batch forms are mutually exclusive with each other, fixed parameter names must not overlap set names, and sets must use matching declared shapes.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The implementation appears to meet the 1..32 behavior and preserves named compare identity, with focused schema and execution evidence reported. The MCP field descriptions contradict the fixed-parameter contract, and the omitted-CLI path needs a focused check after the active merge is resolved.
