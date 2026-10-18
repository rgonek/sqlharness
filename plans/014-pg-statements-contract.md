# pg_stat_statements contract — source matrix

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

Status: design of the v1 cumulative ranking; source matrix (T1), connection sequence and runtime probe (T2), separate contract borrowed from qstop (T3). Correction 2026-10-11 at `729d88f`; delta deferred (T4). The function remains PLANNED.

## Source matrix

This matrix checks the official PostgreSQL 14, 15 and 16 pages plus two local references. It does not describe the read sequence, the ranking, the delta or the code plan. It adds no commands, stubs or capabilities. No live DB and no `dotnet test` were run.

The server version is not the version of the `pg_stat_statements` extension. The module page describes the shape shipped with a given server major version. The installed version is a separate catalog field. `ALTER EXTENSION … UPDATE` changes it, not a server upgrade by itself.

Footers of the opened module pages: PostgreSQL 14.24, section F.30; PostgreSQL 15.19, section F.32; PostgreSQL 16.15, section F.32. The numbers `1.9` and `1.10` do not appear on those HTML pages. They come from the control file of the tag.

The specification `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` is a hypothesis. Its sections 3, 6 and 7 were compared. Row status: fact from the source, divergence from the specification, or `UNPROVEN` with an evidence condition.

### Extension version, not server version

| Row | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Number on the module page | Page F.30 gives neither `default_version` nor an extension number. | Page F.32 does not give that number. | Page F.32 does not give that number. | fact |
| `default_version` in the control file | Tags `REL_14_0` and `REL_14_24`: `default_version = '1.9'`. | Tags `REL_15_0` and `REL_15_19`: `default_version = '1.10'`. | Tags `REL_16_0` and `REL_16_15`: `default_version = '1.10'`. | fact; the `contrib/pg_stat_statements/pg_stat_statements.control` file on the `postgres/postgres` mirror. This is not a server number. |
| Intermediate releases between the `.0` tag and the footer | `REL_14_1` … `REL_14_23` were not opened. | `REL_15_1` … `REL_15_18` were not opened. | `REL_16_1` … `REL_16_14` were not opened. | `UNPROVEN`. Condition: read `default_version` in every intermediate tag. Both opened ends of this major version agree, but the middle of the series was not opened. |
| Where the version comes from at `CREATE EXTENSION` | The `version` parameter: „The default version is whatever is specified in the extension's control file.” | Same sentence. | Same sentence. | fact. An omitted `VERSION` does not mean a server number. |
| `ALTER EXTENSION … UPDATE` | Synopsis: `ALTER EXTENSION name UPDATE [ TO new_version ]`. The `UPDATE` form updates the installed version with an update script. Without `new_version` the command „attempts to update to whatever is shown as the default version in the extension's control file.” | Same. | Same. Section 38.17.4 adds: `ALTER EXTENSION UPDATE` „track which version of the extension is actually installed in a given database.” | fact. This is a separate SQL command, not a server upgrade. The sentence about tracking the installed version was opened directly for PG16; PG14 and PG15 have the same `UPDATE` form on the `ALTER EXTENSION` page. |
| Server upgrade versus extension | `pg_upgrade`, step „Install extension shared object files”: „Do not load the schema definitions, e.g., `CREATE EXTENSION` pgcrypto, because these will be duplicated from the old cluster. If extension updates are available, `pg_upgrade` will report this and create a script that can be run later to update them.” | Same sentence. | Same sentence. | fact. The page does not name the content of that script as `ALTER EXTENSION pg_stat_statements UPDATE`. It says that the extension update is a later script and that the definitions come from the old cluster. |
| Version catalog | `pg_extension.extversion` `text`: „Version name for the extension” (52.22). `pg_available_extensions.default_version`: „Name of default version, or NULL if none is specified.” `installed_version`: „Currently installed version of the extension, or NULL if not installed” (52.65). | The same columns. `pg_extension` is 53.22, the view is 54.2. | The same columns. `pg_extension` is 53.22, the view is 54.2. | fact. `installed_version` and `extversion` are not `server_version`. |
| Installation scope | The views and functions „are not available globally but can be enabled for a specific database with `CREATE EXTENSION pg_stat_statements`.” | Same sentence. | Same sentence. | fact |

### `pg_stat_statements` columns

The view has one row for each distinct combination of database ID, user ID, query ID and whether the command is top-level, up to the module limit. That sentence is in F.30.1, F.32.1 (PG15) and F.32.1 (PG16).

`toplevel` and the plan times are already in the PG14 table. The opened pages do not say that they arrived between PG14 and PG16. Between those tables the I/O columns `temp_blk_read_time` and `temp_blk_write_time` and eight `jit_*` columns are added.

