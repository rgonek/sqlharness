# 012-typed-matrix-proof — closure evidence

## Status

**Code and documentation complete, including the fix wave after the final
review (F1-F10).** The plan verification (filter), the `-warnaserror` build and
the full suite without `Integration` finished without errors in the runs of this
session (see "Test evidence" below). No live-DB/platform evidence — see the
dedicated section below.

Branch `feat/plan-012-typed-matrix`, worktree
`.worktrees/plan-012-typed-matrix`. The branch started from `3d56a81` (not from
the plan base `8aa01f8` — predecessors 007/009/011 were merged in between,
which `progress.md` records as an expected divergence). No merge, no push.

Commit scope T1-T4: `3d56a81..da73e14`, 15 commits: T1 `8bf6331..7b6afd4`
(4), T2 `a451014..879526e` (5), T3 `e57b1fc..30950eb` (4), T4
`c06e940..da73e14` (2 code-test). The final review (`final-findings.md`,
F1-F10) added the fix wave `e906fbd..6760a96` (9 commits, see the table below);
the documents of this evidence are the following commits above `6760a96`, see
"Files changed" below — a commit cannot name its own hash.

| Task | Commits | What was delivered |
|---|---|---|
| T1 | `8bf6331` (red, characterization), `a4bfeb1` (refactor), `4b03997` (typed NULL), `7b6afd4` (operations + typed matrix) | Additive Core model `SqlHarnessParameterInput`/`SqlHarnessParameterMatrixInput`; a single binder (`SqlParameterParser.Bind`, `SqlParameterMatrixParser.Bind`, `ISqlDialect.BindParameters`); text→model compatibility adapter; typed NULL distinguished from the text `"null"` (R3); `TypedParameters`/`TypedMatrix` as additive fields on the operation records. |
| T2 | `a451014` (red), `5ba5419` (fix), `bb119b3` (validate), `fb39d61` (mapper), `879526e` (docs) | `McpOperationMapper` stopped assembling/splitting text (`FormatParameters`/`FormatMatrix` removed -- **`FormatParameters` was a public method of the MCP host set, with no consumers outside the file**, see F9 below); `MapParameters`/`MapMatrix` pass the structure through; `McpMatrixArgument.Values` admits JSON `null`; `ValidationOptions.TypedParameters` (R5) for `sqlharness_validate`; the schema `matrix.values.items.type` = `["string","null"]`. |
| T3 | `e57b1fc`, `8890dc1`, `55fe55a`, `30950eb` | Closure of three leftovers from the T1 review: Unicode/surrogate pairs through the real binder, `decimal(p,s)` on the typed path, case-insensitive duplicate names, rejection of the Postgres type by `PostgresDialect.BindParameters` and by `SqlHarnessModule.ExecuteAsync`, `MeasureParameterSetValidator.ParseShaped` closed to the end, and `SqlHarnessModule.ExecuteWatchNdjsonAsync` (not "ExecuteWatchStreamingAsync" — correction below) on the typed path. |
| T4 | `c06e940`, `da73e14` | Matrix runner invariants (new session/setup per cell, order, first-failure, preserved artifacts/partial report) proven for typed input with comma/empty/null cells; a fixed redaction test that previously could not fail. |
| Final fix wave | `e906fbd`..`6760a96` (9) | F1-F10 fixes from the final review (`.superpowers/sdd/012-typed-matrix/final-findings.md`): see the "Final fix wave" section below for each finding. |

## Scope corrections (controller rulings, copied from `progress.md`)

- **R1** — scope correction: `src/SqlHarness.Core/SqlHarnessModule.cs` (the
  place of matrix/parameter parsing and the collection of known secrets) is in
  scope. Only if the single binding path really requires it may
  `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`,
  `MeasureParameterSets.cs`, `CompareOperationPreparer`, the MCP tool
  description/schema sources, `docs/mcp.md` and their tests be minimally
  touched, where they state the old comma/empty-value restriction. Every file
  outside the plan list must be named in the report with a one-line
  justification. No unrelated refactoring.
