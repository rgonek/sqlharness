# 011-safe-sql-extensions-proof — closure proof

Code tip: `02fbc97` (`test(011/T6): repin discoveryThenSchema byte budget after
capabilities growth` — the last commit touching code or test fixtures),
branch `feat/plan-011-safe-sql-extensions`, worktree
`.worktrees/plan-011-safe-sql-extensions` (worktree base / merge-base with `main`:
`0fad2a8`). All gates below (plan scoped filter, `-warnaserror` build, full
non-Integration suite, `git diff --check`) were observed on this code tip, in this
worktree, on 2026-10-01. Commits after it (`6319a0c` superseded-claim doc fix,
this proof, the `plans/README.md` row and `plans/011-safe-sql-extensions.md`
status/checkbox update) are docs-only (`git show --stat` for each touches only
`*.md` files) and need only a fresh `git diff --check` plus a sanity rerun of
the plan's scoped filter on the final tree, not a full re-gate. No push, no
merge (the plan forbids both without a separate instruction). Header
deliberately names the last code-touching commit, not the moving HEAD, so it
does not go stale as further docs-only commits land.

## Goal

Make the classifier accept four narrow, previously-denied T-SQL/PostgreSQL
constructs (T-SQL `SET` to a same-batch scalar local; T-SQL table variables;
PostgreSQL `TRUNCATE` of proven session temps; a PostgreSQL ANALYZE
design-only spike) without widening any existing denial (dynamic SQL,
persistent DDL, cross-database access, function side effects, transaction
control), and make `Capabilities.cs`/`AGENTS.md`/the historical docs describe
exactly what the code now does — this task (011/T6).

## Commits (`0fad2a8..6319a0c`, 40 commits)