| Column | Type | PG14 F.21 | PG15 F.20 | PG16 F.22 | Status |
|---|---|---|---|---|---|
| `userid` | `oid` | yes | yes | yes | fact |
| `dbid` | `oid` | yes | yes | yes | fact |
| `toplevel` | `bool` | yes | yes | yes | fact. Description: true when the query was executed as top-level; always true when `pg_stat_statements.track` is `top`. Spec §3 includes the column in the PG14 subset. Agrees on presence. |
| `queryid` | `bigint` | yes | yes | yes | fact. „Hash code to identify identical normalized queries.” |
| `query` | `text` | yes | yes | yes | fact. The table row does not say „nullable”. The prose talks about null separately, in the visibility rows. Spec §3 writes `(nullable)`. This agrees with the prose about discarding texts, not with the type row. |
| `plans` | `bigint` | yes | yes | yes | fact. Number of plannings when `pg_stat_statements.track_planning` is enabled, otherwise zero. |
| `total_plan_time` | `double precision` | yes | yes | yes | fact. Milliseconds; zero when `track_planning` is disabled. |
| `min_plan_time` | `double precision` | yes | yes | yes | fact. Same zero condition. |
| `max_plan_time` | `double precision` | yes | yes | yes | fact. Same zero condition. |
| `mean_plan_time` | `double precision` | yes | yes | yes | fact. Same zero condition. |
| `stddev_plan_time` | `double precision` | yes | yes | yes | fact. Same zero condition. |
| `calls` | `bigint` | yes | yes | yes | fact |
| `total_exec_time` | `double precision` | yes | yes | yes | fact |
| `min_exec_time` | `double precision` | yes | yes | yes | fact |
| `max_exec_time` | `double precision` | yes | yes | yes | fact |
| `mean_exec_time` | `double precision` | yes | yes | yes | fact |
| `stddev_exec_time` | `double precision` | yes | yes | yes | fact |
| `rows` | `bigint` | yes | yes | yes | fact |
| `shared_blks_hit` | `bigint` | yes | yes | yes | fact |
| `shared_blks_read` | `bigint` | yes | yes | yes | fact |
| `shared_blks_dirtied` | `bigint` | yes | yes | yes | fact |
| `shared_blks_written` | `bigint` | yes | yes | yes | fact |
| `local_blks_hit` | `bigint` | yes | yes | yes | fact |
| `local_blks_read` | `bigint` | yes | yes | yes | fact |
| `local_blks_dirtied` | `bigint` | yes | yes | yes | fact |
| `local_blks_written` | `bigint` | yes | yes | yes | fact |
| `temp_blks_read` | `bigint` | yes | yes | yes | fact |
| `temp_blks_written` | `bigint` | yes | yes | yes | fact |
| `blk_read_time` | `double precision` | yes | yes | yes | fact, with a description change. PG14: „reading blocks”. PG15 and PG16: „reading data file blocks”. Zero when `track_io_timing` is disabled. |
| `blk_write_time` | `double precision` | yes | yes | yes | fact, with a description change. PG14: „writing blocks”. PG15 and PG16: „writing data file blocks”. Zero when `track_io_timing` is disabled. |
| `temp_blk_read_time` | `double precision` | absent | yes | yes | fact. Time of reading temporary-file blocks, in milliseconds; zero when `track_io_timing` is disabled. Spec §3 does not list this column. Divergence: an I/O column appeared between PG14 and PG15 that section 3 lacks. |
| `temp_blk_write_time` | `double precision` | absent | yes | yes | fact. The same divergence as above, for writing temporary blocks. |
| `wal_records` | `bigint` | yes | yes | yes | fact |
| `wal_fpi` | `bigint` | yes | yes | yes | fact |
| `wal_bytes` | `numeric` | yes | yes | yes | fact |
| `jit_functions` | `bigint` | absent | yes | yes | fact. Spec §3: PG15 adds eight `jit_*`, including this one. Agrees. |
| `jit_generation_time` | `double precision` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_inlining_count` | `bigint` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_inlining_time` | `double precision` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_optimization_count` | `bigint` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_optimization_time` | `double precision` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_emission_count` | `bigint` | absent | yes | yes | fact. Agrees with spec §3. |
| `jit_emission_time` | `double precision` | absent | yes | yes | fact. Agrees with spec §3. |

On the opened PG15 and PG16 pages there is no `jit_*` column besides that set of eight. The PG14 page has no `jit_*` column at all.

### `pg_stat_statements_info` columns

The view has one row. That sentence is in F.30.2, F.32.2 (PG15) and F.32.2 (PG16).

| Column | Type | PG14 F.22 | PG15 F.21 | PG16 F.23 | Status |
|---|---|---|---|---|---|
| `dealloc` | `bigint` | yes | yes | yes | fact. „Total number of times `pg_stat_statements` entries about the least-executed statements were deallocated because more distinct statements than `pg_stat_statements.max` were observed.” Spec §3 lists this column on 14+. Agrees on presence. |
| `stats_reset` | `timestamp with time zone` | yes | yes | yes | fact. „Time at which all statistics in the `pg_stat_statements` view were last reset.” Spec §3 lists `stats_reset timestamptz` on 14+. Agrees on presence and type. |

### GUCs for these columns

| Row | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| `pg_stat_statements.track` | `top`, `all` or `none`. Default `top`. | Same. | Same. | fact. Spec §3: default `top`. Agrees. |
| `pg_stat_statements.track_planning` | Default `off`. Plan times and `plans` are then zero. | Same. | Same. | fact. Spec §3: plan times are 0 unless the GUC is enabled. Agrees. |
| `pg_stat_statements.track_utility` | Default `on`. Utility is everything except `SELECT`, `INSERT`, `UPDATE` and `DELETE`. | Default `on`. The exclusion list adds `MERGE`. | Default `on`. The exclusion list adds `MERGE`. | fact. Spec §3 gives in parentheses utility as non-`SELECT`/`INSERT`/`UPDATE`/`DELETE`, without `MERGE`. Agrees with PG14. Divergence for PG15 and PG16. |
| Utility normalization | „Utility commands … are compared strictly on the basis of their textual query strings.” Plannable: `SELECT`, `INSERT`, `UPDATE` and `DELETE`. | Same sentence about the utility text. Plannable adds `MERGE`. | „Plannable queries … and utility commands are combined” by internal hash. That page has no sentence about strict comparison of the utility text. | fact of the page difference. Spec §3, §6 and §7 do not settle this. This matrix does not design entry identity. |
| `track_io_timing` | `blk_read_time` and `blk_write_time` are zero when disabled. | Same, plus `temp_blk_read_time` and `temp_blk_write_time`. | Same as PG15. | fact. Spec §3 talks about zero for `blk_read_time` / `blk_write_time`. Agrees. The spec is silent about the `temp_blk_*_time` columns; divergence as in the column table. |

### `queryid` and `query` visibility

| Row | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Who sees the text and `queryid` of other users' queries | „only superusers and members of the `pg_read_all_stats` role are allowed to see the SQL text and `queryid` of queries executed by other users.” | „only superusers and roles with privileges of the `pg_read_all_stats` role are allowed to see the SQL text and `queryid` of queries executed by other users.” | Same sentence as PG15. | fact. Spec §6 says „membership”. PG14 says „members of the `pg_read_all_stats` role”. PG15 and PG16 say „roles with privileges of”. The wording divergence is on PG15 and PG16. These pages do not define a difference between the wordings. |
| Whether others see statistics | „Other users can see the statistics, however, if the view has been installed in their database.” | Same sentence. | Same sentence. | fact. Spec §6: a non-superuser sees row statistics, and the text and `queryid` only of their own queries. The source sentence confirms that statistics are visible. It does not say „all rows” in those words. |
| Hidden value: null or no row | The page does not say whether hidden `query` and `queryid` are NULL or the row disappears. | The page does not say. | The page does not say. | `UNPROVEN`. Condition: read the view as a role without `pg_read_all_stats` permissions, for another user's row, on an explicit target; the result must show whether `queryid` and `query` are NULL or the row is absent. That read was not performed. |
| Null `query` with `showtext` | Function `pg_stat_statements(showtext boolean)`. `showtext := false` skips the text: the `OUT` argument corresponding to the `query` column returns null. | Same. | Same. | fact. This is not a permissions rule. That sentence does not mention `queryid`. |
| Null `query` after texts are discarded | Representative texts lie in a disk file and do not consume shared memory. When the file grows excessively, the module may discard texts: existing entries show null in `query`, while the statistics at `queryid` remain. | Same. | Same, plus a separate sentence below. | fact. Spec §3 calls `query` nullable. Agrees with that sentence. |
| Constants in the text on dealloc | That paragraph is not there. | That paragraph is not there. | „Queries on which normalization can be applied may be observed with constant values in `pg_stat_statements`, especially when there is a high rate of entry deallocations.” Advice: raise `pg_stat_statements.max`. | fact for PG16 only. |

### `pg_stat_statements_reset`

The signature in F.30.3, F.32.3 (PG15) and F.32.3 (PG16) is one: `pg_stat_statements_reset(userid Oid, dbid Oid, queryid bigint) returns void`.

The function sentence, the same on all three pages: it discards the `pg_stat_statements` statistics matching the given `userid`, `dbid` and `queryid`. An omitted parameter gets the default `0`(invalid); statistics matching the remaining parameters are reset. No parameters, or all parameters `0`(invalid), discards all statistics. „If all statistics in the `pg_stat_statements` view are discarded, it will also reset the statistics in the `pg_stat_statements_info` view.” By default superuser only; others can be granted it with `GRANT`.

The example in F.30.5, F.32.5 (PG15) and F.32.5 (PG16) calls `pg_stat_statements_reset()`, `pg_stat_statements_reset(0,0,s.queryid)` and `pg_stat_statements_reset(0,0,0)`. The example does not read `pg_stat_statements_info`.

| Row | What the source says | Status |
|---|---|---|
| Reset of everything | No parameters, or all `0`, discards all statistics of the view and resets the statistics of the `pg_stat_statements_info` view. | fact. The page does not separate `stats_reset` and `dealloc` in that sentence. |
| Effect of a full reset on `stats_reset` separately and on `dealloc` separately | `stats_reset` is described as the time of the last reset of all statistics of the view. `dealloc` is described as a counter of evictions caused by `pg_stat_statements.max`, not as an effect of a reset. The function sentence says only that the statistics of the info view are reset when all statistics have been discarded. | `UNPROVEN` whether a full reset sets `stats_reset`, zeroes `dealloc`, or does both. Condition: read both `pg_stat_statements_info` columns before and after `pg_stat_statements_reset()` with no arguments on an explicit target. That read was not performed. |
| Reset of one database | The parameter rule: a given `dbid` with the others `0` resets statistics matching that `dbid`. The page has no separate „reset of database” signature. | fact as to the parameter rule. |
| Effect of a database reset on `stats_reset` and `dealloc` | The sentence about the info view reset is conditional: only when all statistics of the view have been discarded. Resetting one database does not by itself state that condition. | `UNPROVEN`. Condition: read `pg_stat_statements_info` before and after `pg_stat_statements_reset` with the `dbid` of one database and zeros for `userid` and `queryid`, on an explicit target, with statistics present in another database too. That read was not performed. |
| Reset of a single `queryid` | A call with `queryid` discards statistics matching the given parameters. The example `pg_stat_statements_reset(0,0,s.queryid)` leaves the other queries on the list with their previous `calls`. | fact: resetting a single `queryid` is not described as discarding all statistics of the view. The example shows neither `stats_reset` nor `dealloc`. |
| Effect of a single-`queryid` reset on `stats_reset` and `dealloc` | The page does not say that such a reset updates `stats_reset`, zeroes `dealloc`, or only moves the entry counters. The condition „if all statistics … are discarded” is not applied verbatim to a single `queryid`. | `UNPROVEN`. Evidence condition: read `pg_stat_statements_info` before and after `pg_stat_statements_reset` with a specific `queryid` on an explicit target. That read was not performed. |
| Spec §7 versus selective reset | Spec §7 counts a manual `pg_stat_statements_reset`, „full or selective”, among the things that change `stats_reset`. | divergence. The opened pages do not say that a selective reset changes `stats_reset`. The effect cell stays `UNPROVEN`, not a specification fact. |

### `dealloc` and `pg_stat_statements.max`

| Row | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Meaning of `max` | „the maximum number of statements tracked by the module (i.e., the maximum number of rows in the `pg_stat_statements` view).” Default 5000. Server startup only. | Same. | Same. | fact. Spec §3: default 5000, startup only. Agrees. |
| Eviction of an entry | „If more distinct statements than that are observed, information about the least-executed statements is discarded.” The number of such discards is visible in `pg_stat_statements_info`. | Same. | Same. | fact. That is the meaning of `dealloc` from the info table. Spec §3 says that overflow evicts the least-executed entries and increments `info.dealloc`. Agrees with that description. |
| Effect of the delta after an eviction | The page does not talk about a per-entry delta or a partial delta. | The page does not say. | The page does not say. It only adds that on frequent dealloc the text may retain constants. | the source fact ends at eviction and the counter. Spec §7 guesses that per-entry deltas after eviction are „silently partial”. The page does not describe that effect. This matrix does not design the delta. |
| `pg_stat_statements.save` | Whether to save statistics across a server shutdown. `off`: does not save on shutdown and does not load on startup. Default `on`. | Same. | Same. | fact. Spec §7: default `on`, `off` loses statistics on shutdown. Agrees. No delta rule. |

### Local references

| Reference | What was opened | Status |
|---|---|---|
| `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` §3 | The PG14 column subset, `toplevel` and `pg_stat_statements_info` on 14+, eight `jit_*` from PG15, zeros with `track_planning` and `track_io_timing` disabled, `track` default `top`, `track_utility` without `MERGE`, `max` 5000. | hypothesis. Agrees with the tables where the rows above say „agrees”. Divergence: no `temp_blk_read_time` and `temp_blk_write_time`; `track_utility` without `MERGE` on PG15 and PG16. |
| The same specification §6 | A non-superuser sees statistics, and the text and `queryid` of other users' queries require superuser or `pg_read_all_stats` membership. | hypothesis. Statistics visibility agrees with the sentence „Other users can see the statistics”. Membership agrees with the PG14 wording and diverges from the PG15 and PG16 wording. The hidden value (null or no row) is not settled in §6 and is `UNPROVEN` on the pages. |
| The same specification §7 | Every change of `stats_reset`, including a selective `pg_stat_statements_reset`, breaks continuity. A rise of `dealloc` is eviction under `pg_stat_statements.max`. | hypothesis. The description of `dealloc` and `max` agrees with the page. The sentence that a selective reset changes `stats_reset` is not a sentence of the opened pages. Divergence; the effect stays `UNPROVEN`. |
| `src/SqlHarness.Mcp/McpOperationMapper.cs` | `BuildCapabilities`: `pg` is true when `engine` is `"postgres"`. The capabilities document gets `QueryStoreAvailable: !pg`. | code fact, reference point. On Postgres the Query Store flag is false. This is not a `pg_stat_statements` read and not a new capability. |

### Opened URLs

Module pages:

- https://www.postgresql.org/docs/14/pgstatstatements.html
- https://www.postgresql.org/docs/15/pgstatstatements.html
- https://www.postgresql.org/docs/16/pgstatstatements.html

Extension version and upgrade:

- https://www.postgresql.org/docs/14/sql-createextension.html
- https://www.postgresql.org/docs/15/sql-createextension.html
- https://www.postgresql.org/docs/16/sql-createextension.html
- https://www.postgresql.org/docs/14/sql-alterextension.html
- https://www.postgresql.org/docs/15/sql-alterextension.html
- https://www.postgresql.org/docs/16/sql-alterextension.html
- https://www.postgresql.org/docs/16/extend-extensions.html
- https://www.postgresql.org/docs/14/pgupgrade.html
- https://www.postgresql.org/docs/15/pgupgrade.html
- https://www.postgresql.org/docs/16/pgupgrade.html
- https://www.postgresql.org/docs/14/catalog-pg-extension.html
- https://www.postgresql.org/docs/15/catalog-pg-extension.html
- https://www.postgresql.org/docs/16/catalog-pg-extension.html
- https://www.postgresql.org/docs/14/view-pg-available-extensions.html
- https://www.postgresql.org/docs/15/view-pg-available-extensions.html
- https://www.postgresql.org/docs/16/view-pg-available-extensions.html

Control file, tags corresponding to `.0` and to the documentation footer:

- https://raw.githubusercontent.com/postgres/postgres/REL_14_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_14_24/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_15_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_15_19/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_16_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_16_15/contrib/pg_stat_statements/pg_stat_statements.control

Local references:

- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`

