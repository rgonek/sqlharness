### Spec Compliance

- ✅ Prior finding 1 (MCP descriptions) — ADDRESSED. The set/file/matrix descriptions now say that fixed parameter names must not overlap, and named sets must have matching parameter shapes (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:705`, `:706`, `:707`, `:708`). The merged descriptions retain the 1..32 bounds and Plan 096 file-ingress limits.
- ✅ Prior finding 2 (omitted CLI collection) — ADDRESSED. When no `--param-set` is supplied, the CLI passes `null` to the operation (`src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:344`); the existing test asserts `ParameterSets` remains null (`tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs:102`). The report pointers are sufficient.
- ✅ No new breakage found in the scoped conflict resolutions. The CLI description combines the singleton bound with file ingress and typed-value guidance; MCP descriptions preserve batch exclusivity, fixed-name collision rules, matching shapes, engine scope, and 1..32 bounds.

### Strengths

- The MCP schema test now pins the corrected description rules for file, named-set, and matrix inputs (`tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:1856`, `:1857`, `:1858`, `:1859`).
- Merge evidence records focused Core/CLI and MCP checks, plus the final post-fix schema/batch run. Tests were not rerun for this review.

### Issues

#### Critical (Must Fix)

- None.

#### Important (Should Fix)

- None.

#### Minor (Nice to Have)

- None.

### Assessment

**Task quality:** Approved

**Reasoning:** Both prior findings are resolved, and the targeted CLI code and assertion confirm omitted `--param-set` retains ordinary single-query behavior. The inspected conflict-resolution descriptions preserve the relevant Plan 096 and Plan 097 contracts; no new issue was found in this scoped re-review.
