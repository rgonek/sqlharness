# Plan 011/T5 spike: PostgreSQL `ANALYZE` offline parser behaviour

Status: **DESIGN COMPLETE** (not implemented). Date: 2026-10-01.
Scope: offline-parser evidence for canonical PostgreSQL `ANALYZE <table>` plus the
scope and acceptance tests a later implementation task would need. This document
makes no classifier, parser, or dependency change.

Worktree: `D:\Dev\sqlharness\.worktrees\plan-011-safe-sql-extensions`.
Parser: `SqlParserCS` **0.6.5** (`PostgreSqlDialect`), pinned in
`Directory.Packages.props:15` — the same version already used by every other T1–T4
probe in `plans/011-syntax-contract.md`. No dependency bump was made or evaluated
as necessary for this spike.

## 1. Observed parser behaviour (probe-verified, not from memory or docs)

A throwaway xUnit probe (`tests/SqlHarness.Tests/Postgres/_T5AnalyzeProbe.cs`,
written, run, and deleted before any commit — never checked in) called
`new SqlQueryParser().Parse(sql.AsSpan(), new PostgreSqlDialect())` directly,
bypassing `PostgresDocument`/`PostgresSafetyClassifier`, to see the parser's raw
reaction. Exact captured output:

| Input SQL | Result |
|---|---|
| `ANALYZE t` | **throws** `SqlParser.ParserException: Expected TABLE, found t, Line: 1, Col: 9` |
| `ANALYZE VERBOSE t` | **throws** `SqlParser.ParserException: Expected TABLE, found VERBOSE, Line: 1, Col: 9` |
| `ANALYZE (VERBOSE) t` | **throws** `SqlParser.ParserException: Expected TABLE, found (, Line: 1, Col: 9` |
| `ANALYZE (SKIP_LOCKED) t` | **throws** `SqlParser.ParserException: Expected TABLE, found (, Line: 1, Col: 9` |
| `ANALYZE` (bare) | **throws** `SqlParser.ParserException: Expected TABLE, found EOF` |
| `ANALYZE TABLE t` (Hive-style) | **parses** to `SqlParser.Ast.Statement+Analyze` |

`ANALYZE TABLE t` AST, via `.ToString()` on the parsed node:

```
Analyze { Name = t, Partitions = , ForColumns = False, Columns = , CacheMetadata = False, NoScan = False, ComputeStatistics = False }
```

i.e. the real shape is `Statement.Analyze { Name: ObjectName, Partitions,
ForColumns: bool, Columns, CacheMetadata: bool, NoScan: bool, ComputeStatistics: bool }`
— a **Hive** `ANALYZE TABLE ... [PARTITION ...] [FOR COLUMNS ...] [COMPUTE
STATISTICS ...]` shape, with no `VERBOSE` option and no PostgreSQL optional
column list. The parser's grammar for the `ANALYZE` keyword only recognizes the
Hive-style `ANALYZE TABLE` spelling; any other token right after `ANALYZE`
(an identifier, `VERBOSE`, `(`, or end-of-input) fails the `Expected TABLE, found
…` check before any PostgreSQL-specific `ANALYZE` grammar is ever consulted —
there is no PostgreSQL `ANALYZE` grammar in this parser version at all.

### Code path consequence

- `src/SqlHarness.Core/Postgres/PostgresDocument.cs:14-26` (`TryParse`) wraps the
  parser call in `try { ... } catch (Exception) { statements = null; return false; }`.
  Every canonical form above throws, so `TryParse` returns `false` for all of
  them, and `PostgresSafetyClassifier.Classify`
  (`src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs:32-33`) returns
  `Denied(SqlSafetyReason.ParseError)` before the statement ever reaches the
  per-statement switch. These forms are **invisible** to the Unsupported-group
  switch; they fail one step earlier.
- `ANALYZE TABLE t` does reach the switch, lands on
  `case Statement.Analyze:` (`PostgresSafetyClassifier.cs:243`), which is in the
  explicit `Unsupported` group alongside `CreateView`, `AlterTable`, etc., and is
  denied as `UnsupportedStatement`.

### Why this matters for T1's matrix

Plan 011 row **011-T5-R1** (`plans/011-syntax-contract.md`) expected `ParseError`
for canonical `ANALYZE`; this spike's probe confirms that expectation on code,
not on recollection, for four distinct canonical spellings (plain table name,
`VERBOSE` keyword form, parenthesized `(VERBOSE)` option-list form, and
parenthesized `(SKIP_LOCKED)` option-list form) plus the bare-keyword edge case.
Row **011-T5-R2** is confirmed separately: the *only* spelling under `ANALYZE`
this parser version accepts is the unrelated Hive maintenance statement, and it
already denies correctly via the existing `Unsupported` switch arm. No new
confusion risk was found — `Statement.Analyze` carries no field that could be
mistaken for a parsed `VERBOSE`/column-list option, because PostgreSQL `ANALYZE`
is simply never built as an AST at all today.

## 2. Verdict for this task

