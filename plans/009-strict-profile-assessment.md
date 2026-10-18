# 009/T4 — Stricter profile design (assessment, not implementation)

**Status:** DESIGN (not implemented, not a decision — material to be resolved outside this plan)
**Date:** 2026-09-30
**Base:** `361280b`
**Predecessor:** T1–T3 of plan 009 (the static safety boundary); this document does not change them.
**Live evidence:** none — a design document, no live DB, no new server calls.

## Goal and scope

A stricter profile option for the Postgres engine, covering four areas: (1) TEMP
restrictions, (2) `SECURITY DEFINER` functions, (3) operators, (4) external effects. The document describes
only the considered options together with the reasons for rejection or deferral. It contains no
implementation code nor instructions for enabling execution mechanisms (`READ ONLY` transaction,
`SET ROLE` / role switching, `REVOKE` / permission changes) — those mechanisms appear here only
as considered and rejected/deferred options with justification.

Binding background: [PostgreSQL safety policy](../docs/superpowers/specs/2026-09-26-postgres-safety-policy.md)
— threat model, comparison of controls, decision matrix, sections "Chosen policy and trust
boundary", "Classify", "Session: Query and CompareSetup", "Setup and temporary tables".

## Shared assumptions

- The classifier `PostgresSafetyClassifier.Classify` sees only the batch text (AST SqlParserCS);
  it does not see function bodies, views, rules, triggers or operator bodies.
- The Npgsql session executes the classified text without follow-up commands (without `BEGIN`,
  `SET TRANSACTION`, `SET`, `SET ROLE`).
- The permissions of the connection account are an external boundary outside the harness; the harness
  does not read `pg_roles` nor `pg_proc` and does not execute `SET ROLE`, `SET SESSION AUTHORIZATION`, `GRANT`,
  `REVOKE` (policy, "Threat model").
- The `read-only` label means "no visible mutation and no local session work" — it is not
  a guarantee of the absence of hidden effects (policy, "Chosen policy and trust boundary").

## 1. TEMP restrictions

Current state (policy, "Setup and temporary tables"): `CREATE TEMP` / `CREATE TEMPORARY`
(including `CREATE TEMP TABLE AS SELECT` without a write target in the source), DML strictly to session-local
targets, `CREATE INDEX` / `DROP` of those tables and `DROP` of a temporary index —
without the mutation flag, in `Query` and in `CompareSetup`.

Considered tightenings (design, not implementation):

1. **Ban `SELECT INTO TEMP TABLE` in setup** — allowed today as an ambiguous temporary
   write. Tightening would remove one table-creation path outside `CREATE TEMP`.
   Cost: a change to the setup contract documented for agents; deferred — no evidence that
   this single path carries more risk than the remaining allowed TEMP writes.
2. **Limit the number / size of TEMP tables per batch** — no signal from the audit that the size of
   local session work is a vector; deferred as unjustified.
3. **Enforce the `pg_temp` qualifier on every reference** — the name rule already exists
   (first identifier `pg_temp` / `pg_temp_` prefix or a table set from the batch; policy,
   "Classify" item 4). Clarifying the qualification does not close any of the three effects
   (persistent DML, administrative effects, external access); deferred.
   (2026-10-01, superseded by plan 011 — see `plans/011-syntax-contract.md`, rule
   011-T4b-D7: the `pg_temp_` prefix described above is no longer proof of session
   locality for any statement; only an exact `pg_temp` qualifier or a name this
   session recorded from `CREATE TEMP` counts.)

## 2. SECURITY DEFINER functions

Current state (policy, decision matrix): the `SECURITY DEFINER` attribute does not appear in the call
text, so it cannot be enforced from the call alone; `SELECT app.write_something()`
remains `Allowed`; the only control outside the text is the account permissions, which the harness neither
changes nor checks. The same applies to `VOLATILE` functions performing DML and functions hidden
in views.

Considered options (design, not implementation):

1. **Resolving overloads via the catalog (a trusted entry + a `pg_proc` read)** — would give visibility
   into the `SECURITY DEFINER` attribute and volatility only for calls present in the text; it still would not
   see a function hidden in a view or a rule. It requires a trusted catalog entry dependent
   on `search_path` and additional query round trips (policy, "Comparison of controls": the number
   of server calls does not grow today). Rejected in that policy change for that reason; as a stricter
   profile option it remains deferred — without view and operator expansion it would be another
   list, not a boundary.
