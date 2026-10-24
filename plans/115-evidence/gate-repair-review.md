### Specification Verdict

✅ **Pass.** The capability description restores the diagnostic contract: post-redaction Message/Hint floors and character/response-byte caps, Location.Path clipping, tiny-budget error identity and omission counts, timeout units/defaults, the separate Azure CLI token budget, and the reconnect refresh guidance (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:729`).

The shared query description restores per-execution duration versus benchmark evidence, optimizer estimates versus observed `actualRows`, operator `executions`, and dry-run's validation/execution distinction. Selector input and response behavior remain discoverable through the argument description, query description, and capabilities text (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:758-759,797-801,850-854`). These shared descriptions serve both fixed and request catalogs.

The condensed measure and compare descriptions retain their key contracts: optimization subject/scope, isolated ordered sets and failure retention, stability/equivalence boundaries, hash exclusions, statement-evidence scope, saved diagnostic access, and exact statement section guidance (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:747-748`). The annotation descriptions retain label/keep limits, receipt versus full detail, process byte caps, common per-item handling, rollback behavior, journal recording, and the absence of an operation reference (`src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:734-735,763`).

### Quality Verdict

**Approved.** The strings are concise but preserve the described behavior and do not introduce contradictions. The diff changes only catalog descriptions; no runtime behavior or test assertion was altered.

### Findings

None.

### Verification Evidence Reviewed

- The repair report records 125 passed/0 failed for the schema, token-budget, and selector filters; the subsequent description/setup checks passed 3/3 and the annotation-bound check passed 1/1. No tests were rerun for this review.
- The report records final tools/list sizes of 28,971 bytes without annotations, 32,268 bytes for fixed scope with annotations, and 32,743 bytes for request scope with annotations against a 32,768-byte limit.
- The report records branch formatting passing for eight changed C# files. The controller-owned final `just verify` is still running and was not verified by this review.
