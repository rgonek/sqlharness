# Whole-branch review — plan 106

Base 6d543e509734a374caf882bc8c05ffe5ccaa98b2; head d78eac9d. Reviewed the supplied review-6d543e50..d78eac9d.diff against the plan Contract, task ledger, evidence, and affected implementation/tests. Read-only review of product and Git state. Final Windows just verify was running at review time; I did not rerun it.

## Strengths

- Core centralizes the error text floor and clips UTF-16 without splitting surrogate pairs. Both MCP and CLI apply redaction before their error text projection.
- The normal and minimum-budget paths preserve error code/phase for the tested cases; omission accounting unions cell clipping with byte-budget clipping. CLI reserves the writer's UTF-8 line ending.
- Synthetic CLI/MCP tests cover connect, authentication and SQL failures, cell limits 0/200/default/maximum, multibyte text, null hint, credentials, and minimum budgets. Schema/help regressions cover the exposed descriptions.

## Issues

### Critical

None.

### Important

1. **Optional location path can still displace the original error code and phase at the minimum byte budget.**
   - Files: `src/SqlHarness.Mcp/McpResultAdapter.cs:195,214-223`; `src/SqlHarness.Cli/Commands/Renderer.cs:202,229-245`.
   - Both fallback searches shorten only Message and Hint. With the accepted 4096-byte budget and maxCellChars=4096, an error Location.Path of 4096 ASCII characters exceeds the response budget even after Message is empty and Hint is null. Both paths then emit output_budget_too_small, replacing the original code/phase. A shorter multibyte or heavily escaped path can trigger the same case.
   - Fit optional path/location within the remaining budget, or omit it when necessary, while preserving code/phase and counting that field as omitted once. Add minimum-budget regressions for long ASCII and multibyte paths in both renderers. Preserve the normal requested cell limit for paths.

### Minor

1. **Capabilities description loses a discoverable configuration key.**
   - File: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:693-694`.
   - Replacing connectionTimeoutSeconds with generic timeouts makes the exact response field harder to discover from tools/list. Restore the explicit key when catalog space permits. This is the deferred task-3 Minor and does not block the error behavior fix.

## Recommendations

After the Important fix, run the scoped Core/MCP regressions and the required final gate on the new exact head; prior green evidence belongs to d78eac9d.

## Declined to judge

- Live database behavior, loaded MCP binary identity, deployment, and token savings: plan 106 deliberately uses synthetic offline evidence and does not authorize those activities.
- Linux verification: the plan explicitly allows it to remain unverified unless requested.
- Earlier artifact identity and continuation semantics: this diff does not change their implementation.
- The still-running full Windows gate: its result was not available to this reviewer.

## Assessment

**Ready to merge? No.** The normal diagnostic floor is well covered, but a valid minimum-budget and maximum-cell-limit input still loses the original error identity. Re-review the bounded fix and assess the final gate on the fixed head.
