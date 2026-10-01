# 011-safe-sql-extensions-proof — closure proof

## Status

**Code and documentation complete. NOT a full PASS.**

The plan's scoped filter, the `-warnaserror` build and the Core/CLI test
project (`SqlHarness.Tests`) are green. The third closure gate (full
non-Integration suite, exit 0) is **not** claimed as passed: the MCP test
project (`SqlHarness.Mcp.Tests`) is intermittently flaky, on this branch's head
**and** on the base commit `0fad2a8`. No commit on this branch changes a file
under the MCP project (`src/SqlHarness.Mcp`, `tests/SqlHarness.Mcp.Tests`), so
the gate failure is recorded as an independent failure, owned by plan 005
(which diagnoses the MCP timeouts). That the failures at head and at base
share one cause is an inference from "same project, no file changed", not
something this branch proved.

Branch `feat/plan-011-safe-sql-extensions`, worktree
`.worktrees/plan-011-safe-sql-extensions`, base / merge-base with `main`:
`0fad2a8`. No push, no merge (the plan forbids both without a separate
instruction).

Commit range: `0fad2a8..` up to and including the commit that carries this
version of the proof. A commit cannot name its own hash, so the range is
stated through its predecessor: `0fad2a8..7ceed54` is 64 commits (47 up to
`8abaa15`, then the 17 commits of the final fix wave); the proof commit is the
65th and touches only `plans/011-safe-sql-extensions-proof.md`,
`plans/011-safe-sql-extensions.md` and `plans/README.md`. The last commit that
touches code, tests or a test fixture is `9f07deb`; `950c162`, `9870899` and
`7ceed54` touch only `*.md` files. `AGENTS.md` and `README.md` are read by
`ContractsTests`, so the gates of the final fix wave were run on tree `7ceed54`,
after every one of those documents was final.

## Goal

Make the classifier accept four narrow, previously-denied T-SQL/PostgreSQL
constructs (T-SQL `SET` to a same-batch scalar local; T-SQL table variables;
PostgreSQL `TRUNCATE` of proven session temps; a PostgreSQL ANALYZE
design-only spike) without widening any existing denial (dynamic SQL,
persistent DDL, cross-database access, function side effects, transaction
control), and make `Capabilities.cs`/`AGENTS.md`/`README.md`/the historical
docs describe exactly what the code now does.

## Commits