| Task | Commits | What was delivered |
|---|---|---|
| T1 | `053d8c7`, `cd618b7` | `plans/011-syntax-contract.md`: AST matrix for T2–T5 plus standing negatives, probe-verified against ScriptDom 180.37.3 and SqlParserCS 0.6.5; review round-1 fixes (explicit T5-R1 tests, T4-D4 TRUNCATE-only scope). Docs only, no classifier change. |
| T2 | `ec8ebb5` (red), `8437414` (green), `90be14c` (compound/cursor regression) | T-SQL `SET @x = <expr>` allowed only when `@x` is a same-batch scalar local (`DECLARE @x <scalar-or-UDT>` earlier in the batch); RHS walked by the existing external/stateful/cross-db inspection. Session/transaction-option `SET` families, compound (`+=`), cursor `SET`, and use-before-`DECLARE` stay denied. |
| T3 | `4b4aeff` (red), `cbd4fbc` (green) | T-SQL table variables: `DECLARE @t TABLE (...)` then `INSERT`/`UPDATE`/`DELETE`/`MERGE`/`SELECT`/`OUTPUT INTO` against `@t` allowed as session-local once proven by a same-batch `DeclareTableVariableStatement`. Undeclared, scalar-as-table, table-as-scalar, cross-batch, and user-defined-table-type names stay denied. |
| T3b (fix wave) | `bbbf7cf`, `489370c` (red), `a49e062`, `acec836`, `5b67218`, `abf2ff9`, `c70b9e4` | Fixed use-before-`DECLARE` for both `SET` and table-variable targets (declaration-offset proof in `BatchVariableScope`), fixed per-`GO`-batch locality in the parameter-reference collector, then three read-only refactors (one scalar-DECLARE predicate, one scope per batch, one walk per setup statement; dead resolution-kind check removed). |
| T4 | `c811df7` (red), `adf064e` (green), `fe70482`/`bc00a67` (I1: ASCII-only fold), `a44e0e9`/`54fb02a` (M2: `ON COMMIT DROP`), `e4dbb3b` (M3: `EXPLAIN`/`TRUNCATE` interaction pins), `bd5607a` (M4: `TRUNCATE ONLY` decision recorded) | PostgreSQL `TRUNCATE [ONLY] <names>` allowed only when every target is a proven current-session temp (single-part name; not `ON COMMIT DROP`; quoted or all-ASCII-unquoted; recorded from this flow's `CREATE TEMP`/`SELECT INTO TEMP`). Persistent, mixed, `CASCADE`, schema-qualified, and unproven `pg_temp_*`-prefixed targets deny `NonTemporaryWrite`/`UnsupportedStatement`. Ruling R4: `RESTART IDENTITY` stays denied even over proven temps (sequence provenance unproven); `CONTINUE IDENTITY` / omitted is the allowed form. |
| T5 | `808f276` (tests), `e17d55a` (doc) | `plans/011-analyze-parser-spike.md`: probe-verified finding that SqlParserCS 0.6.5 has no grammar for canonical PostgreSQL `ANALYZE` (only the unrelated Hive `ANALYZE TABLE` form parses, to `Statement.Analyze`, already denied). Status **DESIGN COMPLETE, not IMPLEMENTED** — no classifier, parser, or dependency change. |
| T4b (fix wave) | `52cbab9`/`7af94de` (ASCII fold for every statement, not just TRUNCATE), `46feb92` (`ON COMMIT DROP` for every statement), `9d8a4c6` (contract update), `2b31057`/`1d1f09e` (`pg_temp_` prefix no longer proof), `8bc5a57` (`DROP TABLE` revokes unknown-name proofs), `c6b7d06` (63-byte UTF-8 identifier limit), `43142a2` (carried-record pins), `c9ec92e` (contract rows) | Tightened the shared PostgreSQL session-temp proof (`IsSessionLocal`) used by DML/`DROP TABLE`/`CREATE INDEX` in addition to `TRUNCATE`: ASCII-only identifier fold, `ON COMMIT DROP` never proves, a `pg_temp_*` prefix is never proof for any statement (only the exact two-part `pg_temp.<relation>` alias remains, and not for `TRUNCATE`), identifiers over 63 UTF-8 bytes never prove, and `DROP TABLE` of an offline-unknown name revokes every tracked proof in the flow. |
| T6 (this task) | `12a701e`, `64b7232`, `b62dec1`, `02fbc97`, `6319a0c` | This closure: `Capabilities.cs` + a verdict-backed test, `AGENTS.md`, the ANALYZE spike's "no live server" sentence, a byte-budget fixture repin forced by the capabilities growth, and four pre-011 docs' superseded `pg_temp_` claims. |

Regression-test rule observed from the history itself (not re-executed per
commit): every implementing task's commit list shows a `test(...)`/"red-phase"
commit immediately before the corresponding `fix(...)`/`feat(...)` commit
(T2: `ec8ebb5`→`8437414`; T3: `4b4aeff`→`cbd4fbc`; T3b:
`489370c`→`a49e062`/`acec836`; T4: `c811df7`→`adf064e`,
`fe70482`→`bc00a67`, `a44e0e9`→`54fb02a`; T4b:
`52cbab9`→`7af94de`/`46feb92`, `2b31057`→`1d1f09e`, `43142a2`→`8bc5a57`/`c6b7d06`
preceded by their own red commits). This task did not re-checkout those
intermediate commits to re-run the red phase; the final-tree test run below is
the evidence this task itself observed.

## Test evidence (observed this session, final tree `6319a0c`)

Plan scoped filter:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests" --verbosity minimal
```
→ `Passed! - Failed: 0, Passed: 654, Skipped: 0, Total: 654` (SqlHarness.Tests.dll).
Matches the brief's stated baseline at `c9ec92e` (654/654); T6 added no new
tests to these three classes, so the count is unchanged by this task.

Build:

```
dotnet build D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --no-restore -warnaserror
```
→ `Build succeeded. 0 Warning(s) 0 Error(s)`.

Full non-Integration suite:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ SqlHarness.Tests.dll: `Passed! - Failed: 0, Passed: 2467, Skipped: 0, Total: 2467`.
→ SqlHarness.Mcp.Tests.dll: `Passed! - Failed: 0, Passed: 193, Skipped: 4, Total: 197`
(4 skips are the pre-existing 2 live opt-in + 2 foreign-RID publish-smoke
tests, unrelated to this task).

Capabilities-specific:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~CapabilitiesCommandTests" --verbosity minimal
```
→ `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7` (5 pre-existing +
2 new: `SessionTempStatements_sqlserver_entries_match_the_classifiers_real_verdicts`,
`SessionTempStatements_postgres_TRUNCATE_entry_matches_the_classifiers_real_verdicts`).

MCP publish-smoke test, run on its own per the brief's instruction to not
claim a full PASS if this test is flaky:

```
dotnet test D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions\SqlHarness.sln --filter "FullyQualifiedName~McpStdioProcessTests" --verbosity normal
```
→ `Total tests: 6, Passed: 4, Skipped: 2` — `Published_single_file_server_passes_the_stdio_smoke_on_this_rid` **passed**
(45 s) alongside the three in-process tests; the two skips are the
foreign-RID (linux-x64/osx-arm64) variants, expected on this win-x64 host.

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
reproduced** in any of the three test runs this session performed
(full suite twice, `McpStdioProcessTests` alone once).

`git diff --check` → exit 0 (clean) after every commit in this task.
`git status --short` → clean at the end of this task (verified below).

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
- `DECLARE @v dbo.SomeType` followed by `SET @v = ...` stays **allowed**: the
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
  classifier never opens a connection or reads the server's `search_path`
  catalog, so the `pg_temp.<relation>` two-part alias is accepted on the
  assumption that `pg_temp` resolves first in the default `search_path`. A
  session whose `search_path` has been changed (or that genuinely has a
  persistent schema literally named `pg_temp`) is not modeled.
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

## Concerns / disagreements between the brief's summary and the code

- None found that required overriding the brief: every summary line in the
  task-6 brief and its addendum matched a real test or a real code path
  (`IsScalarDeclaration`, `IsSessionLocal`, the TRUNCATE proof points) when
  checked against `SqlSafety.cs` / `PostgresSafetyClassifier.cs` / the
  cited tests.
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
