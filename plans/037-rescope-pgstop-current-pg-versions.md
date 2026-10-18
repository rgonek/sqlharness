# Plan 037: Confirm the pgstop shapes and prepare a runtime capability fixture

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

> Implementer: read the 014 contract and implementation plan. This plan produces documentation and fixtures that specify future tests; it does not implement pgstop. Check drift with `git diff --stat 729d88f..HEAD -- plans/014-pg-statements-contract.md plans/014-pg-statements-implementation.md docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md docs/superpowers/specs/2026-09-10-postgres-engine-design.md`. Analyze the changes and adapt the guidance. Stop only on a step with a genuine unresolved behavioral conflict.

## Status

- **Priority**: P2
- **Effort**: S–M
- **Risk**: LOW
- **Status**: PLANNED; the 2026-10-11 project update does not mean PG17/18 research was performed.
- **Depends on**: contract 014; plan 036 provides a live-proof environment, not the list of versions required to admit runtime.
- **Category**: direction/tests
- **Planned at**: `729d88f`, 2026-10-11

## Problem and outcome

The earlier design admitted only the exact pairs PG14+1.9 and PG15/16+1.10. That entrenched a manual list of numbers as a block on correct diagnostics. Contract 014 now requires a runtime probe of names/types and an explicit projection, so an unknown version with a compatible shape is supported. This plan confirms the required minimum, the optional fields and the PG17/18 fixture from primary sources. Version numbers serve test coverage; they do not exclude a correct shape.

## State and scope

`plans/014-pg-statements-contract.md` has a historical PG14/15/16 matrix plus the new normative step 6 runtime probe. `plans/014-pg-statements-implementation.md` requires fixtures F14/F15/F17/F18/F-new/F-missing/F-type/F-optional/F-schema. There is no PgStatementTop product code yet. `scripts/setup-local-postgres.ps1` uses postgres:16. Do not change the setup in this documentation task.

In scope: both 014 documents, `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, one decision annotation in `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`, the index. Out of scope: pgstop code, DB scripts, delta, reset, extension installation, GUC changes, profiles and user passwords. Work in an isolated worktree, commit as docs(037), no push.

## Steps

### 1. PG17/18 sources

Open the official pages `https://www.postgresql.org/docs/17/pgstatstatements.html` and `https://www.postgresql.org/docs/18/pgstatstatements.html`, plus the system catalogs describing the namespace, columns/types and ownership of the extension objects. Extend the 014 matrix with PG17/18; every factual cell has a source/page version or UNPROVEN with a condition. Separate extversion from server_version. Do not guess names or the semantics of I/O timings.

**Verification:** `git diff --check` → exit 0; every new cell has a URL or an explicit UNPROVEN.

### 2. Capability descriptor and explicit query

Confirm the types required by step 6 of the contract and the catalog way of identifying the object that belongs to the extension. Describe the explicit SQL probe and the explicit projections in the implementation plan; namespace quoted with a helper, metric names from the internal list, no search_path/SELECT *. Optional columns are not a success condition. Map an I/O name change only after source confirmation of the semantics; when the meaning differs, keep unavailable or design a separate field with justification. Do not sum different I/O scopes as the same metric.

**Verification:** `git diff --check` → exit 0; all projections have explicit columns, and no entry rejects solely an unknown version pair.

### 3. Fixture and live proof

In the implementation plan table describe fixtures for the PG14/15/16/17/18 shapes, unknown versions with a compatible minimum, a missing/changed type of a required column, an optional unavailable field, extra columns, a quoted namespace and a false object on search_path. Expected: matching minimum → ranking; mismatched minimum → exit 5 before the ranking query; optional missing → null/unavailable with a reason. The version alone creates no other path. A fixture is not a live proof.

Record how to use the isolated 036 environment for PG17 and PG18 with pg_stat_statements. A live run requires a separate restricted target/authorization per the contract; do not pick a user profile. Leave a missing environment for one version as evidence not gathered; do not restore a runtime allowlist because of it. Live checks the actually present types, discovery/quoting, a simple ranking and a restricted role. Reset/delta are not part of v1.

**Verification:** `git diff --check` → exit 0; all the classes listed above have an input/expected result, and the live status is explicit.

### 4. Reconcile the specifications

In the postgres-statement-diagnostics draft, replace the normative delta/allowlist requirements with the cumulative v1 and the runtime probe per 014. Future delta remains deferred; no function that always yields unknown/incomparable. In engine-design, next to the pg_stat_statements non-goal add an annotation: adopted as plan 014, feature still PLANNED. Update the index with the project evidence, without a claim of implementation.

**Verification:** `git diff --check` → exit 0; `Test-Path` of every cited local path → True; `git diff --stat` stays within scope. Application tests are not needed for documents.

## Done and STOP

- [ ] PG17/18 have source-backed cells or marked missing evidence.
- [ ] The descriptor/probe, required/optional types and safe object qualification are implementable and consistent with contract 014.
- [ ] The fixtures cover an unknown compatible version and a wrong required shape; the version number is not a gate.
- [ ] Specifications and index reconciled, diff check exit 0; no code and no DB runs.

When a source is unavailable, record the specific gap and perform the independent tasks; no invented facts. Stop a dependent step if the sources refute the semantics of a required metric or the safe identification of the object and this cannot be reconciled within the contract scope. Each new major may add coverage; it does not need a separate documentation-driven runtime unlocking. The reviewer checks the meaning of I/O aliases and the separation of an unknown number from a mismatched shape.
