# PostgreSQL safety policy

**Date:** 2026-09-26
**Status:** binding for the classifier and the Npgsql session
**Applies to:** S3 and S4 in the [audit](2026-09-26-project-audit.md). Does not change the SQL Server engine.
**Parser:** SqlParserCS 0.6.5, dialect `PostgreSqlDialect`.

This document is the source of the behavior of `PostgresSafetyClassifier.Classify` and of the session execution scope for `SqlUsage.Query` and `SqlUsage.CompareSetup`. It adds no new interface. It does not authorize the claim "read-only guaranteed".

## Threat model

The actor sending SQL is an agent with access to the CLI (or a direct call into Core). The batch text, the parameters and the profile choice are no evidence of the absence of effects. Consent for persistent DML remains one-off and explicit: `--allow-mutation` and `--confirm-database` equal to the resolved database name. It applies only to `Query`. Setup does not accept it.

Objects already exist in the database whose bodies are not in the submitted text:

- functions, including `VOLATILE` ones performing DML and `SECURITY DEFINER` functions;
- views, rules and triggers;
- operator overloads (`+` and others are an operator node in the AST, not a function call).

The connection account's permissions are a separate boundary. The harness reads neither `pg_roles` nor `pg_proc` and executes no `SET ROLE`, `SET SESSION AUTHORIZATION`, `GRANT` or `REVOKE`.

`search_path` and the current schema resolve unqualified names. The same spelling may denote a different object after a path change. Schema qualification in the text is not an identity either, as long as the harness does not resolve overloads by argument types. Quoting and case folding stay as they are today: an unquoted identifier is compared case-insensitively, and a temporary table key folds an unquoted name to lower case.

The three effects are not a single prohibition:

| Effect | What would have to be excluded | What the SQL text alone shows |
| --- | --- | --- |
| No persistent DML | `INSERT` / `UPDATE` / `DELETE` / `MERGE` and a modifying CTE into a persistent table, as well as DML hidden in a function, view, rule or trigger | Only the write targets present in the AST |
| No administrative effects | changing session configuration, cancelling or killing another backend, advisory locks, changing the role | Only a call whose final name is on the list below |
| No external access | `dblink`, FDW, reading a server catalog or file, large object export | Only a visible name from the existing prefixes `dblink`, `pg_read_`, `pg_ls_`, `lo_` |

## Control comparison

No single control will bear all three effects. The safety promise rests neither on the list of names alone nor on a read-only transaction alone. The harness changes no roles and no permissions.

| Control | Persistent DML visible in the text | Persistent DML in a function, view, rule, trigger | Administrative effect | External access | Setup `CREATE TEMP` | Permission change |
| --- | --- | --- | --- | --- | --- | --- |
| Full SqlParserCS AST | Yes, for targets the parser placed in the tree. The library's grammar is broader than the server's grammar (S4). | No | Only when the call is visible and the name is covered by the filter | Same | Breaks nothing | No |
| Trusted functions after overload resolution | Does not replace the analysis of write targets | Only for calls in the text, and only when the catalog entry is truthful. The owner may mark a function `IMMUTABLE` and still perform DML. It does not see a function hidden in a view nor an operator body. It requires a catalog read dependent on `search_path` | Only with a complete and trusted catalog | Same | Requires additional queries | No, but it does not remove `SECURITY DEFINER` |
| Minimal account permissions | Yes, if the role cannot write | Yes for DML performed as that role; no when `SECURITY DEFINER` acts as an owner with write permission | Only when the role lacks `pg_signal_backend`, is not a superuser, lacks `pg_write_server_files` and the like | Same | Breaks nothing, if the role can create temporary tables | The harness would have to perform `SET ROLE` or `REVOKE` itself. It does not do that |
| `READ ONLY` transaction | Yes, also for DML inside a function, because the PostgreSQL documentation forbids `INSERT` / `UPDATE` / `DELETE` / `MERGE` into a table that is not temporary | Yes for non-temporary DML, also in `SECURITY DEFINER` (the restriction concerns the command in the transaction, not the role name) | No. `set_config`, `pg_cancel_backend`, `pg_terminate_backend` and advisory locks are not on the list of commands forbidden in a read-only transaction | No. Remote SQL and file functions are not that list | Breaks. The same documentation forbids all `CREATE`, also `CREATE TEMP` | No |

