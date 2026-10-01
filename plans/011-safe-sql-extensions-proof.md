# 011-safe-sql-extensions-proof — closure proof

Code tip: `e234f4c` (`test(011/T6): pin capabilities wording exactly and sample
every named denial` — the last commit touching code, tests or test fixtures),
branch `feat/plan-011-safe-sql-extensions`, worktree
`.worktrees/plan-011-safe-sql-extensions` (worktree base / merge-base with `main`:
`0fad2a8`). All gates below (plan scoped filter, `-warnaserror` build, full
non-Integration suite, capabilities filter) were observed on tree `c300cf6`, in
this worktree, on 2026-10-01, during T6 fix round 1. `c300cf6` differs from the
code tip only in `*.md` files. The commit that carries this version of the
proof comes after `c300cf6` and touches only `plans/*.md`. No push, no merge
(the plan forbids both without a separate instruction). The header names the
last code-touching commit, not the moving HEAD, so it does not go stale as
further docs-only commits land.

## Goal

Make the classifier accept four narrow, previously-denied T-SQL/PostgreSQL
constructs (T-SQL `SET` to a same-batch scalar local; T-SQL table variables;
PostgreSQL `TRUNCATE` of proven session temps; a PostgreSQL ANALYZE
design-only spike) without widening any existing denial (dynamic SQL,
persistent DDL, cross-database access, function side effects, transaction
control), and make `Capabilities.cs`/`AGENTS.md`/the historical docs describe
exactly what the code now does — this task (011/T6).

## Commits (`0fad2a8..c300cf6`, 46 commits)