| Task | Commits | What was delivered |
|---|---|---|
| T1 | `053d8c7`, `cd618b7` | `plans/011-syntax-contract.md`: AST matrix for T2–T5 plus standing negatives, probe-verified against the ScriptDom and SqlParserCS packages of the worktree; review round-1 fixes (explicit T5-R1 tests, T4-D4 TRUNCATE-only scope). Docs only, no classifier change. |
| T2 | `ec8ebb5` (red), `8437414` (green), `90be14c` (compound/cursor regression) | T-SQL `SET @x = <expr>` allowed only when `@x` is a same-batch scalar local (`DECLARE @x <scalar-or-UDT>` earlier in the batch); RHS walked by the existing external/stateful/cross-db inspection. Session/transaction-option `SET` families, compound (`+=`), cursor `SET`, and use-before-`DECLARE` stay denied. Allowed in query SQL and measured SQL; denied in `--setup`. |
| T3 | `4b4aeff` (red), `cbd4fbc` (green) | T-SQL table variables: `DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT` against `@t` allowed as session-local once proven by a same-batch `DeclareTableVariableStatement`. `OUTPUT INTO @t` is session-local **only when the statement's primary target is also proven local** (`#temp` or a table variable); with a persistent primary target the statement is a persistent mutation. Undeclared, scalar-as-table, table-as-scalar and cross-batch names stay denied, as does a variable declared with a user-defined table type (it parses as a scalar `DECLARE` and never proves a table position). |
| T3b (fix wave) | `bbbf7cf`, `489370c` (red), `a49e062`, `acec836`, `5b67218`, `abf2ff9`, `c70b9e4` | Fixed use-before-`DECLARE` for both `SET` and table-variable targets (declaration-offset proof in `BatchVariableScope`), fixed per-`GO`-batch locality in the parameter-reference collector, then three read-only refactors (one scalar-DECLARE predicate, one scope per batch, one walk per setup statement; dead resolution-kind check removed). |
| T4 | `c811df7` (red), `adf064e` (green), `fe70482`/`bc00a67` (ASCII-only fold), `a44e0e9`/`54fb02a` (`ON COMMIT DROP`), `e4dbb3b` (`EXPLAIN`/`TRUNCATE` interaction pins), `bd5607a` (`TRUNCATE ONLY` decision recorded) | PostgreSQL `TRUNCATE [ONLY] <names>` allowed only when every target is a proven current-session temp (not `ON COMMIT DROP`; quoted or all-ASCII-unquoted; recorded from this flow's `CREATE TEMP`/`SELECT INTO TEMP`). Persistent, mixed, `CASCADE`, and unproven `pg_temp_*`-prefixed targets deny `NonTemporaryWrite`/`UnsupportedStatement`. `RESTART IDENTITY` stays denied even over proven temps (sequence provenance unproven); `CONTINUE IDENTITY` / omitted is the allowed form. As shipped by T4 every schema-qualified target was denied; the final fix wave allows the one spelling `pg_temp.<proven name>` (below). |
| T5 | `808f276` (tests), `e17d55a` (doc) | `plans/011-analyze-parser-spike.md`: probe-verified finding that the offline parser has no grammar for canonical PostgreSQL `ANALYZE` (only the unrelated Hive `ANALYZE TABLE` form parses, to `Statement.Analyze`, already denied). Status **DESIGN COMPLETE, not IMPLEMENTED** — no classifier, parser, or dependency change. |
| T4b (fix wave) | `52cbab9`/`7af94de` (ASCII fold for every statement), `46feb92` (`ON COMMIT DROP` for every statement), `9d8a4c6` (contract update), `2b31057`/`1d1f09e` (`pg_temp_` prefix no longer proof), `8bc5a57` (`DROP TABLE` revokes unknown-name proofs), `c6b7d06` (63-byte UTF-8 identifier limit), `43142a2` (carried-record pins), `c9ec92e` (contract rows) | Tightened the shared PostgreSQL session-temp proof (`IsSessionLocal`) used by DML/`DROP TABLE`/`CREATE INDEX` in addition to `TRUNCATE`: ASCII-only identifier fold, `ON COMMIT DROP` never proves, a `pg_temp_*` prefix is never proof for any statement (only the exact two-part `pg_temp.<relation>` alias remains), identifiers over 63 UTF-8 bytes never prove, and `DROP TABLE` of an offline-unknown name revokes every tracked proof in the flow. |
| T6 | `12a701e`, `64b7232`, `b62dec1`, `02fbc97`, `6319a0c`, `9212c73`, `f5af9ec` | `Capabilities.cs` + verdict-backed tests, `AGENTS.md`, the ANALYZE spike's "no live server" sentence, a byte-budget fixture repin, four pre-011 docs' superseded `pg_temp_` claims, the first version of this proof, and the `plans/README.md` row. |
| T6 fix round 1 | `8d38b3d`, `e234f4c`, `3c77953`, `1037e4c`, `c300cf6`, `8abaa15` | The `search_path`, 63-byte, quoting and `DROP TABLE` revocation rules stated for every PostgreSQL session-temp statement; the `OUTPUT INTO` primary-target qualifier; exact capability-text pins with a negative sample per named denial. `CapabilitiesCommandTests` grew from 7 tests (5 pre-existing and 2 from the first T6 version) to 64 cases: the 2 loose T6 tests were replaced by an exact-text pin and by `_allows_what_it_claims` / `_denies_what_it_names` theories. The new tests also showed that `SET` is denied in compare setup; the documents now say so. No classifier or validator change. |
| Final fix wave | `b399a63`/`7a0bdc7` (M5), `d1a255c`/`cdd8c87` (I2), `920fc19`/`212b6cd`/`1dfcb87` (M2, M4), `db0fa21` (M11 pins), `ff54b16` (M12), `3e134d6` (M10 comment), `6eee42a`/`1ac57eb` (M9), `a521294` (M1), `9f07deb` (M7, M8), `950c162`, `9870899`, `7ceed54` (docs), then the commit carrying this proof (I1) | See "Final fix wave" below. One widening (`TRUNCATE pg_temp.<proven name>`), four tightenings, a denial hint, end-to-end tests, and corrected documents. |

Regression-test rule observed from the history itself (not re-executed per
commit): every behaviour change has a `test(...)` red commit immediately before
the corresponding `fix(...)`/`feat(...)` commit (T2: `ec8ebb5`→`8437414`; T3:
`4b4aeff`→`cbd4fbc`; T3b: `489370c`→`a49e062`/`acec836`; T4:
`c811df7`→`adf064e`, `fe70482`→`bc00a67`, `a44e0e9`→`54fb02a`; T4b:
`52cbab9`→`7af94de`/`46feb92`, `2b31057`→`1d1f09e`/`8bc5a57`/`c6b7d06`; final
wave: `b399a63`→`7a0bdc7`, `d1a255c`→`cdd8c87`, `920fc19`→`212b6cd`/`1dfcb87`,
`6eee42a`→`1ac57eb`). The red commits of the final wave record the failing
output in their commit messages. Intermediate commits of T1–T6 were not
re-checked-out to re-run their red phase.

## Test evidence

Three sources, kept apart. Nothing below is merged into one number.

### A. Final fix wave implementer, tree `7ceed54`, 2026-10-01

Run once each, in this order, in this worktree, on one Windows/x64 machine.

```
dotnet build D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --no-restore -warnaserror
```
→ `Build succeeded. 0 Warning(s) 0 Error(s)`, exit code 0.

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal
```
→ `Passed!  - Failed:     0, Passed:   797, Skipped:     0, Total:   797` (SqlHarness.Tests.dll), exit code 0.
At `8abaa15` this filter gave 661; the final fix wave added 136 cases to
`SqlSafetyTests` and `PostgresSafetyTests`.

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal
```
→ SqlHarness.Tests.dll: `Passed!  - Failed:     0, Passed:  2712, Skipped:     0, Total:  2712`.
→ SqlHarness.Mcp.Tests.dll: `Passed!  - Failed:     0, Passed:   193, Skipped:     4, Total:   197`.
Exit code 0 for this one run. The 4 skips are the 2 live opt-in tests and the
2 foreign-RID publish-smoke tests. One clean run does not show that the MCP
project is stable; see B.

`CapabilitiesCommandTests` alone: 72 passed, 0 failed, 0 skipped.

One further failure was seen by this implementer during the wave, in the
Core/CLI project, and is recorded here because it is a failure:
a run of `SqlHarness.Tests` (non-Integration) on the working tree that became
commit `ff54b16` gave 2652 passed / 1 failed / 0 skipped; the failing test was
`SqlHarness.Tests.Auth.ProcessRunnerTests.RunAsync_CancellationTerminatesEntireProcessTree`
(a process-tree termination test that starts `powershell.exe`). Three reruns of
`ProcessRunnerTests` (5/0/0 each) and an immediate rerun of the whole project
(2653/0/0) passed, and so did every later run of that project in the wave,
including the gate run above. The failure message was not captured. No commit
on this branch changes `ProcessRunner` or its tests. It is recorded as a
second intermittent test, not explained.

### B. Controller-observed runs (reported to the final fix wave; not re-derived here)

- At `8abaa15`, one full non-Integration run gave SqlHarness.Tests 2531 passed
  / 0 failed / 0 skipped and SqlHarness.Mcp.Tests 192 passed / 1 failed / 4
  skipped. The failing test was
  `McpStdioProcessTests.Inprocess_host_returns_zero_on_immediate_eof_without_stdout_bytes`.
- Five further runs of the SqlHarness.Mcp.Tests project at `8abaa15` each gave
  193 / 0 / 4.
- Five runs of the same project at base `0fad2a8` gave four times 193 / 0 / 4
  and once 192 / 1 / 4, failing
  `McpSecretRedactionTests.Transport_failure_logs_generic_text_without_exception_content`.
- MCP stdio tests also failed intermittently in at least three implementer
  full-solution runs earlier on this branch and passed on rerun.
- No commit on this branch changes a file under the MCP project.

So at `8abaa15` the MCP project failed in 1 of 6 controller runs, and at the
base in 1 of 5, in two different tests of that project.

### C. Earlier implementer runs (historical, superseded by A for counts)

- T6, first version, tree `6319a0c`/`f5af9ec`: scoped filter 654/0/0; full
  suite SqlHarness.Tests 2467/0/0 and SqlHarness.Mcp.Tests 193/0/4;
  `McpStdioProcessTests` alone 4 passed / 2 skipped. The first full-suite run of
  that task failed one test,
  `AgentWorkflowTests.Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access`
  (`Expected: 5710, Actual: 6219`), because the capabilities text had grown;
  the exact byte pin was moved in `02fbc97`.
- T6 fix round 1, tree `c300cf6`: scoped filter 661/0/0; full suite
  SqlHarness.Tests 2531/0/0 and SqlHarness.Mcp.Tests 193/0/4;
  `CapabilitiesCommandTests` 64/0/0. The byte pin moved again
  (`Expected: 6219, Actual: 6887`, repinned in `e234f4c`).

The earlier versions of this proof said the MCP flake "was not reproduced".
That was true of the runs those sessions made and is not a statement about
the project: B shows it fails intermittently at the same commits.

### Capabilities byte pin

`tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json`,
`discoveryThenSchema` (the `capabilities --json` + `schema --json` pair),
exact UTF-8 byte pin: 5710 → 6219 (`02fbc97`) → 6887 (`e234f4c`) → **7100**
(`9f07deb`; first run after the rewording: `Expected: 6887, Actual: 7100`).
The ceiling is 8192 bytes, unchanged by this branch; 1092 bytes remain.

`git diff --check` → exit 0 after every commit of the final fix wave.
`git status --short` → clean at the end of the wave.

## Final fix wave (review of `0fad2a8..8abaa15`)

The whole-branch review found 2 Important and 12 Minor items. Two Minor items
were left unchanged on the controller's decision and go to the user: M3 (proof
state carried in the runtime subtype of the temp set) and M6 (`SET` denied in
`--setup` while `DECLARE @v int = <expr>` is allowed there).

