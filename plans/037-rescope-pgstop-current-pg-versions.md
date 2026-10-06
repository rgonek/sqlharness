# Plan 037: The `pgstop` design covers the current PostgreSQL majors (17, 18) before anyone implements it

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- plans/014-pg-statements-contract.md plans/014-pg-statements-implementation.md docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md docs/superpowers/specs/2026-09-10-postgres-engine-design.md scripts/setup-local-postgres.ps1`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (direction; a design-only task)
- **Effort**: S–M (documents and fixtures; no product code)
- **Risk**: LOW
- **Depends on**: none. Live verification of each column shape needs plan 036 or an operator-authorized server.
- **Category**: direction
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Plan 014 designed `pgstop`, a read-only `pg_stat_statements` counterpart to SQL Server's `qstop`. The design only opened the PostgreSQL 14, 15 and 16 documentation and defines column SQL **only** for the pairs "server 14 + extension 1.9" and "server 15/16 + extension 1.10". Every other pair, explicitly including "server newer than 16", gets no column SQL and fails (`plans/014-pg-statements-contract.md`, `## Kolumny` / step 6; `plans/014-pg-statements-implementation.md` "Decyzje, które zadania niosą": "SQL kolumn jest tylko dla PostgreSQL 14 … oraz … 15 albo 16"). As of October 2026, PostgreSQL 17 and 18 are the current majors, and PG14 is close to end of life. Implemented as designed, the feature would refuse most new deployments. pg_stat_statements also changed columns after 1.10. For example, newer versions rename `blk_read_time`/`blk_write_time` to `shared_blk_read_time`/`shared_blk_write_time` and add `local_blk_*` and `stats_since` columns. These are recollections, not verified facts, and must be checked against the official sources in the same way plan 014 T1 did.

There is also an unrecorded decision reversal. `docs/superpowers/specs/2026-09-10-postgres-engine-design.md` (around line 44, "Non-goals") lists "A `pg_stat_statements` analog of Query Store `qstop`". The later audit roadmap (06/T5) and plan 014 adopted it at the user's request, but the engine spec was never annotated.

## Current state

- `plans/014-pg-statements-contract.md` (Polish): `## Macierz źródeł` with per-version tables for PG14/15/16 (sources: the official module pages F.30/F.32 and the `pg_stat_statements.control` file at tags `REL_14_0`/`REL_14_24`, `REL_15_0`/`REL_15_19`, `REL_16_0`/`REL_16_15`); `### Kolumny pg_stat_statements` (per-column presence per version); a connection-sequence section (resolve → auth → identity → `server_version_num` floor `140000` → `installed_version` → column SQL).
- `plans/014-pg-statements-implementation.md`: decisions (only the two pairs get column SQL), file list, tests (including the "V-pair": a newer server is not exit 0).
- `scripts/setup-local-postgres.ps1:26` pins the playground image `postgres:16`.
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`: the original hypothesis spec.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Whitespace check | `git diff --check` | no output |
| Link/path check | for each path you cite, `Test-Path <path>` | `True` |

No `dotnet test` is required (documents only). Do not run application tests for this plan, matching the plan-014 convention.

## Scope

**In scope**: `plans/014-pg-statements-contract.md` (add PG17/PG18 rows and columns), `plans/014-pg-statements-implementation.md` (extend the allowed pairs and test table), `docs/superpowers/specs/2026-09-10-postgres-engine-design.md` (one superseded note), `plans/README.md`. Optionally, `scripts/setup-local-postgres.ps1` gains a `-Image` parameter defaulting to `postgres:16`; if so, update its Pester and C# script tests in the same commit.

**Out of scope**: any product code (`pgstop` stays PLANNED), and changing plan 014's sequence, exit codes or delta semantics.

## Git workflow

Branch `docs/plan-037-pgstop-versions`. Commits `docs(037): ...`. Write in Polish, matching the 014 documents. Do NOT push.

## Steps

### Step 1: Source matrix rows for PG17 and PG18

Using the same source types as T1 (official module pages for 17 and 18, and the `pg_stat_statements.control` `default_version` at `REL_17_0` / latest `REL_17_*` and `REL_18_0` / latest `REL_18_*` on the `postgres/postgres` mirror), add columns `PG17` and `PG18` to every table in `## Macierz źródeł`, and record page footers and section numbers as T1 did. Each cell is a fact with a source, or `UNPROVEN` with the condition for proof. Do not fill cells from memory.

If you have no network access, STOP: this plan's value is in sourced facts.

**Verify**: every new cell cites a URL or a tag path, or says `UNPROVEN` with a condition. `git diff --check` is clean.

### Step 2: Column table

Extend `### Kolumny pg_stat_statements` with PG17/PG18 presence per column, including renamed and added columns, each with its source. Mark renames explicitly (old name → new name, version), because the column SQL must branch on them.

### Step 3: Allowed pairs and tests

In `plans/014-pg-statements-implementation.md`: extend "SQL kolumn jest tylko dla…" to the new pairs proven in Steps 1–2 (e.g. server 17 + its default version, server 18 + its default version). Keep "any other pair → no column SQL, `UNPROVEN`, not exit 0". Add test rows for each new pair to the test table, and state that a live column-shape check per pair needs plan 036's lane (add `postgres:17`/`:18` to its matrix) or an authorized target.

### Step 4: Record the decision reversal

In `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`, after the non-goal bullet about `pg_stat_statements`, add one line: "Superseded 2026-09-26: adopted as plan 014 (`pgstop`) at the user's request; see `plans/014-pg-statements-contract.md`." Do not delete the original bullet.

**Verify**: `git diff --check` is clean; `git diff --stat` touches only in-scope files.

## Test plan

None (documents only). The future implementation's tests are what Step 3 defines.

## Done criteria

- [ ] Contract tables have PG17/PG18 columns with sourced cells or explicit `UNPROVEN`
- [ ] The implementation design lists the new pairs and test rows
- [ ] The engine spec has the superseded note
- [ ] `git diff --check` clean; `plans/README.md` row updated

## STOP conditions

- No network access to the official sources.
- The sources show a change that breaks plan 014's sequence (e.g. `installed_version` semantics changed). Report it; do not redesign here.

## Maintenance notes

- Each new PostgreSQL major needs a new matrix column before `pgstop` will accept it. Put a yearly reminder in `plans/README.md`.
- Implementing `pgstop` remains plan 014's implementation design plus this extension. Schedule it after plan 036 provides live column-shape proof.
