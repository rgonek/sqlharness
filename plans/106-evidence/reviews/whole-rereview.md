# Scoped whole-branch re-review — d78eac9d..43d0a189

## Finding verdicts

- **Important: optional Location.Path displaces original error code/phase at the 4096-byte minimum** — **ADDRESSED.** `src/SqlHarness.Mcp/McpResultAdapter.cs:185-205,214-231` and `src/SqlHarness.Cli/Commands/Renderer.cs:192-220,238-254` now include the already cell-clipped path in byte-budget fitting and remove it in the minimum envelope. Both count path omission once, including overlap with cell clipping. The added CLI/MCP regressions exercise 4096-character ASCII and multibyte/escaped paths at maxCellChars=4096 and maxResultBytes=4096, asserting the original code/phase, byte ceiling, and one omission. The fix report records RED before the fix and final focused GREEN after formatting: Core 95/95, MCP token-budget plus schema 117/117.
- **Minor: capabilities description says generic timeouts rather than connectionTimeoutSeconds** — **ADDRESSED.** `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:693-695` restores the exact key. `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs:336,367,396,410-418` checks fixed/request catalogs with annotations off/on; tools/list byte-budget checks remain. The fix report records the 117/117 focused MCP run after formatting.

## New breakage in the fix diff

None found. The byte fallback changes only optional path projection, retaining the normal requested cell limit and existing redaction order. The direct branch formatter and diff check are reported exit 0 on 43d0a189. I did not rerun tests.

## Out-of-scope observations

The controller-owned full Windows gate was starting on 43d0a189; its result was unavailable at this re-review. This is a final-gate matter, not a fix-diff finding.

## Verdict

**Fix round: All findings addressed, no new Critical/Important breakage.** Final readiness remains subject to the controller's exact-head Windows gate and plan closeout.