## Connection sequence

This section is step T2. It does not repeat the matrix. It adds no commands, stubs or capabilities. It does not design the ranking, the delta or the file list. No live DB and no `dotnet test` were run.

The order is single: resolve, auth, identity, server version, extension version, then column-dependent SQL. Outputs are determined by the existing `OperationFailureMapper.Map`. The numbers are in `SqlHarnessExitCode`. The adapter does not get its own classifier. `QueryStoreAvailable: !pg` in `src/SqlHarness.Mcp/McpOperationMapper.cs` stays. This sequence is not a new capability.

1. **Resolve.** `TargetResolver.Resolve` in `src/SqlHarness.Core/Targets/TargetResolver.cs` builds `ResolvedTarget` from the profile or from the `--unsafe-direct` path. `TargetProfile` in `src/SqlHarness.Core/Targets/TargetProfile.cs` has no server version field. `ResolvedTarget` does not carry one either: it has `Server`, `Database`, `Auth`, `Mode`, `Engine` and `Transport`. The engine comes from `SqlEngineNames.Parse`. An omitted `engine` stays `SqlEngine.SqlServer` and from the auth step onward does not enter this sequence. Postgres requires `AuthStrategy.Sql`. Another strategy, an unknown profile, a bad `--var` and a bad transport are `SqlHarnessSafetyException` before the connection is opened. The mapper yields exit `2` (`SqlHarnessExitCode.Safety`). The PostgreSQL 14 floor cannot be checked on the profile, because there is no version there.

