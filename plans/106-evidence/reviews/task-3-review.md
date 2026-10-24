### Spec Compliance

- ❌ Issues found: The MCP `sqlharness_capabilities` description documents redaction, the Message/Hint floor and caps, and the `Location.Path` limit, but omits the tiny-budget guarantee that the error code and phase remain and truncation is reported. The runtime guide includes that behavior, but the MCP runtime-policy surface is incomplete (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:695`; compare `docs/mcp/runtime.md:10`).

### Strengths

- The MCP argument text is shared through one compact constant, and the capabilities policy is shared by fixed and request-scope catalogs (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:696`).
- The runtime guide distinguishes result-cell and path clipping from redacted Message/Hint clipping, including the 2048 floor, 4096 ceiling, final byte budget, and tiny-budget truncation behavior (`docs/mcp/runtime.md:10`).
- The report records focused CLI/MCP regressions and catalog-size measurements below the existing 32,768-byte tools/list ceiling; no ceiling increase is claimed (`plans/106-evidence/task-3.md:25`).

### Issues

#### Critical (Must Fix)

None.

#### Important (Should Fix)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:695` — Add the tiny-budget guarantee to the `sqlharness_capabilities` runtime policy: preserve the original error code and phase and report omitted content in `truncation`. The MCP capability is the declared home for runtime policy, so consumers discovering behavior through tools/list and capabilities otherwise miss this contract. Compress surrounding description text if needed and keep the existing request-scope, annotations-enabled tools/list limit unchanged.

#### Minor (Nice to Have)

None.

### Assessment

**Task quality:** Needs fixes

**Reasoning:** The docs and CLI/MCP descriptions cover the main cell-versus-error limits and the focused evidence supports the reported implementation. The MCP capabilities description still omits one required tiny-budget behavior, leaving the public runtime policy incomplete.
