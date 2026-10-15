# Observation: write statements and index maintenance cannot be evidenced read-only

Status: collecting observations, not approved for implementation. Recorded 2026-10-15
against repository HEAD `4f1e5a1`. No implementation plan is requested.

## Observed workflow

An optimization investigation used Query Store only as a prioritization aid. One of the most
expensive application statement families was a `DELETE ... WHERE Key IN (@p1, ..., @pN)` produced by
a data-access library that expands a list into individual parameters. Each list length produced a
separate query text and query id, so Query Store showed a dozen or more ids for one source line.

Identifying the family took four hand-written `query` calls over Query Store catalog views: one
top-consumer list, two unsuccessful attempts to cut the statement body out of a long parameterized
text (the parameter declaration header filled the default 2048-character cell), and one head/tail
extraction. The agent then matched the statement to application source. Removed CLI helpers
(`qstop`, `indexes`) are listed in README:461; hand-written catalog SQL is the current contract.

The statement could not be measured: `query`, `measure` and `compare` reject persistent DML.
The agent recorded two proposals as unmeasured:

1. **List expansion → table-valued parameter.** One query shape instead of one per list length.
   An existing table-type parameter is already used two lines later in the same method. The expected
   benefits (fewer compilations and plan-cache entries, one Query Store id) are hypotheses.
2. **Apparently redundant nonclustered index.** The target table has a clustered primary key whose
   leading column is the `IN` key, plus a single-column nonclustered index on that same column. Every
   synchronization `DELETE`/`INSERT` must maintain both. The agent found this with a hand-written
   `sys.indexes`/`sys.index_columns` query. Dropping the index is a persistent schema change, and its
   write-cost share was not measured.

## Verified behavior and interpretation

- Read-only rejection of persistent DML is the documented safety contract, not a defect.
- [Plan 072](../../plans/072-mutation-risk-tiers.md) (risk tiers) and
  [plan 073](../../plans/073-mutation-approval-broker.md) (approved execution through a broker)
  concern executing approved mutations. Neither describes measuring a write statement without
  persisting it, for example in an always-rolled-back transaction or as an estimated-only plan.
- [Plan 063](../../plans/063-missing-index-overlap.md) compares missing-index hints with existing
  indexes. It does not cover redundancy among existing indexes (a nonclustered index whose keys are
  a leading prefix of another index's keys), nor the maintenance cost of such indexes.
- The cost observed in Query Store is aggregate. It does not separate seek work, clustered index
  deletes and nonclustered index maintenance. A per-operator DML plan would be needed for that.

Classification: ux gap / missing capability (by design today). Confidence: high that the session
could not produce this evidence; the value of each possible capability is not established.

## Questions for later synthesis

- How often are the dominant costs in investigated systems write statements rather than reads?
- Which evidence form would be sufficient and safe:
  - an estimated-only plan for DML (no execution), showing per-index maintenance operators;
  - an actual plan and IO from a DML statement in a transaction that is always rolled back
    (locks, log and trigger side effects need explicit analysis);
  - or nothing beyond the approval broker in plan 073?
- Would a read-only existing-index redundancy check (leading-prefix duplicates, with usage stats
  where available) be worth a bounded surface, or is catalog SQL guidance enough?
- Should Query Store guidance show how to normalize expanded-list families (strip numbered parameter
  suffixes) so one source statement is reported once?

Cheapest next checks: count, across the observation logs, the sessions whose prioritized statement
was DML; and dry-run classify a rolled-back DML batch against plan 072's tier tests to see whether
a rollback-only tier would be expressible. No live database is required for either.