Sources of server behavior, not a live test result from this change: [SET TRANSACTION](https://www.postgresql.org/docs/17/sql-set-transaction.html), [WITH](https://www.postgresql.org/docs/17/queries-with.html), [administrative functions](https://www.postgresql.org/docs/17/functions-admin.html).

## Decision matrix

A function identifier is the last component of the name, regardless of schema and quoting, compared case-insensitively. The list is a text filter, not evidence of an effect. A mutation approved by flags does not lift the filter.

| Item | Allowed effect | Enforcement | Rejection message |
| --- | --- | --- | --- |
| `set_config` | None. A visible call must not change the configuration, including `search_path`, either as `Query` or as `CompareSetup` | Rejection on the exact final name, at every AST location covered by the traversal from the Classify section | `UnsupportedStatement.` |
| `pg_cancel_backend` | None. No signal to another backend | Exact final name | `UnsupportedStatement.` |
| `pg_terminate_backend` | None. No termination of another backend | Exact final name | `UnsupportedStatement.` |
| Advisory locks | None. No `pg_advisory_lock`, `pg_advisory_xact_lock`, `pg_try_advisory_lock`, `unlock` variants, nor any other visible name beginning with `pg_advisory_` or `pg_try_advisory_` | The prefixes `pg_advisory_` and `pg_try_advisory_` on the final name. The prefix also covers a hypothetical effect-free function with such a name; that is an over-broad filter rejection, not evidence of an effect | `UnsupportedStatement.` |
| Sequence functions | `nextval` and `setval`: none (sequence state change). `currval` and `lastval`: the classifier admits the call. Their effect, as visible in the documentation, is reading session state, not writing the sequence. That is not a guarantee of no effects under name shadowing | `nextval` and `setval` are on the exact-name list. `currval` and `lastval` are not. No overload resolution | For `nextval` and `setval`: `UnsupportedStatement.` For `currval` and `lastval`: no rejection |
| `VOLATILE` function performing DML | No effect is admitted as a "side effect of SELECT". The classifier sees neither the body nor the volatility. `SELECT app.write_something()` stays `Allowed`, `HasMutation=false` | No list of names and no read-only transaction. The only control outside the text is the account permissions, which the harness neither changes nor checks | No rejection. Decision `Allowed`. The `read-only` report label is not a guarantee |
| `SECURITY DEFINER` | As above: the attribute does not occur in the call text. The allowed effect is not "none confirmed"; the owner's effect remains outside the AST | It cannot be enforced from the call alone. The harness does not `SET ROLE` and does not take permissions away from the owner | No rejection. Decision `Allowed` |
| Function inside a view | As above. `SELECT * FROM app.hidden_view` contains no call | The relation scan is not expanded to the view definition | No rejection. Decision `Allowed` |
| Effect-free function named `lo_` | None through this filter. `lo_custom_readonly` is rejected just like `lo_export` | The `lo_` prefix on the final name. The name is no evidence of the absence of effects; narrowing the prefix without a catalog boundary would fake such proof. This is a deliberate over-broad rejection (audit B4), not a vulnerability | `UnsupportedStatement.` |
| Executing SQL from text | None. The text argument is not classified, so a visible call may execute SQL that this classifier does not see | Rejection on the exact final name: `query_to_xml`, `query_to_xmlschema`, `query_to_xml_and_xmlschema`, `cursor_to_xml`, `cursor_to_xmlschema`, `ts_stat` | `UnsupportedStatement.` |
| Administrative functions and WAL | None. No session delay, notification, reading server file metadata, configuration reload, log rotation, WAL switch, restore point, statistics reset, creation or removal of a replication slot, logical message and replication origin change | The prefixes `pg_sleep` (`pg_sleep`, `pg_sleep_for`, `pg_sleep_until`), `pg_stat_reset`, `pg_create_` (physical and logical replication slots and `pg_create_restore_point`) and `pg_replication_origin_` on the final name. Exact final names that these prefixes do not cover: `pg_notify`, `pg_stat_file`, `pg_reload_conf`, `pg_rotate_logfile`, `pg_switch_wal`, `pg_drop_replication_slot`, `pg_logical_emit_message`. The prefix also covers a hypothetical effect-free function with such a name; that is an over-broad filter rejection, not evidence of an effect | `UnsupportedStatement.` |

The same name rules cover the existing items that this change does not relax: the exact `pg_read_file`, `pg_ls_dir`, `lo_import`, `query_to_xml`, `query_to_xmlschema`, `query_to_xml_and_xmlschema`, `cursor_to_xml`, `cursor_to_xmlschema`, `ts_stat`, `pg_notify`, `pg_stat_file`, `pg_reload_conf`, `pg_rotate_logfile`, `pg_switch_wal`, `pg_drop_replication_slot`, `pg_logical_emit_message`; the prefixes `dblink`, `pg_read_`, `pg_ls_`, `pg_sleep`, `pg_stat_reset`, `pg_create_`, `pg_replication_origin_`. They concern a function expression, a function in `FROM` and a table function. The text literal `'set_config'` is not a call.

The rejection message contains no SQL text and no arguments. `RejectionDescription` with an empty `Detail` is `"{Reason}."`.

## Chosen policy and trust boundary

The policy is called **effects visible in the text, session without exact commands**.

The boundary is settled, and therefore the claim "read-only guaranteed" is false:

1. **Enforced.** Before connecting, the classifier rejects a parse error, every disallowed call from the matrix, and every write target placed in the AST. A persistent target in `Query` requires the existing mutation contract. `CompareSetup` does not accept a persistent write. Persistent DDL remains `UnsupportedStatement` and the mutation flags do not unlock it.
2. **Enforced as non-interference.** The Npgsql session executes the already classified text without a wrapper. It sets no read-only transaction, `search_path`, role nor permissions.
3. **Outside the harness.** The effects of functions, views, operators, rules, triggers and `search_path` are constrained solely by the account role prepared outside SQLHarness. This change neither creates nor checks that role. The absence of that external boundary is the reason why points 1 and 2 do not add up to a read-only guarantee.

The JSON label from `ClassificationLabel` stays unchanged in meaning and in text: `mutation`, `session-local` or `read-only`. The word `read-only` means "no visible mutation and no session-local work". It does not mean the absence of hidden effects. This document does not rework that label, because it is shared by both engines.

A read-only transaction does not enter the implementation. On its own it blocks neither the administrative rows nor external access, and `CREATE TEMP` is a `CREATE`, so setup would stop meeting the temporary tables contract or would require separate runs and a different transaction for the DDL. That would increase the number of calls and would still not close the trust boundary.

Overload resolution via the catalog does not enter this change. Without a trusted entry and without expanding views and operators it would be just another list, not a boundary.

## Classify

`Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables)`:

1. Parses the whole batch. A parser exception yields `ParseError`. An empty batch yields `UnsupportedStatement`. Any use other than `Query` and `CompareSetup` yields `UnsupportedStatement`.
2. Traverses every statement. The library's traversal does not enter `TableFactor.Derived`, the `CREATE TABLE AS` query, the `DELETE` filter, the `CREATE TABLE` element, nor `CREATE INDEX` expressions. The `CREATE TABLE` element is not an `IElement`, so `Visit` itself sees neither the column default value, nor the `GENERATED` expression, nor the column and table `CHECK`, nor the `WITH` / `OPTIONS` / `TBLPROPERTIES` lists (a `SqlOption` value is an expression), nor `ORDER BY` nor the element's other expressions. The `WHERE` predicate, the key expression and the index's `WITH` list also stay outside that traversal. The classifier enters those locations itself, with the same name filter, recursively, as well as the locations the library already visits (the `SELECT` list, CTEs, the `INSERT` source, `IN` and scalar subqueries, when the library visits them). A visible call in `CREATE TEMP` rejects the whole batch, also when `INSERT … DEFAULT VALUES` follows.
3. A visible call from the matrix rejects the whole batch as `UnsupportedStatement`, also with `allowMutation=true` and also in `CompareSetup`.
4. Then it classifies the statements one by one. It evaluates the write targets of each statement against the temporary tables known before that statement, and adds the `TEMP` name declared by that statement to the set only after that evaluation, so that a later statement sees the table from the earlier `CREATE TEMP`, whereby a write in a CTE of the same statement does not see the relation that this statement creates.

A write target is an `INSERT`, `UPDATE`, `DELETE` or `MERGE` target found in a statement, in a CTE, in an `INSERT` source and in a subquery the classifier enters. A target without a resolvable relation name yields `UnsupportedStatement`. `SELECT INTO` yields `SelectIntoNotAllowed` in `Query` (also with mutation consent) and `NonTemporaryWrite` in `CompareSetup`.

When all found targets are session-local, the statement is local work. When at least one is not, the statement is a mutation, even if it simultaneously creates a temporary table. No targets in a `SELECT` without `INTO` is a text read (`Allowed`, no mutation and no local work).

A target is session-local when the name is a single identifier present in the set of temporary tables of this session and this batch, or when it has exactly two parts and the first is exactly `pg_temp` (the server alias for the temporary schema of the current session). **Changed by plan 011** (2026-10-01, `plans/011-syntax-contract.md`, rule 011-T4b-D7; the sentence above is the binding rule, previously the prefix was also evidence): the prefix `pg_temp_` — in the schema name (`pg_temp_<N>`) or in the relation name — is no longer evidence for any statement.

`Query` under mutation: no `allowMutation` yields `MutationNotAllowed`; no `confirmDatabase` yields `DatabaseConfirmationRequired`; a different database name, compared ordinally, yields `DatabaseConfirmationMismatch`; a matching pair of flags yields `Allowed` with `HasMutation=true`. Local work sets `HasSessionLocalWork` and returns the set of temporary names.

`CompareSetup` ignores the mutation flags. A text read and exclusively temporary work are permitted. A mutation, `SELECT INTO` and a non-temporary write yield `NonTemporaryWrite`. Persistent DDL and an unknown statement yield `UnsupportedStatement`.

## Session: Query and CompareSetup

Both uses have the same execution scope:

- `NpgsqlSession` sets `CommandText` to the SQL passed in `SqlExecutionCommand`, `CommandType=Text` and the existing timeout. It appends no `BEGIN`, `SET TRANSACTION`, `SET`, `SET ROLE` nor any other prefix.
- The connection string sets no `default_transaction_read_only`, `search_path`, role-changing `Options` nor transaction mode.
- After `Open` there is still only the existing identity query. The policy adds no round trip.
- `Query` sends one classified batch. `CompareSetup` sends one classified setup batch over the same connection that is later used for the repetitions. The temporary object from setup stays visible.
- `measure` and `compare` still wrap the measured statement in `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` and, when they compare rows, execute an unmeasured sidecar. Those are existing engine calls, not new calls introduced by this policy.

The number of server calls does not grow. The classification is local.

## Setup and temporary tables

Without mutation flags, still permitted in `Query` and in `CompareSetup`:

- `CREATE TEMP` / `CREATE TEMPORARY`, also `CREATE TEMP TABLE AS SELECT` without a write target in the source query;
- `INSERT` / `UPDATE` / `DELETE` / `MERGE` and modifying CTEs whose every target is session-local;
- `CREATE INDEX` and `DROP` of those tables, and `DROP` of an index marked as temporary.

`UNLOGGED` and `CREATE TABLE` without `TEMP` remain persistent DDL. **Changed by plan 011** (2026-10-01, 011-T4b-D7; the following is the binding rule): a write to `pg_temp_fake` is NO LONGER treated as local — the `pg_temp_<N>` qualifier stopped being evidence; only the exact `pg_temp` alias, or a name proven by `CREATE TEMP` in this session, remains local.

A temporary batch that additionally contains a visible persistent write target is not "local only". In setup it is `NonTemporaryWrite`. In `Query` it requires the mutation contract.

## S4 — AST coverage, not an exploit

PostgreSQL allows a modifying CTE only in a `WITH` attached to a top-level statement (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`). The `WITH` stands before that statement. The example from the documentation moves rows through `WITH deleted AS (DELETE …) INSERT INTO …`, not through `INSERT … WITH …`.

SqlParserCS accepts the following shapes, and this document does not call them server bypasses:

- `INSERT INTO x WITH changed AS (INSERT INTO public.items …) SELECT …` — the library sees both targets. The server grammar does not place `WITH` between `INSERT` and the source. The classifier rejects the persistent target anyway.
- `CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items …) SELECT …` — the library sees the `INSERT` in the `AS` query. `CREATE TABLE AS` is not a statement to which the documentation attaches a modifying `WITH`. The classifier treats `public.items` as a mutation anyway.
- `CREATE TEMP TABLE x AS WITH changed AS (DELETE FROM public.items …) SELECT …` — the library returns a parse error. The `ParseError` decision is a rejection of the library's syntax, not evidence of execution on the server.

A shape conforming to the server documentation, `WITH changed AS (INSERT INTO public.items …) SELECT …` or `WITH changed AS (…) INSERT INTO …`, remains an ordinary mutation when the target is not session-local. Rejecting that shape was already correct before this policy and is not a "confirmed S4 exploit".

The offline tests must call the first two shapes syntax accepted by the library and must not claim the server executed them. This change performs no tests of functions and temporary tables against a database. Appending a synthetic result for such a run is forbidden.