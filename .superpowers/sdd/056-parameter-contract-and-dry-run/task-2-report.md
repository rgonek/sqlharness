# Task 2 report: Dapper parity string sizing

## Implementation

Added `SqlParameterParser.DefaultStringParameterSize = 4000` and applied a shared sizing rule to unsized `nvarchar`, `varchar`, `nchar`, `char`, and untyped strings: size 4000 through length 4000, then `SqlMaxSize` above that. Typed and untyped NULL strings use size 4000. Explicit declarations such as `nvarchar(26)` and `nvarchar(max)` retain their declared sizes.

Added coverage for all four unsized declared string types, the 4000/4001 boundary, untyped values and NULL, typed NULL, and an explicit size. Updated related expectations in the parameter matrix, measure parameter-set validation, and parameter-hash tests.

## TDD evidence

**RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlParameterParser"`

Expected failure before implementation: `Failed: 14, Passed: 79, Total: 93`. The failures showed current-length sizes (3 or 6 rather than 4000), null sizes rather than 4000, and rejection or current-length sizing above 4000.

**GREEN** — `dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~SqlParameterParser"`

Passed: `Failed: 0, Passed: 93, Skipped: 0, Total: 93`.

After updating dependent assertions, `dotnet test tests/SqlHarness.Tests --no-restore --filter "FullyQualifiedName~SqlParameterParser|FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~MeasureParameterSetValidationTests|FullyQualifiedName~TypedParameterHasherTests"` passed: `Failed: 0, Passed: 250, Skipped: 0, Total: 250`.

## Full-suite evidence

The first `dotnet test SqlHarness.sln` run exposed two outdated assertions: unsized `char=` was expected to be rejected, and the canonical hash for an unsized `nvarchar` string encoded the former inferred size. I updated those expectations and reran the suite.

Final `dotnet test SqlHarness.sln`:

- `SqlHarness.Tests`: `Failed: 0, Passed: 3823, Skipped: 14, Total: 3837`.
- `SqlHarness.Mcp.Tests`: `Failed: 0, Passed: 400, Skipped: 4, Total: 404`.
- The skips are environment-gated integration and published-process checks.

## Files changed

- `src/SqlHarness.Core/SqlSafety.cs` — default size constant, shared sizing rule, and typed/untyped NULL and string binding behavior.
- `tests/SqlHarness.Tests/SqlParameterParserTests.cs` — default, boundary, NULL, and explicit-size behavior.
- `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs` — default-size expectations and empty unsized `char` acceptance.
- `tests/SqlHarness.Tests/MeasureParameterSetValidationTests.cs` — the size metadata expectations for one- and four-character unsized values now both use 4000; value hashes remain distinct.
- `tests/SqlHarness.Tests/TypedParameterHasherTests.cs` — updated the canonical unsized string hash to `57AECF159674E07124B1FBD96D65498B766EA8FF16D1C1697FB369B7F84FD08A`.

## Self-review and concerns

The production change is limited to unsized string binding; explicit sized bindings still use the declared size. `git diff --check` passed. One behavior follows the supplied constraints but deserves confirmation during integration: inputs longer than 4000 for `char` and `nchar` receive `Size = -1`, although SQL Server fixed-width `char`/`nchar` types do not have a MAX declaration. No database call was made, per task scope.