2. **Auth.** `EngineSessionFactory` routes `SqlEngine.Postgres` to `NpgsqlSessionFactory.ConnectAsync`. In `ConnectAsync`, `PostgresConnectionString.Build` runs first. A missing user or an empty or absent password variable is `SqlHarnessSafetyException` before `NpgsqlConnection.OpenAsync`. The mapper yields exit `2`, not exit `3`, also when the caller already set `OperationPhase.Authentication`. The message may name the variable. It does not contain the password value. `OpenAsync` itself is the authentication. An `NpgsqlException` in the `Authentication` phase maps to exit `3` (`SqlHarnessExitCode.Authentication`). That is before identity confirmation and before the extension read. After a failed `OpenAsync` the session does not return to the caller.

3. **Identity.** After a successful open, still inside `ConnectAsync`, `ReadIdentityAsync` runs `PostgresPing.IdentitySql`: `current_database()` and `COALESCE(inet_server_addr()::text, 'localhost')`. That is not `server_version` and not the extension version. The comment in `NpgsqlSessionFactory` leaves `inet_server_addr()` on the report. Whether the target matches is decided by the established connection in `PostgresEndpointIdentity.Matches`. False ends with `SqlTargetMismatchException` from `src/SqlHarness.Core/SqlExecution.cs`. The mapper yields exit `4` (`SqlHarnessExitCode.TargetMismatch`) regardless of the phase. `ConnectAsync` closes the session in `catch` via `DisposeAsync` and rethrows. Exit `3` and exit `4` are both before the extension attempt. An `NpgsqlException` of the identity query, as long as the caller's phase is still `Authentication`, stays exit `3`. The same applies to a missing identity row: `ReadIdentityAsync` then throws `InvalidOperationException`, and the mapper branch for the `Authentication` phase also yields exit `3`. That is not exit `4` nor exit `5`.