**No implementation is possible without a parser change.** Per the plan 011/T5
constraints, this task does not implement PostgreSQL `ANALYZE` support, does not
touch `PostgresSafetyClassifier.cs` or `PostgresDocument.cs`, and does not bump
`SqlParserCS`. The anchor tests added by this task
(`tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`:
`T5_Analyze_additional_canonical_forms_are_parse_errors`,
`T5_Analyze_hive_form_stays_unsupported`, alongside the pre-existing
`Canonical_analyze_is_a_parse_error_until_the_parser_supports_it`) pin exactly
this behaviour as a regression anchor so a future parser upgrade is forced to
either keep these tests green (ANALYZE stays denied) or consciously change them
alongside new ALLOW coverage.

## 3. Scope of a future implementation task (NOT done here)

If a later plan revision decides to support canonical PostgreSQL
`ANALYZE [VERBOSE] [table [(column [, ...])]]`, the work would require, in
order:

1. **Parser dependency change.** `SqlParserCS` 0.6.5 has no grammar rule for
   canonical PostgreSQL `ANALYZE`. The only way to get an AST is either (a) a
   newer `SqlParserCS` release that adds PostgreSQL `ANALYZE` support (to be
   confirmed by checking that project's changelog/issue tracker — not assumed),
   or (b) a local pre-processing/alternate-grammar approach, which plan 011
   already forbids as a "regex bypass" — so realistically this gates on (a).
   Any dependency bump needs its own regression review per repo policy (the
   whole `011-syntax-contract.md` matrix for every prior AST probe would need
   re-verification against the new parser version, since a parser upgrade can
   silently change *other* statements' shapes too, not just `ANALYZE`).
2. **New AST case.** Once an upstream AST node exists for canonical `ANALYZE`
   (call it `Statement.<NewNodeName>` — exact name unknown until upstream ships
   it), add a `case Statement.<NewNodeName>:` arm to
   `PostgresSafetyClassifier.ClassifyStatement` (the switch currently at
   `PostgresSafetyClassifier.cs:~160-261`), separate from the existing
   `case Statement.Analyze:` (Hive) arm which must keep its current deny
   verdict unchanged — the two must never be merged or share a branch.
3. **Target-proof rule**, modeled on T4's TRUNCATE proof
   (`ClassifyTruncate`, `PostgresSafetyClassifier.cs:264+`): decide whether
   `ANALYZE` on a persistent (non-temp) table is safe to allow as read-only
   maintenance (it does not mutate rows, but it does mutate planner statistics
   and can take locks — this needs its own safety ruling, not an automatic
   inheritance of TRUNCATE's temp-only posture). This ruling is explicitly out
   of scope for this spike; it is plan-level policy work for whichever task
   picks this up.
4. **Option coverage.** `VERBOSE` is cosmetic (output verbosity only) and would
   likely be allow-safe once the base statement is allowed. Any column-list
   form (`ANALYZE t (col1, col2)`) and multi-table form (`ANALYZE t1, t2`) need
   explicit AST field handling once the real shape is known.

### Acceptance tests a future implementation task would need

File: `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs` (same file as
every other Postgres safety test; no other file is justified since the
classifier under test lives in the same assembly and existing ANALYZE anchors
are already there).

| Planned test name | Asserts |
|---|---|
| `Analyze_canonical_form_supported_after_parser_spike` | `ANALYZE t` (or `ANALYZE <proven-target>` per whatever target-proof rule is chosen in step 3 above) parses to the new AST node and reaches an explicit ALLOW or a deliberate, documented DENY — not an accidental `ParseError` — once the parser dependency is upgraded. Must also assert `Reason`/`Allowed` match the chosen policy, not just "no longer a parse error". |
| `Analyze_verbose_form_supported_after_parser_spike` | `ANALYZE VERBOSE t` behaves identically to `ANALYZE t` (verbosity is cosmetic and must not change the allow/deny verdict). |
| (new) `Analyze_hive_form_still_distinguished_from_canonical_form` | Regression: `ANALYZE TABLE t` (Hive) must still deny as `UnsupportedStatement` through its own unchanged `case Statement.Analyze:` arm, proving the new canonical-`ANALYZE` arm and the old Hive arm were not accidentally merged. |
| (new) `Analyze_dependency_upgrade_does_not_change_other_probed_AST_shapes` | Spot-check a sample of the other `011-syntax-contract.md`-probed constructs (e.g. `TRUNCATE`, `CREATE TEMP`, three-/four-part names) still classify exactly as before after the `SqlParserCS` bump, since a transitive grammar change in the new parser version is the main regression risk called out in step 1. |

These names are **not** added as test code by this task (adding them now would
require either a parser capable of producing the AST — which does not exist in
0.6.5 — or stub/fake tests asserting against an implementation that was never
written, both of which plan 011/T5 forbids). They are documented here so the
implementation task that eventually picks this up has a concrete, file-anchored
acceptance checklist instead of re-deriving it.

## 4. Explicit non-goals of this task (per brief)

- No regex bypass around the parser.
- No `SqlParserCS` dependency bump (even though step 1 above shows one would be
  required for real support — it was evaluated and explicitly **not**
  performed here).
- No change to `PostgresSafetyClassifier.cs` or `PostgresDocument.cs`.
- No reinterpretation of `Statement.Analyze` (Hive) as canonical PostgreSQL
  `ANALYZE`.

T5 status: DESIGN COMPLETE