2. **Role switching / revoking permissions from the owner (`SET ROLE`, `REVOKE`)** — described here
   only as a considered and rejected option: the harness deliberately does not execute `SET ROLE`,
   `SET SESSION AUTHORIZATION`, `GRANT` nor `REVOKE` (policy, "Threat model"); a permission
   change is an external boundary outside the harness. This design contains no instructions
   for enabling such mechanisms.
3. **A manually maintained list of `SECURITY DEFINER` function names** — without resolving overloads
   by argument types, schema qualification in the text is not identity (policy, "Threat
   model"); the list would fake evidence. Rejected.

## 3. Operators

Current state (policy, "Threat model"): operator overloads (`+` and others are an operator node
in the AST, not a function call); the operator body is outside the text just like a function
body.

Considered options (design, not implementation):

1. **Expanding operators to base functions via the catalog** — requires the same trusted
   catalog entry as item 2.1 (a `search_path`-dependent catalog read, full resolution of overloads
   by types). Without it this would be another list of names, not a boundary. Deferred for the same
   reason as the function-overload resolution.
2. **Banning selected operators by name/symbol in the text** — the operator symbol does not attest
   to an effect (an overload can add DML to any symbol, and a symbol ban would also block
   purely benign uses); a list of symbols does not close the persistent-DML effect. Rejected.

## 4. External effects

Current state (policy, decision matrix): text filters — exact `pg_sleep`,
`pg_read_file`, `pg_ls_dir`, `lo_import`, `set_config`, `pg_cancel_backend`,
`pg_terminate_backend`, `nextval` / `setval`; prefixes `dblink`, `pg_read_`, `pg_ls_`, `lo_`,
`pg_advisory_` / `pg_try_advisory_`. The filter is a text filter, not proof of an effect; a mutation
approved by flags does not lift the filter.

Considered tightenings (design, not implementation):

1. **Extending the prefixes / exact names** — every new entry is another redundant filter
   rejection without changing the trust boundary (effects hidden in functions, views, operators
   remain invisible). Acceptable only as a deliberate redundant rejection in the style of the `lo_`
   entry (audit B4), never as proof of the absence of an effect. Deferred — no vector indicated.
2. **Blocking external access at the role level** (no `pg_write_server_files`,
   no FDW/`dblink` rights) — an external boundary outside the harness; this design contains no
   instructions for configuring it. Noted as a condition, not part of the harness.
3. **`READ ONLY` transaction as a tightening** — described here only as a considered
   and rejected option: it blocks neither administrative rows nor external access
   (`set_config`, `pg_cancel_backend`, `pg_terminate_backend`, advisory locks and remote SQL
   are not on the list of commands forbidden in a read-only transaction), and at the same time it breaks the
   temporary table contract (`CREATE TEMP` is `CREATE`, so setup would stop working or would require
   separate runs and a different transaction for DDL). This design contains no code nor instructions
   for enabling a read-only transaction.

## What this design deliberately does not do

- It does not implement a `READ ONLY` transaction nor role switching — reasons for rejection: section 4.3
  (it breaks `CREATE TEMP`, it does not block administrative/external effects) and section 2.2
  (roles and permissions are an external boundary outside the harness).
- It does not add a catalog read (`pg_proc`, `pg_roles`), view expansion nor operator expansion —
  that would require a trusted entry and additional round trips; without them it would be another list, not a
  boundary (sections 2.1, 3.1).
- It does not change `ClassificationLabel`, the mutation contract (`--allow-mutation` +
  `--confirm-database`) nor the scope of the Npgsql session.
- It does not change the T1–T3 files nor any code or test.

## Implementation condition (outside this plan)

Every future implementation of tightenings from this design requires a separate decision and — for the catalog
items (2.1, 3.1) — a trusted catalog entry, view/operator expansion and acceptance
of additional server round trips; for the permission items (2.2, 4.2) — preparation of the account
role outside SQLHarness. Minimal account permissions remain an external boundary outside the harness.