- **I1** — this proof, the plan status line and the index row say "not a full
  PASS" and carry the MCP record above; closure-checklist line 3 is unticked.
- **I2** — the one widening. `TRUNCATE [ONLY] pg_temp.<name>` is allowed when
  `<name>` is a proven session temp under exactly the proof of the unqualified
  form. Every other schema-qualified target (`public.x`, `pg_temp_<N>.x`,
  quoted `"PG_TEMP".x`, three-part names, `pg_temp.<unproven>`), `CASCADE`,
  `RESTART IDENTITY`, `ON CLUSTER` and mixed lists stay denied
  (`Final_Truncate_schema_qualified_denials_are_unchanged`, 35 cases × both
  usages × with and without approval, all of which already passed at the red
  commit `d1a255c`).
- **M2** — after an unquoted non-ASCII `ON COMMIT DROP` declaration,
  `CREATE TEMP TABLE IF NOT EXISTS` proves no name at all (tighten).
- **M4** — a `CREATE TEMP TABLE` qualified with anything other than the
  `pg_temp` alias records no proof (tighten). The two-part `pg_temp.x`
  declaration keeps recording proof.
- **M5** — `SET @v.Member = x`, `SET @v::Member = x` denied (tighten).
- **M9** — a fixed hint in `SqlSafetyDecision.Detail` on a
  `MutationNotAllowed` / `NonTemporaryWrite` denial when the flow holds a TEMP
  declaration that recorded no proof. Reason and exit code unchanged.