4. **Server version.** After the session is returned by `ConnectAsync` the phase is `OperationPhase.Sql`. `SELECT current_setting('server_version_num')` returns text parsed culture-invariantly; an invalid read is exit `5`. We keep the PG14+ scope: a number below `140000` is exit `2`, and the session is closed by `await using`. The server number serves context and the PG14+ scope, not the choice of the exact list of allowed version pairs.

5. **Extension.** Read the installed `pg_stat_statements`, its version and namespace from the catalog in the resolved database. No installation is exit `5`, never an empty success. Do not call `CREATE EXTENSION`, `ALTER EXTENSION` or a reset. Find the view belonging to that extension; do not trust an object of the same name found through `search_path`. Quote the namespace as an identifier through the existing PostgreSQL helper (doubling the quotes); select columns only from the internal list. Report the extension version as diagnostic context. An unknown number is not a rejection.

6. **Runtime shape probe.** After identity and confirmation of the installation, read the column names and types of the extension's view from the catalogs. Required: `userid`/`dbid` (`oid`), `toplevel` (`bool`), `queryid` (`bigint`, the value may be NULL), `query` (`text`, the value may be NULL), `calls`/`rows`/`shared_blks_hit`/`shared_blks_read` (`bigint`), `total_exec_time`/`max_exec_time` (`double precision`). A missing required column or a mismatched type is exit `5` with the reason incompatible shape; missing permissions or an invalid read are also exit `5`. Do not return a success artifact then. Ignore extra columns. The SQL selects explicit names, never `SELECT *`; do not send a reference to an unconfirmed column. PG14/15/16/17/18 and unknown versions with a compatible shape pass the same check.

Metric columns beyond the minimum are optional: plan times (`double precision`), local/temp counters (`bigint`), `wal_bytes` (`numeric`), I/O times (`double precision`). A missing or unsupported type yields `null`/unavailable with a reason description, never 0. Optional sums are available only when the data of all aggregated rows is valid; do not present an incomplete sum as complete. Supporting aliases of new I/O names requires confirmation of their semantics in the official documentation (plan 037). Without that they remain unavailable. `pg_stat_statements_info`, its `dealloc`/`stats_reset` and the GUCs are optional context: a missing object/column or missing permissions yield unavailable context with a reason, not a rejection of the available ranking. Do not swallow other execution/transport errors. The executor confirms catalog SQL, object ownership and types on primary sources and fixtures; the source matrix below describes the tested examples, not a runtime allowlist.

Specification §9 puts a missing extension and missing permissions under the same exit `5`. This section keeps that number and separates the reasons. A readable view with zero rows is neither of those two reasons. Specification §9 calls such a readable empty result exit `0`. This section of the report does not design that. This sequence does not touch exit `6`, `7` and `8`. There is no new exit code.

Sources of the server version read, opened in this step:

- https://www.postgresql.org/docs/14/runtime-config-preset.html (`server_version` is `string`, `server_version_num` is `integer`)
- https://www.postgresql.org/docs/14/libpq-status.html (`PQserverVersion`: major times `10000` plus minor; version 11.0 is `110000`)

Local references of this step:

- `src/SqlHarness.Core/Targets/TargetResolver.cs`
- `src/SqlHarness.Core/Targets/TargetProfile.cs`
- `src/SqlHarness.Core/SqlEngine.cs`
- `src/SqlHarness.Core/EngineSessionFactory.cs`
- `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs`
- `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`
- `src/SqlHarness.Core/Postgres/PostgresPing.cs`
- `src/SqlHarness.Core/Postgres/PostgresEndpointIdentity.cs`
- `src/SqlHarness.Core/SqlExecution.cs`
- `src/SqlHarness.Core/OperationFailureMapper.cs`
- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, sections 2 and 9, as a hypothesis where steps 5 and 6 do not refute it

