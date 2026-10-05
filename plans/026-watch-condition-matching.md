# Plan 026: `watch --until` matches bit/boolean, quoted strings and exponent floats, and keeps polling while the row does not exist yet

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/WatchCondition.cs src/SqlHarness.Core/WatchRunner.cs tests/SqlHarness.Tests/WatchConditionTests.cs tests/SqlHarness.Tests/WatchTests.cs AGENTS.md README.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plans/016-restore-green-ci.md
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

`watch` polls a read-only query until `--until "<column> <op> <value>"` holds on the first row of the first result set, or until `--max-duration` (exit 7). Several common predicates **silently never match**, so the agent waits the full duration (default 15 minutes, up to 24 hours) and gets exit 7:

- A `bit` (SQL Server) or `boolean` (PostgreSQL) column arrives as `bool`. `bool` is not `IFormattable`, so it formats as `"True"`/`"False"`, and `--until "Done = 1"` or `"Done = true"` never matches.
- The design spec's own example is `Status = 'Completed'`. The operand is used **verbatim**, so the quotes are part of it, and the comparison is ordinal.
- `double` values of 1e15 or more, or below 1e-5, format with an exponent (`1E-05`). `decimal.TryParse(..., NumberStyles.Number)` rejects exponents, so the comparison falls back to text. Equality then never matches, and `<`/`>` throw "requires numeric values".
- A progress query that returns **no row yet** (`WHERE JobId = @id` before the job row exists) throws `"Watch condition requires a first row."`. The whole watch fails with exit 2 on the first poll instead of continuing to poll.

## Current state

- `src/SqlHarness.Core/WatchCondition.cs`:
  - `Parse` (`:36-68`) splits on the leftmost operator; `Operand` is `span[(bestIndex + bestLength)..].Trim().ToString()`.
  - `IsMet` (`:70-112`):

    ```csharp
            if (firstResultSet.Rows.Count == 0)
                throw new SqlHarnessSafetyException(MissingRowMessage);
            ...
            var cell = row[ordinal];
            if (cell is null)
                throw new SqlHarnessSafetyException(NullValueMessage);

            var leftText = FormatValue(cell);
            var rightText = Operand;

            var leftNumeric = decimal.TryParse(leftText, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = decimal.TryParse(rightText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftNumeric && rightNumeric)
                return CompareNumeric(leftNumber, rightNumber, Operator);

            return CompareText(leftText, rightText, Operator);
    ```
  - `FormatValue` (`:134-141`): string → as is; `IFormattable` → invariant `ToString(null, …)`; else `Convert.ToString(value, InvariantCulture)`.
  - `CompareText`: `Equal`/`NotEqual` ordinal; other operators throw `NonNumericCompareMessage`.
- `src/SqlHarness.Core/WatchRunner.cs:387-393`: evaluates `condition.IsMet(firstSet)` each poll. Exceptions propagate and end the watch.
- Tests: `tests/SqlHarness.Tests/WatchConditionTests.cs` (17 test methods). Pattern:

  ```csharp
      [Theory]
      [InlineData("Count >= 10", 10, true)]
      [InlineData("Status = Completed", "Completed", true)]
      public void Predicate_evaluates_first_row_column(string text, object value, bool expected)
      {
          var condition = WatchCondition.Parse(text);
          Assert.Equal(expected, condition.IsMet(Result("Count", value, alternateName: "Status")));
      }
  ```
- Contract text: `AGENTS.md:80` ("`--until` (predicate on the first row of the first result set)"). Spec: `docs/superpowers/specs/2026-07-29-agent-oriented-command-extensions-design.md:83`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Watch tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~Watch"` | all pass |
| MCP watch tests | `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName~Watch"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/WatchCondition.cs`, `src/SqlHarness.Core/WatchRunner.cs` (only if Step 4 needs it), `tests/SqlHarness.Tests/WatchConditionTests.cs`, `tests/SqlHarness.Tests/WatchTests.cs` (one runner-level test), `AGENTS.md`, `README.md` (the `--until` description).

**Out of scope**: new operators (LIKE, AND/OR), `--until-unchanged` semantics, NDJSON event shapes.