- **M1** — end-to-end tests through `SqlHarnessModule.ExecuteAsync` for
  `query`, `measure` and `compare` on both engines.
- **M7 / M8** — capabilities and documents agree on where `SET` is allowed and
  say that a setup table variable is not visible to measured SQL; the four
  plan 011 capability strings hold no character that JSON-escapes.
- **M10 / M11 / M12** — stale text corrected, unpinned edges pinned, one
  unreachable fallback removed.

Existing tests whose expectation changed in the final fix wave:

1. `PostgresSafetyTests.T4_Truncate_schema_qualified_name_is_denied_even_when_relation_name_is_a_tracked_temp`
   — the case `CREATE TEMP TABLE t (id int); TRUNCATE pg_temp.t` was removed
   (I2 allows it).
2. `CapabilitiesCommandTests.SessionTempStatements_postgres_TRUNCATE_entry_denies_what_it_names`
   — the same case was replaced by the unproven `TRUNCATE pg_temp.t` and by
   `pg_temp_3.t` (I2).
3. `PostgresSafetyTests.T4b_Ascii_name_is_still_proven_after_a_non_ascii_on_commit_drop_declaration`
   — asserted the allow that M2 closes; replaced by
   `Final_If_not_exists_after_non_ascii_on_commit_drop_proves_no_name_at_all`
   and `Final_Plain_create_is_still_proven_after_a_non_ascii_on_commit_drop_declaration`.
