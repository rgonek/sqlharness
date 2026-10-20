### Scoped Re-review

- **ADDRESSED** — The prior test only selected the Baseline tab. The fix now activates the Diff tab, switches the actual mode control from Side by side to Unified, verifies the rendered diff receives Unified mode, clears persisted tab/mode preferences, closes and reopens the disclosure, then asserts the Diff tab, Unified radio, and unified diff remain selected/rendered. See `src/SqlHarness.Dashboard/ui/src/components/CompareDetail.test.tsx:137`, `:140`, `:142`, `:146`, `:149`-`:151`. The cleared preference keys match the implementation in `src/SqlHarness.Dashboard/ui/src/components/SqlDiff.tsx:10`-`:11`.
- **New breakage in fix diff:** None identified. The fix adds test-only editor stubs and the missing mode-retention assertions; it does not change product code. The stub renders the mode prop in its accessible label, so the unified-mode assertion checks the state passed by SqlDiff rather than an unconditional fixture result.
- **Verification evidence (not rerun):** The updated report records 7 passing CompareDetail tests plus successful typecheck and lint at `3-report.md:22`-`:23`.

**Assessment:** Finding addressed; scoped re-review clean.