- **R2** — no new CLI argument/file format. The legacy `--matrix
  name:type=v1,v2` keeps its exact meaning (the comma separates values, an
  empty value is rejected, the same error texts). The CLI project files are
  untouched.
- **R3** — typed NULL in the matrix: the MCP `values` admits JSON `null`
  elements = a typed NULL of the matrix type. `null`, `""` and the text
  `"null"` are three different values; two nulls are a duplicate. The report
  cell `ParameterValue` is JSON `null` only for the typed NULL cell; for every
  legacy input the JSON stays byte-identical.
- **R4** — the typed path covers the matrix AND the fixed parameters of every
  operation mapped by MCP (no text-join for any of them), through additive
  fields. The legacy public constructors `IReadOnlyList<string> Parameters`
  / `string Matrix` keep working through the compatibility adapter. A single
  binder, the same type rejections on both paths.
- **R5** — R4 also covers the MCP tool `validate` —
  `src/SqlHarness.Core/SqlValidation.cs` gets an additive typed entry point
  (`ValidationOptions.TypedParameters`), the existing overloads and the CLI call
  untouched — `validate` is an operation mapped by MCP, and leaving it on
  text-join would preserve the bridge that this plan removes.

**F2 correction (final fix wave):** the R1-R5 above are a **summary**, not a
`verbatim` quote -- each ruling in `progress.md` is longer and contains its own
justification and a "cost if wrong" clause, which this evidence must quote on
its own, because `progress.md` is a local scratch file under
`.superpowers/` and will be deleted (this evidence does not link to it as a
path that will exist). The "cost if wrong" clauses of the source
rulings:

- R1: the diff touches files the plan author wanted frozen; every such
  file must be named in the report/evidence with a justification (see "Files
  changed outside the plan list" below -- requirement met).
- R2: the CLI still cannot express a comma/an empty value in the
  matrix; it remains as an additional follow-up (see "Open items" below).
- R3: MCP consumers assuming a non-null string for `parameterValue`
  break on a null cell; the public record annotation changes
  `string -> string?`.
- R4: a larger diff through the operation records than a matrix-only change.
- R5: one additional public Core entry point outside the plan's file
  list.

## Corrections to be quoted verbatim (from the T4 brief)

- `tests/SqlHarness.Tests/WatchNdjsonTests.cs` was touched in T3, because
  **the T3 brief carried that coverage leftover** (the T1 review leftover:
  "ExecuteWatchNdjsonAsync typed path untested") — **not because of R1**. The
  T3 report wrongly attributed it to R1; `progress.md` records this as a minor
  T3 leftover: "controller brief named ExecuteWatchStreamingAsync; real method
  is SqlHarnessModule.ExecuteWatchNdjsonAsync". The streaming watch method is
  **`SqlHarnessModule.ExecuteWatchNdjsonAsync`** (not
  `ExecuteWatchStreamingAsync`, which does not exist in this code). This
  evidence quotes the name correctly.

## Task T4 — what was proven

### 1. Matrix runner invariants for typed input (comma/empty/null)

The leftover from the T2 review (`progress.md`): "No MCP-project test executes
a matrix cell with comma / empty / null values (Core fakes are not visible
there); executed cells are proven only by Core fake-session tests. Item 1
above must therefore cover all three value kinds at the Core
runner/module level."

Inventory before adding: `CompareMatrixTests.cs` already proved the new
session/setup/order for typed `a,b`/`""`/`null`/`"null"` on the success path
(`Typed_matrix_runs_comma_empty_null_and_null_text_as_four_cells`) and the
redaction of a value containing a comma on the failure path already in cell 0
(`Typed_matrix_failure_redacts_a_value_that_contains_a_comma`, zero completed
cells). What was missing was a test in which **the failure happens after
completed typed cells** (comma, empty string) — proving at the same time
first-failure stopping, preservation of earlier artifacts/partial report and
not opening further connections.

Added `Typed_matrix_failure_stops_the_run_after_comma_and_empty_cells_and_keeps_only_their_artifacts`
(`tests/SqlHarness.Tests/CompareMatrixTests.cs`, commit `c06e940`): 5 typed
values (`"se,cret"`, `""`, `null`, `"x4"`, `"x5"`), SQL failure on cell 2
(typed NULL). It proves:

- `factory.ConnectCount == 3` — cells 3 and 4 never opened a connection;
- `factory.Sessions.Count == 3`, each session a separate object
  (`Assert.NotSame`);
- cells 0 and 1 (`"se,cret"`, `""`) each got `SetupCount == 1`;
- `partialReport.Cells` = indices `[0, 1]` with `ParameterValue` `["se,cret",
  ""]`, in call order;
- artifact directories preserved for both completed cells
  (`artifacts.Directories.Count == 2`, they exist on disk, linked to the
  correct report cells);
- the error message names `cell 2` and `@BatchSize`, contains neither
  `se,cret`, `x4` nor `x5`.

Characterization of the already-correct `CompareMatrixRunner` behavior — RED
confirmed by a temporary mutation (the `catch` swapped `throw` for `continue`):
```
Assert.Equal() Failure: Values differ
Expected: SqlExecution
Actual:   Success
Failed: 1, Passed: 0
```
The mutation was reverted before the commit; `git diff --stat
src/SqlHarness.Core/CompareMatrixRunner.cs` empty after the revert, confirmed
before commit `c06e940`.

### 2. Matrix vs. param-set confidentiality contract — unchanged

- R3 (JSON null for typed NULL) was already proven in T1
  (`Typed_matrix_runs_comma_empty_null_and_null_text_as_four_cells`:
  `{"Index":2,"ParameterValue":null}`); T4 does not change this behavior,
  and did not require a new test.
- `--param-set` (the `.sqljson` file) **did not** open a new route through the
  typed input: `SqlHarnessParameterSetInput.Parameters` remains the declaration
  text in T1/T2/T3 (recorded as a non-blocking leftover in each of those
  reports); `TypedParameters`/`TypedMatrix` do not touch the param-set path. In
  line with the T4 brief ("Add a test if typed input opened a new") — no new test was
  added, because the route did not change; the existing param-set
  confidentiality coverage (`tests/SqlHarness.Tests/ParameterSetFileReaderTests.cs`,
  `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`) is unrelated to
  this plan and untouched.

### 3. Tests of redaction of the new typed errors

The leftover from the T2 review (`progress.md`): the test
`McpSecretRedactionTests.Typed_values_with_separator_characters_...`
`DoesNotContain` assertions could not fail, because the Core binder messages are
constant (they never interpolate values) — the only real line of defense was
the message equality assertion. Fixed in commit `da73e14`:

1. The test was changed to
   `Typed_values_with_separator_characters_reach_core_whole_and_its_rejection_is_a_constant_message`
   with a comment documenting exactly what it proves (a structure without
   join/split + a constant message), plus an added equality assertion for the
   `query` path (previously pinned only for `matrix`):
   `"Invalid value for SQL parameter 'n' of type 'int'."`.
2. The new test
   `Execution_phase_failure_that_echoes_a_typed_matrix_value_is_redacted_only_when_collected_as_a_known_secret`
   drives exactly that message shape which **actually** echoes a value — an
   execution-phase failure (the same shape produced by
   `CompareMatrixTests.Typed_matrix_failure_redacts_a_value_that_contains_a_comma`
   through a real fake session) — through `McpResultAdapter.Adapt`, once with
   the value registered as a known secret (redaction), once without (leak).
   The second call is a counter-example confirming that the first
   assertion is not vacuous.

   RED confirmed by a temporary mutation of `McpResultAdapter.RedactError`
   (skipping `SecretRedactor.Redact` for `Message`):
   ```
   Assert.DoesNotContain() Failure: Sub-string found
   String: ···"measured-run-failed:fikcyjna-exec-polowa-"···
   Found:  "fikcyjna-exec-polowa-2201,fikcyjna-exec-r"···
   Failed: 1, Passed: 0
   ```
   The mutation was reverted before the commit; `git diff --stat
   src/SqlHarness.Mcp/McpResultAdapter.cs` empty after the revert.

3. The remaining cases from the brief ("invalid typed value", "duplicate value",
   "unsupported type", "engine-rejected type") already have non-trivial
   coverage from T1-T3, checked in this session without a need for a new test:
   - *invalid typed value* (unknown type) —
     `McpMappingTests.Duplicate_and_unknown_type_parameters_fail_in_core_without_value_echo`
     (`DoesNotContain(SecretValue, ...)`, the message interpolates only the type
     name: `"Unsupported SQL parameter type '{type}'."`).
   - *duplicate value* — pinned by exact equality to a constant message
     `"... contains a duplicate value."`
     (`SqlParameterMatrixTests.Model_matrix_rejects_two_nulls_as_a_duplicate`,
     `McpMappingTests.Matrix_value_rejections_come_from_the_core_binder`) —
     equality is stronger than `DoesNotContain`, because it excludes any other
     text, not only one known value.
   - *unsupported/engine-rejected type* (Postgres `money`/`hierarchyid`/...) —
     `CompareMatrixTests.Postgres_rejects_unsupported_matrix_types_before_connect`,
     `Postgres_rejects_unsupported_typed_fixed_parameter_before_connect`
     (Core, `DoesNotContain(secret, ...)`, the message interpolates only the type
     name: `"SQL parameter type '{typeName}' is not supported on
     Postgres."`), and `McpMappingTests.Postgres_rejected_parameter_types_stay_core_owned`
     (MCP, through `validate`). The brief permits coverage at the Core level
     ("McpSecretRedactionTests, and Core where applicable").

None of these three categories required a new T4 test.

## Final fix wave — F1-F10 (`final-findings.md`)

The final whole-branch review returned the verdict "ready to merge with
fixes": 0 Critical, F1-F2 Important, F3-F10 Minor. Every finding was fixed in
this wave (`e906fbd..6760a96`); the full report with file:line locations,
commands and outputs: `.superpowers/sdd/012-typed-matrix/final-fix-report.md`.
Summary:

- **F1** (test) — `McpSecretRedactionTests.cs`: the test claimed to prove the
  collection of a typed matrix value as a known secret on the MCP path. No
  production site (`McpToolCatalog.cs:343,420,437`,
  `McpExecutionGate.cs:92,108`) passes `knownSecrets` to
  `McpResultAdapter.Adapt` -- Core redacts `SqlHarnessOutcome` before MCP sees
  it. Renamed to
  `McpResultAdapter_redacts_an_execution_phase_value_it_is_given_as_a_known_secret`
  and the comment, pointing at the Core
  `CompareMatrixTests.Typed_matrix_failure_redacts_a_value_that_contains_a_comma`
  as the real guardian of the collection. Commit `46b12cf`.
- **F2** (docs) — this evidence: R1-R5 are a summary, not `verbatim`; the
  "cost if wrong" clauses added (see the "Scope corrections" section above).
  Current commit.
- **F3** (docs + test) — `SqlParameterMatrixParser.Parse`,
  `SqlParameterParser.Parse`, `ISqlDialect.ParseParameters` (both
  implementations) have no production consumer (confirmed by `grep` across
  `src/`); a "legacy text adapter" comment was added on each, pointing at the
  real production path (`SqlParameterInputs.Resolve`/`ResolveMatrix`
  + `BindParameters`). `Legacy_matrix_checks_itself_before_the_fixed_parameters`
  pinned an order that production never had (production binds
  fixed first, then matrix -- the reverse of `.Parse`); renamed to
  `Legacy_matrix_Parse_checks_itself_before_reading_any_fixed_declaration`.
  Commits `6b89a2f`, `14e3c98`.
- **F4** (test) — `CompareMatrixTests.cs`: three `DoesNotContain`
  assertions ("se,cret"/"x4"/"x5") could not fail (cell 2 is a typed NULL, cells
  3/4 never connect); removed with a comment. Commit `522a85d`.
- **F5** (refactor) — `MeasureParameterSets.cs`: `TypeToken` re-parsed a
  grammar that `SqlParameterParser.ToInput` already has; two nearly identical
  `ParseShaped` overloads merged through a shared helper
  `Shape`. Behavior, error texts and order unchanged (text→model conversion
  stays lazy, in the same `try`/`catch`, which prepends the `setName`
  prefix). `MeasureParameterSet*Tests` guard: 85/0 before and after.
  Commit `b2090bb`.
- **F6** (test) — no test echoing a typed FIXED parameter (as distinct from
  matrix) in the execution phase; added
  `Typed_fixed_parameter_failure_redacts_a_value_that_contains_a_comma`.
  RED confirmed by removing BOTH secret registrations at once
  (`SqlParameterSecrets.AddValues(knownSecrets, operation.TypedParameters)`
  and the `foreach` with `AddTypedSecret` after `fixedParameters`) -- removing
  either ONE of them alone left the test green (the other still registered
  the same value). The mutation was reverted before the commit. Commit
  `e125df0`.
- **F7** (docs) — `docs/mcp.md`: a clause added that the `parameterValue`
  of a cell in the result is JSON null for the typed NULL cell, not only that
  `values` on input accepts JSON null. Commit `6760a96`.
- **F8** (fix + test) — `SqlHarnessParameterInput`/`SqlHarnessParameterMatrixInput`
  (`Contracts.cs`): the generated `ToString()` printed `Value`/`Values`.
  Overridden to print only `Name`/`Type` (and a counter for the matrix). RED
  confirmed for `SqlHarnessParameterInput` (the matrix did not leak by
  default, because `string[]`/`List<string?>`.`ToString()` does not enumerate
  the elements -- overridden anyway, because that is a property of a concrete
  collection type, not a safety guarantee). Commit `02f368b`.
- **F9** (docs) — this evidence: the removal of `McpOperationMapper.FormatParameters`
  (T2) marked as the removal of a public API of the MCP host set (the method
  was `public static`, with no consumers outside the file and tests -- `git grep`
  in `3d56a81` confirms). See the T2 row in the table above.
- **F10** (test) — `McpMappingTests.cs`: two `CancellationTokenSource`
  added on this branch (`Matrix_comma_empty_null_and_null_text_bind_in_core_as_distinct_values`,
  `Matrix_value_rejections_come_from_the_core_binder`) were undisposed;
  changed to `using var cts`. Commit `e906fbd`.

## Test evidence (this session, this worktree, Windows/x64, after the F1-F10 wave)

The verification command (`constraints.md`), run after commit `6760a96`:
```
dotnet test SqlHarness.sln --no-build --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal
```
→ `SqlHarness.Tests.dll`: `Passed! - Failed: 0, Passed: 137, Skipped: 0, Total: 137`.
→ `SqlHarness.Mcp.Tests.dll`: `Passed! - Failed: 0, Passed: 73, Skipped: 0, Total: 73`.
(State after T4: 134 / 73. Final fix wave: SqlHarness.Tests +3 net -- F8
added 2 new tests, F6 added 1, F4 removed 3 assertions from one existing
test without removing tests, F3/F5 added/removed no tests, they changed
existing ones. SqlHarness.Mcp.Tests without a count change (73): F1/F10 changed
existing tests, they added no new ones.)

```
dotnet build SqlHarness.sln --no-restore -warnaserror
```
→ `Build succeeded. 0 Warning(s) 0 Error(s)`, exit 0.

```
dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ `SqlHarness.Tests.dll`: `Passed! - Failed: 0, Passed: 2811, Skipped: 0, Total: 2811`
  (it was 2808 after T4; final fix wave +3, matching the filter above).
→ `SqlHarness.Mcp.Tests.dll`: `Passed! - Failed: 0, Passed: 212, Skipped: 4, Total: 216`
  (4 skips = 2 live opt-in `McpStdioLiveTests` + 2 foreign-RID publish-smoke
  `McpStdioProcessTests`, as in every earlier task of this plan).
  **No failure in this run** — neither of the two known flaky host tests
  (`McpStdioProcessTests.Inprocess_host_returns_zero_on_precancelled_token_without_stdout_bytes`,
  `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`) failed.

The MCP project was rerun on its own, given its history of flakiness in T2 (one
`McpStdioProcessTests` and one `McpLifecycleTests` failed individually in T2,
both passing on a retry):
```
dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ `Passed! - Failed: 0, Passed: 212, Skipped: 4, Total: 216` (second,
independent run, also without a failure).

Two clean runs of the MCP project in this session do not prove that these two
tests will never fail (T1 and T2 recorded individual failures of the same
tests, both passing on a retry) — only that no failure was observed in this
session, in this wave or in the previous one. No file of this plan
touches the stdio/lifecycle host code (plan 005 is the owner of that
flakiness).

Separately, as a refactoring guardian for F5 (`MeasureParameterSets.cs`):
```
dotnet test tests/SqlHarness.Tests --no-build --filter 'FullyQualifiedName~MeasureParameterSet' --verbosity minimal
```
→ before the refactoring: `Passed! - Failed: 0, Passed: 85, Skipped: 0, Total: 85`.
→ after the refactoring: `Passed! - Failed: 0, Passed: 85, Skipped: 0, Total: 85`
  (identical).

```
git diff --check
```
→ exit 0 (empty output), checked after commit `6760a96`.

```
git status --short
```
→ empty after the last commit of this evidence; the documents of this evidence
were added in the following commit, see "Files changed" below.

## Files changed in the final fix wave (F1-F10)

Within the plan list:
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs` (F10, commit `e906fbd`).
- `src/SqlHarness.Core/SqlParameterMatrix.cs`, `src/SqlHarness.Core/SqlSafety.cs`,
  `src/SqlHarness.Core/Dialect/ISqlDialect.cs`,
  `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`,
  `src/SqlHarness.Core/Postgres/PostgresDialect.cs` (F3 comments, commit
  `6b89a2f`).
- `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs` (F3 test rename, commit
  `14e3c98`; F8 two new tests, commit `02f368b`).
- `tests/SqlHarness.Tests/CompareMatrixTests.cs` (F4, commit `522a85d`; F6,
  commit `e125df0`).
- `src/SqlHarness.Core/MeasureParameterSets.cs` (F5, commit `b2090bb`).
- `src/SqlHarness.Core/Contracts.cs` (F8, commit `02f368b`).
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs` (F1, commit
  `46b12cf`).
- `docs/mcp.md` (F7, commit `6760a96`).
- `plans/012-typed-matrix-proof.md`, `plans/README.md` (F2/F9, closure
  evidence -- this and the following commit).

Outside the plan list: **no new ones**. All production files touched in
this wave (`SqlParameterMatrix.cs`, `SqlSafety.cs`, `ISqlDialect.cs`,
`SqlServerDialect.cs`, `PostgresDialect.cs`, `MeasureParameterSets.cs`,
`Contracts.cs`) were already on the "outside the plan with an R1/R3
justification" list from T1-T4 (see the section below) or are literally on the
plan list (`Contracts.cs`). Two temporary production mutations used solely as
RED/fail-ability evidence for F6 (`SqlHarnessModule.cs`, two secret
registrations removed together) were reverted before any commit --
confirmed by an empty `git diff --stat` for `SqlHarnessModule.cs`
immediately before commit `e125df0`.

## Files changed in T4

Within the plan list:
- `tests/SqlHarness.Tests/CompareMatrixTests.cs` (commit `c06e940`).
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs` (commit `da73e14`).

Outside the plan list: **none**. T4 did not touch any production file and no
test file other than the two which are literally listed in the plan scope. Two
temporary production mutations
(`CompareMatrixRunner.cs`, `McpResultAdapter.cs`) used solely as RED
evidence were reverted before any commit — confirmed by an empty
`git diff --stat` for both files immediately before commits
`c06e940` and `da73e14`.

### Files changed outside the plan list in T1-T3 (copied from earlier
### task reports, for the completeness of the closure evidence)

- `src/SqlHarness.Core/SqlHarnessModule.cs` — R1 (the place of
  matrix/parameter parsing, collection of known secrets).
- `src/SqlHarness.Core/CompareCellRunner.cs` — R1 (`CompareOperationPreparer.PrepareFixed`
  binds the compare/matrix fixed parameters).
- `src/SqlHarness.Core/Dialect/ISqlDialect.cs`,
  `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`,
  `src/SqlHarness.Core/Postgres/PostgresDialect.cs` — R1 (new interface
  member `BindParameters`, the Postgres type rejection wired through the
  dialect).
- `src/SqlHarness.Core/MeasureParameterSets.cs` — R1 (measure-sets binds
  fixed parameters on its own, reads the type token from the text).
- `src/SqlHarness.Core/BenchmarkSummary.cs`,
  `src/SqlHarness.Core/AgentOutputProjection.cs` — R3 (nullability of
  `ParameterValue` moved to the `--json-summary` projection).
- `tests/SqlHarness.Tests/BenchmarkRunTests.cs`,
  `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs` — fake
  `ISqlDialect` implementations needed the new member (pass-through).
- `tests/SqlHarness.Tests/BenchmarkSummaryTests.cs` — one assertion
  compiled under `string?`.
- `src/SqlHarness.Core/SqlValidation.cs` — R5 (typed entry point for
  `validate`).
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — R1 (one `[Description]`
  string saying "no commas").
- `docs/mcp.md` — R1 (one point describing the old restriction).
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — R1 (schema/description
  test + a JSON null test through a real client/server).
- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs` — two assertions
  reading the `operation.Parameters` text moved to `TypedParameters`.
- `tests/SqlHarness.Tests/Postgres/PostgresParameterTests.cs` — R1 (test
  file of `Postgres/PostgresDialect.cs`, named in R1 as touchable).
- `tests/SqlHarness.Tests/WatchNdjsonTests.cs` — **not R1** (correction
  above): touched, because the T3 brief carried the `ExecuteWatchNdjsonAsync`
  coverage leftover from the T1 review; the file and the method are not in the
  literal plan scope, but the leftover could not be closed from
  `CompareMatrixTests.cs`, because `SqlHarnessModule.ExecuteAsync` never calls
  the NDJSON entry point.

## No live-DB / platform evidence

No task of this plan (T1-T4) opened a connection to a live SQL
Server or PostgreSQL and did not touch the `~/.sqlharness` profile nor a
real secret. Every number above comes from fake
sessions/readers/test modules (`MatrixSessionFactory`, `RecordingModule`,
`FakeGainStore`, etc.) and from a synthetic `SQLHARNESS_HOME` made for the
needs of the test (`tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`:
`Typed_values_with_separator_characters_...` creates a temporary directory, it does
not read real profiles). Run only on one Windows/x64 worktree;
no evidence for another platform/architecture. The behavior
of the server on which the Postgres binder relies (the real rejection of the
`money`/`geography` type by `npgsql`, the real server error message
format), comes from the Postgres/`npgsql` type documentation, not from observation
of a live server.

## Open items for the user

- **Awaiting user confirmation (from the final review, F1-F10 wave):**
  - **R2** — the CLI still cannot express a comma/an empty/NULL value in the
    matrix (the legacy `--matrix name:type=v1,v2` stays unchanged, per ruling
    R2); this is a deliberate scope decision of plan 012, not an oversight.
  - **R3** — the `parameterValue` of a compare-matrix report cell may be JSON
    `null` for MCP consumers (the typed NULL cell); MCP consumers
    assuming a non-null string must adapt (see `docs/mcp.md`, the clause added
    in F7).
- Non-blocking leftovers, carried over unchanged from T1-T3 (no task
  in scope 012 would cover them): `.sqljson` param-set files
  remain declaration text, not the MCP JSON structure; the error messages of
  the typed path still say „The --matrix option ...” / „SQL parameters must be
  supplied ...” (text shared with the CLI, not specific to MCP).
  None of them is a safety gap — described in the T1 reports (items 3,
  4) and T2 (items 2, 5).
- Two flaky MCP host test items
  (`McpStdioProcessTests.Inprocess_host_returns_zero_on_precancelled_token_without_stdout_bytes`,
  `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`) — not
  observed in this session, but historically flaky on this branch
  (T1, T2); owner: plan 005.