4. `CapabilitiesCommandTests.SessionTempStatements_lists_are_pinned_exactly`
   and the byte pin — the four reworded capability strings (M7, M8).

## T5 status

**DESIGN COMPLETE, not IMPLEMENTED.** `plans/011-analyze-parser-spike.md`
documents, from a throwaway offline-parser probe (written, run, and deleted
before any commit — never checked in), that the offline parser has no grammar
rule for canonical PostgreSQL `ANALYZE [VERBOSE] table`: every canonical
spelling throws `ParserException` and is caught by `PostgresDocument.TryParse`,
surfacing as `SqlSafetyReason.ParseError`. The only `ANALYZE ...` spelling this
parser version accepts is the unrelated Hive `ANALYZE TABLE ...` form
(`Statement.Analyze`), already denied `UnsupportedStatement` through its own
switch arm. No classifier, parser, or dependency change was made for T5. The
spike doc states that no live PostgreSQL server was used for the probe (commit
`b62dec1`, review finding T5-I1).

## No live-database / platform evidence

No task on this branch ran a live SQL Server or PostgreSQL connection or
touched a `~/.sqlharness` profile or secret. Every number above comes from the
fake reader/session/module test doubles in the suite, run on one Windows/x64
worktree. No evidence exists for any other OS/architecture. Server-side
behaviour the classifier relies on (PostgreSQL rejecting
`CREATE TEMP TABLE public.x`, a failed statement ending a batch, name
resolution under a non-default `search_path`, the fold of `İ` under a
single-byte Turkish locale, SQL Server rejecting a duplicate `DECLARE`) is
taken from the engines' documentation, not observed.

## Decisions / known limitations (plain language)

- The parameter-reference validator follows the syntax contract exactly: an
  undeclared table-position `@name` (for example `INSERT @x (Id) VALUES (1)`
  with no `DECLARE @x TABLE`) is never listed as a required parameter — the
  classifier denies the whole batch as `UnsupportedStatement` regardless of
  whether a caller supplies a `--param` of that name. "Not required" never
  means "allowed". Backed by
  `SqlParameterReferenceValidatorTests.T3_Validate_undeclared_table_target_is_rejected`
  and `T3b_Validation_report_denies_undeclared_table_position_name_end_to_end`.
- T-SQL `SET @v = <expr>` is allowed in query SQL (`SqlUsage.Query`: `query`,
  `watch`, `snapshot`, the measured `measure` / `compare` SQL) and **denied in
  setup** (`SqlUsage.CompareSetup`, `UnsupportedStatement`): setup accepts
  `SELECT` and session-local work only, and a `SET` is neither. Backed by
  `SqlSafetyTests.T6_Compare_setup_denies_SET_local_scalar_assignment`,
  `SqlHarnessMeasureTests.Measure_runs_table_variable_setup_and_SET_in_measured_SQL_unchanged`
  and `SqlHarnessMeasureTests.Measure_rejects_unproven_SET_and_table_variable_SQL_before_authentication`.
  Whether setup should accept it (it accepts `DECLARE @v int = <expr>`) is open
  and goes to the user (review item M6).
