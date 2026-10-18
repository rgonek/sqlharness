# Observation: application table-valued parameters have a direct benchmark form

Status: addressed by plan 083 implementation on the `plan-083` feature branch. Recorded 2026-10-15
against repository HEAD `4f1e5a1`; implementation and final-fix evidence are in
[plan 083](../../plans/083-table-and-list-parameters.md) and its [verification record](../../plans/083-verification-evidence.md).
The final review findings have been fixed. The controller gate and operator UAT remain pending;
live SQL Server behavior is not yet verified.

## Observed workflow

An optimization investigation targeted an application statement whose list inputs arrive as
table-valued parameters of a user-defined table type (one non-nullable `int` column, no key).
The first benchmark attempt declared local variables of that named type in the measured batch.
One `query` call was rejected with `safety_rejected` / `UnsupportedStatement` and the detail
"Named user-defined types are not proven as table variables". The agent then declared inline
`DECLARE @x TABLE (Id INT NOT NULL)` variables with the same shape, filled them from catalog data
in the same batch, and every later call succeeded. The rejection cost one call and one rewrite.

## Verified source and interpretation

- `src/SqlHarness.Core/SqlSafety.cs:112-113` defines the rejection detail. The behavior matches
  the documented safety contract (README "table variables ... when the `DECLARE` comes earlier in
  the same batch"), so this is not a defect.
- The README parameter type list has no structured/table-valued binding; typed parameters cover
  scalar, date/time, `hierarchyid` and spatial types only.
- The inline substitute is a query-shape experiment, not the application's execution form:
  - Fill statements become part of every measured batch. Batch totals then mix fill work with the
    statement under test, and the saved `statements` section has no per-statement logical reads
    (see [statement metrics](statement-metrics-and-local-object-noise.md)).
  - Compilation behavior can differ. A parameter's row count is sniffed at compile time. A batch-local
    table variable gets deferred compilation on recent compatibility levels. Plan reuse across matrix
    cells then fixes the first cell's cardinality. The journal showed one plan hash per variant across
    all cells; the agent noticed this from estimated rows and forced a fresh compile with a comment.

Classification: ux gap (works as designed; the application's input form cannot be reproduced).
Confidence: high for the rejection and missing binding; the size of any measurement distortion
from the substitution is not established.

## Resolution

Plan 083 adds catalog-resolved SQL Server TVP binding and Dapper-style list parameters to the
read-only query and benchmark paths, with journaling, labels, and reproduction scripts. The inline
table-variable workaround is no longer needed to represent those application inputs. The original
workflow evidence and its uncertainty about measurement distortion remain useful historical context.

The feature branch has focused implementation coverage. No live database or SSMS reproduction was
performed as part of this documentation task; those checks remain in Plan 083 UAT.

## Historical questions for later product research

- How often do investigated statements take table-valued or list parameters?
- Would a typed row-set parameter (declared column types plus bounded JSON rows, bound as a
  structured parameter) reproduce the application form? Or is a verified catalog check that a named
  type is a table type enough to accept `DECLARE @x <type>`?
- Should benchmark output identify fill statements so they can be excluded from totals?

Cheapest next checks: an offline dry run of a batch that declares a named table type, to confirm
the rejection path; and a synthetic test that binds a structured parameter through the existing
parameter binder, to see where typing would need to live. No live database is needed.
