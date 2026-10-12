# Plan 024: Results containing PostgreSQL arrays/inet/NaN or SQL Server spatial/hierarchyid columns no longer fail the whole operation

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**: `git diff --stat 5280f78..HEAD -- src/SqlHarness.Core/CanonicalResults.cs tests/SqlHarness.Tests/CanonicalResultsTests.cs README.md AGENTS.md`
> Inspect changes against the current source and adapt implementation details
> within the authorized intent. A changed file or obsolete excerpt alone is
> not a STOP condition. Stop for an incompatible contract, missing required
> proof, or a change that exceeds the approved scope. Record the reconciled base.

## Status

- **Status**: IN PROGRESS — user-confirmed 2026-10-11; complete scope, branch fix/plan-024-canonical-types.
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
- `tests/SqlHarness.Tests/QueryResultCollectorTests.cs:211` also used `float.PositiveInfinity` as its unsupported value. When phase A enables non-finite values, update only that fixture to a genuinely unsupported test scalar so the test continues to cover partial raw-message handling.
- The test project pins `pl-PL` culture in some tests (`CanonicalResultsTests.cs` ~253-280). New encodings must be culture-invariant, and a test must prove it under `pl-PL`.

## Commands you will need

| Purpose | Command | Expected on success |
|---|---|---|
| Build | `dotnet build SqlHarness.sln -warnaserror` | 0 warnings |
| Canonical tests | `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~CanonicalResults|FullyQualifiedName~ResultEquivalence|FullyQualifiedName~Snapshot"` | all pass |
| Full gate | `pwsh -NoProfile -File scripts/verify.ps1` | `verify: OK` |

## Scope

**In scope**: `src/SqlHarness.Core/CanonicalResults.cs` (`CanonicalScalarCodec` only), `tests/SqlHarness.Tests/CanonicalResultsTests.cs`, `README.md` and `AGENTS.md` (a short "result types" note). The `QueryResultCollectorTests.cs` fixture adjustment described above is also in scope because infinity now succeeds by design; it must retain the unsupported-scalar partial-message assertion without changing collector production code.

**Out of scope**: reader changes (`GetValue`), equivalence semantics (`ResultEquivalence.cs`), display/rendering of these values in the CLI (`Renderer.cs`; check it does not throw too, and if it does, record it as a follow-up rather than fixing it here), and Npgsql version (plan 022).

### Authorized whole-branch final fix (2026-10-11)

The controller authorized a narrow extension after whole-branch review found that snapshot persistence discarded multidimensional array dimensions. `SnapshotStore` may persist nullable dimensions as additive optional metadata, and `SnapshotDiffer` may compare them. Cover persistence/load, equal and different shapes with the same flattened elements, old version 1 snapshots without dimensions, and runner `Different` / exit 8 behavior. Preserve canonical scalar tags, legacy version 1 documents, and ordinary scalar persisted bytes; do not bump the document version. No changes to equivalence semantics, rendering, readers, lower-bound behavior, or live database behavior are authorized.

### Scoped re-review and accepted minor

The final-fix re-review marked the original shape-loss finding **ADDRESSED** and found no new Critical or Important issue. It identified one Minor compatibility edge: a multidimensional snapshot created on this unmerged Plan 024 branch has no persisted dimensions, so comparing it with a fresh same-shape capture conservatively reports a difference (exit 8). The controller accepts this behavior: the pre-Plan 024 base `aae13b9` rejected ordinary arrays, so released snapshots cannot contain these values; a dimensionless document cannot establish shape equality; branch-local development snapshots may need recapture. Legacy version 1 documents remain readable, and scalar compatibility is preserved. This ruling and gate evidence are recorded in [the durable proof](024-canonical-results-proof.md).

## Git workflow

Branch `fix/plan-024-canonical-types`. Commits per step. Do NOT push.

## Steps

### Whole-plan execution — user correction, 2026-10-11

Execute the complete plan in one delivery, including its PostgreSQL scope. General/SQL Server priority determines which whole plan is chosen next; it does not authorize deferring part of a selected mixed plan. Steps below are internal implementation order. Preserve required proof and both platform gates; do not mark a partial scope DONE. Historical completed-step evidence elsewhere in this file remains unchanged.

- **Phase B provider reconciliation:** Npgsql 10 maps PostgreSQL `cidr` to `System.Net.IPNetwork` by default (Npgsql 10.0 release notes: https://www.npgsql.org/doc/release-notes/10.0.html). The referenced Npgsql 10.0.3 assembly confirms `IPNetwork` implements `IFormattable`, so `CanonicalScalarCodec` already accepts it using the generic stable type tag `System.Net.IPNetwork` and invariant string. Preserve that existing representation and its hash; do not replace it with a new `cidr` tag or add an obsolete `NpgsqlCidr` arm. The result remains supported as `System.Net.IPNetwork`. Phase B is complete in `3d097ff`; focused canonical/equivalence/snapshot tests passed 171/171, and Windows/Linux full gates passed. Evidence, gate logs, prior Phase A cancellation flake and clean reruns, and the no-live-database-read boundary are preserved in [the durable Plan 024 proof](024-canonical-results-proof.md).

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
| Npgsql 10 default PostgreSQL `cidr` result (`System.Net.IPNetwork`) | existing `System.Net.IPNetwork` IFormattable tag | `ToString(null, CultureInfo.InvariantCulture)`; preserve the pre-existing generic encoding |

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

- [x] Canonical/equivalence/snapshot tests and Windows/Linux full gates pass
- [x] `rg -n "Non-finite System.Double values are not supported" src` returns no match
- [x] The canonical tests and `QueryResultCollectorTests.cs` changes stay within the listed phase requirements
- [x] `plans/README.md` row updated and states that live PG/SQL Server reads were not exercised

The snapshot-shape final fix is an additional branch-review requirement: version 1 snapshot diff must report different multidimensional shapes even when flattened elements match, while loading old documents and preserving ordinary scalar bytes.

## STOP conditions

- Any **existing** expected-JSON or hash assertion changes, outside the non-finite test. That would mean an existing encoding changed.
- `SqlGeography`/`SqlHierarchyId` cannot be constructed in tests on this platform (native spatial DLL missing): skip that row with a clear `if` guard and note it, but do not remove the production arm.
- `Renderer.cs` or the MCP adapter throws on these values during a full CLI test. Report it as a follow-up; do not expand scope.

## Maintenance notes

- Canonical encodings are a persistence format (snapshots under `~/.sqlharness/snapshots`, compare artifacts). Never change an existing type tag or text form; add new tags only.
- Live verification of what SqlClient 7 actually returns for UDT columns (assembly binding of `Microsoft.SqlServer.Types`) needs an authorized SQL Server. Add it to the live CI lane (plan 036) when it exists.