- Only the plain form `SET @v = <expr>` is allowed. A member, static-member or
  method target (`SET @v.Member = x`, `SET @v::Member = x`, `SET @x.modify(...)`)
  is denied (`SqlSafetyTests.Final_Query_denies_SET_with_a_member_or_method_target`).
- `DECLARE @v dbo.SomeType` followed by `SET @v = ...` stays **allowed** in
  query SQL (`SqlSafetyTests.T6_Query_allows_SET_to_local_declared_with_user_defined_type`):
  such a name may be a scalar alias type that the offline parser cannot
  distinguish from a user-defined table type. The same `@v` is still denied as
  a DML target or as a `SELECT ... FROM` source
  (`SqlSafetyTests.T3_Query_denies_scalar_variable_as_DML_target`).
- A table variable lives in its declaring batch. One declared in `--setup` is
  not visible to the measured SQL, which is a separate batch; `#temp` carries
  data out of setup
  (`CapabilitiesCommandTests.SessionTempStatements_sqlserver_table_variable_from_setup_is_not_visible_to_measured_SQL`).
- A table variable declared twice in one batch is not denied by the
  classifier: the verdict stays session-local and SQL Server rejects the
  duplicate declaration at compile time
  (`SqlSafetyTests.Final_Twice_declared_table_variable_stays_session_local`).
  The whole-branch review expected a denial here; the shipped behaviour is
  pinned as it is.
- T-SQL `OUTPUT INTO @t` is session-local only when the statement's primary
  target is also proven local. `UPDATE dbo.Clients ... OUTPUT inserted.Id INTO @t`
  is a persistent mutation: `MutationNotAllowed` without approval,
  `NonTemporaryWrite` in setup
  (`SqlSafetyTests.T3_Persistent_write_stays_a_mutation_when_a_table_variable_is_involved`).
- **`search_path` (residual false-allow, unqualified names only).** The
  PostgreSQL session-temp proof is name-based, not connection-based: the
  classifier never opens a connection and never reads the server's
  `search_path`. For an **unqualified** name the proof assumes the default
  `search_path`, where `pg_temp` is resolved first. Under a role-level,
  database-level or session `search_path` that puts a persistent schema ahead
  of `pg_temp`, a proven unqualified `t` can resolve to a persistent `t`, and
  the classifier does not detect that; the statement runs without mutation
  approval. This predates plan 011 for temp DML; plan 011 adds `TRUNCATE` to
  the statements it applies to. An explicit `pg_temp.<relation>` always
  addresses the session temp schema and does not depend on `search_path`, and
  since the final fix wave `TRUNCATE` accepts that spelling for a proven temp,
  so every session-temp write has a spelling that is immune. Contract rows
  011-T4-A2 and 011-T4-L1. No test can show the server side of this;
  `CapabilitiesCommandTests.SessionTempStatements_postgres_proof_is_name_based_and_reads_no_search_path`
  pins that a recorded name is the whole proof. Accepting the residual for
  unqualified names is the user's decision.
- An identifier longer than 63 bytes never proves a session temp and is never
  proven (`PostgresSafetyTests.T4b_Name_longer_than_63_bytes_never_proves_and_is_never_proven`,
  `T4b_Name_of_exactly_63_bytes_stays_session_local`). The 63-byte limit is
  counted in UTF-8; other server encodings and a non-default `NAMEDATALEN`
  are not modelled.
- Server case-folding under single-byte non-UTF-8 locales is not modelled.
  The classifier folds A-Z only and treats every unquoted non-ASCII
  identifier as unproven, which is stricter than a UTF-8 server needs. The one
  place that reasoned about such a name's stored form — "it is non-ASCII under
  every fold", used to let `IF NOT EXISTS` prove an ASCII name — was wrong for
  a Turkish locale and was removed (M2).
