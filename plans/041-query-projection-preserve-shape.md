# Plan 041: Preserve useful query result shape under output budgets

> **Executor instructions**: Execute W1, then W2. Read the relevant source and tests before editing.
> Run the focused acceptance checks and update the status row in `plans/README.md` when complete.
> This plan describes work; accepting the plan does not execute it.
>
> **Drift check (run first)**: `git diff --stat 542315a..HEAD -- src/SqlHarness.Core/AgentOutputProjection.cs src/SqlHarness.Mcp/McpResultAdapter.cs src/SqlHarness.Cli/Commands/Renderer.cs docs/mcp.md`
> Compare any changes with the current-state descriptions below before proceeding. Stop if another plan
> already owns an incompatible change to query projection or the result envelope.

## Status

- **Status**: TODO
- **Priority**: P2
- **Effort**: M
- **Risk**: MEDIUM (shared projection and serialized response budgets)
- **Depends on**: completed plan 038/W1
- **Category**: UX gap
- **Planned at**: commit `542315a`, 2026-10-09

## Evidence and scope

A read-only diagnosis in System2 inspected data received from System1. A query with three result sets
returned only the first result set's first column and first row, with `detailLimit: 1` and explicit omissions.
The agent recovered the useful evidence through a compact aggregate query and a bounded example query.
These were two follow-up queries; the aggregate was the extra call needed to work around the projection.
No real query text, identifiers, parameter values, infrastructure details or workload counts are retained here.

This does not establish that the old small-report defect returned. Plan 038/W1 already added an untruncated
first attempt. The source still reduces multiple result dimensions together when a report exceeds the budget.
The observed server reported version `1.0.0`; that version alone does not identify its build commit.

## W1: Preserve query schemas and result-set visibility during degradation

**Evidence.** A query response can remain valid JSON while losing almost all useful relational structure.
Packing several facts into one string cell recovered evidence but made the agent responsible for presentation.

**Current state.** `AgentOutputProjection.GetCandidateDetailLimits` starts with `int.MaxValue`, followed by
`CalculateDetailLimit` and successively smaller values. At the default budget and cell cap, the estimate is 1,
so it can jump from a full projection to one item. `ProjectResultSets` applies that same limit to result sets,
column metadata, rows and row cells. The MCP adapter and CLI renderer use the shared candidate sequence.

**Change.** Keep the full-result first attempt. For query reports, try progressively smaller row samples
before dropping columns or result-set identity. Use bounded candidates such as 128, 64, 32, 16, 8, 4, 2, 1
and 0 where applicable, without materializing an unbounded search.

- Keep every returned row aligned with a complete returned column schema.
- Retain each result set's ordinal and row/omission counts when that metadata fits; a large first result set
  must not automatically hide later small summary result sets.
- Sample rows before truncating their column dimension. If even a schema or result-set inventory cannot fit,
  return explicit omission metadata through the existing envelope or a reviewed additive representation.
- Preserve source reports, row counts, cell clipping and existing scope/redaction protections.
- Keep non-query projection behavior unchanged unless focused tests establish a necessary shared adjustment.

**Acceptance.** Use synthetic fixtures, not captured operational results:

1. A small three-result-set report that fits the wire budget remains complete with `truncation: null`.
2. A report with one large detail table and two small summary result sets retains useful summaries and aligned
   detail rows. Every omitted result set or row is accounted for; no partial result implies completeness.
3. A wide schema and oversized cells produce a bounded, explicit fallback without corrupting row/schema alignment.
4. Each MCP response fits the actual serialized `CallToolResult` byte budget, including both representations
   and escaping. CLI agent output remains bounded under its own contract.
5. Projection does not change the source report, result hash, target identity, or scope ownership.

## W2: Cover and document the query fallback contract

**Evidence.** Plan 038/W1 covers complete small reports and bounded oversized output. The practical ability
to read later summaries and complete row schemas also needs an explicit acceptance contract.

**Current state.** `docs/mcp.md` describes whole-envelope budgeting and degradation, but not query-specific
priorities for result sets, schemas and row samples.

**Change.** Add focused projection and MCP budget tests for W1 and document the selected query fallback.
Reuse existing test infrastructure. Coordinate any additive metadata with envelope/schema tests before coding it.

**Acceptance.** Focused Core/MCP projection tests and the repository verification gate pass. Documentation
matches the emitted fields and omission semantics. Existing benchmark/artifact projection coverage remains green.

## Out of scope

- Increasing the default output budget or silently changing `maxRows`.
- Query execution, authentication, target selection, SQL safety or database discovery.
- A new artifact reader for raw query values or publishing operational evidence.
- Session-report API implementation.

## STOP conditions

- The current-state description no longer matches the source or another active plan owns the change.
- A proposed fix requires exposing raw sensitive data through a new retrieval surface.
- Required schemas or result-set inventories cannot fit and the proposed fallback has no explicit omission contract.
- Changes weaken wire-budget enforcement, redaction, scope ownership or source-report immutability.