## Separate contract borrowed from qstop

This section is step T3. It does not repeat the matrix and the sequence. It adds no commands, stubs or capabilities. It does not design the delta or the file list. No live DB and no `dotnet test` were run.

`qstop` remains a SQL Server contract. In `ExecuteQueryStoreTopAsync` the `SqlEngine.Postgres` engine gets an `InvalidOperationException` with the text „Query Store is available only on SQL Server.” The phase is already `Sql`, so the mapper yields exit `5` (`SqlHarnessExitCode.SqlExecution`). `QueryStoreAvailable: !pg` in `src/SqlHarness.Mcp/McpOperationMapper.cs` stays. This contract is not a change to `qstop` and not a new capability.

### What this contract does not take from `qstop`

| From `qstop` | Source in the code | This contract |
|---|---|---|
| `--window`, `windowMinutes` (1..44640 minutes, default `24h`) | `QueryStoreTopCommand.Settings`, `QueryStoreWindowParser`, the `DATEADD` predicate in `QueryStoreTopQuery.Sql` | There is no `--window`. The counters are cumulative. The absence of `--window` is a contract boundary. |
| `lastExecutionAt` | `MAX(rs.last_execution_time)`; the field `QueryStoreTopItemReport.LastExecutionAt` | There is no recency field. The column matrix has no per-entry time. |
| `totalCpuMilliseconds`, `averageCpuMilliseconds`, `maximumCpuMilliseconds` | Second `ORDER BY` key and report fields | There is no CPU metric. The contract does not insert a zero to make the shape look like `qstop`. `CpuTimeMs` equal to 0 on `measure` and `compare` is not a field of this report. |
| `queryHash` | Query Store column and field of a `queries.jsonl` line | The view has no such column. The contract does not synthesize it from the text. |
| `querySqlText` | Third field of the line; `RequiredText` rejects null | The line carries `query`. A null in `query` is not a read error. |
| `totalLogicalReads`, `averageLogicalReads`, `maximumLogicalReads`, `objectName`, `planCount` | Fields of `QueryStoreTopItemReport` | Not included. The reads here are view block counters, not logical reads. |

The `qstop` order is `total_duration_milliseconds` DESC, `total_cpu_milliseconds` DESC, `execution_count` DESC, `query_id` ASC. This contract does not use that order. The `SqlHarnessQueryStoreTopReport` record is not this contract's record: there is no `windowMinutes` here.

The JSON names of the `qstop` line are `queryId`, `queryHash`, `querySqlText` (`QueryTextArtifact` under `JsonSerializerDefaults.Web`; the test `QueryStoreArtifactWriterTests` checks that trio). Of that trio this contract keeps the spelling `queryId`. The line text is named `query`, not `querySqlText`.

### Current-database filter

Module pages: when `pg_stat_statements` is active it tracks statistics across all databases of the server. The `dbid` column is „OID of database in which the statement was executed” and refers to `pg_database.oid`. The view itself is not a current-database filter.

This contract's filter is on the server side, before aggregation. Rows stay when their `dbid` is the OID of this connection's database. The OID is a row of `pg_database` whose `datname` is `current_database()` of that session. The comparison is OID to OID. The client does not substitute a name. There is no user argument with a database name: neither a flag, nor a separate `--var`, nor a name from the profile inserted as a predicate. The session identity has already been verified in the sequence. This filter is not a second endpoint check.

A row with a different `dbid` is not a position and is not added to the position with the same `queryid` number. The pages say that a hash collision does not merge queries belonging to different databases into one view entry. This contract does not sum databases anyway.

### Known `queryid`

The view entry, from the matrix, is one combination of database ID, user ID, query ID and whether the command is top-level. A position of this contract is not an entry. A position is one known `queryid` in the filter database.

A known `queryid` is a non-null `bigint` returned in a row after the filter. The report field is named `queryId`: the spelling of specification §5 and of `qstop` JSON. The value is that number, with sign. There is no absolute value and no second identifier.

`queryid` is not a key across servers. The pages: a hash from the parse-analysis tree; dropping and recreating a table separates entries; the result depends on the architecture; it is not stable across major versions; logical replication does not preserve the usefulness of `queryid` for summing costs. A position is a key on this server, in this major version and in this connection's database. That is not a delta and not a comparison between servers.

### Hidden or null `queryid`

Hiding remains `UNPROVEN`, by the matrix row „Hidden value: null or no row”. The page does not say whether `query` and `queryid` of another user's query are NULL or the row disappears. PG14: „only superusers and members of the `pg_read_all_stats` role”. PG15 and PG16: „only superusers and roles with privileges of the `pg_read_all_stats` role”. Other users see statistics if the view is installed in their database. The evidence condition stays the one from the matrix. That read was not performed. This section adds no field that would require settling the shape.

Representation without an invented identifier:

- A missing row is not a position.
- A NULL in `queryid` is not a known key. `QueryStoreTopQuery.RequiredInt64` does not turn null into `long`. Null does not become `QueryId`. Here NULL likewise does not become a position.
- Rows without a known `queryid` are not merged. They are not joined by a shared null, the `query` text, `userid`, `toplevel`, a row number or `queryHash`. The text is not a key.
- The number `0` in `pg_stat_statements_reset` means an invalid parameter. It is not an identifier of unknown rows. A non-null `queryid` equal to `0`, if the view returned it, would be a known `bigint` value, not a shared position of hidden ones.
- Such rows do not enter the order, do not take a place in `--top` and do not get a `queries.jsonl` line. Unknown ids do not form one position nor one aggregation pair. The delta is outside this section.