- A `CREATE TEMP TABLE` named with a qualifier other than `pg_temp` records no
  proof (`PostgresSafetyTests.Final_Qualified_create_temp_records_no_proof_under_its_relation_name`).
- `DROP TABLE` of a target whose stored name is unknown offline (unquoted
  non-ASCII or over 63 bytes; reachable only as `pg_temp.<relation>`) clears
  every proof in the flow, unrelated names and caller-supplied names included,
  until a new `CREATE TEMP TABLE` proves a name again
  (`PostgresSafetyTests.T4b_Drop_with_unknown_stored_name_revokes_every_tracked_temp`,
  `Final_Clear_all_revokes_a_name_from_a_caller_built_set`).
- PostgreSQL `TRUNCATE` denial for a persistent or mixed target list uses
  `SqlSafetyReason.NonTemporaryWrite` (not `MutationNotAllowed`) and mutation
  approval does not unlock it — backed by
  `PostgresSafetyTests.T4_Truncate_persistent_target_is_denied` and
  `T4_Truncate_mixed_targets_are_denied`. The CLI exit code is unchanged:
  every denied `SqlSafetyDecision` maps to exit `2`.
- A `pg_temp_`-prefixed name (as a bare relation name, or as a `pg_temp_<N>`
  schema qualifier) is not proof of session locality for any statement. Only
  the exact two-part `pg_temp.<relation>` alias counts, by itself for DML /
  `DROP TABLE` / `CREATE INDEX`, and together with a proven relation name for
  `TRUNCATE`. Backed by
  `PostgresSafetyTests.T4b_Pg_temp_prefix_is_not_proof_for_any_statement` and
  `T4_Truncate_pg_temp_prefix_without_provenance_is_denied`.
- Proof state (the `TRUNCATE`-proven subset and the `ON COMMIT DROP` record)
  travels in the runtime subtype of the set returned in
  `SqlSafetyDecision.SessionTempTables`. A caller that copied the set into a
  plain collection would lose the `ON COMMIT DROP` record, which fails open.
  No production caller copies it. Not changed on this branch (review item M3,
  for the user).
- `watch`, `snapshot` and the MCP tools have no end-to-end test of the new
  allows. They call the same Core classifier with `SqlUsage.Query`
  (`SqlHarnessModule`), and no MCP file is changed by this branch.

## Deliverables

1. `src/SqlHarness.Core/SqlSafety.cs`, `SqlValidation.cs`,
   `Postgres/PostgresSafetyClassifier.cs`: the classifier and validator
   changes of T2–T4b and the final fix wave.
2. `src/SqlHarness.Core/Capabilities.cs`: `sessionTempStatements` lists the
   T2/T3/T4 constructs actually allowed, each phrased with its real denial
   boundary. `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` pins the
   text exactly and runs the real classifier on a positive sample and on one
   negative sample per denial each entry names.
3. `AGENTS.md`, `README.md` (worktree copies): the PostgreSQL engine notes and
   the benchmark setup contract describe `SET`, table variables and PostgreSQL
   `TRUNCATE` with their allow/deny boundaries.
4. `plans/011-syntax-contract.md`, `plans/011-analyze-parser-spike.md`, this
   document, the `plans/README.md` row and the plan's status line, checklist
   and scope corrections.
5. Four pre-011 documents corrected where they still described a `pg_temp_`
   prefix as proof of a PostgreSQL session-local target (listed in the plan's
   scope corrections).
6. End-to-end tests in `QueryTests.cs`, `MeasureTests.cs`, `CompareTests.cs`,
   `Postgres/PostgresQueryTests.cs` and `Postgres/PostgresBenchmarkTests.cs`.

## Open items for the user

- Accept or reject the `search_path` residual for unqualified names (I2).
- M3: carry the proof state in a dedicated type instead of a runtime subtype.
- M6: allow `SET` in `--setup`, or keep the denial.
- The MCP test project's intermittent failures (plan 005) and the one
  `ProcessRunnerTests` failure recorded above.