## Git workflow

Branch `fix/plan-026-watch-condition`. Commits `test(026): ...`, `fix(026): ...`, `docs(026): ...`. Do NOT push.

## Steps

### Step 1: RED tests

Add to `WatchConditionTests.cs`:
- `bool` values: `("Done = 1", true, true)`, `("Done = 0", false, true)`, `("Done = true", true, true)`, `("Done = TRUE", true, true)`, `("Done = false", true, false)`, `("Done != 1", false, true)`.
- Quoted operands: `("Status = 'Completed'", "Completed", true)`, `("Status = \"Completed\"", "Completed", true)`, `("Status = 'It''s'", "It's", true)`. That last row means a doubled single quote inside single quotes is an escaped quote. If you choose not to support escaping, drop the row and document "no escapes".
- Exponent: `("Ratio = 0.00001", 0.00001d, true)`, `("Ratio < 0.001", 0.00001d, true)`, `("Big >= 1e15", 1e15d, true)`.
- Unquoted text stays ordinal and case-sensitive: `("Status = completed", "Completed", false)`. This pins current behavior.

**Verify**: the new rows fail and the existing rows pass.

### Step 2: Normalize values and operands

- In `FormatValue`, map `bool` to `"1"`/`"0"` before the `IFormattable` check.
- In `IsMet`, normalize the operand once: if it is wrapped in matching `'…'` or `"…"`, strip one level (and unescape doubled quotes of that kind). Then, if the operand equals `true`/`false` case-insensitively, map it to `"1"`/`"0"`. A quoted `'true'` is a string literal, so apply the boolean mapping **only to unquoted** operands. Do this in `Parse` so it happens once; store the normalized operand plus a flag `IsQuoted`.
- If the operand was quoted, always use `CompareText` (a quoted value is a string, never numeric).
- Numeric parsing: `NumberStyles.Float | NumberStyles.AllowThousands` is wrong because thousands separators are ambiguous. Use `NumberStyles.Float` for both sides. Values outside `decimal` range fail `TryParse` and fall back to text. That is acceptable; note it in the comment.

**Verify**: watch tests all pass.

### Step 3: Missing row and NULL mean "not met yet"

Change `IsMet` so that `Rows.Count == 0` returns `false` (keep polling) instead of throwing. Treat a `NULL` cell the same way: `false`. Keep the throws for **structural** problems that polling cannot fix: missing result set, missing column, duplicate column.

Before changing, run `grep -rn "requires a first row\|cannot be NULL" tests`. If any test pins the throwing behavior, update it to assert `false`, and list it in the commit message.

Add a runner-level test in `WatchTests.cs` using the existing fake-session pattern there. First poll returns zero rows, second returns `Done = 1`. Assert exit reason `ConditionMet` at poll 2.

**Verify**: watch tests (Core and MCP) pass.

### Step 4: Docs

`AGENTS.md:80` and the README watch section: add "Values compare numerically when both sides parse as numbers (exponents allowed); `bit`/`boolean` compare as `1`/`0` (`true`/`false` accepted); quote a string operand to force text comparison; no row or a NULL value means the condition is not met yet."

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

About 14 new theory rows and 1 runner test, following `Predicate_evaluates_first_row_column`.

## Done criteria

- [ ] Watch tests in both test projects pass; full gate passes
- [ ] `grep -n "MissingRowMessage" src/SqlHarness.Core/WatchCondition.cs` → no use inside `IsMet` (the constant may be removed)
- [ ] AGENTS.md and README describe the comparison rules
- [ ] `plans/README.md` row updated

## STOP conditions

- An MCP test pins the throwing behavior for missing rows as part of a controlled outcome (`watch_*`). Report it; the MCP contract may need a decision.
- Changing NULL handling breaks an `--until "X != ..."` expectation where NULL was meant to match. Report the test.

## Maintenance notes

- If more expressive predicates are requested (AND/OR, LIKE), write a small tokenizer instead of growing the operator-index heuristic.
- `NDJSON` watch events are unaffected: they report hashes and changes, not predicate evaluation details.