Visibility of statistics in the view does not create a position without a known `queryid`. The report is not a dump of every view row.

`qstop` aborts the entire read on a null `query_id` (`InvalidOperationException`). This contract does not copy that throw. The matrix leaves the hidden shape as `UNPROVEN` and says statistics may be visible. Exit `5` for the entire read would treat that shape as an error. A read that reached the ranking and produced no known `queryid` is an empty position list.

An empty position list after a successful read is exit `0` (`SqlHarnessExitCode.Success`). It is not a missing extension and not missing permissions. Those two stay exit `5` in the sequence, before the ranking starts. An empty list does not claim that the view had no rows. It claims that the filter database had no position with a known `queryid`. Specification §8 and §9 call a readable absence of rows exit `0`. This section keeps that number also for a list without a known `queryid`.

The success path of `ExecuteQueryStoreTopAsync` writes the artifact after `ReadAsync` and has no separate branch for zero metrics. An empty `qstop` list is also exit `0` with an artifact. This contract does the same for an empty position list: the artifact exists, `queries.jsonl` has no lines.

### Aggregation

Aggregation concerns only rows with the same known `queryid` after the database filter. `userid` and `toplevel` do not split a position. Several view rows with the same `queryid` become one position. A repeated `queryid` before aggregation is not an error. `qstop` throws on a duplicated `query_id` in the metrics set (`RequireMatchingTexts`), because its SQL already grouped the plans. Here the view grain is finer than the position, so that throw does not apply. After aggregation there is one position per known `queryid`. A position does not compute its own hash. It takes `queryid` from the view.

The formulas are the aggregation contract after the runtime probe. Required columns must be valid; optional ones yield available sums or null/unavailable per step 6. `total_exec_time` is in milliseconds. There is no division by 1000 as in the `qstop` SQL.

- `executionCount` = `SUM(calls)`
- `totalDurationMs` = `SUM(total_exec_time)`
- `maximumDurationMs` = `MAX(max_exec_time)`
- `averageDurationMs` = `totalDurationMs / SUM(calls)`, only when `SUM(calls)` is not 0
- `totalPlanMs` = `SUM(total_plan_time)`
- `maximumPlanMs` = `MAX(max_plan_time)`
- `averagePlanMs` = `totalPlanMs / SUM(calls)`, only when `SUM(calls)` is not 0
- `rowsReturned` = `SUM(rows)`
- `sharedBlocksHit` = `SUM(shared_blks_hit)`
- `sharedBlocksRead` = `SUM(shared_blks_read)`
- `localBlocksRead` = `SUM(local_blks_read)`
- `localBlocksWritten` = `SUM(local_blks_written)`
- `tempBlocksRead` = `SUM(temp_blks_read)`
- `tempBlocksWritten` = `SUM(temp_blks_written)`
- `walBytes` = `SUM(wal_bytes)`
- `blockReadMs` = `SUM(blk_read_time)`
- `blockWriteMs` = `SUM(blk_write_time)`
- `toplevelOnly` is true if and only if every row of this position has `toplevel` true

`tempBlocksRead` and `tempBlocksWritten` are the counters `temp_blks_read` and `temp_blks_written`. They are not the times `temp_blk_read_time` and `temp_blk_write_time`.

`mean_exec_time`, `mean_plan_time`, `min_*` and `stddev_*` are not position fields. A row mean is not a position mean. Specification §4 also talks in general terms about the sum of read, write, buffer and WAL counters. The field list is narrower and is the list from specification §5. Columns outside that list get no fields. In particular they do not get them for `shared_blks_dirtied`, `shared_blks_written`, `local_blks_hit`, `local_blks_dirtied`, `wal_records` and `wal_fpi`.

The specification divides by `SUM(calls)` and gives no result for zero. The page says that `plans` and `calls` do not always agree, because plan and execution statistics update separately. At `SUM(calls) = 0` the contract substitutes neither 0, nor null, nor `mean_*`. There are no `averageDurationMs` and `averagePlanMs` fields in that position. When calls are non-zero but the optional total_plan_time is unavailable, averagePlanMs also remains unavailable per step 6. That is not exit `5`. The ranking key is still the sum of `total_exec_time`, not the mean.

The line text is the `query` column, verbatim, without truncation. A null, when the server returned null, stays null. Matrix: after texts are discarded `query` is null and the statistics at `queryid` remain. That is not an error. A null from `pg_stat_statements(showtext := false)` is a separate matrix fact and is not the line text. A read with `showtext := false` is not a source of `queries.jsonl`.

When every row of a position has `query` null, the line has null. When every row has the same non-null text, the line has that text. The text comparison is an equality of that content, not an order of rows. When the values differ, including when a null sits next to a text, choosing the „first” row would depend on the scan. That choice does not exist. The page binds the rest of the text to the first query that had the given `queryid` at that entry, not to a position composed of several `userid` or of both `toplevel` values. Which text belongs to such a position is `UNPROVEN`. Condition: read `query` for one known `queryid` with two `userid` or with both `toplevel` values, on an explicit target. That read was not performed. Until then the line keeps `queryId` equal to that known `queryid`, with sign (`bigint`), and sets only `query` to null. It does not contain any of those texts. That null is not a claim that the server returned null.

### Ranking

The ranking follows a successful runtime probe from step 6. The exact pair of server and extension numbers is not a gate. A missing compatible shape is exit `5`.

The ranking keys use only the required minimum columns confirmed at runtime. Extra I/O and jit columns do not become new ranking keys nor stdout fields.

The order is the order of positions after aggregation, not the order of view rows. The keys, a hypothesis from specification §4, give a total order:

1. `SUM(total_exec_time)` descending
2. `SUM(calls)` descending
3. `SUM(shared_blks_hit) + SUM(shared_blks_read)` descending
4. `queryid` ascending

