# Plan 024: Results containing PostgreSQL arrays/inet/NaN or SQL Server spatial/hierarchyid columns no longer fail the whole operation

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs README.md AGENTS.md`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (canonical encoding is the basis of result equivalence and snapshot diff)
- **Depends on**: plans/016-restore-green-ci.md. Coordinate with plans/022 (Npgsql type mappings may change).
- **Category**: bug
- **Planned at**: commit `5280f78`, 2026-10-03

## Why this matters

Every row read by `query`, `measure`, `compare`, `watch`, `snapshot` and the catalog commands passes through `CanonicalScalarCodec.Prepare`, which builds a stable, culture-invariant JSON encoding used for result hashing and equivalence. `Prepare` **throws** `NotSupportedException` for:
- non-finite floating values (`'NaN'::float8`, `Infinity`), which are legal in PostgreSQL and SQL Server `float` results computed client-side;
- any value that is not a listed primitive and not `IFormattable`. Npgsql returns arrays (`int[]` → `Int32[]`, `text[]` → `String[]`), `inet` → `System.Net.IPAddress`, `macaddr` → `PhysicalAddress`, `bit varying` → `BitArray`, geometric types. SQL Server `geography`/`geometry`/`hierarchyid` come back as `SqlGeography`/`SqlGeometry`/`SqlHierarchyId`, which are not `IFormattable`.

The exception fails the **whole operation**, not just display. So `SELECT * FROM t` on a PostgreSQL table with an array column cannot be measured or compared at all. The docs list these types only as **parameters** and never mention a result limitation. The fix adds explicit, invariant encodings for these types. It changes no existing hash, because every value that gets a new encoding currently throws.

## Current state

- `src/SqlHarness.Core/CanonicalResults.cs:33-54`:

  ```csharp
  internal static class CanonicalScalarCodec
  {
      internal readonly record struct PreparedScalar(object? Value, string? InvariantValue);

      public static PreparedScalar Prepare(object? value) => value switch
      {
          float number when !float.IsFinite(number) => throw new NotSupportedException(
              "Non-finite System.Single values are not supported canonical scalars."),
          double number when !double.IsFinite(number) => throw new NotSupportedException(
              "Non-finite System.Double values are not supported canonical scalars."),
          null or DBNull or string or char or bool or byte[] or
              byte or sbyte or short or ushort or int or uint or long or ulong or
              decimal or float or double or Guid or DateTime or DateTimeOffset or
              DateOnly or TimeOnly or TimeSpan => new PreparedScalar(value, null),
          IFormattable formattable => new PreparedScalar(
              value,
              formattable.ToString(null, CultureInfo.InvariantCulture)
                  ?? throw new NotSupportedException(...)),
          _ => throw new NotSupportedException(
              $"Canonical scalar type '{value.GetType().FullName}' is not supported."),
      };
  ```
- `Write` (`:56` onwards) emits `{"type": ..., "isNull": ..., "length": ..., "value": ...}` per scalar, with helpers `WriteScalarHeader`, `WriteInteger` and `WriteInvariantText(writer, typeName, text)`. Example case: `case Guid guid: WriteInvariantText(writer, "guid", guid.ToString("D"));`. Read the end of `Write` to see how `InvariantValue` (the `IFormattable` branch) is written. New encodings must follow the same envelope shape.
- Readers pass raw provider values: `Postgres/NpgsqlSessionFactory.cs:152` `public object GetValue(int ordinal) => _reader.GetValue(ordinal);` and `SqlExecution.cs` `SqlClientReader.GetValue` (same shape).
- Tests that **pin the current rejection** and must be updated deliberately: `tests/SqlHarness.Tests/CanonicalResultsTests.cs:89-124`, i.e. `Unsupported_scalar_rejection_leaves_the_accumulator_clean_and_usable` (uses a test-only `CultureDependentScalar`, which stays unsupported) and `Non_finite_floating_scalar_is_rejected_without_changing_state` with `NonFiniteFloatingValues` (`double.NaN`, `±Infinity`, plus floats). The second test changes meaning.
- The test project pins `pl-PL` culture in some tests (`CanonicalResultsTests.cs` ~253-280). New encodings must be culture-invariant, and a test must prove it under `pl-PL`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Canonical tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/CanonicalResults.cs` (`CanonicalScalarCodec` only), `tests/SqlHarness.Tests/CanonicalResultsTests.cs`, `README.md` and `AGENTS.md` (a short "result types" note).

**Out of scope**: reader changes (`GetValue`), equivalence semantics (`ResultEquivalence.cs`), display/rendering of these values in the CLI (`Renderer.cs`; check it does not throw too, and if it does, record it as a follow-up rather than fixing it here), and Npgsql version (plan 022).

## Git workflow

Branch `fix/plan-024-canonical-types`. Commits per step. Do NOT push.

## Steps

### Step 1: Non-finite floats get tagged encodings

Replace the two throwing arms. Non-finite `float`/`double` produce a `PreparedScalar` that `Write` emits as `WriteInvariantText(writer, "singleNonFinite" | "doubleNonFinite", text)`, where `text` is `"NaN"`, `"Infinity"` or `"-Infinity"`. Use those exact strings, not `ToString()`, which is culture-dependent in some runtimes. The `Write` switch's `float number when float.IsFinite(number)` arms already exist. Add the non-finite arms before the generic `IFormattable` handling so they never reach it.

Rewrite `Non_finite_floating_scalar_is_rejected_without_changing_state` into `Non_finite_floating_scalars_have_stable_tagged_encodings`. For each value, assert two accumulators fed the same row produce the same hash, and `NaN` vs `Infinity` produce different hashes. Add an explicit expected-JSON test for `double.NaN` mirroring the expected-string style in `Unsupported_scalar_rejection_...`.

**Verify**: canonical tests pass.

### Step 2: Arrays encode recursively

Add a `Prepare` arm for `Array array when value is not byte[]`, and a `Write` case that emits header type `"array"` with `"length"` = element count and `"value"` = a JSON array where each element is written with the same scalar envelope via a recursive `Write(writer, Prepare(element))`. Multi-dimensional arrays (`array.Rank > 1`): encode `"type": "array"` with an extra `"dimensions": [..]` property and the elements in row-major order (`foreach` over the array gives that order). Null elements (`DBNull`/`null` inside `object[]`, or `int?[]`) use the existing null envelope.

Tests: `int[] {1,2,3}`, `string[] {"a", null}`, `int[,]` 2×2, an empty `int[]`. Assert stable hashes, distinct hashes for `{1,2}` vs `{2,1}`, and an explicit expected JSON for `{1,2}`.

**Verify**: canonical tests pass.

### Step 3: Named invariant-text types

Add an explicit arm set, matched by **runtime type full name** for provider types so Core takes no compile-time dependency it does not already have. `Microsoft.SqlServer.Types` and `Npgsql` are already referenced by Core. Check `src/SqlHarness.Core/SqlHarness.Core.csproj`; if both are referenced, use the types directly:

| Runtime type | Canonical type tag | Invariant text |
|---|---|---|
| `System.Net.IPAddress` | `ipAddress` | `ToString()` |
| `System.Net.NetworkInformation.PhysicalAddress` | `macAddress` | `ToString()` |
| `System.Collections.BitArray` | `bits` | `'0'`/`'1'` per bit, index order |
| `Microsoft.SqlServer.Types.SqlHierarchyId` | `hierarchyId` | `ToString()` (canonical `/1/2/`) |
| `Microsoft.SqlServer.Types.SqlGeography` / `SqlGeometry` | `geography` / `geometry` | `STSrid` + `;` + `STAsText().Value` |
| `NpgsqlTypes.NpgsqlCidr` (if it exists in the referenced Npgsql) | `cidr` | `ToString()` |

For each, null instances (`INullable.IsNull == true`) must use the null envelope.

Any other non-`IFormattable` type keeps the existing throw. Improve the message to name the type and say it is not supported as a **result** column, and keep the existing test with `CultureDependentScalar` passing.

Tests: one per row (construct the values directly; `SqlGeography.Point(1, 2, 4326)`, `SqlHierarchyId.Parse("/1/2/")`), each run once under `CultureInfo.CurrentCulture = new CultureInfo("pl-PL")` to prove invariance. Follow the culture-pinning pattern already in this test file.

**Verify**: canonical tests pass.

### Step 4: Docs

`README.md` and `AGENTS.md` (near the parameter type list): "Result hashing supports arrays, inet/macaddr/bit varying, non-finite floats, and SQL Server spatial/hierarchyid values; other provider-specific result types fail with a clear `not supported as a result column` error — cast them to text in the query."

**Verify**: `pwsh -NoProfile -File scripts/verify.ps1` → `verify: OK`.

## Test plan

New tests listed in Steps 1–3, about 15. The rewritten non-finite test is the only changed existing test.

## Done criteria

- [ ] Canonical/equivalence/snapshot tests and full gate pass
- [ ] `grep -n "Non-finite System.Double values are not supported" src` → no match
- [ ] `git diff main -- tests/SqlHarness.Tests/CanonicalResultsTests.cs` changes only the non-finite test and adds new tests
- [ ] `plans/README.md` row updated (state that live PG/SQL Server reads of these types were not exercised unless they were)

## STOP conditions

- Any **existing** expected-JSON or hash assertion changes, outside the non-finite test. That would mean an existing encoding changed.
- `SqlGeography`/`SqlHierarchyId` cannot be constructed in tests on this platform (native spatial DLL missing): skip that row with a clear `if` guard and note it, but do not remove the production arm.
- `Renderer.cs` or the MCP adapter throws on these values during a full CLI test. Report it as a follow-up; do not expand scope.

## Maintenance notes

- Canonical encodings are a persistence format (snapshots under `~/.sqlharness/snapshots`, compare artifacts). Never change an existing type tag or text form; add new tags only.
- Live verification of what SqlClient 7 actually returns for UDT columns (assembly binding of `Microsoft.SqlServer.Types`) needs an authorized SQL Server. Add it to the live CI lane (plan 036) when it exists.