| Task | Commits | What was delivered |
|---|---|---|
| T1 | `053d8c7`, `cd618b7` | `plans/011-syntax-contract.md`: AST matrix for T2–T5 plus standing negatives, probe-verified against ScriptDom 180.37.3 and SqlParserCS 0.6.5; review round-1 fixes (explicit T5-R1 tests, T4-D4 TRUNCATE-only scope). Docs only, no classifier change. |
| T2 | `ec8ebb5` (red), `8437414` (green), `90be14c` (compound/cursor regression) | T-SQL `SET @x = <expr>` allowed only when `@x` is a same-batch scalar local (`DECLARE @x <scalar-or-UDT>` earlier in the batch); RHS walked by the existing external/stateful/cross-db inspection. Session/transaction-option `SET` families, compound (`+=`), cursor `SET`, and use-before-`DECLARE` stay denied. |
| T3 | `4b4aeff` (red), `cbd4fbc` (green) | T-SQL table variables: `DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT`/`OUTPUT INTO` against `@t` allowed as session-local once proven by a same-batch `DeclareTableVariableStatement`. Undeclared, scalar-as-table, table-as-scalar, cross-batch, and user-defined-table-type names stay denied. |
| T3b (fix wave) | `bbbf7cf`, `489370c` (red), `a49e062`, `acec836`, `5b67218`, `abf2ff9`, `c70b9e4` | Fixed use-before-`DECLARE` for both `SET` and table-variable targets (declaration-offset proof in `BatchVariableScope`), fixed per-`GO`-batch locality in the parameter-reference collector, then three read-only refactors (one scalar-DECLARE predicate, one scope per batch, one walk per setup statement; dead resolution-kind check removed). |
| T4 | `c811df7` (red), `adf064e` (green), `fe70482`/`bc00a67` (I1: ASCII-only fold), `a44e0e9`/`54fb02a` (M2: `ON COMMIT DROP`), `e4dbb3b` (M3: `EXPLAIN`/`TRUNCATE` interaction pins), `bd5607a` (M4: `TRUNCATE ONLY` decision recorded) | PostgreSQL `TRUNCATE [ONLY] <names>` allowed only when every target is a proven current-session temp (single-part name; not `ON COMMIT DROP`; quoted or all-ASCII-unquoted; recorded from this flow's `CREATE TEMP`/`SELECT INTO TEMP`). Persistent, mixed, `CASCADE`, schema-qualified, and unproven `pg_temp_*`-prefixed targets deny `NonTemporaryWrite`/`UnsupportedStatement`. Ruling R4: `RESTART IDENTITY` stays denied even over proven temps (sequence provenance unproven); `CONTINUE IDENTITY` / omitted is the allowed form. |
| T5 | `808f276` (tests), `e17d55a` (doc) | `plans/011-analyze-parser-spike.md`: probe-verified finding that SqlParserCS 0.6.5 has no grammar for canonical PostgreSQL `ANALYZE` (only the unrelated Hive `ANALYZE TABLE` form parses, to `Statement.Analyze`, already denied). Status **DESIGN COMPLETE, not IMPLEMENTED** — no classifier, parser, or dependency change. |
| T4b (fix wave) | `52cbab9`/`7af94de` (ASCII fold for every statement, not just TRUNCATE), `46feb92` (`ON COMMIT DROP` for every statement), `9d8a4c6` (contract update), `2b31057`/`1d1f09e` (`pg_temp_` prefix no longer proof), `8bc5a57` (`DROP TABLE` revokes unknown-name proofs), `c6b7d06` (63-byte UTF-8 identifier limit), `43142a2` (carried-record pins), `c9ec92e` (contract rows) | Tightened the shared PostgreSQL session-temp proof (`IsSessionLocal`) used by DML/`DROP TABLE`/`CREATE INDEX` in addition to `TRUNCATE`: ASCII-only identifier fold, `ON COMMIT DROP` never proves, a `pg_temp_*` prefix is never proof for any statement (only the exact two-part `pg_temp.<relation>` alias remains, and not for `TRUNCATE`), identifiers over 63 UTF-8 bytes never prove, and `DROP TABLE` of an offline-unknown name revokes every tracked proof in the flow. |
| T6 (this task) | `12a701e`, `64b7232`, `b62dec1`, `02fbc97`, `6319a0c`, `9212c73`, `f5af9ec` | This closure: `Capabilities.cs` + a verdict-backed test, `AGENTS.md`, the ANALYZE spike's "no live server" sentence, a byte-budget fixture repin forced by the capabilities growth, four pre-011 docs' superseded `pg_temp_` claims, the first version of this proof, and the `plans/README.md` row with the plan status. |
| T6 fix round 1 | `8d38b3d` (capabilities wording), `e234f4c` (tests + byte pin), `3c77953` (`AGENTS.md`), `1037e4c` (specs), `c300cf6` (`README.md`) | Review findings I1-I3 and M1-M8: the `search_path`, 63-byte, quoting and `DROP TABLE` revocation rules stated for every PostgreSQL session-temp statement; the `OUTPUT INTO` primary-target qualifier; aliased-target, user-defined-table-type, compound and cursor `SET` denials named; exact capability-text pins with a negative sample per named denial. The new tests also showed that `SET` is denied in compare setup; the documents now say so. No classifier or validator change. |

Regression-test rule observed from the history itself (not re-executed per
commit): every implementing task's commit list shows a `test(...)`/"red-phase"
commit immediately before the corresponding `fix(...)`/`feat(...)` commit
(T2: `ec8ebb5`→`8437414`; T3: `4b4aeff`→`cbd4fbc`; T3b:
`489370c`→`a49e062`/`acec836`; T4: `c811df7`→`adf064e`,
`fe70482`→`bc00a67`, `a44e0e9`→`54fb02a`; T4b:
`52cbab9`→`7af94de`/`46feb92`, `2b31057`→`1d1f09e`/`8bc5a57`/`c6b7d06`;
`43142a2` adds pins afterwards). This task did not re-checkout those
intermediate commits to re-run the red phase; the final-tree test run below is
the evidence this task itself observed.

## Test evidence (observed in T6 fix round 1, tree `c300cf6`)

Plan scoped filter:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests" --verbosity minimal
```
→ `Passed!  - Failed:     0, Passed:   661, Skipped:     0, Total:   661` (SqlHarness.Tests.dll).
The baseline at `c9ec92e` and at `f5af9ec` was 654/654; fix round 1 added 7
cases to `SqlSafetyTests` (`T6_Query_allows_SET_to_local_declared_with_user_defined_type`,
3 cases; `T6_Compare_setup_denies_SET_local_scalar_assignment`, 4 cases).

Build:

```
dotnet build D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --no-restore -warnaserror
```
→ `Build succeeded. 0 Warning(s) 0 Error(s)`, exit code 0.

Full non-Integration suite:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ SqlHarness.Tests.dll: `Passed!  - Failed:     0, Passed:  2531, Skipped:     0, Total:  2531`.
→ SqlHarness.Mcp.Tests.dll: `Passed!  - Failed:     0, Passed:   193, Skipped:     4, Total:   197`
(4 skips are the pre-existing 2 live opt-in + 2 foreign-RID publish-smoke
tests, unrelated to this task). At `f5af9ec` the first number was 2467; the
64 added cases are 57 in `CapabilitiesCommandTests` and 7 in `SqlSafetyTests`.

Capabilities-specific:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~CapabilitiesCommandTests" --verbosity minimal
```
→ `Passed!  - Failed:     0, Passed:    64, Skipped:     0, Total:    64` (5
pre-existing tests + 59 `SessionTempStatements_*` cases). Fix round 1 replaced
the two loose tests of the first T6 version: `SessionTempStatements_lists_are_pinned_exactly`
pins both lists verbatim, and each plan 011 entry has an `_allows_what_it_claims`
and a `_denies_what_it_names` theory that runs the real classifier on a
positive sample and on one negative sample per denial the entry names.

MCP publish-smoke test, run on its own per the brief's instruction to not
claim a full PASS if this test is flaky:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~McpStdioProcessTests" --verbosity normal
```
→ `Total tests: 6, Passed: 4, Skipped: 2` — `Published_single_file_server_passes_the_stdio_smoke_on_this_rid` **passed**
(45 s) alongside the three in-process tests; the two skips are the
foreign-RID (linux-x64/osx-arm64) variants, expected on this win-x64 host.
This standalone run was observed on tree `6319a0c` in the first T6 session
and was not repeated in fix round 1, where the MCP project passed inside the
full-suite run above.

Honest record of a red-phase incident during this task's own verification
(not an MCP flake — a byte-count regression this task's documentation change
caused): the first full-suite run in this session failed exactly one test,
`SqlHarness.Tests.AgentWorkflowTests.Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access`
(`Expected: 5710, Actual: 6219` UTF-8 bytes for the `capabilities --json` +
`schema --json` pair), because `sessionTempStatements` grew once this task
documented the T2/T3/T4 constructs. The byte count stayed well inside the
8192-byte budget (`InRange` still passed); only the exact-match regression
pin in `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json`
needed repinning (`5710` → `6219`, commit `02fbc97`). After that fix, the
full suite above (2467/0/0 + 193/0/4) is a clean rerun with no failures of
any kind, including the MCP publish-smoke test — the historically-reported
intermittent MCP stdio flake (per this task's briefing) was **not
reproduced** in any of the three test runs that session performed
(full suite twice, `McpStdioProcessTests` alone once), nor in the one
full-suite run of fix round 1.

Fix round 1 moved the same pin again, for the same reason: the reworded and
added `sessionTempStatements` entries grew the pair from 6219 to 6887 UTF-8
bytes (first run after the wording change: `Expected: 6219, Actual: 6887`;
repinned in `e234f4c`). The 8192-byte ceiling is unchanged and leaves 1305 B.

`git diff --check` → exit 0 (clean) after every commit in this task.
`git status --short` → clean at the end of this task.

## T5 status

**DESIGN COMPLETE, not IMPLEMENTED.** `plans/011-analyze-parser-spike.md`
documents, from a throwaway offline-parser probe (written, run, and deleted
before any commit — never checked in), that SqlParserCS 0.6.5 has no grammar
rule for canonical PostgreSQL `ANALYZE [VERBOSE] table`: every canonical
spelling throws `ParserException` and is caught by `PostgresDocument.TryParse`,
surfacing as `SqlSafetyReason.ParseError`. The only `ANALYZE ...` spelling this
parser version accepts is the unrelated Hive `ANALYZE TABLE ...` form
(`Statement.Analyze`), already denied `UnsupportedStatement` through its own
switch arm. No classifier, parser, or dependency change was made for T5, and
none was made in this closure task either. This task added one sentence to
the spike doc (commit `b62dec1`) making explicit that no live PostgreSQL
server was used for the probe — the finding rests solely on the offline
parser's observed behaviour (carried review finding T5-I1).

## No live-database / platform evidence

This task ran no live SQL Server or PostgreSQL connection and touched no
`~/.sqlharness` profile or secret. Every number above comes from the fake
reader/session/module test doubles already in the suite, run on this one
Windows/x64 worktree. No evidence exists for any other OS/architecture, and
none was sought (out of scope for a docs-only closure task).

## Decisions / known limitations (plain-language, not ruling numbers)

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
  `SELECT` and session-local work only, and a `SET` is neither. The first T6
  version of `AGENTS.md` listed `SET` among the setup allowances; fix round 1
  corrected the documents instead of the classifier. Backed by
  `SqlSafetyTests.T6_Compare_setup_denies_SET_local_scalar_assignment` and
  `CapabilitiesCommandTests.SessionTempStatements_sqlserver_SET_entry_is_denied_in_setup_as_it_says`.
  Whether setup should accept it is a question for a later plan.
- `DECLARE @v dbo.SomeType` followed by `SET @v = ...` stays **allowed** in
  query SQL (`SqlSafetyTests.T6_Query_allows_SET_to_local_declared_with_user_defined_type`): the
  classifier's one definition of "scalar DECLARE" (`SqlSafety.cs`,
  `IsScalarDeclaration`) treats both a built-in `SqlDataTypeReference` and a
  `UserDataTypeReference` (a schema-qualified type name such as `dbo.SomeType`)
  as scalar-looking, because such a name may be a scalar alias type
  (`CREATE TYPE dbo.SomeType FROM int`) that the offline parser cannot
  distinguish from a user-defined table type without a database connection.
  The same `@v` is still denied as a DML target or as a `SELECT ... FROM`
  source (`SqlSafetyTests.T3_Query_denies_scalar_variable_as_DML_target`,
  `T3b_Query_denies_less_common_unproven_table_variable_shapes`), because only
  a `DeclareTableVariableStatement` proves a table-position use.
- The PostgreSQL session-temp proof is name-based, not connection-based: the
  classifier never opens a connection and never reads the server's
  `search_path`. The limitation concerns **unqualified** names proven as
  session temps, the only form `TRUNCATE` accepts and the usual form for temp
  DML: the proof assumes the default `search_path`, where `pg_temp` is
  resolved first. Under a role-level, database-level or session `search_path`
  that puts a persistent schema ahead of `pg_temp`, a proven unqualified `t`
  can resolve to a persistent `t`, and the classifier does not detect that.
  An explicit `pg_temp.<relation>` always addresses the session temp schema
  and does not depend on `search_path`. No test can show the server side of
  this; `CapabilitiesCommandTests.SessionTempStatements_postgres_proof_is_name_based_and_reads_no_search_path`
  pins that a recorded name is the whole proof.
- An identifier longer than 63 bytes never proves a session temp and is never
  proven (`PostgresSafetyTests.T4b_Name_longer_than_63_bytes_never_proves_and_is_never_proven`,
  `T4b_Name_of_exactly_63_bytes_stays_session_local`). The 63-byte limit is
  counted in UTF-8; other server encodings and a non-default `NAMEDATALEN`
  are not modelled.
- Server case-folding under single-byte non-UTF-8 locales is not modelled.
  The classifier folds A-Z only and treats every unquoted non-ASCII
  identifier as unproven, which is stricter than a UTF-8 server needs.
- `DROP TABLE` of a target whose stored name is unknown offline (unquoted
  non-ASCII or over 63 bytes; reachable only as `pg_temp.<relation>`) clears
  every proof in the flow, unrelated names included, until a new
  `CREATE TEMP TABLE` proves a name again
  (`PostgresSafetyTests.T4b_Drop_with_unknown_stored_name_revokes_every_tracked_temp`,
  `T4b_Drop_with_unknown_stored_name_revokes_a_carried_temp`,
  `T4b_Drop_with_known_stored_name_keeps_the_other_temps`).
- T-SQL `OUTPUT INTO @t` is session-local only when the statement's primary
  target is also proven local. `UPDATE dbo.Clients ... OUTPUT inserted.Id INTO @t`
  is a persistent mutation: `MutationNotAllowed` without approval,
  `NonTemporaryWrite` in setup
  (`SqlSafetyTests.T3_Persistent_write_stays_a_mutation_when_a_table_variable_is_involved`).
- PostgreSQL `TRUNCATE` denial for a persistent or mixed target list uses
  `SqlSafetyReason.NonTemporaryWrite` specifically (not `MutationNotAllowed`,
  which is the T-SQL/PostgreSQL DML denial reason) — backed by
  `PostgresSafetyTests.T4_Truncate_persistent_target_is_denied` and
  `T4_Truncate_mixed_targets_are_denied`. The CLI exit code is unchanged:
  every denied `SqlSafetyDecision` still maps to exit `2` (validation/safety),
  the same as any other denial reason.
- An unquoted PostgreSQL temp-table name containing any non-ASCII character
  is never proven a session temp on either the declaring or the using side —
  the server's fold of such a name depends on server encoding, which the
  offline classifier cannot observe. Users must quote such names
  (`"é"`) to get proof. Separately, a `pg_temp_`-prefixed name (as a bare
  relation name, or as a `pg_temp_<N>` schema qualifier) is **no longer**
  proof of session locality for any statement, including `TRUNCATE` — only
  an exact two-part `pg_temp.<relation>` alias (not for `TRUNCATE`, which
  accepts no schema qualifier at all) or a name this flow actually recorded
  from `CREATE TEMP`/`SELECT INTO TEMP` counts. Backed by
  `PostgresSafetyTests.T4b_Pg_temp_prefix_is_not_proof_for_any_statement` and
  `T4_Truncate_pg_temp_prefix_without_provenance_is_denied`.

## Deliverables (this task, 011/T6)

1. `src/SqlHarness.Core/Capabilities.cs`: `sessionTempStatements` now lists
   the T2/T3/T4 constructs actually allowed (SET to a same-batch scalar
   local; `DECLARE @t TABLE` + proven DML; PostgreSQL `TRUNCATE [ONLY]` of
   proven session temps), each phrased with its real denial boundary.
   `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` gained two tests
   that classify a real positive and a real negative sample SQL statement
   through `SqlSafetyClassifier`/`PostgresSafetyClassifier` and assert the
   verdict matches the capability text, not just a list-of-names check.
2. `AGENTS.md` (worktree copy): the PostgreSQL engine notes and the
   benchmark setup contract now describe `SET`, table variables, and PG
   `TRUNCATE` with their real allow/deny boundaries; the canonical `ANALYZE`
   denial sentence is unchanged (still true).
3. `plans/011-analyze-parser-spike.md`: one sentence added stating no live
   PostgreSQL server was used, carrying forward review finding T5-I1.
4. This document.
5. `plans/README.md` row 011 and `plans/011-safe-sql-extensions.md` status
   line / checkboxes updated to match what this task actually observed.
6. (Scope extension approved by the controller) Four pre-011 documents
   corrected where they still described a `pg_temp_` prefix as proof of a
   PostgreSQL session-local target: `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`,
   `docs/superpowers/specs/2026-09-10-postgres-engine-design.md` (both
   corrected in place), and `docs/superpowers/plans/2026-09-10-postgres-engine.md`
   plus `plans/009-strict-profile-assessment.md` (both historical plan
   documents, given a dated "superseded by plan 011" note instead of a
   rewrite).

## Fix round 1 (review of `c9ec92e..f5af9ec`)

Commits `8d38b3d`, `e234f4c`, `3c77953`, `1037e4c`, `c300cf6`, then the commit
carrying this version of the proof.

- `Capabilities.cs`: the `SET` entry says plain `=`, query SQL only, and names
  the compound and cursor denials; the table-variable entry carries the
  `OUTPUT INTO` primary-target qualifier and names aliased DML targets and
  user-defined table types as denied; a new PostgreSQL entry names the
  session-temp proof rules for every statement (`search_path` assumption,
  quoted or all-ASCII name of at most 63 UTF-8 bytes, `DROP TABLE`
  revocation). Additive: one new array element, two reworded ones; no field
  renamed or removed, `ContractVersion` unchanged.
- `AGENTS.md`: the same rules in full, in the PostgreSQL engine notes and the
  benchmark setup contract.
- `README.md` (scope extension approved by the controller): the two passages
  that list the session-only statements now include the plan 011 constructs.
- The two in-place spec corrections now read "Changed by plan 011" and say
  which sentence is the current rule; the engine-design spec has dated notes
  on the ASCII-only fold and on the `TRUNCATE` verdict.
- This proof: commit count, the T4b red→green pair, the two wording slips,
  the limitations above.

## Concerns / disagreements between the brief's summary and the code

- One disagreement, found in fix round 1: the documents of the first T6
  version presented T-SQL `SET` as allowed in setup, and the classifier
  denies it there (see the limitations above). The documents were corrected;
  the classifier was not changed. Every other summary line in the task-6
  brief and its addendum matched a real test or a real code path
  (`IsScalarDeclaration`, `IsSessionLocal`, the TRUNCATE proof points).
- The MCP stdio publish-smoke test, called out as intermittently flaky in
  the task briefing, did not fail in this session (observed passing in two
  full-suite runs and one standalone run). This is recorded as an honest
  non-reproduction, not as evidence the flake is fixed — no code in the MCP
  stdio path was touched by this task.
- The one test failure actually observed this session
  (`AgentWorkflowTests.Capability_discovery_then_schema_uses_two_bounded_calls_without_database_access`)
  was a direct, expected consequence of growing `Capabilities.cs` per
  deliverable 1, not a pre-existing or MCP-related flake; it is fixed by the
  byte-budget repin in commit `02fbc97` and does not reappear in the final
  full-suite run recorded above.