Each position has one known `queryid`, so the fourth key settles a tie among the earlier ones. Two positions do not have four equal keys. Row order, `userid` and the text are not keys. A tie does not depend on row order.

At most the first `--top` positions of that order remain. The scope is 1..500, the same as `OperationLimits.IsTop` (`TopMin` 1, `TopMax` 500) and the check in `ExecuteQueryStoreTopAsync`. This section does not carry over the `20` default from `QueryStoreTopCommand.Settings`. It does not put `--window` next to `--top`. `--top` in this section is a list cut, not a new capability and not a new command.

Position counters are view values after aggregation. The contract subtracts nothing from them. It does not turn them into durations. The effect of a selective reset on `stats_reset` stays `UNPROVEN` from the matrix. This section does not design the delta.

### Stdout and `queries.jsonl`

Position stdout has metrics and `queryId`. There is no `query` field, no SQL text, no `queryHash`, no CPU, no recency and no `windowMinutes`. The names are the spelling of specification §5: `queryId`, `toplevelOnly`, `executionCount`, `totalDurationMs`, `averageDurationMs`, `maximumDurationMs`, `totalPlanMs`, `averagePlanMs`, `maximumPlanMs`, `rowsReturned`, `sharedBlocksHit`, `sharedBlocksRead`, `localBlocksRead`, `localBlocksWritten`, `tempBlocksRead`, `tempBlocksWritten`, `walBytes`, `blockReadMs`, `blockWriteMs`. The specification abbreviates some of them with a slash (`sharedBlocksHit/Read`, `localBlocksRead/Written`, `tempBlocksRead/Written`, `blockReadMs/blockWriteMs`, `totalPlanMs / averagePlanMs / maximumPlanMs`). The expansion is that list, not a new field. Execution-time mean disappears at `SUM(calls) = 0`; the planning mean also at an unavailable optional sum. The remaining optional metrics are null/unavailable with a reason per step 6.

Report context, not a position:

- `server_version_num` already read in the sequence. That is not the `server_version` string and not `installed_version`. Specification §5 says „server version string”. This section narrows that to the number the sequence already has. `installed_version` is separate context, not a gate of allowed version pairs.
- `pg_stat_statements.track`, `pg_stat_statements.track_utility`, `pg_stat_statements.track_planning`, in GUC spelling. The value, when available, is the one from the server. Missing context is unavailable per step 6; there is no abbreviated alias.
- `track_io_timing`, in that spelling, next to `blockReadMs` and `blockWriteMs`. The read is only `current_setting('track_io_timing')`. The value is the one from the server. That is not a GUC change, not a ranking key and not SQL text on stdout. Matrix: `blk_read_time` and `blk_write_time` are zero when that GUC is disabled. This value distinguishes „not tracked” from „fast”.
- `dealloc` and `stats_reset`, in the column spelling of the info view. `stats_reset` is not `lastExecutionAt` and not a „last 24h” label.

Read context after the runtime probe per the optionality rules of step 6. It is not a continuity condition nor a basis for delta computation. These reads do not call `pg_stat_statements_reset`.

`artifactDirectory` is the JSON name of the `ArtifactDirectory` field from the `qstop` report: the artifact directory, without SQL text in the file name. The directory layout and the type names belong to the file plan, not here. The report does not take a database name as a ranking argument.

SQL text is only in the local `queries.jsonl`. One line per position that entered the result. The line has only `queryId` and `query`. Stdout, stderr, the error body and text file names do not contain it. The line is locally sensitive just like `queries.jsonl` in `qstop`: no pasting and no publishing without review. It does not enter a gain. Specification §6 draws the same boundary.

Artifact write failure is exit `6` (`SqlHarnessExitCode.LocalStorage`). In `ExecuteQueryStoreTopAsync` the `Artifact` phase maps the exception to that number, and the result carries no report. This contract leaves that as is: exit `6` without a report.

### References of this step

- `src/SqlHarness.Core/QueryStoreTop.cs`
- `src/SqlHarness.Core/QueryStoreArtifacts.cs`
- `src/SqlHarness.Core/Contracts.cs` (`QueryStoreTopItemReport`, `SqlHarnessQueryStoreTopReport`, `SqlHarnessExitCode`)
- `src/SqlHarness.Core/SqlHarnessModule.cs` (`ExecuteQueryStoreTopAsync`)
- `src/SqlHarness.Core/OperationLimits.cs`
- `src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `tests/SqlHarness.Tests/QueryStoreArtifactWriterTests.cs` (`qstop` line field names)
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, sections 4, 5, 6 and 8, as a hypothesis about the order and the stdout fields where this section does not narrow them
- module pages from the matrix, reopened for the `dbid` filter, representative text and `queryid` boundaries: https://www.postgresql.org/docs/14/pgstatstatements.html , https://www.postgresql.org/docs/15/pgstatstatements.html , https://www.postgresql.org/docs/16/pgstatstatements.html

## Delta — deferred

V1 returns the cumulative ranking of a single read. It does not contain `Compare`, `unknown`/`incomparable` statuses, a snapshot store or in-memory fields existing only for the delta. It does not read `pg_stat_statements.save` nor the server start time for future comparison. An earlier design, which never subtracted counters, was removed from the implementation scope.

A possible later plan may design the observed difference of counters on the same target/database and compatible entry identity. It must define the observation period, reset/eviction/restart, missing entries, counter drops and the possibility of an undetected selective reset. The difference will not be evidence of full continuity. Detected loss of continuity voids the comparison; equality of global markers alone does not prove entry continuity. This is not a v1 task nor a promise of a new feature.

The source matrix keeps historical UNPROVEN questions about resets; they are not a gate for the cumulative ranking and do not require performing a reset on a user database.
