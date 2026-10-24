# Plan 104 Task 2 review

Reviewed the exact package `.superpowers/sdd/104-stable-local-object-io-aggregation/review-257a27a3..1b2138bf.diff` against `implementation-task-2-brief.md` and the implementation report. The reported focused result is 91 passed; I did not rerun it.

## Spec compliance: NEEDS FIX

- **P2 — a partially parsed 3615 message can be treated as complete.** In `src/SqlHarness.Core/Diagnostics.cs:280-285`, `TableIoAccumulator.Add` marks the accumulator incomplete only when `ParseRows` returns no rows. `ParseRows` skips lines rejected by `TryParse` (`Diagnostics.cs:180-188, 221-238`), so a message containing one valid row and one unparseable table row leaves `Complete` true. When text was omitted, `SqlServerDialect` trusts that flag and builds typed summaries from the accumulator (`src/SqlHarness.Core/Dialect/SqlServerDialect.cs:177-188`). If the provider can deliver such a mixed message, the summary is partial but is exposed as complete, contrary to the brief's “Missing/unavailable evidence is not measured zero” constraint and its complete-detail fallback requirement. Preserve message-level parse completeness and mark typed detail unavailable when a relevant row is skipped. **Provider framing for this mixed case was not verified.**

The remaining reviewed requirements are represented in the implementation: the shared Core identity classifier runs on raw names; compare and journal label allocation uses paired/operation-wide identities; sums remain grouped by variant, parameter set, and cell; raw per-run maps and LOB totals are retained; and legacy names remain Unknown.

## Quality: PASS

No separate actionable quality finding. The classifier and collision-safe display-name allocator are cohesive Core helpers, and journal grouping keeps shared labels separate from metric aggregation.

## Cannot verify within this review

Task 3 dashboard fold removal and MCP/artifact consumer contracts are outside this package and remain unreviewed. The focused test result is taken from the supplied report/log, not independently rerun. The accepted empirical SQL Server recognition rule still does not establish a cross-version guarantee, as the brief already records.
